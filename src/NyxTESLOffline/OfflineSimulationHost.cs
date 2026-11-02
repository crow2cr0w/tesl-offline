using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CcgRpc;
using HarmonyLib;
using SPTESL;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = System.Object;

namespace NyxTESLOffline
{
    /*
     * Minimal authoritative server reconstructed on top of the retail classes.
     *
     * Scope of 0.5.7 reconstruction:
     *   - create the two PlayerState objects from the retail PlayerDescription;
     *   - create the original game zones and ServerCard objects;
     *   - create avatar and hidden decks using the original catalog;
     *   - complete the original encrypted TCP bootstrap;
     *   - drive EventGameStart/ActionMatchIntro and the retail mulligan history;
     *   - expose incremental CardCommandList-driven turns without routine
     *     FullGameState replacement;
     *   - accept creature/support PLAY, ATTACK, RING and END TURN decisions;
     *   - reconstruct rune breaks, prophecy draw/decline and normal draws;
     *   - restore the stripped TransferCardToZone state primitive;
     *   - run a deterministic local AI turn (play, attack, then pass);
     *   - finish basic avatar-lethal matches through retail Simulation.EndGame().
     *
     * This is intentionally not a parallel game engine.  State is kept in the
     * original Simulation/PlayerState/ServerCardCatalog graph so surviving TESL
     * rules can be re-enabled incrementally.
     */
    internal static class OfflineSimulationHost
    {
        private enum PendingCustomActionKind
        {
            None,
            PlayCard,
            ActivatedAbility
        }

        private sealed class HostState
        {
            internal Simulation Simulation;
            internal TcpNetworkServer Server;
            internal MatchConfiguration.StartInfo StartInfo;
            internal Scenario Scenario;
            internal PlayerServiceClient[] Clients;
            internal bool[] InitSent;
            internal bool WaitingLogged;
            internal bool GameInitialized;
            internal int ListUid;
            internal int ActivePlayerIndex;
            internal int StartingPlayerIndex;
            internal bool[] MulliganCommitted;
            internal int PendingProphecyPlayerIndex;
            internal int OutstandingProphecyListUid;
            internal int HumanTurns;
            internal int AiTurns;
            internal int AiActionFrame;
            internal bool AiActionPending;
            internal bool AiResumeAttacks;
            internal int PausedAttackVisualUid;
            internal bool PausedAttackVisualHeld;
            internal bool PausedAttackVisualComplete;
            internal int PausedAttackVisualReleasedFrame;
            internal ServerCard PausedAttackCompletionActor;
            internal ServerCard PausedAttackCompletionTarget;
            internal bool PausedAttackCompletionDone;
            internal bool PausedAttackEndMarkerSent;
            internal bool PausedAttackHumanPromptSent;
            internal readonly Dictionary<int, int> MasterOfThievesExtraAttacksUsed =
                new Dictionary<int, int>();
            internal readonly HashSet<int> AutoExaltPrompted = new HashSet<int>();
            internal ServerEffectUtilities.TriggerQueues TriggerQueues;
            internal List<CustomDisplayCard> CustomUiCards;
            internal PendingCustomActionKind PendingCustomAction;
            internal int PendingCustomPlayerIndex;
            internal ServerCard PendingCustomCard;
            internal ServerEffectUtilities.TriggerContext PendingCustomContext;
            internal CcgRpc.Decision PendingCustomFirstDecision;
            internal bool PendingCustomBoardCard;
            internal bool PendingCustomFromProphecy;
            internal bool GameplayFaulted;
            internal bool TickFaultLogged;
            internal bool MechanicalHeartRescueInProgress;
            internal int LastMechanicalHeartHistoryIndex;
            internal int LastHeartbeatLogFrame;
            /* Deduplicate Tick when both Simulation.FixedUpdate and Plugin.Update
             * drive the host in the same Unity frame. */
            internal int LastTickFrame = -1;
            internal int TcpWaitAttempts;
            internal float LastTcpWaitDiagAt = -1f;
        }

        private static readonly Dictionary<Simulation, HostState> States =
            new Dictionary<Simulation, HostState>();

        internal static bool IsAttackVisualHeld(int actorUid)
        {
            foreach (HostState state in States.Values)
                if (state.PausedAttackVisualUid == actorUid &&
                    state.PausedAttackVisualHeld)
                    return true;
            return false;
        }

        // Temporary action cards live in the generic Board zone while their
        // native damage action is logged. Use their owner's portrait as the
        // visual origin so ActionDamage FX (for example Lightning Bolt and
        // Crushing Blow) launch from the avatar instead of the left lane.
        internal static bool NormalizeActionDamageFxOrigin(
            HistoryLog historyLog, HistoryItem item)
        {
            if (historyLog == null || item == null ||
                item.Type != CcgRpc.HistoryType.ActionDamage ||
                item.ActorCardNetUid == 0)
                return false;

            foreach (HostState state in States.Values)
            {
                if (state.Simulation == null ||
                    !Object.ReferenceEquals(state.Simulation.HistoryLog, historyLog))
                    continue;

                ServerCard source = state.Simulation.CardCatalog.FindDynamicCard(
                    new CardNetUid(item.ActorCardNetUid));
                if (source == null || source.CurrentGameZoneName != GameZoneName.Board)
                    return false;

                ServerCard avatar = state.Simulation.CardCatalog.FindCardInDynamicZone(
                    card => card.OwnerNetId == source.OwnerNetId,
                    source.OwnerNetId, GameZoneName.AvatarBoard);
                if (avatar == null)
                    return false;

                int previousActor = item.ActorCardNetUid;
                item.ActorCardNetUid = avatar.NetUid.Id;
                item.UseActingLane = false;
                Plugin.Log("action damage FX origin normalized: source=" + previousActor
                    + " avatar=" + avatar.NetUid.Id + " useActingLane=false asset="
                    + item.AssetHash);
                return true;
            }
            return false;
        }

        internal static void MarkAttackVisualComplete(int actorUid)
        {
            foreach (HostState state in States.Values)
                if (state.PausedAttackVisualUid == actorUid)
                    state.PausedAttackVisualComplete = true;
        }

        internal static bool ReleaseAttackVisualAtEndMarker(int actorUid)
        {
            foreach (HostState state in States.Values)
            {
                if (state.PausedAttackVisualUid != actorUid ||
                    !state.PausedAttackEndMarkerSent)
                    continue;
                state.PausedAttackVisualHeld = false;
                state.PausedAttackVisualReleasedFrame = Time.frameCount;
                return true;
            }
            return false;
        }

        private static readonly FieldInfo TcpThreadDataField =
            AccessTools.Field(typeof(TcpNetworkServer), "_threadData");
        private static FieldInfo TcpAwaitingListField;

        private static readonly FieldInfo PlayerServiceClientsField =
            AccessTools.Field(typeof(Simulation), "PlayerServiceClients");
        private static readonly FieldInfo ObserverClientsField =
            AccessTools.Field(typeof(Simulation), "ObserverClients");
        private static readonly FieldInfo TurnStateStackField =
            AccessTools.Field(typeof(Simulation), "_turnStateStack");
        private static readonly FieldInfo GameStateField =
            AccessTools.Field(typeof(Simulation), "_gameState");
        private static readonly FieldInfo CurrentPlayerField =
            AccessTools.Field(typeof(Simulation), "<CurrentPlayer>k__BackingField");
        private static readonly FieldInfo HistoryOpenHeaderCountField =
            AccessTools.Field(typeof(HistoryLog), "_openHeaderCount");
        private static readonly FieldInfo NetworkServerField =
            AccessTools.Field(typeof(Simulation), "_networkServer");
        private static readonly FieldInfo GamePropertiesField =
            AccessTools.Field(typeof(Simulation), "_gameProperties");
        private static readonly FieldInfo ScenarioIdField =
            AccessTools.Field(typeof(Simulation), "_scenarioId");
        private static readonly FieldInfo ScenarioLossStreakField =
            AccessTools.Field(typeof(Simulation), "_scenarioLossStreak");
        private static readonly FieldInfo SkipIntroField =
            AccessTools.Field(typeof(Simulation), "_skipIntroCheat");
        private static readonly FieldInfo IsBonusMatchField =
            AccessTools.Field(typeof(Simulation), "_isBonusMatch");
        private static readonly FieldInfo IsLegendsMatchField =
            AccessTools.Field(typeof(Simulation), "_isLegendsMatch");
        private static readonly FieldInfo MatchModeField =
            AccessTools.Field(typeof(Simulation), "_matchMode");
        private static readonly FieldInfo MatchTypeField =
            AccessTools.Field(typeof(Simulation), "_matchType");
        private static readonly MethodInfo SendFullStateMethod =
            AccessTools.Method(typeof(Simulation), "SendFullStateToSpecificClient");
        private static readonly MethodInfo EndGameMethod =
            AccessTools.Method(typeof(Simulation), "EndGame");

        internal static void Start(
            Simulation simulation,
            TcpNetworkServer tcpServer,
            MatchConfiguration.StartInfo startInfo)
        {
            if (Object.ReferenceEquals(simulation, null))
                throw new ArgumentNullException("simulation");
            if (Object.ReferenceEquals(tcpServer, null))
                throw new ArgumentNullException("tcpServer");
            if (Object.ReferenceEquals(startInfo, null))
                throw new ArgumentNullException("startInfo");
            if (Object.ReferenceEquals(startInfo.Users, null) || startInfo.Users.Length != 2)
                throw new InvalidOperationException("StartInfo must contain exactly two users");

            /*
             * The retail binary omits several [NonSerialized] fields that the
             * original server-side ServerCard path assumes are constructor-
             * initialized.  Restore only those structural defaults before any
             * ServerCard is constructed.
             */
            int recoveredCards = 0;
            if (RetailRuleDataOverlay.IsSettled &&
                RetailRuleDataOverlay.RulesAvailable)
            {
                // The overlay is loaded before matchmaking. Re-parsing the
                // complete JSON bundle here blocks the Unity thread long
                // enough for the retail TCP client to reject the match.
                RetailServerDataHydrator.Hydrate(GameManager.Instance.Config);
                foreach (CardSet set in GameManager.Instance.Config.CardCollection)
                    if (!Object.ReferenceEquals(set, null) &&
                        !Object.ReferenceEquals(set.Cards, null))
                        recoveredCards += set.Cards.Count;
            }
            else
            {
                CardRulePipeline.Apply(GameManager.Instance.Config);
                recoveredCards = RetailRuleDataOverlay.ApplyPackagedRulesOrThrow(
                    GameManager.Instance.Config);
            }
            Plugin.Log("retail rules enforced before ServerCard creation: "
                + recoveredCards + " cards");

            ApplyUiMatchContext(startInfo);

            Scenario scenario = null;
            if (!Object.ReferenceEquals(GameManager.Instance, null))
                scenario = GameManager.Instance.FindScenario(startInfo.ScenarioId);

            if (Object.ReferenceEquals(scenario, null))
                throw new InvalidOperationException(
                    "scenario not found for id=" + startInfo.ScenarioId);

            int startingPlayerIndex = DetermineStartingPlayer(scenario, startInfo);

            PlayerServiceClient[] clients = new PlayerServiceClient[2];
            bool[] initSent = new bool[2];

            int i;
            for (i = 0; i < 2; ++i)
            {
                MatchConfiguration.User user = startInfo.Users[i];
                PlayerUniqueId playerUid = new PlayerUniqueId(
                    i == startingPlayerIndex ? 1 : 2);
                bool isHuman = user.Type == PlayerType.Normal;

                clients[i] = new PlayerServiceClient(
                    isHuman,
                    playerUid,
                    user.EncryptionKey,
                    user.SecretKey,
                    user.SpectatorKey,
                    user.RetryInfo,
                    false,
                    user.AllowTP,
                    false);

                if (isHuman)
                {
                    Plugin.Log(
                        "human PlayerServiceClient prepared: index=" + i
                        + " netId=" + playerUid.Id
                        + " secretLen="
                        + (Object.ReferenceEquals(user.SecretKey, null)
                            ? -1 : user.SecretKey.Length)
                        + " encryptLen="
                        + (Object.ReferenceEquals(user.EncryptionKey, null)
                            ? -1 : user.EncryptionKey.Length));
                }
            }

            simulation.RandomGenerator = startInfo.RandomNumberGenerator;
            simulation.CardCatalog = new ServerCardCatalog(startInfo.RandomNumberGenerator);
            SetField(NetworkServerField, simulation, tcpServer, "_networkServer");
            SetField(PlayerServiceClientsField, simulation, clients, "PlayerServiceClients");
            SetField(ScenarioIdField, simulation, startInfo.ScenarioId, "_scenarioId");
            SetField(ScenarioLossStreakField, simulation, startInfo.ScenarioLossStreak, "_scenarioLossStreak");
            SetField(SkipIntroField, simulation, startInfo.SkipIntro, "_skipIntroCheat");
            SetField(IsBonusMatchField, simulation, startInfo.IsBonusMatch, "_isBonusMatch");
            SetField(IsLegendsMatchField, simulation, startInfo.IsLegendsMatch, "_isLegendsMatch");
            SetField(MatchModeField, simulation, startInfo.MatchMode, "_matchMode");
            SetField(MatchTypeField, simulation, startInfo.MatchType, "_matchType");
            SetField(GamePropertiesField, simulation, scenario.GameProperties, "_gameProperties");

            HostState state = new HostState();
            state.Simulation = simulation;
            state.Server = tcpServer;
            state.StartInfo = startInfo;
            state.Scenario = scenario;
            state.Clients = clients;
            state.InitSent = initSent;
            state.TriggerQueues = new ServerEffectUtilities.TriggerQueues();
            state.CustomUiCards = new List<CustomDisplayCard>();
            state.ListUid = 0;
            state.ActivePlayerIndex = startingPlayerIndex;
            state.StartingPlayerIndex = startingPlayerIndex;
            state.MulliganCommitted = new bool[2];
            state.PendingProphecyPlayerIndex = -1;
            state.OutstandingProphecyListUid = 0;
            state.PendingCustomAction = PendingCustomActionKind.None;
            state.PendingCustomPlayerIndex = -1;

            ApplyScenarioPlayerDescriptions(state);
            BuildAuthoritativeState(state);
            States[simulation] = state;

            simulation.GameLengthStopwatch.Reset();
            simulation.GameLengthStopwatch.Start();

            if (!Object.ReferenceEquals(simulation.OnMatchStart, null))
                simulation.OnMatchStart.Invoke(startInfo.MatchMode, startInfo.MatchType);

            Plugin.Log("embedded authoritative state created: players="
                + simulation.Players.Count
                + " scenario=" + scenario.ResourceId.UniqueName
                + " mode=" + startInfo.MatchMode
                + " type=" + startInfo.MatchType);
            Plugin.Log("waiting for retail client TCP login on 127.0.0.1:4001");
        }

        private static void ApplyUiMatchContext(MatchConfiguration.StartInfo startInfo)
        {
            OfflineMatchRequest request = Plugin.ActiveLocalMatch;
            if (Object.ReferenceEquals(request, null))
                return;

            startInfo.MatchMode = (MatchMode)((int)request.MatchMode);
            startInfo.MatchType = (MatchType)((int)request.MatchType);

            CcgRpc.RetryInfo retry = startInfo.Users[0].RetryInfo;
            if (Object.ReferenceEquals(retry, null))
            {
                retry = new CcgRpc.RetryInfo();
                MatchConfiguration.User human = startInfo.Users[0];
                human.RetryInfo = retry;
                startInfo.Users[0] = human;
            }

            retry.MatchMode = (int)request.MatchMode;
            retry.MatchType = (int)request.MatchType;
            retry.AiDifficulty = (int)request.AiDifficulty;
            retry.MatchSeed = request.MatchSeed;
            retry.ArenaMatchOrderIndex = request.ArenaMatchOrderIndex;
            retry.Region = request.Region ?? String.Empty;
            retry.FriendBuid = request.FriendBuid ?? String.Empty;
            retry.PlayerDeckId = request.DeckId;

            if (!Object.ReferenceEquals(request.CampaignData, null))
            {
                CcgRpc.CampaignMatchInfo info = new CcgRpc.CampaignMatchInfo();
                info.ChapterEventId = request.CampaignData.chapterEventId;
                info.IsMastery = request.CampaignData.isMastery;
                info.ScenarioName = request.CampaignData.scenarioName ?? String.Empty;
                info.IntroChoice = (int)request.CampaignData.introChoice;
                retry.CampaignMatchInfo = info;
            }
        }

        private static void ApplyScenarioPlayerDescriptions(HostState state)
        {
            int i;
            for (i = 0; i < 2; ++i)
            {
                MatchConfiguration.User user = state.StartInfo.Users[i];
                CcgRpc.PlayerDescription description = user.CustomDeck;
                if (Object.ReferenceEquals(description, null))
                    continue;

                CustomDeck customDeck = FindScenarioDeckOverride(state, i);
                if (!Object.ReferenceEquals(customDeck, null))
                {
                    PresetDeck preset = FindPresetDeck(
                        customDeck,
                        RequestedDeckDifficulty(Plugin.ActiveLocalMatch));
                    if (!Object.ReferenceEquals(preset, null) &&
                        !Object.ReferenceEquals(preset.Cards, null) &&
                        preset.Cards.Count > 0)
                    {
                        description.Deck.Clear();
                        description.Premium.Clear();
                        int c;
                        for (c = 0; c < preset.Cards.Count; ++c)
                        {
                            DeckCardInfo info = preset.Cards[c];
                            int n;
                            for (n = 0; n < info.Count; ++n)
                            {
                                description.Deck.Add(info.Id);
                                description.Premium.Add(false);
                            }
                        }
                        Plugin.Log("scenario deck override: player=" + (i + 1)
                            + " deck=" + customDeck.ResourceId.UniqueName
                            + " cards=" + description.Deck.Count);
                    }
                    else if (!Object.ReferenceEquals(preset, null))
                    {
                        Plugin.Log("scenario preset deck card list absent in retail binary; "
                            + "preserving prepared local deck for player=" + (i + 1)
                            + " deck=" + customDeck.ResourceId.UniqueName);
                    }

                    if (user.Type == PlayerType.AiOpponent)
                        CustomDeckUtilities.SetAIAvatarData(
                            description,
                            state.StartInfo.RandomNumberGenerator,
                            customDeck);
                }

                if (user.Type == PlayerType.AiOpponent &&
                    !Object.ReferenceEquals(state.Scenario.AiOpponent, null) &&
                    state.Scenario.AiOpponent.resourceId != 0)
                {
                    description.AiOpponentId = state.Scenario.AiOpponent.resourceId;
                }

                if (user.Type == PlayerType.AiOpponent)
                {
                    EnsureRetailAiIdentity(state, description, customDeck);
                    ApplyAiCardBack(description, state.StartInfo.RandomNumberGenerator);
                }
                else
                    ApplyHumanCardBack(description);

                NormalizePremiumDeck(description);
                if (description.Deck.Count < 40)
                {
                    Plugin.Warn("short deck preserved without synthetic padding: type="
                        + user.Type + " cards=" + description.Deck.Count);
                }
                user.CustomDeck = description;
                state.StartInfo.Users[i] = user;
            }
        }

        private static void ApplyHumanCardBack(CcgRpc.PlayerDescription description)
        {
            if (Object.ReferenceEquals(description, null))
                return;

            const string category = "cardback";
            int index = description.InventoryTypes.IndexOf(category);
            if (index >= 0 && index < description.InventoryValues.Count)
                description.InventoryValues[index] = Plugin.SelectedCardBack;
            else
            {
                description.InventoryTypes.Add(category);
                description.InventoryValues.Add(Plugin.SelectedCardBack);
            }

            Plugin.Log("local player card back: " + Plugin.SelectedCardBack);
        }

        private static void ApplyAiCardBack(
            CcgRpc.PlayerDescription description,
            RandomIntegerGenerator random)
        {
            if (Object.ReferenceEquals(description, null))
                return;

            const string category = "cardback";
            string selected = Plugin.RandomCardBack(random);
            int index = description.InventoryTypes.IndexOf(category);
            if (index >= 0 && index < description.InventoryValues.Count)
                description.InventoryValues[index] = selected;
            else
            {
                description.InventoryTypes.Add(category);
                description.InventoryValues.Add(selected);
            }
            Plugin.Log("AI card back: " + selected);
        }

        /* Keep premium metadata aligned with the real deck.  The retail
         * StartSinglePlayerMatch path initializes every entry to false; Plugin
         * restores exact user inventory flags before Simulation.Start.  Scenario
         * and synthetic decks have no premium metadata, so missing entries are
         * conservatively regular rather than fabricated premium copies. */
        private static void NormalizePremiumDeck(CcgRpc.PlayerDescription description)
        {
            if (Object.ReferenceEquals(description, null))
                return;

            while (description.Premium.Count > description.Deck.Count)
                description.Premium.RemoveAt(description.Premium.Count - 1);
            while (description.Premium.Count < description.Deck.Count)
                description.Premium.Add(false);
        }

        /* Retail client updates magicka/runes from ActionSetPlayerAttribute
         * history items.  Plain PlayerState.SetAttribute only mutates the host
         * and leaves the UI at 0/0. */
        private static void EmitPlayerAttribute(
            HostState state,
            PlayerState player,
            AttributeType attribute)
        {
            if (Object.ReferenceEquals(state, null) ||
                Object.ReferenceEquals(player, null) ||
                Object.ReferenceEquals(state.Simulation, null) ||
                Object.ReferenceEquals(state.Simulation.HistoryLog, null))
                return;

            HistoryItem item = new HistoryItem();
            item.Type = CcgRpc.HistoryType.ActionSetPlayerAttribute;
            item.AssetHash = -1;
            item.ActingPlayerInstanceId = player.NetId.Id;
            item.PlayerAttributeName = (int)attribute;
            item.PlayerAttributeValue = player.QueryAttribute(attribute).Value;
            state.Simulation.HistoryLog.AddInstant(item);
        }

        /* Current magicka is a dedicated retail history action.
         * SequenceSetMana handles ActionSetMana and calls
         * PlayerInfoController.SetAP().  ActionSetPlayerAttribute(5) does not. */
        private static void EmitManaState(
            HostState state,
            PlayerState player)
        {
            if (Object.ReferenceEquals(state, null) ||
                Object.ReferenceEquals(player, null) ||
                Object.ReferenceEquals(state.Simulation, null) ||
                Object.ReferenceEquals(state.Simulation.HistoryLog, null))
                return;

            HistoryItem item = new HistoryItem();
            item.Type = CcgRpc.HistoryType.ActionSetMana;
            item.AssetHash = -1;
            item.ActingPlayerInstanceId = player.NetId.Id;
            item.PlayerAttributeName = (int)AttributeType.Mana;
            item.PlayerAttributeValue =
                player.QueryAttribute(AttributeType.Mana).Value;
            state.Simulation.HistoryLog.AddInstant(item);

            Plugin.Log("history emit mana: player=" + player.NetId.Id
                + " value=" + item.PlayerAttributeValue);
        }

        private static void EnsureRetailAiIdentity(
            HostState state,
            CcgRpc.PlayerDescription description,
            CustomDeck scenarioDeck)
        {
            int invalidId = UniqueResourceId.GetInvalid().Id;
            if (description.AiOpponentId != 0 &&
                description.AiOpponentId != invalidId)
            {
                ApplyAiDisplayName(description);
                return;
            }

            CustomDeck sourceDeck = scenarioDeck;
            if (Object.ReferenceEquals(sourceDeck, null) &&
                !Object.ReferenceEquals(GameManager.Instance.AiOpponentSelectedDeck, null) &&
                !Object.ReferenceEquals(GameManager.Instance.Config.CustomDecks, null))
            {
                string selectedName = GameManager.Instance.AiOpponentSelectedDeck.Name;
                int i;
                for (i = 0; i < GameManager.Instance.Config.CustomDecks.Count; ++i)
                {
                    CustomDeck candidate = GameManager.Instance.Config.CustomDecks[i];
                    if (!Object.ReferenceEquals(candidate, null) &&
                        candidate.ResourceId.UniqueName == selectedName)
                    {
                        sourceDeck = candidate;
                        break;
                    }
                }
            }

            if (!Object.ReferenceEquals(sourceDeck, null) &&
                !Object.ReferenceEquals(sourceDeck.ValidAvatars, null) &&
                sourceDeck.ValidAvatars.Count > 0)
            {
                CustomDeckUtilities.SetAIAvatarData(
                    description,
                    state.StartInfo.RandomNumberGenerator,
                    sourceDeck);
            }

            if ((description.AiOpponentId == 0 ||
                 description.AiOpponentId == invalidId) &&
                !Object.ReferenceEquals(GameManager.Instance.Config.AiOpponents, null))
            {
                List<AiOpponent> candidates = new List<AiOpponent>();
                AiOpponent fallback = null;
                int j;
                for (j = 0; j < GameManager.Instance.Config.AiOpponents.Count; ++j)
                {
                    AiOpponent ai = GameManager.Instance.Config.AiOpponents[j];
                    if (Object.ReferenceEquals(ai, null) ||
                        ai.ResourceId.Id == 0 ||
                        ai.ResourceId.Id == invalidId)
                        continue;
                    if (Object.ReferenceEquals(fallback, null))
                        fallback = ai;
                    if (!String.IsNullOrEmpty(ai.DisplayNameKey.RawKey))
                        candidates.Add(ai);
                }

                AiOpponent selected = fallback;
                if (candidates.Count > 0 &&
                    !Object.ReferenceEquals(state.StartInfo.RandomNumberGenerator, null))
                {
                    int index = state.StartInfo.RandomNumberGenerator.GenerateInRange(
                        0, candidates.Count - 1);
                    selected = candidates[index];
                }
                if (!Object.ReferenceEquals(selected, null))
                    description.AiOpponentId = selected.ResourceId.Id;
            }

            ApplyAiDisplayName(description);
        }

        private static void ApplyAiDisplayName(CcgRpc.PlayerDescription description)
        {
            if (Object.ReferenceEquals(description, null) ||
                Object.ReferenceEquals(GameManager.Instance.Config.AiOpponents, null))
                return;

            int i;
            for (i = 0; i < GameManager.Instance.Config.AiOpponents.Count; ++i)
            {
                AiOpponent ai = GameManager.Instance.Config.AiOpponents[i];
                if (Object.ReferenceEquals(ai, null) ||
                    ai.ResourceId.Id != description.AiOpponentId)
                    continue;

                string display = null;
                try
                {
                    if (!String.IsNullOrEmpty(ai.DisplayNameKey.RawKey) &&
                        !Object.ReferenceEquals(LocalizationManager.Instance, null))
                    {
                        string localized;
                        if (LocalizationManager.Instance.TryGetText(
                                ai.DisplayNameKey.RawKey, out localized))
                            display = localized;
                    }
                }
                catch
                {
                    display = null;
                }

                if (String.IsNullOrEmpty(display))
                    display = ai.ResourceId.UniqueName;
                if (!String.IsNullOrEmpty(display))
                    description.PlayerName = display;

                if (!Object.ReferenceEquals(GameNetworkManager.Instance, null))
                    GameNetworkManager.Instance.OpponentMatchingAvatarName =
                        ai.ResourceId.UniqueName;

                Plugin.Log("retail AI identity selected: id=" + ai.ResourceId.Id
                    + " unique=" + ai.ResourceId.UniqueName
                    + " name=" + description.PlayerName);
                return;
            }
        }

        private static CustomDeck FindScenarioDeckOverride(HostState state, int playerIndex)
        {
            if (Object.ReferenceEquals(state.Scenario.DeckOverrides, null) ||
                Object.ReferenceEquals(GameManager.Instance.Config.CustomDecks, null))
                return null;

            bool human = state.StartInfo.Users[playerIndex].Type == PlayerType.Normal;
            int bits = SlotBits(playerIndex, human);
            DeckDifficulty wanted = RequestedDeckDifficulty(Plugin.ActiveLocalMatch);
            DeckOverride fallback = null;
            int i;
            for (i = 0; i < state.Scenario.DeckOverrides.Count; ++i)
            {
                DeckOverride item = state.Scenario.DeckOverrides[i];
                if (Object.ReferenceEquals(item, null) ||
                    Object.ReferenceEquals(item.Override, null) ||
                    ((((int)item.SlotMask) & bits) == 0))
                    continue;
                if (Object.ReferenceEquals(fallback, null))
                    fallback = item;
                if (item.Difficulty == wanted)
                {
                    fallback = item;
                    break;
                }
            }

            if (Object.ReferenceEquals(fallback, null))
                return null;

            for (i = 0; i < GameManager.Instance.Config.CustomDecks.Count; ++i)
            {
                CustomDeck deck = GameManager.Instance.Config.CustomDecks[i];
                if (!Object.ReferenceEquals(deck, null) &&
                    deck.ResourceId.Id == fallback.Override.resourceId)
                    return deck;
            }
            return null;
        }

        private static DeckDifficulty RequestedDeckDifficulty(OfflineMatchRequest request)
        {
            if (Object.ReferenceEquals(request, null))
                return DeckDifficulty.Normal;
            if (request.AiDifficulty == SparkleServices.Matching.IAiDifficulty.Expert)
                return DeckDifficulty.Hardest;
            if (request.AiDifficulty == SparkleServices.Matching.IAiDifficulty.Adept)
                return DeckDifficulty.Hard;
            return DeckDifficulty.Normal;
        }

        private static PresetDeck FindPresetDeck(CustomDeck deck, DeckDifficulty wanted)
        {
            if (Object.ReferenceEquals(deck, null) ||
                Object.ReferenceEquals(deck.Decks, null) || deck.Decks.Count == 0)
                return null;
            int i;
            for (i = 0; i < deck.Decks.Count; ++i)
            {
                if (!Object.ReferenceEquals(deck.Decks[i], null) &&
                    deck.Decks[i].Difficulty == wanted)
                    return deck.Decks[i];
            }
            return deck.Decks[0];
        }

        private static void BuildAuthoritativeState(HostState state)
        {
            Simulation simulation = state.Simulation;
            GameConfiguration config = GameManager.Instance.Config;

            simulation.Players.Clear();
            simulation.CurrentOpponents.Clear();

            int i;
            for (i = 0; i < 2; ++i)
            {
                MatchConfiguration.User user = state.StartInfo.Users[i];
                PlayerUniqueId uid = new PlayerUniqueId(
                    i == state.StartingPlayerIndex ? 1 : 2);
                PlayerState player = BuildPlayerState(state, i, uid, user);
                simulation.Players.Add(player);
            }

            /* Zones must exist before any ServerCard is constructed. */
            for (i = 0; i < 2; ++i)
            {
                PlayerState player = simulation.Players[i];
                GameZoneOverrideList zoneOverride = BuildZoneOverrides(state, i);
                simulation.CardCatalog.CreatePlayerGameZones(
                    player.NetId, config.Zones, zoneOverride);
            }

            ServerEffectUtilities.TriggerGameState triggerState = TriggerState(state);

            for (i = 0; i < 2; ++i)
            {
                MatchConfiguration.User user = state.StartInfo.Users[i];
                PlayerState player = simulation.Players[i];
                CreatePlayerCards(triggerState, player, user.CustomDeck);
            }

            GameState gameState = GetGameState(simulation);
            gameState.GamePhase = GamePhase.Start;
            gameState.MulliganPlayerResponses.Clear();
        }

        private static PlayerState BuildPlayerState(
            HostState state,
            int playerIndex,
            PlayerUniqueId uid,
            MatchConfiguration.User user)
        {
            PlayerState player = new PlayerState();
            CcgRpc.PlayerDescription description = user.CustomDeck;

            player.NetId = uid;
            player.Name = (!Object.ReferenceEquals(description, null) &&
                           !String.IsNullOrEmpty(description.PlayerName))
                ? description.PlayerName
                : (user.Type == PlayerType.Normal ? "Offline" : "AI");
            player.IsHuman = user.Type == PlayerType.Normal;
            player.PlayerType = (int)user.Type;
            player.Rank = user.Rank;
            player.LegendRank = user.LegendRank;
            player.ScenarioLossStreak = state.StartInfo.ScenarioLossStreak;
            player.ArenaBonusMatch = user.ArenaBonusMatch;
            player.MulliganState = PlayerMulliganState.Mulligan_Draw;

            if (!Object.ReferenceEquals(description, null))
            {
                int i;
                for (i = 0; i < description.Deck.Count; ++i)
                    player.StartingDeck.Add(description.Deck[i]);
                for (i = 0; i < description.Premium.Count; ++i)
                    player.StartingDeckPremiums.Add(description.Premium[i]);
                player.DeckModifiedTime = description.DeckModifiedTime;
                player.AiOpponentReference.resourceId = description.AiOpponentId;

                if (!player.IsHuman &&
                    !Object.ReferenceEquals(state.Scenario.AiOpponent, null) &&
                    state.Scenario.AiOpponent.resourceId != 0)
                {
                    player.AiOpponentReference.resourceId =
                        state.Scenario.AiOpponent.resourceId;
                }

                int inventoryCount = Math.Min(
                    description.InventoryTypes.Count,
                    description.InventoryValues.Count);
                for (i = 0; i < inventoryCount; ++i)
                    player.AccountInventory[description.InventoryTypes[i]] =
                        description.InventoryValues[i];
            }

            CopyDefaultPlayerAttributes(player);
            ApplyScenarioPlayerOverride(state, playerIndex, player);
            EnsurePlayerAttributes(state, player, playerIndex);
            EnsureMatchStatStorage(player);

            return player;
        }

        private static void CopyDefaultPlayerAttributes(PlayerState player)
        {
            GameConfiguration config = GameManager.Instance.Config;
            if (Object.ReferenceEquals(config.PlayerDefinitions, null) ||
                config.PlayerDefinitions.Count == 0 ||
                Object.ReferenceEquals(config.PlayerDefinitions[0], null))
                return;

            CopyAttributes(player, config.PlayerDefinitions[0]);
        }

        private static void ApplyScenarioPlayerOverride(
            HostState state,
            int playerIndex,
            PlayerState player)
        {
            if (Object.ReferenceEquals(state.Scenario.PlayerOverrides, null))
                return;

            int bits = SlotBits(playerIndex, player.IsHuman);
            int i;
            for (i = 0; i < state.Scenario.PlayerOverrides.Count; ++i)
            {
                PlayerOverride item = state.Scenario.PlayerOverrides[i];
                if (Object.ReferenceEquals(item, null) ||
                    Object.ReferenceEquals(item.Override, null))
                    continue;
                if ((((int)item.SlotMask) & bits) == 0)
                    continue;
                CopyAttributes(player, item.Override);
            }
        }

        private static void CopyAttributes(PlayerState player, Entity definition)
        {
            int i;
            for (i = 0; i < definition.EntityAttributes.Count; ++i)
            {
                IntAttribute intAttribute = definition.EntityAttributes[i] as IntAttribute;
                if (!Object.ReferenceEquals(intAttribute, null))
                    player.SetAttribute(intAttribute.Name, intAttribute.Value);
            }
        }

        private static void EnsurePlayerAttributes(HostState state, PlayerState player, int playerIndex)
        {
            EnsureAttribute(player, AttributeType.HandSize, 10);
            /* The retail default player definition contains HandSize=0 in this
             * depot.  EnsureAttribute only fills missing entries, so token
             * creation treats every nonempty hand as full and immediately
             * discards cards created into it (for example J'zargo's scroll). */
            if (player.FindHandSize() <= 0)
                player.SetAttribute(AttributeType.HandSize, 10);
            EnsureAttribute(player, AttributeType.StartingHealth, 30);
            EnsureAttribute(player, AttributeType.NumStartingRunes, 5);
            EnsureAttribute(player, AttributeType.NumRunes, 5);
            EnsureAttribute(player, AttributeType.NumRunesLost, 0);
            EnsureAttribute(player, AttributeType.RuneHealth, 5);
            EnsureAttribute(player, AttributeType.MaxMana, 0);
            EnsureAttribute(player, AttributeType.Mana, 0);
            EnsureAttribute(player, AttributeType.TurnCount, 0);

            if (playerIndex != state.StartingPlayerIndex)
            {
                /* The Ring of Magicka belongs to the player going second.
                 * It is visible from match start (three charges) but only usable
                 * during that player's active turn. */
                player.SetAttribute(AttributeType.RingUsesLeft, 3);
                player.SetAttribute(AttributeType.CanUseRing, 0);
            }
            else
            {
                player.SetAttribute(AttributeType.RingUsesLeft, 0);
                player.SetAttribute(AttributeType.CanUseRing, 0);
            }
        }

        private static void EnsureAttribute(
            PlayerState player,
            AttributeType name,
            int value)
        {
            if (!player.GetAttribute(name).HasValue)
                player.SetAttribute(name, value);
        }

        private static void EnsureMatchStatStorage(PlayerState player)
        {
            int required = (int)PlayerMatchStatType.CountOf;
            while (player.MatchStatValues.Count < required)
                player.MatchStatValues.Add(0);
        }

        private static GameZoneOverrideList BuildZoneOverrides(
            HostState state,
            int playerIndex)
        {
            GameZoneOverrideList result = new GameZoneOverrideList();
            bool any = false;
            bool human = state.StartInfo.Users[playerIndex].Type == PlayerType.Normal;
            int bits = SlotBits(playerIndex, human);

            if (!Object.ReferenceEquals(state.Scenario.GameZoneOverrides, null))
            {
                int i;
                for (i = 0; i < state.Scenario.GameZoneOverrides.Count; ++i)
                {
                    GameZoneOverrideList item = state.Scenario.GameZoneOverrides[i];
                    if (Object.ReferenceEquals(item, null) ||
                        ((((int)item.SlotMask) & bits) == 0))
                        continue;

                    int j;
                    for (j = 0; j < item.ZoneList.Count; ++j)
                    {
                        result.ZoneList.Add(item.ZoneList[j]);
                        any = true;
                    }
                }
            }

            if (!Object.ReferenceEquals(state.StartInfo.OptionalCreatureBoardOverride, null) &&
                playerIndex < state.StartInfo.OptionalCreatureBoardOverride.Length &&
                !Object.ReferenceEquals(
                    state.StartInfo.OptionalCreatureBoardOverride[playerIndex], null))
            {
                result.ZoneList.Add(
                    state.StartInfo.OptionalCreatureBoardOverride[playerIndex]);
                any = true;
            }

            return any ? result : null;
        }

        private static int SlotBits(int playerIndex, bool human)
        {
            int bits = playerIndex == 0
                ? (int)PlayerSlotMask.FirstPlayer
                : (int)PlayerSlotMask.SecondPlayer;
            bits |= human ? (int)PlayerSlotMask.Human : (int)PlayerSlotMask.Ai;
            return bits;
        }

        private static void CreatePlayerCards(
            ServerEffectUtilities.TriggerGameState triggerState,
            PlayerState player,
            CcgRpc.PlayerDescription description)
        {
            if (Object.ReferenceEquals(description, null))
                return;

            int avatarCardId = description.AvatarCardId;
            if (avatarCardId == 0 &&
                !Object.ReferenceEquals(GameManager.Instance.Config.AvatarCollection, null) &&
                !Object.ReferenceEquals(
                    GameManager.Instance.Config.AvatarCollection.FallbackAvatar, null))
            {
                avatarCardId =
                    GameManager.Instance.Config.AvatarCollection.FallbackAvatar.resourceId;
            }

            if (avatarCardId != 0)
            {
                ServerCard avatar = ServerCardCatalog.CreateCard(
                    triggerState,
                    avatarCardId,
                    false,
                    GameZoneName.AvatarBoard,
                    0,
                    CardInsertionType.Bottom,
                    player.NetId,
                    null,
                    null);
                if (!Object.ReferenceEquals(avatar, null) && avatar.CurrentHealth <= 0)
                {
                    CardAttributeQueryResult maxHealth =
                        avatar.QueryAttribute(AttributeType.MaxHealth);
                    avatar.SetAvatarHealthPreGame(maxHealth.Value > 0 ? maxHealth.Value : 30);
                }
            }

            int deckCount = description.Deck.Count;
            int i;
            for (i = 0; i < deckCount; ++i)
            {
                /* Opening cards must originate in Deck.  PlayerActions.DrawCards
                 * emits ActionDrawForMulligan only when MulliganState is Draw;
                 * pre-populating Hand bypasses the retail draw animation. */
                ServerCardCatalog.CreateCard(
                    triggerState,
                    description.Deck[i],
                    true,
                    GameZoneName.Deck,
                    0,
                    CardInsertionType.Random,
                    player.NetId,
                    null,
                    null);
            }

            /* This is a structural invariant, not a recovery heuristic.
             * Internal_CreateCard creates in RemoveFromGame and then reaches
             * TransferCardToZone to enter Deck.  If the retail transfer stub
             * has not actually been restored, every card remains outside Deck. */
            int materializedDeck = triggerState.CardCatalog.FindCardCount(
                GameZoneName.Deck, player.NetId);
            if (materializedDeck != deckCount)
            {
                throw new InvalidOperationException(
                    "authoritative deck materialization failed for player="
                    + player.NetId.Id + " expected=" + deckCount
                    + " actual=" + materializedDeck);
            }

            Plugin.Log(
                "deck materialized: player=" + player.NetId.Id
                + " cards=" + materializedDeck
                + " avatarCardId=" + avatarCardId);
        }

        private static int DetermineStartingPlayer(
            Scenario scenario,
            MatchConfiguration.StartInfo startInfo)
        {
            if (scenario.StartingPlayer == SinglePlayerFirstToAct.AiPlayer)
                return 1;
            if (scenario.StartingPlayer == SinglePlayerFirstToAct.HumanPlayer)
                return 0;

            return startInfo.RandomNumberGenerator.GenerateInRange(0, 1);
        }

        /*
         * Drive every live host from Plugin.Update so TCP attach / InitClient
         * do not depend on Simulation.FixedUpdate running (the MonoBehaviour
         * may not tick until CDGameBoard is active, which caused RejectTimeout
         * with tcpClient connected but no InitClient).
         */
        internal static void TickAllActiveHosts()
        {
            if (States.Count == 0)
                return;

            List<Simulation> snapshot = new List<Simulation>(States.Keys);
            int i;
            for (i = 0; i < snapshot.Count; ++i)
                Tick(snapshot[i]);
        }

        internal static int ActiveHostCount
        {
            get { return States.Count; }
        }

        internal static void Tick(Simulation simulation)
        {
            HostState state;
            if (!States.TryGetValue(simulation, out state))
                return;

            /* One logical tick per Unity frame even if FixedUpdate + Update both
             * call us. */
            int frame = Time.frameCount;
            if (state.LastTickFrame == frame)
                return;
            state.LastTickFrame = frame;

            /* Keep transport maintenance independent from gameplay.  A malformed
             * card/effect must not silently kill the loopback session and turn
             * into the retail "disconnected from your match" popup. */
            try
            {
                AttachHumanConnection(state);
            }
            catch (Exception ex)
            {
                MarkGameplayFault(state, "AttachHumanConnection", ex);
            }

            try
            {
                MaintainHumanConnection(state);
            }
            catch (Exception ex)
            {
                LogTickFaultOnce(state, "MaintainHumanConnection", ex);
            }

            try
            {
                PumpHumanNetwork(state);
            }
            catch (Exception ex)
            {
                MarkGameplayFault(state, "PumpHumanNetwork", ex);
            }

            if (!state.GameplayFaulted)
            {
                try
                {
                    RunAiProphecyIfNeeded(state);
                    RunPausedHumanAttackIfNeeded(state);
                    RunAiIfNeeded(state);
                }
                catch (Exception ex)
                {
                    MarkGameplayFault(state, "RunAiIfNeeded", ex);
                }
            }
        }

        private static void MaintainHumanConnection(HostState state)
        {
            int i;
            for (i = 0; i < state.Clients.Length; ++i)
            {
                PlayerServiceClient client = state.Clients[i];
                MatchConfiguration.User user = state.StartInfo.Users[i];
                if (Object.ReferenceEquals(client, null) || user.Type != PlayerType.Normal)
                    continue;
                if (!client.IsTrueNetworkConnection())
                    continue;

                /* The retail client always starts its heartbeat timer after TCP
                 * connect.  The original embedded server normally emits enough
                 * traffic that the client never goes 18 s without receiving a
                 * frame.  Our reconstructed server can sit idle while the human
                 * thinks, so explicitly send server heartbeats. */
                bool alive = client.DectectConnectionLoss(true);
                if (!alive)
                {
                    Plugin.Warn("local player TCP connection marked lost");
                    continue;
                }

                if (Time.frameCount - state.LastHeartbeatLogFrame > 900)
                {
                    state.LastHeartbeatLogFrame = Time.frameCount;
                    Plugin.Log("local match transport alive; heartbeat maintenance active");
                }
            }
        }

        private static void MarkGameplayFault(HostState state, string stage, Exception ex)
        {
            if (!state.GameplayFaulted)
            {
                state.GameplayFaulted = true;
                Plugin.Warn("local gameplay paused after " + stage + " failure: " + ex);
                Plugin.Warn("TCP session kept alive for diagnosis; match host state was NOT discarded");
            }
        }

        private static void LogTickFaultOnce(HostState state, string stage, Exception ex)
        {
            if (state.TickFaultLogged)
                return;
            state.TickFaultLogged = true;
            Plugin.Warn("local transport maintenance failure at " + stage + ": " + ex);
        }

        internal static void ReportOuterTickFailure(Simulation simulation, Exception ex)
        {
            HostState state;
            if (States.TryGetValue(simulation, out state))
                MarkGameplayFault(state, "Simulation.FixedUpdate outer guard", ex);
            else
                Plugin.Warn("Simulation.FixedUpdate failed before local host state existed: " + ex);
        }

        private static void AttachHumanConnection(HostState state)
        {
            int i;
            for (i = 0; i < state.Clients.Length; ++i)
            {
                PlayerServiceClient client = state.Clients[i];
                MatchConfiguration.User user = state.StartInfo.Users[i];

                if (Object.ReferenceEquals(client, null) || user.Type != PlayerType.Normal)
                    continue;

                if (!client.IsTrueNetworkConnection())
                {
                    string secret = client.GetSecretKey();
                    TcpNetworkServer.NewConnection connection =
                        state.Server.AcquireRequestedConnection(secret, null);

                    if (Object.ReferenceEquals(connection.Client, null))
                    {
                        state.TcpWaitAttempts++;
                        float now = Time.realtimeSinceStartup;
                        bool first = !state.WaitingLogged;
                        bool periodic = state.LastTcpWaitDiagAt < 0f ||
                            (now - state.LastTcpWaitDiagAt) >= 2.0f;
                        if (first || periodic)
                        {
                            state.WaitingLogged = true;
                            state.LastTcpWaitDiagAt = now;
                            int awaiting = CountAwaitingConnections(state.Server);
                            Plugin.Log(
                                "TCP wait: player=" + (i + 1)
                                + " attempts=" + state.TcpWaitAttempts
                                + " secretLen="
                                + (Object.ReferenceEquals(secret, null) ? -1 : secret.Length)
                                + " status=" + client.GetConnectionStatus()
                                + " awaitingList=" + awaiting
                                + " scene=" + SceneManager.GetActiveScene().name);
                            if (first)
                                Plugin.Log(
                                    "waiting for StartSinglePlayerMatch TCP login on 127.0.0.1:4001");
                        }
                        continue;
                    }

                    if (!client.SetConnection(connection.Client, connection.UserName, true))
                    {
                        Plugin.Warn(
                            "failed to attach TCP connection to local player="
                            + (i + 1)
                            + " status=" + client.GetConnectionStatus());
                        continue;
                    }

                    Plugin.Log("local TCP client accepted: player=" + (i + 1)
                        + " attempts=" + state.TcpWaitAttempts
                        + " scene=" + SceneManager.GetActiveScene().name);
                }

                if (!state.InitSent[i] &&
                    client.GetConnectionStatus() == PlayerServiceClient.ConnectionStatus.Connected)
                {
                    InitClient init = new InitClient();
                    init.PlayerInstanceId = state.Simulation.Players[i].NetId.Id;
                    init.ScenarioId = state.StartInfo.ScenarioId;
                    init.RetryInfo = user.RetryInfo;
                    init.IsSpectating = false;
                    init.IsDualSpectating = false;
                    init.RejectReason = RejectReason.RejectNone;
                    init.GameserverVersion = "nyx-offline-0.6.0-alpha14";

                    if (!client.SendInitClientMessage(init))
                    {
                        Plugin.Warn("SendInitClientMessage failed for player " + (i + 1)
                            + " status=" + client.GetConnectionStatus());
                        continue;
                    }

                    state.InitSent[i] = true;
                    Plugin.Log("InitClient sent: player=" + (i + 1)
                        + " scenario=" + init.ScenarioId
                        + " mode=" + state.StartInfo.MatchMode
                        + " type=" + state.StartInfo.MatchType
                        + " netId=" + init.PlayerInstanceId);

                    try
                    {
                        SendInitialState(state, i);
                    }
                    catch (Exception ex)
                    {
                        MarkGameplayFault(state, "SendInitialState", ex);
                        continue;
                    }

                    if (!Object.ReferenceEquals(state.Simulation.OnPlayerInitialConnection, null))
                        state.Simulation.OnPlayerInitialConnection.Invoke();
                }
            }
        }

        /* Best-effort peek at TcpNetworkServer awaiting list for diagnostics.
         * Returns -1 when reflection cannot reach the private field. */
        private static int CountAwaitingConnections(TcpNetworkServer server)
        {
            if (Object.ReferenceEquals(server, null))
                return -1;
            try
            {
                if (Object.ReferenceEquals(TcpThreadDataField, null))
                    return -1;
                object threadData = TcpThreadDataField.GetValue(server);
                if (Object.ReferenceEquals(threadData, null))
                    return -1;
                if (Object.ReferenceEquals(TcpAwaitingListField, null))
                {
                    /* Resolve once against the live thread-data type. */
                    TcpAwaitingListField = AccessTools.Field(
                        threadData.GetType(), "AwaitingList");
                }
                if (Object.ReferenceEquals(TcpAwaitingListField, null))
                    return -1;
                object listObj = TcpAwaitingListField.GetValue(threadData);
                ICollection collection = listObj as ICollection;
                if (Object.ReferenceEquals(collection, null))
                    return -1;
                return collection.Count;
            }
            catch
            {
                return -1;
            }
        }

        private static void SendInitialState(HostState state, int humanIndex)
        {
            if (state.GameInitialized)
                return;

            /* Do not send FullGameState here. SequenceFullGameState is a rejoin
             * path and initializes the board with rejoin=true, bypassing the
             * original match-intro/coin and mulligan sequences. */
            StartOpeningSequence(state);
            state.GameInitialized = true;
            Plugin.Log("retail EventGameStart/match intro/mulligan pipeline sent");
        }

        private static void StartOpeningSequence(HostState state)
        {
            Simulation simulation = state.Simulation;
            ServerEffectUtilities.TriggerGameState triggerState = TriggerState(state);

            simulation.HistoryLog.StartEvent(
                CcgRpc.HistoryType.EventGameStart,
                PlayerUniqueId.GetInvalid(),
                CardNetUid.GetInvalid(),
                CardNetUid.GetInvalid(),
                -1);

            HistoryItem intro = new HistoryItem();
            intro.Type = CcgRpc.HistoryType.ActionMatchIntro;
            intro.IsBonusMatch = state.StartInfo.IsBonusMatch;

            /* SequenceMatchIntro marks the first HistoryPlayerDescription with
             * the "first" coin UI.  PlayerInstanceId ordering independently
             * determines IntroSettings.IsPlayerFirst, so emit UID 1 first. */
            int first = state.StartingPlayerIndex;
            int second = first == 0 ? 1 : 0;
            intro.MatchIntroPlayers.Add(
                simulation.Players[first].HistoryCreate_FullPlayerDescription(
                    simulation.CardCatalog));
            intro.MatchIntroPlayers.Add(
                simulation.Players[second].HistoryCreate_FullPlayerDescription(
                    simulation.CardCatalog));

            AddMatchIntroLanes(state, intro, simulation.Players[0]);
            simulation.HistoryLog.AddInstant(intro);

            /* MatchIntro carries portrait metadata and health, not avatar
             * ClientCards. The avatars were materialized before EventGameStart,
             * so their initial transfer is absent from the opening history.
             * Publish them after MatchIntro initializes the board. Retail
             * SequenceMoveCards binds each PlayerAvatar's UID, attributes and
             * collider, allowing both targeting and SequenceCardAttack lookup.
             * Do this once, during opening; replaying it for an already-visible
             * avatar would make SequenceMoveCards recycle that portrait. */
            HistoryItem avatars = new HistoryItem();
            avatars.Type = CcgRpc.HistoryType.ActionMoveCards;
            avatars.AssetHash = -1;
            for (int avatarIndex = 0;
                 avatarIndex < simulation.Players.Count; ++avatarIndex)
            {
                PlayerState avatarOwner = simulation.Players[avatarIndex];
                ServerCard avatar = simulation.CardCatalog.FindCardInDynamicZone(
                    delegate(ServerCard candidate) {
                        return candidate.OwnerNetId == avatarOwner.NetId;
                    }, avatarOwner.NetId, GameZoneName.AvatarBoard);
                if (Object.ReferenceEquals(avatar, null))
                    throw new InvalidOperationException(
                        "opening avatar missing for player=" + avatarOwner.NetId.Id);

                HistoryCard avatarHistory =
                    avatar.HistoryCreate_FullStateCard(simulation.CardCatalog);
                avatarHistory.IsRevealed = true;
                avatars.AffectedCardsArray.Add(avatarHistory);
                Plugin.Log("opening avatar publish: player=" + avatarOwner.NetId.Id
                    + " uid=" + avatarHistory.CardNetUid
                    + " card=" + avatarHistory.CardId
                    + " health=" + avatarHistory.CurrentHealth
                    + " attrs=" + avatarHistory.AffectedAttributeName.Count);
            }
            simulation.HistoryLog.AddInstant(avatars);

            /* Board3DUI does not infer the ring from FullPlayerDescription.
             * SequenceSetPlayerAttribute drives ShowRingUsesLeft() (attr 12)
             * and SetRingCanUseState() (attr 13), so publish both while the
             * EventGameStart queue is being built. */
            PlayerState ringOwner = simulation.Players[second];
            EmitPlayerAttribute(state, ringOwner, AttributeType.RingUsesLeft);
            EmitPlayerAttribute(state, ringOwner, AttributeType.CanUseRing);
            Plugin.Log("ring initialized: player=" + ringOwner.NetId.Id
                + " charges="
                + ringOwner.QueryAttribute(AttributeType.RingUsesLeft).Value
                + " canUse=0");

            /* Simulation.ProcessStartGame is stripped in this retail build.
             * Keep the authoritative opening event, then dispatch the native
             * WhenGameStarts trigger before mulligan so startup effects enter
             * the same retail trigger queue as every later event. */
            ServerEffectUtilities.TriggerEffectEvent(
                triggerState,
                new ServerEffectUtilities.TriggerEvent(
                    EffectTriggerType.WhenGameStarts,
                    (ServerCard)null,
                    simulation.Players));
            DrainAutomaticTriggerQueues(state, false);

            int i;
            for (i = 0; i < simulation.Players.Count; ++i)
            {
                PlayerState player = simulation.Players[i];
                int deckBefore = simulation.CardCatalog.FindCardCount(
                    GameZoneName.Deck, player.NetId);
                int handBefore = simulation.CardCatalog.FindCardCount(
                    GameZoneName.Hand, player.NetId);
                if (deckBefore < 3)
                {
                    throw new InvalidOperationException(
                        "mulligan requires three deck cards for player="
                        + player.NetId.Id + " deck=" + deckBefore);
                }

                player.MulliganState = PlayerMulliganState.Mulligan_Draw;
                List<ServerCard> drawn = DrawCardsWithFallback(
                    state,
                    triggerState,
                    player,
                    3,
                    CcgRpc.HistoryType.ActionDrawForMulligan,
                    "mulligan-open");

                int deckAfter = simulation.CardCatalog.FindCardCount(
                    GameZoneName.Deck, player.NetId);
                int handAfter = simulation.CardCatalog.FindCardCount(
                    GameZoneName.Hand, player.NetId);
                if (Object.ReferenceEquals(drawn, null) ||
                    drawn.Count != 3 ||
                    deckBefore - deckAfter != 3 ||
                    handAfter - handBefore < 3)
                {
                    throw new InvalidOperationException(
                        "ActionDrawForMulligan invariant failed for player="
                        + player.NetId.Id
                        + " drawn="
                        + (Object.ReferenceEquals(drawn, null) ? -1 : drawn.Count)
                        + " deck=" + deckBefore + "->" + deckAfter
                        + " hand=" + handBefore + "->" + handAfter);
                }

                Plugin.Log("mulligan draw materialized: player="
                    + player.NetId.Id + " cards=" + drawn.Count
                    + " handAdded=" + (handAfter - handBefore)
                    + " hand=" + handAfter + " deck=" + deckAfter);
            }

            simulation.HistoryLog.EndEvent();
            simulation.HistoryLog.SendUnsentMessages(
                state.Clients,
                GetObserverClients(simulation),
                HistoryLog.FullGameStateSendRule.Exclude);

            StartMulligan(state);
        }

        /*
         * Retail PlayerActions.DrawCards.StaticExecute is partially stripped:
         * it often returns an empty list even when Deck holds cards.  Prefer
         * retail when it works; otherwise MoveCard Deck -> Hand and emit the
         * requested history type so the client can animate the draw.
         */
        private static List<ServerCard> DrawCardsWithFallback(
            HostState state,
            ServerEffectUtilities.TriggerGameState triggerState,
            PlayerState player,
            int count,
            CcgRpc.HistoryType historyType,
            string reason)
        {
            return OfflineDrawUtilities.Draw(triggerState, player, count, historyType, reason);
        }

        private static void AddMatchIntroLanes(
            HostState state,
            HistoryItem intro,
            PlayerState player)
        {
            List<NetDynamicGameZone> zones =
                state.Simulation.CardCatalog.GetNetDynamicGameZones(player);
            int i;
            for (i = 0; i < zones.Count; ++i)
            {
                NetDynamicGameZone zone = zones[i];
                if (zone.ZoneName != (int)GameZoneName.CreatureBoard)
                    continue;

                int lane;
                for (lane = 0; lane < zone.Lanes.Count; ++lane)
                    intro.MatchIntroLanes.Add(zone.Lanes[lane]);
                return;
            }
        }

        private static void StartMulligan(HostState state)
        {
            Simulation simulation = state.Simulation;
            GameState gameState = GetGameState(simulation);
            gameState.GamePhase = GamePhase.Mulligan;

            simulation.HistoryLog.StartEvent(
                CcgRpc.HistoryType.EventMulliganStart,
                PlayerUniqueId.GetInvalid(),
                CardNetUid.GetInvalid(),
                CardNetUid.GetInvalid(),
                -1);

            int i;
            for (i = 0; i < simulation.Players.Count; ++i)
            {
                PlayerState player = simulation.Players[i];
                HistoryItem commandHistory = new HistoryItem();
                commandHistory.Type = CcgRpc.HistoryType.CardCommandList;
                commandHistory.ActingPlayerInstanceId = player.NetId.Id;
                commandHistory.CardCommandItem =
                    simulation.CardCatalog.GenerateMulliganCardCommandItem(player);
                simulation.HistoryLog.AddCardCommandItem(commandHistory);
                player.LastSentCommandItem = commandHistory.CardCommandItem;
            }

            /* The current local AI keeps its opening three.  It is still marked
             * complete through the same state machine so the human response is
             * the only remaining gate. */
            state.MulliganCommitted[1] = true;
            simulation.Players[1].MulliganState =
                PlayerMulliganState.Mulligan_Complete;

            simulation.HistoryLog.EndEvent();
            simulation.HistoryLog.SendUnsentMessages(
                state.Clients,
                GetObserverClients(simulation),
                HistoryLog.FullGameStateSendRule.Exclude);
        }

        private static void ProcessMulliganDecision(
            HostState state,
            CcgRpc.Decision decision)
        {
            if (decision.Type == CcgRpc.DecisionType.Mulligan)
            {
                /* Selection changes are client-side previews.  The retail client
                 * intentionally sends CommandUid=0 for them; no turn command
                 * validation or state transition belongs here. */
                return;
            }

            if (decision.Type != CcgRpc.DecisionType.MulliganCommit ||
                state.MulliganCommitted[0])
                return;

            PlayerState player = state.Simulation.Players[0];
            List<ServerCard> selected = new List<ServerCard>();
            List<HistoryCard> responseCards = new List<HistoryCard>();
            int i;
            for (i = 0; i < decision.DiscardCardNetUids.Count; ++i)
            {
                ServerCard card = state.Simulation.CardCatalog.FindDynamicCard(
                    new CardNetUid(decision.DiscardCardNetUids[i]));
                if (Object.ReferenceEquals(card, null) ||
                    card.OwnerNetId.Id != player.NetId.Id ||
                    card.CurrentGameZoneName != GameZoneName.Hand ||
                    selected.Contains(card))
                    continue;

                selected.Add(card);
                responseCards.Add(
                    card.HistoryCreate_FullStateCard(state.Simulation.CardCatalog));
            }

            state.Simulation.HistoryLog.StartEvent(
                CcgRpc.HistoryType.EventMulliganResponse,
                player.NetId,
                CardNetUid.GetInvalid(),
                CardNetUid.GetInvalid(),
                -1,
                false,
                EffectTriggerType.Invalid,
                SanitizeRule.Default,
                responseCards);

            ServerEffectUtilities.TriggerGameState triggerState = TriggerState(state);
            for (i = 0; i < selected.Count; ++i)
            {
                ServerCard card = selected[i];
                int visibleUid = card.NetUid.Id;
                Plugin.Log("mulligan discard begin: card=" + card.CardId
                    + " instance=" + card.InstanceId.Id
                    + " visibleUid=" + visibleUid
                    + " zone=" + card.CurrentGameZoneName);
                GameZoneOperationResult moved = ServerCardCatalog.MoveCard(
                    triggerState,
                    new GameZoneId(player.NetId, GameZoneType.Hand),
                    new GameZoneId(player.NetId, GameZoneType.Deck),
                    card.InstanceId,
                    0,
                    CardInsertionType.Bottom,
                    ServerCardCatalog.MoveOperationType.Untyped);
                if (moved != GameZoneOperationResult.Succeeded)
                    continue;

                Plugin.Log("mulligan discard moved: card=" + card.CardId
                    + " instance=" + card.InstanceId.Id
                    + " oldUid=" + visibleUid
                    + " hiddenUid=" + card.NetUid.Id
                    + " previousType=" + card.PreviousGameZoneType
                    + " zone=" + card.CurrentGameZoneName);

                HistoryCard historyCard =
                    card.HistoryCreate_FullStateCard(state.Simulation.CardCatalog);
                /* Hidden Deck receives a new opaque NetUid internally.  The
                 * animation must still refer to the card that was visible in
                 * Hand, hence the historical UID is the pre-transfer UID. */
                historyCard.CardNetUid = visibleUid;
                HistoryItem discard = new HistoryItem();
                discard.Type = CcgRpc.HistoryType.ActionDiscardForMulligan;
                discard.AssetHash = -1;
                discard.AffectedCardsArray.Add(historyCard);
                state.Simulation.HistoryLog.PlayerDiffLogger.
                    DiffDeckCountsAndAddToHistoryItem(
                        state.Simulation.Players,
                        state.Simulation.CardCatalog,
                        discard);
                state.Simulation.HistoryLog.AddInstant(discard);
            }

            player.MulliganState = PlayerMulliganState.Mulligan_Redraw;
            if (selected.Count > 0)
            {
                List<ServerCard> redrawn = DrawCardsWithFallback(
                    state,
                    triggerState,
                    player,
                    selected.Count,
                    CcgRpc.HistoryType.ActionRedrawForMulligan,
                    "mulligan-redraw");
                Plugin.Log(
                    "mulligan redraw: discarded=" + selected.Count
                    + " drawn=" + redrawn.Count
                    + " hand="
                    + state.Simulation.CardCatalog.FindCardCount(
                        GameZoneName.Hand, player.NetId));
            }
            player.MulliganState = PlayerMulliganState.Mulligan_Complete;
            state.MulliganCommitted[0] = true;

            state.Simulation.HistoryLog.EndEvent();
            state.Simulation.HistoryLog.SendUnsentMessages(
                state.Clients,
                GetObserverClients(state.Simulation),
                HistoryLog.FullGameStateSendRule.Exclude);

            if (state.MulliganCommitted[0] && state.MulliganCommitted[1])
            {
                GetGameState(state.Simulation).GamePhase = GamePhase.MainLoop;
                BeginTurn(state, state.StartingPlayerIndex, true);
                AnnounceTurnAndCommands(state);
            }
        }

        private static void EmitTurnStartEvent(HostState state, int playerIndex)
        {
            PlayerState player = state.Simulation.Players[playerIndex];
            state.Simulation.HistoryLog.StartEvent(
                CcgRpc.HistoryType.EventTurnStart,
                player.NetId,
                CardNetUid.GetInvalid(),
                CardNetUid.GetInvalid(),
                -1);
            state.Simulation.HistoryLog.EndEvent();
            state.Simulation.HistoryLog.SendUnsentMessages(
                state.Clients,
                GetObserverClients(state.Simulation),
                HistoryLog.FullGameStateSendRule.Exclude);
        }

        private static void PumpHumanNetwork(HostState state)
        {
            if (!state.GameInitialized)
                return;

            PlayerServiceClient humanClient = state.Clients[0];
            if (Object.ReferenceEquals(humanClient, null) ||
                humanClient.GetConnectionStatus() !=
                    PlayerServiceClient.ConnectionStatus.Connected)
                return;

            humanClient.ExtractMessagesFromNetworkStream();

            CcgRpc.Interrupt interrupt;
            while (!Object.ReferenceEquals(
                (interrupt = humanClient.PopNextInterrupt()), null))
            {
                PlayerState human = state.Simulation.Players[0];
                Plugin.Log("interrupt received: type=" + interrupt.Type
                    + " commandUid=" + interrupt.CommandUid);
                state.Simulation.OnInterrupt(interrupt, human.NetId);

                if (interrupt.Type == CcgRpc.InterruptType.ControlStarted)
                    Plugin.Log("client acknowledged CardCommandList uid=" + interrupt.CommandUid);

                if (IsGameFinished(state))
                    return;
            }

            CcgRpc.Decision decision;
            while (!Object.ReferenceEquals(
                (decision = humanClient.PopNextDecision()), null))
            {
                ProcessHumanDecision(state, decision);
                if (state.GameplayFaulted || IsGameFinished(state))
                    break;
            }
        }

        private static void ProcessHumanDecision(
            HostState state,
            CcgRpc.Decision decision)
        {
            LocalDecisionBridge.Dispatch(
                decision,
                delegate { return DescribeBridgeState(state); },
                delegate(CcgRpc.Decision item)
                {
                    ProcessHumanDecisionCore(state, item);
                });
        }

        private static void ProcessHumanDecisionCore(
            HostState state,
            CcgRpc.Decision decision)
        {
            if (GetGameState(state.Simulation).GamePhase == GamePhase.Mulligan &&
                (decision.Type == CcgRpc.DecisionType.Mulligan ||
                 decision.Type == CcgRpc.DecisionType.MulliganCommit))
            {
                ProcessMulliganDecision(state, decision);
                return;
            }

            if (state.PendingCustomAction != PendingCustomActionKind.None &&
                state.PendingCustomPlayerIndex == 0)
            {
                ProcessHumanPendingCustomAction(state, decision);
                return;
            }

            if (state.PendingProphecyPlayerIndex == 0)
            {
                if (decision.CommandUid != state.ListUid)
                {
                    Plugin.Warn("stale prophecy decision uid=" + decision.CommandUid
                        + " expected=" + state.ListUid);
                    SendCommandList(state, 0);
                    return;
                }

                state.OutstandingProphecyListUid = 0;

                if (decision.Type == CcgRpc.DecisionType.ToHand)
                {
                    ResolveHumanProphecyToHand(state, decision);
                    return;
                }

                if (decision.Type == CcgRpc.DecisionType.Play)
                {
                    if (TryPlayCard(state, 0, decision))
                    {
                        if (state.PendingCustomAction !=
                            PendingCustomActionKind.None)
                        {
                            SendPendingCustomActionCommandList(state);
                            return;
                        }
                        state.PendingProphecyPlayerIndex = -1;
                        CheckForRuneBreaks(state, state.Simulation.Players[0]);
                        if (state.PendingProphecyPlayerIndex < 0)
                            CheckForRuneBreaks(
                                state, state.Simulation.Players[1]);
                        ResumeAfterProphecyDecision(state);
                    }
                    else
                    {
                        SendCommandList(state, 0);
                    }
                    return;
                }

                Plugin.Warn("unsupported prophecy decision: " + decision.Type);
                SendCommandList(state, 0);
                return;
            }

            ServerEffectUtilities.TriggerContext triggerDecisionContext;
            int triggerDecisionPlayerIndex;
            if (TryPeekActiveDecisionTrigger(
                state, out triggerDecisionContext, out triggerDecisionPlayerIndex) &&
                triggerDecisionPlayerIndex == 0)
            {
                if (decision.CommandUid != state.ListUid)
                {
                    Plugin.Warn("stale effect decision uid=" + decision.CommandUid
                        + " expected=" + state.ListUid);
                    SendCommandList(state, 0);
                    return;
                }

                ProcessHumanTriggeredEffectDecision(
                    state, triggerDecisionContext, decision);
                return;
            }

            if (state.ActivePlayerIndex != 0)
                return;

            if (state.PausedAttackVisualUid != 0)
            {
                Plugin.Warn("human turn input deferred until prophecy attack completes");
                return;
            }

            if (decision.CommandUid != state.ListUid)
            {
                Plugin.Warn("stale local decision uid=" + decision.CommandUid
                    + " expected=" + state.ListUid);
                SendCommandList(state, 0);
                return;
            }

            if (decision.Type == CcgRpc.DecisionType.NoCommand)
            {
                Plugin.Log("human EndTurn command received");
                EndTurn(state, 0);
                return;
            }

            if (decision.Type == CcgRpc.DecisionType.UseRing)
            {
                PlayerState player = state.Simulation.Players[0];
                state.Simulation.HistoryLog.StartEvent(
                    CcgRpc.HistoryType.EventUseRing,
                    player.NetId,
                    CardNetUid.GetInvalid(),
                    CardNetUid.GetInvalid(),
                    -1);
                bool used = PlayerActions.UseRing.StaticExecute(
                    state.Simulation.HistoryLog,
                    TriggerState(state),
                    player);
                state.Simulation.HistoryLog.EndEvent();
                state.Simulation.HistoryLog.SendUnsentMessages(
                    state.Clients,
                    GetObserverClients(state.Simulation),
                    HistoryLog.FullGameStateSendRule.Exclude);
                Plugin.Log("human ring use=" + used
                    + " charges="
                    + player.QueryAttribute(AttributeType.RingUsesLeft).Value);
                SendCommandList(state, 0);
                return;
            }

            if (decision.Type == CcgRpc.DecisionType.Play)
            {
                TryPlayCard(state, 0, decision);
                if (state.PendingCustomAction != PendingCustomActionKind.None)
                {
                    SendPendingCustomActionCommandList(state);
                    return;
                }
                SendCommandList(state, 0);
                return;
            }

            if (decision.Type == CcgRpc.DecisionType.Activate)
            {
                TryActivateAbility(state, 0, decision);
                if (state.PendingCustomAction != PendingCustomActionKind.None)
                {
                    SendPendingCustomActionCommandList(state);
                    return;
                }
                if (!IsGameFinished(state))
                    SendCommandList(state, 0);
                return;
            }

            if (decision.Type == CcgRpc.DecisionType.Attack)
            {
                TryAttack(state, 0, decision);
                if (!IsGameFinished(state) &&
                    state.PausedAttackVisualUid == 0)
                    SendCommandList(state, 0);
                return;
            }

            Plugin.Warn("decision not reconstructed yet: " + decision.Type);
            SendCommandList(state, 0);
        }

        private static string DescribeBridgeState(HostState state)
        {
            if (Object.ReferenceEquals(state, null) ||
                Object.ReferenceEquals(state.Simulation, null))
                return "host=unavailable";

            return "phase=" + GetGameState(state.Simulation).GamePhase
                + " active=" + state.ActivePlayerIndex
                + " listUid=" + state.ListUid
                + " p1=" + DescribeBridgePlayer(state, 0)
                + " p2=" + DescribeBridgePlayer(state, 1);
        }

        private static string DescribeBridgePlayer(HostState state, int index)
        {
            PlayerState player = state.Simulation.Players[index];
            ServerCard avatar = state.Simulation.CardCatalog.FindCardInDynamicZone(
                delegate(ServerCard card) { return true; },
                player.NetId,
                GameZoneName.AvatarBoard);
            string health = Object.ReferenceEquals(avatar, null)
                ? "?" : avatar.CurrentHealth.ToString();

            return "[hp=" + health
                + ",mana=" + player.QueryAttribute(AttributeType.Mana).Value
                + ",hand=" + state.Simulation.CardCatalog.FindCardCount(
                    GameZoneName.Hand, player.NetId)
                + ",deck=" + state.Simulation.CardCatalog.FindCardCount(
                    GameZoneName.Deck, player.NetId)
                + ",creatures=" + state.Simulation.CardCatalog.FindCardCount(
                    GameZoneName.CreatureBoard, player.NetId)
                + ",supports=" + state.Simulation.CardCatalog.FindCardCount(
                    GameZoneName.SupportBoard, player.NetId)
                + "]";
        }

        private static int GetPendingCustomChoiceCount(HostState state)
        {
            if (state.PendingCustomAction == PendingCustomActionKind.None ||
                Object.ReferenceEquals(
                    state.PendingCustomContext.TriggeredEffect, null))
                return 0;

            return Math.Max(
                state.CustomUiCards.Count,
                state.PendingCustomContext.TriggeredEffect.
                    CustomUiEffectGroups.Count);
        }

        private static bool ValidatePendingCustomDecision(
            HostState state,
            CcgRpc.Decision decision)
        {
            if (state.PendingCustomAction == PendingCustomActionKind.None)
                return false;
            if (decision.CommandUid != state.ListUid)
                return false;

            TriggeredEffect effect =
                state.PendingCustomContext.TriggeredEffect;
            if (Object.ReferenceEquals(effect, null))
                return false;

            if (decision.Type == CcgRpc.DecisionType.NoCommand)
                return effect.OptionalCustomUiType;
            if (decision.Type != CcgRpc.DecisionType.CustomChoice)
                return false;

            int choiceCount = GetPendingCustomChoiceCount(state);
            return decision.CustomChoice >= 0 &&
                   decision.CustomChoice < choiceCount;
        }

        private static void ClearPendingCustomAction(HostState state)
        {
            state.PendingCustomAction = PendingCustomActionKind.None;
            state.PendingCustomPlayerIndex = -1;
            state.PendingCustomCard = null;
            state.PendingCustomContext =
                new ServerEffectUtilities.TriggerContext();
            state.PendingCustomFirstDecision = null;
            state.PendingCustomBoardCard = false;
            state.PendingCustomFromProphecy = false;
            state.CustomUiCards.Clear();
        }

        private static void AddEndCustomUiHistory(
            HostState state,
            ServerCard source)
        {
            HistoryItem endCustomUi = new HistoryItem();
            endCustomUi.Type = CcgRpc.HistoryType.ActionEndCustomUi;
            endCustomUi.ActorCardNetUid = Object.ReferenceEquals(source, null)
                ? 0
                : source.NetUid.Id;
            state.Simulation.HistoryLog.AddInstant(endCustomUi);
        }

        private static void ProcessHumanPendingCustomAction(
            HostState state,
            CcgRpc.Decision decision)
        {
            if (!ValidatePendingCustomDecision(state, decision))
            {
                Plugin.Warn("invalid pending custom decision type="
                    + decision.Type + " choice=" + decision.CustomChoice
                    + " uid=" + decision.CommandUid
                    + " expected=" + state.ListUid);
                SendPendingCustomActionCommandList(state);
                return;
            }

            CompletePendingCustomAction(state, decision);
        }

        private static bool CompletePendingCustomAction(
            HostState state,
            CcgRpc.Decision decision)
        {
            PendingCustomActionKind kind = state.PendingCustomAction;
            if (kind == PendingCustomActionKind.None)
                return false;

            int playerIndex = state.PendingCustomPlayerIndex;
            ServerCard card = state.PendingCustomCard;
            ServerEffectUtilities.TriggerContext context =
                state.PendingCustomContext;
            CcgRpc.Decision firstDecision = state.PendingCustomFirstDecision;
            bool boardCard = state.PendingCustomBoardCard;
            bool fromProphecy = state.PendingCustomFromProphecy;
            ServerEffectUtilities.TriggerGameState triggerState = TriggerState(state);

            if (Object.ReferenceEquals(card, null) ||
                Object.ReferenceEquals(firstDecision, null) ||
                Object.ReferenceEquals(context.TriggeredEffect, null))
            {
                Plugin.Warn("pending custom action lost its source context");
                return false;
            }

            if (kind == PendingCustomActionKind.PlayCard)
            {
                ServerEffectUtilities.ExecuteOnPlayCardEffects(
                    triggerState, context, firstDecision, decision);
                AddEndCustomUiHistory(state, card);
                ServerEffectUtilities.EndTriggerEventLog(
                    triggerState, ref context);
                FinalizePlayedCardAfterEffects(
                    state, triggerState, card, boardCard, true);
                state.Simulation.HistoryLog.EndEvent();
            }
            else if (kind == PendingCustomActionKind.ActivatedAbility)
            {
                ServerEffectUtilities.ExecuteOnActivateCardEffects(
                    triggerState, context, firstDecision, decision);
                AddEndCustomUiHistory(state, card);
                card.IncrementAbilityUseCount();
                card.IncrementActivationCount();
                HistoryLoggingUtilities.LogFullCardRefresh(
                    state.Simulation.HistoryLog,
                    state.Simulation.CardCatalog,
                    card,
                    true);
                ServerEffectUtilities.TriggerEffectEvent(
                    triggerState,
                    new ServerEffectUtilities.TriggerEvent(
                        EffectTriggerType.AfterActivatedAbility,
                        card,
                        state.Simulation.Players));
                state.Simulation.HistoryLog.EndEvent();
            }

            ClearPendingCustomAction(state);

            DrainAutomaticTriggerQueues(state, false);
            ResolveDyingNonAvatarCards(state);
            if (fromProphecy)
            {
                /* Continue the rune chain that was paused to let this prophecy
                 * resolve before considering rune damage on the other player. */
                state.PendingProphecyPlayerIndex = -1;
                CheckForRuneBreaks(
                    state, state.Simulation.Players[playerIndex]);
                if (state.PendingProphecyPlayerIndex < 0)
                    CheckForRuneBreaks(
                        state,
                        state.Simulation.Players[playerIndex == 0 ? 1 : 0]);
            }
            else
            {
                CheckForRuneBreaks(state);
            }
            DrainAutomaticTriggerQueues(state, true);
            CheckForAvatarDeath(state);
            state.Simulation.HistoryLog.SendUnsentMessages(
                state.Clients,
                GetObserverClients(state.Simulation),
                HistoryLog.FullGameStateSendRule.Exclude);

            if (IsGameFinished(state))
                return true;

            if (fromProphecy)
            {
                ResumeAfterProphecyDecision(state);
                return true;
            }

            if (playerIndex == 0)
                SendCommandList(state, 0);
            return true;
        }

        private static void SendPendingCustomActionCommandList(HostState state)
        {
            if (state.PendingCustomAction == PendingCustomActionKind.None ||
                state.PendingCustomPlayerIndex != 0)
                return;

            TriggeredEffect effect =
                state.PendingCustomContext.TriggeredEffect;
            if (Object.ReferenceEquals(effect, null))
                return;

            PlayerState player = state.Simulation.Players[0];
            CardCommandList list = new CardCommandList();
            state.ListUid += 1;
            list.ListUid = state.ListUid;
            list.MustSelectCommand = !effect.OptionalCustomUiType;
            list.IsMulliganPhase = false;

            CardCommandState command = new CardCommandState();
            command.CardNetUid = Object.ReferenceEquals(
                state.PendingCustomCard, null)
                ? 0
                : state.PendingCustomCard.NetUid.Id;
            command.ValidCardCommandMask = (int)CardCommand.EffectGroupChoice;
            if (effect.OptionalCustomUiType)
                command.ValidCardCommandMask |= (int)CardCommand.DeclineToPlay;
            command.CustomCardUiEnum = (int)effect.CustomUiType;
            command.NumActionChoices = GetPendingCustomChoiceCount(state);
            if (!String.IsNullOrEmpty(effect.CustomUiPrompt.RawKey))
                command.CustomPromptLocKey = effect.CustomUiPrompt.RawKey;

            int i;
            for (i = 0; i < state.CustomUiCards.Count; ++i)
            {
                CustomDisplayCard display = state.CustomUiCards[i];
                if (!Object.ReferenceEquals(display, null) &&
                    !Object.ReferenceEquals(display.HistoryCard, null))
                    command.DisplayCards.Add(display.HistoryCard);
            }
            list.ValidCommands.Add(command);

            CardCommandItem item = new CardCommandItem();
            item.CommandList = list;
            HistoryItem history = new HistoryItem();
            history.Type = CcgRpc.HistoryType.CardCommandList;
            history.ActingPlayerInstanceId = player.NetId.Id;
            history.CardCommandItem = item;
            state.Simulation.HistoryLog.AddCardCommandItem(history);
            player.LastSentCommandItem = item;
            state.Simulation.HistoryLog.SendUnsentMessages(
                state.Clients,
                GetObserverClients(state.Simulation),
                HistoryLog.FullGameStateSendRule.Exclude);

            Plugin.Log("custom UI CardCommandList sent: uid=" + list.ListUid
                + " card=" + command.CardNetUid
                + " ui=" + effect.CustomUiType
                + " choices=" + command.NumActionChoices);
        }

        private static bool ActivatedAbilityPrerequisitesMet(
            HostState state,
            PlayerState player,
            ServerCard card,
            ActivatedAbility ability)
        {
            if (Object.ReferenceEquals(ability, null) ||
                !ability.IsValidAbility() ||
                !card.HasPotentialActivatedAbility() ||
                card.HasConsumedAllAbilityUsesForThisTurn())
                return false;

            PlayerState opponent = state.Simulation.Players[0].NetId.Id ==
                player.NetId.Id
                ? state.Simulation.Players[1]
                : state.Simulation.Players[0];
            CardConditionalArguments cardArguments = new CardConditionalArguments(
                state.Simulation.CardCatalog,
                card,
                card,
                null,
                null,
                null,
                LaneIdentifier.GetInvalid());
            PlayerConditionalArguments playerArguments =
                new PlayerConditionalArguments(
                    state.Simulation.CardCatalog,
                    player,
                    player,
                    null,
                    null,
                    opponent);
            TriggerConditionalArguments triggerArguments =
                new TriggerConditionalArguments(0, AttributeType.None, -1, 0);

            int i;
            for (i = 0; i < ability.ActivationCardRequirements.Count; ++i)
            {
                ICardRequirement requirement = ability.ActivationCardRequirements[i];
                if (Object.ReferenceEquals(requirement, null) ||
                    !requirement.IsRequirementMet(
                        cardArguments, playerArguments, triggerArguments))
                    return false;
            }

            PlayerConditionalLog conditionalLog = new PlayerConditionalLog(
                state.Simulation.HistoryLog, card);
            for (i = 0; i < ability.ActivationPlayerRequirements.Count; ++i)
            {
                IPlayerRequirement requirement =
                    ability.ActivationPlayerRequirements[i];
                if (Object.ReferenceEquals(requirement, null) ||
                    !requirement.IsRequirementMet(
                        conditionalLog,
                        playerArguments,
                        cardArguments,
                        triggerArguments))
                    return false;
            }
            return true;
        }

        private static bool TryBuildActivatedAbilityContext(
            HostState state,
            PlayerState player,
            ServerCard card,
            out ServerEffectUtilities.TriggerContext context)
        {
            context = new ServerEffectUtilities.TriggerContext();
            ActivatedAbility ability = card.ActivatedAbility;
            if (!ActivatedAbilityPrerequisitesMet(state, player, card, ability) ||
                ability.AbilityGroups.Count == 0)
                return false;

            AbilityGroup group = ability.AbilityGroups[0];
            if (Object.ReferenceEquals(group, null))
                return false;

            /* The retail catalog currently has one multi-group ability:
             * Reconstruction Engine. Its two direct groups are consecutive
             * parts of one activation (debuff, then create token). Keep the
             * existing pending-custom-UI path for single-group abilities; a
             * multi-group custom choice needs its own continuation state. */
            if (ability.AbilityGroups.Count > 1)
            {
                int groupIndex;
                for (groupIndex = 0;
                     groupIndex < ability.AbilityGroups.Count;
                     ++groupIndex)
                {
                    AbilityGroup candidate = ability.AbilityGroups[groupIndex];
                    if (Object.ReferenceEquals(candidate, null) ||
                        candidate.CustomUiType != CustomCardUiType.None ||
                        (!Object.ReferenceEquals(candidate.CustomUiEffectGroups, null) &&
                         candidate.CustomUiEffectGroups.Count != 0))
                        return false;
                }
            }

            TriggeredEffect effect = new TriggeredEffect();
            effect.EffectGroup = group.EffectGroup;
            effect.TriggerType = EffectTriggerType.AbilityEffect;
            effect.ClientTargetingType = ability.TargetingType;
            effect.TargetSelectors = ability.TargetSelectors;
            effect.CustomUiType = group.CustomUiType;
            effect.OptionalCustomUiType = group.OptionalCustomUiType;
            effect.CustomUiEffectGroups = group.CustomUiEffectGroups;
            effect.PostCustomUiEffectGroup = group.PostCustomUiEffectGroup;

            context.TriggeredEffect = effect;
            context.EffectOwner = card;
            context.TriggerEvent = new ServerEffectUtilities.TriggerEvent(
                EffectTriggerType.AbilityEffect,
                card,
                state.Simulation.Players);
            context.QueueAddCounter = -1;
            context.EffectIndex = 0;
            context.ActingLaneIndex = card.CurrentLaneIndex;
            context.ActingLaneIndexType = 0;
            context.DidMainEffectsRun = false;
            return true;
        }

        private static bool FillActivatedAbilityCommand(
            HostState state,
            PlayerState player,
            ServerCard card,
            CardCommandState command)
        {
            ServerEffectUtilities.TriggerContext context;
            if (!TryBuildActivatedAbilityContext(
                state, player, card, out context))
                return false;

            PlayTargetingType targetingType =
                context.TriggeredEffect.ClientTargetingType;
            List<ServerCard> cardTargets = new List<ServerCard>();
            List<CcgRpc.BoardLaneTarget> laneTargets =
                new List<CcgRpc.BoardLaneTarget>();
            if (targetingType != PlayTargetingType.NoTarget)
            {
                cardTargets = GatherTriggeredEffectCardTargets(state, context);
                laneTargets = GatherTriggeredEffectLaneTargets(state, context);
                if (targetingType == PlayTargetingType.RequiredTarget &&
                    cardTargets.Count == 0 && laneTargets.Count == 0)
                    return false;

                /* Optional targeted activated abilities need a distinct cancel
                 * path from EndTurn.  Do not advertise them until that protocol
                 * branch is reconstructed rather than silently removing the
                 * no-target choice. */
                if (targetingType == PlayTargetingType.OptionalTarget &&
                    (cardTargets.Count > 0 || laneTargets.Count > 0))
                    return false;
            }

            command.ValidCardCommandMask |= (int)CardCommand.ActiveAbility;
            int i;
            for (i = 0; i < cardTargets.Count; ++i)
            {
                if (!command.ActiveAbilityTargets.Contains(
                    cardTargets[i].NetUid.Id))
                    command.ActiveAbilityTargets.Add(cardTargets[i].NetUid.Id);
            }
            for (i = 0; i < laneTargets.Count; ++i)
            {
                if (!command.LaneTargets.Contains(laneTargets[i]))
                    command.LaneTargets.Add(laneTargets[i]);
            }
            return true;
        }

        private static bool CanOfferExalt(
            PlayerState player, ServerCard card)
        {
            return !Object.ReferenceEquals(card, null) &&
                card.CurrentGameZoneType == GameZoneType.Board &&
                !card.Silenced &&
                card.QueryAttribute(AttributeType.ExaltCost).Value > 0 &&
                card.QueryAttribute(AttributeType.Exalted).Value <= 0 &&
                player.QueryAttribute(AttributeType.Mana).Value >=
                    card.QueryAttribute(AttributeType.ExaltCost).Value;
        }

        private static bool FillExaltCommand(
            PlayerState player, ServerCard card, CardCommandState command)
        {
            if (!CanOfferExalt(player, card))
                return false;
            command.ValidCardCommandMask |= (int)CardCommand.ActiveAbility;
            return true;
        }

        private static bool ValidateActivatedAbilityDecision(
            HostState state,
            PlayerState player,
            ServerCard card,
            ServerEffectUtilities.TriggerContext context,
            CcgRpc.Decision decision)
        {
            if (decision.Type != CcgRpc.DecisionType.Activate ||
                decision.ActorCardNetUid != card.NetUid.Id)
                return false;

            PlayTargetingType targetingType =
                context.TriggeredEffect.ClientTargetingType;
            if (targetingType == PlayTargetingType.NoTarget)
            {
                return !new CardNetUid(decision.TargetCardNetUid).IsValid() &&
                       decision.LaneTarget == CcgRpc.BoardLaneTarget.None;
            }
            return ValidateTriggeredEffectDecision(state, context, decision);
        }

        private static void EmitPlayerTargetedCardEvent(
            HostState state,
            ServerCard source,
            CardNetUid targetUid)
        {
            if (targetUid.IsValid() == false)
                return;
            ServerCard target = state.Simulation.CardCatalog.FindDynamicCard(targetUid);
            if (target == null)
                return;
            ServerEffectUtilities.TriggerEffectEvent(
                TriggerState(state),
                new ServerEffectUtilities.TriggerEvent(
                    EffectTriggerType.WhenPlayerTargetsCard,
                    source,
                    target,
                    state.Simulation.Players));
        }

        private static bool TryActivateAbility(
            HostState state,
            int playerIndex,
            CcgRpc.Decision decision)
        {
            PlayerState player = state.Simulation.Players[playerIndex];
            ServerCard card = state.Simulation.CardCatalog.FindDynamicCard(
                new CardNetUid(decision.ActorCardNetUid));
            if (Object.ReferenceEquals(card, null) ||
                card.OwnerNetId.Id != player.NetId.Id ||
                card.CurrentGameZoneType != GameZoneType.Board)
                return false;

            ServerEffectUtilities.TriggerContext context;
            if (!TryBuildActivatedAbilityContext(
                state, player, card, out context))
            {
                if (!card.HasPotentialActivatedAbility() &&
                    CanOfferExalt(player, card) &&
                    decision.Type == CcgRpc.DecisionType.Activate &&
                    decision.ActorCardNetUid == card.NetUid.Id &&
                    !new CardNetUid(decision.TargetCardNetUid).IsValid() &&
                    decision.LaneTarget == CcgRpc.BoardLaneTarget.None)
                {
                    ServerEffectUtilities.TriggerEffectEvent(
                        TriggerState(state),
                        new ServerEffectUtilities.TriggerEvent(
                            EffectTriggerType.PrepareExalt,
                            card,
                            state.Simulation.Players));
                    Plugin.Log("exalt prepared: player=" + player.NetId.Id
                        + " card=" + card.CardId + " cost="
                        + card.QueryAttribute(AttributeType.ExaltCost).Value);
                    return true;
                }
                Plugin.Warn("activated ability rejected: card=" + card.CardId
                    + " actor=" + decision.ActorCardNetUid
                    + " target=" + decision.TargetCardNetUid
                    + " laneTarget=" + decision.LaneTarget);
                return false;
            }

            if (!ValidateActivatedAbilityDecision(
                    state, player, card, context, decision))
            {
                Plugin.Warn("activated ability rejected: card=" + card.CardId
                    + " actor=" + decision.ActorCardNetUid
                    + " target=" + decision.TargetCardNetUid
                    + " laneTarget=" + decision.LaneTarget);
                return false;
            }

            EmitPlayerTargetedCardEvent(
                state,
                card,
                new CardNetUid(decision.TargetCardNetUid));

            int assetHash = card.FindOverrideOrDefaultAssetHash(
                CardFxAction.ActivateAbility);
            state.Simulation.HistoryLog.StartEvent(
                CcgRpc.HistoryType.EventActivatedAbility,
                player.NetId,
                card.NetUid,
                CardNetUid.GetInvalid(),
                assetHash);

            ServerEffectUtilities.TriggerGameState triggerState = TriggerState(state);
            ServerEffectUtilities.TriggerDirectEffectEvent(
                triggerState,
                new ServerEffectUtilities.TriggerEvent(
                    EffectTriggerType.BeforeActivatedAbility,
                    card,
                    state.Simulation.Players));

            int abilityGroupIndex;
            for (abilityGroupIndex = 0;
                 abilityGroupIndex < card.ActivatedAbility.AbilityGroups.Count;
                 ++abilityGroupIndex)
            {
                AbilityGroup abilityGroup =
                    card.ActivatedAbility.AbilityGroups[abilityGroupIndex];
                if (abilityGroupIndex != 0)
                {
                    context.TriggeredEffect.EffectGroup =
                        abilityGroup.EffectGroup;
                    context.TriggeredEffect.CustomUiType =
                        abilityGroup.CustomUiType;
                    context.TriggeredEffect.OptionalCustomUiType =
                        abilityGroup.OptionalCustomUiType;
                    context.TriggeredEffect.CustomUiEffectGroups =
                        abilityGroup.CustomUiEffectGroups;
                    context.TriggeredEffect.PostCustomUiEffectGroup =
                        abilityGroup.PostCustomUiEffectGroup;
                }
                ServerEffectUtilities.ExecuteOnActivateCardEffects(
                    triggerState,
                    context,
                    decision,
                    null);
            }

            if (context.TriggeredEffect.CustomUiType != CustomCardUiType.None)
            {
                context.DidMainEffectsRun = true;
                if (ServerEffectUtilities.IsPlayerDecisionRequired(
                    context, state.CustomUiCards))
                {
                    state.PendingCustomAction =
                        PendingCustomActionKind.ActivatedAbility;
                    state.PendingCustomPlayerIndex = playerIndex;
                    state.PendingCustomCard = card;
                    state.PendingCustomContext = context;
                    state.PendingCustomFirstDecision = decision;
                    state.PendingCustomBoardCard = true;
                    state.PendingCustomFromProphecy = false;
                    state.Simulation.HistoryLog.SendUnsentMessages(
                        state.Clients,
                        GetObserverClients(state.Simulation),
                        HistoryLog.FullGameStateSendRule.Exclude);
                    Plugin.Log((playerIndex == 0 ? "human" : "AI")
                        + " activated ability awaiting custom UI card="
                        + card.CardId + " ui="
                        + context.TriggeredEffect.CustomUiType);
                    return true;
                }
            }

            card.IncrementAbilityUseCount();
            card.IncrementActivationCount();
            HistoryLoggingUtilities.LogFullCardRefresh(
                state.Simulation.HistoryLog,
                state.Simulation.CardCatalog,
                card,
                true);
            ServerEffectUtilities.TriggerEffectEvent(
                triggerState,
                new ServerEffectUtilities.TriggerEvent(
                    EffectTriggerType.AfterActivatedAbility,
                    card,
                    state.Simulation.Players));

            state.Simulation.HistoryLog.EndEvent();
            DrainAutomaticTriggerQueues(state, false);
            ResolveDyingNonAvatarCards(state);
            CheckForRuneBreaks(state);
            DrainAutomaticTriggerQueues(state, true);
            CheckForAvatarDeath(state);
            state.Simulation.HistoryLog.SendUnsentMessages(
                state.Clients,
                GetObserverClients(state.Simulation),
                HistoryLog.FullGameStateSendRule.Exclude);

            Plugin.Log((playerIndex == 0 ? "human" : "AI")
                + " activated ability card=" + card.CardId
                + " target=" + decision.TargetCardNetUid
                + " lane=" + decision.LaneTarget);
            return true;
        }

        private static bool TryPlayCard(
            HostState state,
            int playerIndex,
            CcgRpc.Decision decision)
        {
            PlayerState player = state.Simulation.Players[playerIndex];
            ServerCard card = state.Simulation.CardCatalog.FindDynamicCard(
                new CardNetUid(decision.ActorCardNetUid));
            if (Object.ReferenceEquals(card, null) ||
                card.OwnerNetId.Id != player.NetId.Id)
                return false;

            bool fromProphecy =
                card.CurrentGameZoneName == GameZoneName.Prophecy;
            if (!fromProphecy && card.CurrentGameZoneName != GameZoneName.Hand)
                return false;

            SPTESL.Card baseCard = GameManager.Instance.GetCard(card.CardId);
            if (Object.ReferenceEquals(baseCard, null))
                return false;

            BoardLaneMask prerequisiteLanes =
                state.Simulation.CardCatalog.GatherOpenCreatureBoardLanesMask(
                    player.NetId);
            if (!PlayCardPrerequisitesMet(
                state, player, card, baseCard, ref prerequisiteLanes))
            {
                Plugin.Warn("play rejected by retail requirements: card="
                    + card.CardId + " uid=" + card.NetUid.Id);
                return false;
            }

            bool isCreature = baseCard.HasSubtype(CardSubtype.Creature);
            bool isSupport = baseCard.HasSubtype(CardSubtype.Support);
            bool isItem = baseCard.HasSubtype(CardSubtype.Item);
            bool boardCard = isCreature || isSupport || isItem;

            PlayCardAbility actionAbility;
            bool supportedAction =
                TryGetSupportedActionAbility(player, card, baseCard, out actionAbility);
            if (!boardCard && !supportedAction)
                return false;
            if (supportedAction &&
                !ValidateActionDecision(state, player, card, actionAbility, decision))
            {
                Plugin.Warn("play rejected: invalid action target card="
                    + card.CardId + " actor=" + decision.ActorCardNetUid
                    + " target=" + decision.TargetCardNetUid
                    + " laneTarget=" + decision.LaneTarget
                    + " targeting=" + actionAbility.PlayTargetingType);
                return false;
            }

            EmitPlayerTargetedCardEvent(
                state,
                card,
                new CardNetUid(decision.TargetCardNetUid));

            ServerCard equipTarget = null;
            if (isItem)
            {
                equipTarget = FindValidItemEquipTarget(state, player, decision);
                if (Object.ReferenceEquals(equipTarget, null))
                {
                    equipTarget = CreateMobilizeRecruit(
                        state, player, card, baseCard, decision);
                    if (Object.ReferenceEquals(equipTarget, null))
                        return false;
                }
            }

            int manaCost = card.QueryAttribute(AttributeType.ManaCost).Value;
            int mana = player.QueryAttribute(AttributeType.Mana).Value;
            if (!fromProphecy) manaCost = SharedStatFamilyRules.ManaCost(card, manaCost, mana);
            if (IsForbiddenByOpponentManaLock(state, player, card, manaCost))
            {
                Plugin.Log("play rejected by opponent mana lock: card=" + card.CardId
                    + " cost=" + manaCost);
                return false;
            }
            if (!fromProphecy && manaCost > mana)
                return false;

            Plugin.Log((playerIndex == 0 ? "human" : "AI")
                + " play decision: card=" + card.CardId
                + " actor=" + decision.ActorCardNetUid
                + " cost=" + manaCost
                + " availableMana=" + mana
                + " laneIndex=" + decision.LaneIndex
                + " laneTarget=" + decision.LaneTarget
                + " position=" + decision.PositionIndex);

            int lane = decision.LaneIndex;
            /* PlayToPosition uses LaneIndex in the retail input path.
             * LaneTarget belongs to targeted effects, so it must not override
             * a valid placement lane.  Accept MyLeft/MyRight only as a
             * compatibility fallback when the placement lane is absent. */
            if (lane < 0)
            {
                if (decision.LaneTarget == CcgRpc.BoardLaneTarget.MyLeftLane)
                    lane = 0;
                else if (decision.LaneTarget == CcgRpc.BoardLaneTarget.MyRightLane)
                    lane = 1;
            }
            if (lane < 0)
                lane = 0;

            ServerCard sacrificeTarget = null;
            if (isCreature)
            {
                BoardLaneMask open =
                    state.Simulation.CardCatalog.
                        GatherOpenCreatureBoardLanesMask(player.NetId);
                if (lane < 0 || lane > 1)
                    return false;

                bool laneOpen = BoardLaneMaskUtilities.IsLaneSet(open, lane);
                if (laneOpen)
                {
                    if (decision.DiscardCardNetUids.Count != 0)
                        return false;
                }
                else
                {
                    ServerCardCatalog catalog = state.Simulation.CardCatalog;
                    if (!catalog.IsLaneFull(
                            GameZoneName.CreatureBoard, lane, player.NetId) ||
                        decision.DiscardCardNetUids.Count != 1)
                    {
                        Plugin.Warn("play rejected: full lane requires one sacrifice lane="
                            + lane + " card=" + card.CardId);
                        return false;
                    }

                    sacrificeTarget = catalog.FindDynamicCard(
                        new CardNetUid(decision.DiscardCardNetUids[0]));
                    List<ServerCard> discardable =
                        catalog.GatherDiscardableCardsInZone(
                            player.NetId, GameZoneName.CreatureBoard, lane);
                    if (Object.ReferenceEquals(sacrificeTarget, null) ||
                        !discardable.Contains(sacrificeTarget))
                    {
                        Plugin.Warn("play rejected: invalid sacrifice target lane="
                            + lane + " uid=" + decision.DiscardCardNetUids[0]);
                        return false;
                    }
                }
            }
            else if (decision.DiscardCardNetUids.Count != 0)
            {
                /* Betray is encoded on the decision as the creature sacrificed
                 * before resolving the same retail ActionPlayInformation.  The
                 * retail Simulation.ProcessBetray path is stripped, so consume
                 * the explicit client choice here and keep the normal effect
                 * executor/history pipeline. */
                if (decision.DiscardCardNetUids.Count != 1 ||
                    baseCard.BetrayRequirements == null ||
                    baseCard.BetrayRequirements.PlayerRequirements == null ||
                    baseCard.BetrayRequirements.PlayerRequirements.Count == 0)
                    return false;
                sacrificeTarget = state.Simulation.CardCatalog.FindDynamicCard(
                    new CardNetUid(decision.DiscardCardNetUids[0]));
                if (sacrificeTarget == null ||
                    sacrificeTarget.OwnerNetId.Id != player.NetId.Id ||
                    sacrificeTarget.CurrentGameZoneName != GameZoneName.CreatureBoard ||
                    !sacrificeTarget.HasSubtype(CardSubtype.Creature) ||
                    sacrificeTarget.IsDying())
                    return false;
            }

            int playSourceUid = card.NetUid.Id;
            GameZoneName playSourceZone = card.CurrentGameZoneName;
            state.Simulation.HistoryLog.StartEvent(
                CcgRpc.HistoryType.EventPlayCard,
                player.NetId,
                card.NetUid,
                CardNetUid.GetInvalid(),
                -1);

            ServerEffectUtilities.TriggerGameState triggerState = TriggerState(state);
            if (!Object.ReferenceEquals(sacrificeTarget, null))
            {
                CardActions.Destroy.StaticExecuteSingleTarget(
                    triggerState, sacrificeTarget, card, -1, true);
                ResolveDyingNonAvatarCards(state);
                Plugin.Log((playerIndex == 0 ? "human" : "AI")
                    + " sacrificed creature=" + sacrificeTarget.CardId
                    + " uid=" + sacrificeTarget.NetUid.Id
                    + " to play=" + card.CardId + " lane=" + lane);
            }
            GameZoneOperationResult move = ServerCardCatalog.MoveCard(
                triggerState,
                new GameZoneId(player.NetId,
                    fromProphecy ? GameZoneType.Prophecy : GameZoneType.Hand),
                new GameZoneId(player.NetId, GameZoneType.Board),
                card.InstanceId,
                lane,
                CardInsertionType.Bottom,
                ServerCardCatalog.MoveOperationType.Untyped);

            if (move == GameZoneOperationResult.Succeeded)
            {
                /* Hand/Prophecy -> Board is public information.  The surviving
                 * ServerCard.ChangeZone() clears IsRevealed whenever the zone
                 * type changes, which is correct for many hidden transitions but
                 * wrong for a card that has just been openly played.  Keeping the
                 * authoritative board card revealed also makes opponent play
                 * previews/card-specific FX receive the real card state. */
                if (card.CurrentGameZoneType == GameZoneType.Board)
                    card.IsRevealed = true;

                Plugin.Log((playerIndex == 0 ? "human" : "AI")
                    + " play zone transition: card=" + card.CardId
                    + " instance=" + card.InstanceId.Id
                    + " premium=" + card.Premium
                    + " canonical=" + VariantCardRules.CanonicalId(card.CardId)
                    + " uid=" + playSourceUid + "->" + card.NetUid.Id
                    + " zone=" + playSourceZone + "->" + card.CurrentGameZoneName
                    + " previousType=" + card.PreviousGameZoneType
                    + " lane=" + card.CurrentLaneIndex);

                HistoryItem play = new HistoryItem();
                play.Type = CcgRpc.HistoryType.ActionPlayToBoard;
                play.AssetHash = card.FindOverrideOrDefaultAssetHash(
                    GetPlayCardFxAction(baseCard));
                play.ActingPlayerInstanceId = player.NetId.Id;
                play.ActorCardNetUid = playSourceUid;
                if (!Object.ReferenceEquals(equipTarget, null))
                    play.EquipCardNetUid = equipTarget.NetUid.Id;
                /*
                 * Keep the retail Previous -> Current zone delta for persistent
                 * board cards, but include the authoritative attributes.
                 * A ClientCard made from the revealed opponent-hand placeholder
                 * has a BaseCard but its dynamic Attributes[] remain zero unless
                 * the history carries AffectedAttributeName/Value.  MinimalCard
                 * omits those values, hiding power/health and breaking client
                 * ability/target checks.  An AI action remains in the temporary
                 * Board zone. Its public play preview must carry full state:
                 * SequenceUtils.CreateBestClientCard otherwise reuses the
                 * hidden hand placeholder, leaving BaseCard null and rendering
                 * a blank card.  The later private Deck -> View movement stays
                 * sanitized and does not reveal the AI's next draw.
                 */
                HistoryCard playCard =
                    card.HistoryCreate_FullStateCard(state.Simulation.CardCatalog);
                playCard.CardId = card.CardId;
                playCard.IsPremium = card.Premium;
                playCard.IsRevealed = true;
                playCard.IsFullState = playerIndex == 1 &&
                    card.CurrentGameZoneName == GameZoneName.Board;
                play.AffectedCardsArray.Add(playCard);

                Plugin.Log("history play delta: card=" + playCard.CardId
                    + " uid=" + playCard.CardNetUid
                    + " full=" + playCard.IsFullState
                    + " revealed=" + playCard.IsRevealed
                    + " previousType=" + playCard.PreviousGameZoneType
                    + " zone=" + (!Object.ReferenceEquals(
                        playCard.CurrentPosition, null)
                            ? playCard.CurrentPosition.GameZoneName.ToString()
                            : "null")
                    + " lane=" + (!Object.ReferenceEquals(
                        playCard.CurrentPosition, null)
                            ? playCard.CurrentPosition.LaneIndex.ToString()
                            : "-1")
                    + " attrs=" + playCard.AffectedAttributeName.Count);
                try
                {
                    state.Simulation.HistoryLog.PlayerDiffLogger.
                        DiffDeckCountsAndAddToHistoryItem(
                            state.Simulation.Players,
                            state.Simulation.CardCatalog,
                            play);
                }
                catch (Exception)
                {
                    /* Diff is best-effort; play history is still valid. */
                }
                state.Simulation.HistoryLog.AddInstant(play);

                /* Shadow lane cover is a native card enchantment. Apply it
                 * after the play event so the retail Cover sequence runs only
                 * once the creature has entered its lane. Standard Shadow
                 * Cover lasts until the start of the next turn. The retail
                 * helper marks Cover to expire on attack. Keep that native
                 * lifetime for creatures that can attack from Shadow. */
                int charge = card.QueryAttribute(AttributeType.Charge).Value;
                int taunt = card.QueryAttribute(AttributeType.Taunt).Value;
                int immuneToCover = card.QueryAttribute(AttributeType.ImmuneToCover).Value;
                if (boardCard && card.CurrentLaneType == CardLaneType.Stealth &&
                    charge <= 0 &&
                    taunt <= 0 && immuneToCover <= 0)
                {
                    CardActions.Cover.StaticExecuteSingleTarget(
                        state.Simulation.HistoryLog,
                        triggerState,
                        null,
                        card,
                        true,
                        card.FindOverrideOrDefaultAssetHash(CardFxAction.Cover));

                    Plugin.Log("shadow lane cover applied (start of next turn): card=" + card.CardId
                        + " uid=" + card.NetUid.Id + " lane=" + card.CurrentLaneIndex);
                }
                else if (boardCard && card.CurrentLaneType == CardLaneType.Stealth)
                    Plugin.Log("shadow lane cover skipped: card=" + card.CardId
                        + " uid=" + card.NetUid.Id + " lane=" + card.CurrentLaneIndex
                        + " charge=" + charge + " guard=" + taunt
                        + " immune=" + immuneToCover);

                if (!fromProphecy)
                {
                    bool retailManaHistory = true;
                    try
                    {
                        PlayerActions.ModifyMana.StaticExecute(
                            triggerState, player, -manaCost);
                    }
                    catch (Exception)
                    {
                        retailManaHistory = false;
                        /* Fallback when retail ModifyMana is stripped. */
                        player.SetAttribute(
                            AttributeType.Mana,
                            Math.Max(0, mana - manaCost));
                    }

                    /* Successful ModifyMana already emits ActionModifyMana,
                     * which SequenceModifyMana turns into SetAP().  Only the
                     * stripped fallback needs an explicit ActionSetMana. */
                    if (!retailManaHistory)
                    {
                        EmitManaState(state, player);
                        ServerEffectUtilities.TriggerEffectEvent(
                            triggerState,
                            new ServerEffectUtilities.TriggerEvent(
                                EffectTriggerType.WhenManaChanges,
                                player));
                    }
                }
                player.NumCardsPlayedThisTurn += 1;
                player.LastCardPlayedId = card.CardId;
                SharedStatFamilyRules.RecordPaidCost(card, fromProphecy ? 0 : manaCost);
                ReferenceSummonRules.ConsumeDiscount(TriggerState(state), player.NetId);

                if (!Object.ReferenceEquals(equipTarget, null))
                {
                    CardActions.Equip.StaticExecute(
                        triggerState,
                        card,
                        equipTarget,
                        card.FindOverrideOrDefaultAssetHash(CardFxAction.Equip));

                    if (RecoveredCardRules.RemoveCravenConscriptShackle(equipTarget))
                    {
                        HistoryLoggingUtilities.LogFullCardRefresh(
                            state.Simulation.HistoryLog,
                            state.Simulation.CardCatalog,
                            equipTarget,
                            true);
                        Plugin.Log("Craven Conscript equipped: permanent shackle removed; card="
                            + equipTarget.CardId + " uid=" + equipTarget.NetUid.Id);
                    }
                }

                ServerEffectUtilities.TriggerContext playContext =
                    new ServerEffectUtilities.TriggerContext();
                bool pendingCustom = false;
                if (supportedAction)
                {
                    pendingCustom = ExecuteSupportedActionPlay(
                        state,
                        triggerState,
                        player,
                        card,
                        actionAbility,
                        decision,
                        out playContext);
                }

                if (pendingCustom)
                {
                    state.PendingCustomAction = PendingCustomActionKind.PlayCard;
                    state.PendingCustomPlayerIndex = playerIndex;
                    state.PendingCustomCard = card;
                    state.PendingCustomContext = playContext;
                    state.PendingCustomFirstDecision = decision;
                    state.PendingCustomBoardCard = boardCard;
                    state.PendingCustomFromProphecy = fromProphecy;

                    state.Simulation.HistoryLog.SendUnsentMessages(
                        state.Clients,
                        GetObserverClients(state.Simulation),
                        HistoryLog.FullGameStateSendRule.Exclude);
                    Plugin.Log((playerIndex == 0 ? "human" : "AI")
                        + " play awaiting custom UI card=" + card.CardId
                        + " ui=" + actionAbility.CustomUiType);
                    return true;
                }

                FinalizePlayedCardAfterEffects(
                    state,
                    triggerState,
                    card,
                    boardCard,
                    supportedAction);
            }

            state.Simulation.HistoryLog.EndEvent();

            if (supportedAction && !boardCard)
            {
                state.Simulation.HistoryLog.StartEvent(CcgRpc.HistoryType.EventTrigger, player.NetId, card.NetUid, CardNetUid.GetInvalid(), -1);
                SharedStatFamilyRules.OnPlayed(triggerState, card);
                state.Simulation.HistoryLog.EndEvent();
            }

            if (move == GameZoneOperationResult.Succeeded)
            {
                DrainAutomaticTriggerQueues(state, false);
                ResolveDyingNonAvatarCards(state);
                if (!fromProphecy)
                    CheckForRuneBreaks(state);
                DrainAutomaticTriggerQueues(state, true);
                CheckForAvatarDeath(state);
                state.Simulation.HistoryLog.SendUnsentMessages(
                    state.Clients,
                    GetObserverClients(state.Simulation),
                    HistoryLog.FullGameStateSendRule.Exclude);

                Plugin.Log((playerIndex == 0 ? "human" : "AI")
                    + " played card=" + card.CardId
                    + " lane=" + lane
                    + " cost=" + manaCost
                    + " manaBefore=" + mana
                    + " manaAfter=" + (fromProphecy ? mana : mana - manaCost)
                    + (fromProphecy ? " prophecy=true" : String.Empty));
                return true;
            }

            return false;
        }

        private static bool TryGetSupportedActionAbility(
            PlayerState player,
            ServerCard card,
            SPTESL.Card baseCard,
            out PlayCardAbility ability)
        {
            ability = null;

            if (Object.ReferenceEquals(baseCard.ActionPlayInformation, null) ||
                baseCard.ActionPlayInformation.Count == 0)
                return false;

            int actionLevel = player.GetActionLevel(card.CardId);
            if (actionLevel < 0 || actionLevel >= baseCard.ActionPlayInformation.Count)
                return false;

            ability = baseCard.ActionPlayInformation[actionLevel];
            if (Object.ReferenceEquals(ability, null))
                return false;

            return true;
        }

        private static ServerCard FindValidItemEquipTarget(
            HostState state,
            PlayerState player,
            CcgRpc.Decision decision)
        {
            CardNetUid equipUid = new CardNetUid(decision.EquipCardNetUid);
            if (!equipUid.IsValid())
                return null;

            ServerCard target = state.Simulation.CardCatalog.FindDynamicCard(equipUid);
            if (Object.ReferenceEquals(target, null) ||
                target.OwnerNetId.Id != player.NetId.Id ||
                target.CurrentGameZoneName != GameZoneName.CreatureBoard)
                return null;

            return target;
        }

        private static bool IsMobilizeCard(SPTESL.Card card)
        {
            return card != null && card.MobilizeRecruitId != null &&
                card.MobilizeRecruitId.CardIdReference != 0;
        }

        private static int DecodeMobilizeLane(CcgRpc.Decision decision)
        {
            if (decision.LaneTarget == CcgRpc.BoardLaneTarget.MyRightLane)
                return 1;
            if (decision.LaneTarget == CcgRpc.BoardLaneTarget.MyLeftLane)
                return 0;
            return decision.LaneIndex;
        }

        private static ServerCard CreateMobilizeRecruit(
            HostState state,
            PlayerState player,
            ServerCard item,
            SPTESL.Card baseCard,
            CcgRpc.Decision decision)
        {
            if (!IsMobilizeCard(baseCard))
                return null;

            int lane = DecodeMobilizeLane(decision);
            if (lane < 0 || lane > 1 ||
                state.Simulation.CardCatalog.IsLaneFull(
                    GameZoneName.CreatureBoard, lane, player.NetId))
                return null;

            SPTESL.Card recruitDefinition = GameManager.Instance.GetCard(
                baseCard.MobilizeRecruitId.CardIdReference);
            if (recruitDefinition == null)
                return null;

            GameZone.Position position = state.Simulation.CardCatalog
                .FindInsertionPosition(
                    player.NetId, GameZoneName.CreatureBoard, lane,
                    CardInsertionType.Bottom);
            int before = state.Simulation.CardCatalog.FindCardCount(
                GameZoneName.CreatureBoard, player.NetId);
            PlayerActions.CreateToken.StaticExecute(
                TriggerState(state), item, recruitDefinition, item.Premium,
                GameZoneType.Board, position, player.NetId, 1,
                item.FindOverrideOrDefaultAssetHash(CardFxAction.Mobilize), item);

            List<ServerCard> recruits = state.Simulation.CardCatalog.GatherCardsInZone(
                delegate(ServerCard candidate)
                {
                    return candidate.OwnerNetId == player.NetId &&
                        candidate.CardId == recruitDefinition.ResourceId.Id &&
                        candidate.CurrentGameZoneName == GameZoneName.CreatureBoard &&
                        candidate.CurrentLaneIndex == lane;
                }, player.NetId, GameZoneName.CreatureBoard);
            if (recruits.Count == 0 || before >=
                state.Simulation.CardCatalog.FindCardCount(
                    GameZoneName.CreatureBoard, player.NetId))
                return null;
            Plugin.Log("mobilize recruit materialized item=" + item.CardId
                + " recruit=" + recruitDefinition.ResourceId.Id
                + " lane=" + lane + " uid=" + recruits[recruits.Count - 1].NetUid.Id);
            return recruits[recruits.Count - 1];
        }

        private static bool TryGetChoiceFreeActionAbility(
            PlayerState player,
            ServerCard card,
            SPTESL.Card baseCard,
            out PlayCardAbility ability)
        {
            if (!TryGetSupportedActionAbility(
                player, card, baseCard, out ability))
                return false;
            return ability.PlayTargetingType == PlayTargetingType.NoTarget;
        }

        private static List<ServerCard> GatherActionCardTargets(
            HostState state,
            PlayerState player,
            ServerCard source,
            PlayCardAbility ability)
        {
            List<ServerCard> result = new List<ServerCard>();
            if (ability.PlayTargetingType == PlayTargetingType.NoTarget)
                return result;

            List<DecisionConditionalExpression> selectors =
                ability.TargetSelectors;
            bool hasCardSelector = false;
            if (!Object.ReferenceEquals(selectors, null))
            {
                int selectorIndex;
                for (selectorIndex = 0;
                     selectorIndex < selectors.Count;
                     ++selectorIndex)
                {
                    DecisionConditionalExpression selector =
                        selectors[selectorIndex];
                    if (!Object.ReferenceEquals(selector, null) &&
                        selector.DecisionType == SPTESL.DecisionType.Card)
                    {
                        hasCardSelector = true;
                        break;
                    }
                }
            }

            if (!hasCardSelector &&
                !Object.ReferenceEquals(selectors, null) &&
                selectors.Count > 0)
                return result;

            /*
             * GatherInPlayCardList() is the surviving retail source, but add
             * both AvatarBoard zones explicitly.  Avatar cards are valid card
             * targets (Lightning Bolt is the concrete failing case) and some
             * reconstructed zone states do not enumerate the static avatar in
             * the same dynamic pass as Creature/Support cards.
             */
            List<ServerCard> candidates =
                state.Simulation.CardCatalog.GatherInPlayCardList();

            // Retail actions such as Strange Brew and Barter explicitly select
            // a card in Hand. Keep hidden cards out of ordinary board targeting.
            bool targetsHand = false;
            if (!Object.ReferenceEquals(selectors, null))
                foreach (DecisionConditionalExpression selector in selectors)
                    if (!Object.ReferenceEquals(selector, null) &&
                        selector.DecisionType == SPTESL.DecisionType.Card &&
                        !Object.ReferenceEquals(selector.CardConditionalExpressions, null))
                        foreach (ICardConditionalExpression expression in
                            selector.CardConditionalExpressions)
                        {
                            CardConditionals.Zone zone =
                                expression as CardConditionals.Zone;
                            if (!Object.ReferenceEquals(zone, null) &&
                                zone.Check == CardConditionals.Zone.Requirement.IsIn &&
                                GameZone.IsTypeInMask(GameZoneType.Hand,
                                    zone.ZoneTypeMask))
                                targetsHand = true;
                        }
            if (targetsHand)
                foreach (PlayerState owner in state.Simulation.Players)
                    candidates.AddRange(
                        state.Simulation.CardCatalog.GatherCardsInZone(
                            delegate(ServerCard candidate) { return true; },
                            owner.NetId, GameZoneName.Hand));

            int ownerIndex;
            for (ownerIndex = 0;
                 ownerIndex < state.Simulation.Players.Count;
                 ++ownerIndex)
            {
                PlayerState owner =
                    state.Simulation.Players[ownerIndex];
                List<ServerCard> avatars =
                    state.Simulation.CardCatalog.GatherCardsInZone(
                        delegate(ServerCard candidate) { return true; },
                        owner.NetId,
                        GameZoneName.AvatarBoard);
                int a;
                for (a = 0; a < avatars.Count; ++a)
                {
                    bool present = false;
                    int c;
                    for (c = 0; c < candidates.Count; ++c)
                    {
                        if (!Object.ReferenceEquals(candidates[c], null) &&
                            candidates[c].InstanceId.Id ==
                                avatars[a].InstanceId.Id)
                        {
                            present = true;
                            break;
                        }
                    }
                    if (!present)
                        candidates.Add(avatars[a]);
                }
            }

            List<Effect> effects = Object.ReferenceEquals(
                ability.EffectGroup, null)
                ? null
                : ability.EffectGroup.Effects;

            int i;
            for (i = 0; i < candidates.Count; ++i)
            {
                ServerCard target = candidates[i];
                if (Object.ReferenceEquals(target, null) ||
                    (target.CurrentGameZoneName != GameZoneName.Hand &&
                    !target.CanBeExplicitlyTargetedByCard(
                        source,
                        effects,
                        ability.IsDamageOnly)))
                    continue;

                if (ActionCardSelectorsMatch(
                    state, player, source, target, selectors))
                    result.Add(target);
            }

            return result;
        }

        private static bool ActionCardSelectorsMatch(
            HostState state,
            PlayerState player,
            ServerCard source,
            ServerCard target,
            List<DecisionConditionalExpression> selectors)
        {
            if (Object.ReferenceEquals(selectors, null) ||
                selectors.Count == 0)
                return true;

            PlayerState opponent =
                state.Simulation.Players[0] == player
                    ? state.Simulation.Players[1]
                    : state.Simulation.Players[0];
            PlayerState targetPlayer = FindPlayerByUid(
                state.Simulation.Players, target.OwnerNetId);

            TriggerConditionalArguments triggerArguments =
                new TriggerConditionalArguments(
                    0, (AttributeType)0, -1, 0);

            bool foundCardSelector = false;
            int i;
            for (i = 0; i < selectors.Count; ++i)
            {
                DecisionConditionalExpression selector = selectors[i];
                if (Object.ReferenceEquals(selector, null) ||
                    selector.DecisionType !=
                        SPTESL.DecisionType.Card)
                    continue;

                foundCardSelector = true;

                /*
                 * Primary context matches the retail action-decision path:
                 * source is Self/Inciter and the candidate is DecisionTarget.
                 */
                CardConditionalArguments cardArguments =
                    new CardConditionalArguments(
                        state.Simulation.CardCatalog,
                        source,
                        source,
                        null,
                        target,
                        null,
                        LaneIdentifier.GetInvalid());
                PlayerConditionalArguments playerArguments =
                    new PlayerConditionalArguments(
                        state.Simulation.CardCatalog,
                        player,
                        player,
                        null,
                        targetPlayer,
                        opponent);

                if (EvaluateCardDecisionSelector(
                    selector,
                    target,
                    cardArguments,
                    playerArguments,
                    triggerArguments))
                    return true;

                /*
                 * Some serialized action selectors in the retail data were
                 * authored against EventTarget even though the input command is
                 * a DecisionTarget.  The original GatherSpellCardTargets body
                 * is stripped from this binary, so evaluate the same candidate
                 * once more with the decision candidate mirrored into
                 * EventTarget.  This restores the original accepted target set
                 * without bypassing any selector expression.
                 */
                CardConditionalArguments mirroredCardArguments =
                    new CardConditionalArguments(
                        state.Simulation.CardCatalog,
                        source,
                        source,
                        target,
                        target,
                        null,
                        LaneIdentifier.GetInvalid());
                PlayerConditionalArguments mirroredPlayerArguments =
                    new PlayerConditionalArguments(
                        state.Simulation.CardCatalog,
                        player,
                        player,
                        targetPlayer,
                        targetPlayer,
                        opponent);

                if (EvaluateCardDecisionSelector(
                    selector,
                    target,
                    mirroredCardArguments,
                    mirroredPlayerArguments,
                    triggerArguments))
                    return true;
            }

            return !foundCardSelector;
        }

        private static bool EvaluateCardDecisionSelector(
            DecisionConditionalExpression selector,
            ServerCard target,
            CardConditionalArguments cardArguments,
            PlayerConditionalArguments playerArguments,
            TriggerConditionalArguments triggerArguments)
        {
            if (Object.ReferenceEquals(
                selector.CardConditionalExpressions, null))
                return true;

            int j;
            for (j = 0;
                 j < selector.CardConditionalExpressions.Count;
                 ++j)
            {
                ICardConditionalExpression expression =
                    selector.CardConditionalExpressions[j];
                if (Object.ReferenceEquals(expression, null) ||
                    !expression.Evaluate(
                        target,
                        cardArguments,
                        playerArguments,
                        triggerArguments))
                    return false;
            }
            return true;
        }

        private static List<CcgRpc.BoardLaneTarget> GatherActionLaneTargets(
            HostState state,
            PlayerState player,
            ServerCard source,
            PlayCardAbility ability)
        {
            List<CcgRpc.BoardLaneTarget> result =
                new List<CcgRpc.BoardLaneTarget>();
            List<DecisionConditionalExpression> selectors = ability.TargetSelectors;
            if (Object.ReferenceEquals(selectors, null) || selectors.Count == 0)
                return result;

            PlayerState opponent = state.Simulation.Players[0] == player
                ? state.Simulation.Players[1]
                : state.Simulation.Players[0];
            int selectorIndex;
            for (selectorIndex = 0; selectorIndex < selectors.Count; ++selectorIndex)
            {
                DecisionConditionalExpression selector = selectors[selectorIndex];
                if (Object.ReferenceEquals(selector, null) ||
                    (selector.DecisionType != SPTESL.DecisionType.Lane &&
                     selector.DecisionType != SPTESL.DecisionType.LaneLeftOrRight))
                    continue;

                int ownerIndex;
                for (ownerIndex = 0; ownerIndex < 2; ++ownerIndex)
                {
                    PlayerState laneOwner = ownerIndex == 0 ? player : opponent;
                    int laneCount = state.Simulation.CardCatalog.FindLaneCount(
                        GameZoneName.CreatureBoard, laneOwner.NetId);

                    int laneIndex;
                    for (laneIndex = 0; laneIndex < laneCount && laneIndex < 2;
                         ++laneIndex)
                    {
                        LaneIdentifier lane = new LaneIdentifier(
                            GameZoneName.CreatureBoard,
                            laneOwner.NetId,
                            laneIndex);
                        if (!ActionLaneSelectorMatches(
                            state,
                            player,
                            opponent,
                            laneOwner,
                            source,
                            lane,
                            selector))
                            continue;

                        CcgRpc.BoardLaneTarget encoded = EncodeLaneTarget(
                            ownerIndex == 0, laneIndex);
                        if (encoded != CcgRpc.BoardLaneTarget.None &&
                            !result.Contains(encoded))
                            result.Add(encoded);
                    }
                }
            }
            return result;
        }

        private static bool ActionLaneSelectorMatches(
            HostState state,
            PlayerState player,
            PlayerState opponent,
            PlayerState laneOwner,
            ServerCard source,
            LaneIdentifier lane,
            DecisionConditionalExpression selector)
        {
            CardConditionalArguments cardArguments = new CardConditionalArguments(
                state.Simulation.CardCatalog,
                source,
                source,
                null,
                null,
                null,
                lane);
            PlayerConditionalArguments playerArguments =
                new PlayerConditionalArguments(
                    state.Simulation.CardCatalog,
                    player,
                    player,
                    null,
                    laneOwner,
                    opponent);
            LaneConditionalArguments laneArguments = new LaneConditionalArguments(
                state.Simulation.CardCatalog,
                LaneIdentifier.GetInvalid(),
                lane);

            if (Object.ReferenceEquals(selector.LaneConditionalExpressions, null))
                return true;
            int i;
            for (i = 0; i < selector.LaneConditionalExpressions.Count; ++i)
            {
                ILaneConditionalExpression expression =
                    selector.LaneConditionalExpressions[i];
                if (Object.ReferenceEquals(expression, null) ||
                    !expression.Evaluate(
                        lane,
                        laneArguments,
                        cardArguments,
                        playerArguments))
                    return false;
            }
            return true;
        }

        private static CcgRpc.BoardLaneTarget EncodeLaneTarget(
            bool self,
            int laneIndex)
        {
            if (laneIndex == 0)
                return self
                    ? CcgRpc.BoardLaneTarget.MyLeftLane
                    : CcgRpc.BoardLaneTarget.OtherLeftLane;
            if (laneIndex == 1)
                return self
                    ? CcgRpc.BoardLaneTarget.MyRightLane
                    : CcgRpc.BoardLaneTarget.OtherRightLane;
            return CcgRpc.BoardLaneTarget.None;
        }

        private static PlayerState FindPlayerByUid(
            List<PlayerState> players,
            PlayerUniqueId uid)
        {
            int i;
            for (i = 0; i < players.Count; ++i)
            {
                if (!Object.ReferenceEquals(players[i], null) &&
                    players[i].NetId.Id == uid.Id)
                    return players[i];
            }
            return null;
        }

        private static bool FillSupportedActionCommand(
            HostState state,
            PlayerState player,
            ServerCard card,
            PlayCardAbility ability,
            CardCommandState command)
        {
            List<ServerCard> cardTargets = GatherActionCardTargets(
                state, player, card, ability);
            List<CcgRpc.BoardLaneTarget> laneTargets = GatherActionLaneTargets(
                state, player, card, ability);

            if (ability.PlayTargetingType == PlayTargetingType.RequiredTarget &&
                cardTargets.Count == 0 && laneTargets.Count == 0)
                return false;

            int i;
            for (i = 0; i < cardTargets.Count; ++i)
                command.PlayTargets.Add(cardTargets[i].NetUid.Id);
            for (i = 0; i < laneTargets.Count; ++i)
                command.LaneTargets.Add(laneTargets[i]);

            if (laneTargets.Count > 0)
                command.ValidCardCommandMask |= (int)CardCommand.PlayToLane;
            if (cardTargets.Count > 0 ||
                ability.PlayTargetingType != PlayTargetingType.RequiredTarget)
                command.ValidCardCommandMask |= (int)CardCommand.PlayPositionless;
            return command.ValidCardCommandMask != 0;
        }

        private static bool FillReconstructedBoardPlayCommand(
            HostState state,
            PlayerState player,
            PlayerState opponent,
            ServerCard card,
            SPTESL.Card baseCard,
            PlayCardAbility actionAbility,
            BoardLaneMask legalCreatureBoardLanes,
            BoardLaneMask filledCreatureBoardLanes,
            CardCommandState command)
        {
            BoardPlacementMasks.ForCard(state.Simulation.CardCatalog,player.NetId,card,
                ref legalCreatureBoardLanes,ref filledCreatureBoardLanes);
            command.ValidBoardLanes = legalCreatureBoardLanes.Value;
            command.FullBoardLanes = filledCreatureBoardLanes.Value;

            GameZoneName boardZone = card.FindGameZoneName(GameZoneType.Board);
            if (boardZone == GameZoneName.SupportBoard)
            {
                int supportLaneCount = state.Simulation.CardCatalog.FindLaneCount(
                    boardZone, player.NetId);
                if (supportLaneCount > 0 &&
                    legalCreatureBoardLanes.Value == filledCreatureBoardLanes.Value)
                {
                    bool canReplace = false;
                    int laneIndex;
                    for (laneIndex = 0;
                         laneIndex < supportLaneCount;
                         ++laneIndex)
                    {
                        List<ServerCard> discardable =
                            state.Simulation.CardCatalog.GatherDiscardableCardsInZone(
                                player.NetId, boardZone, laneIndex);
                        if (discardable.Count > 0)
                        {
                            canReplace = true;
                            break;
                        }
                    }
                    if (!canReplace)
                        return false;
                }
            }

            if (boardZone == GameZoneName.CreatureBoard)
                command.ValidCardCommandMask |= (int)CardCommand.PlayToPosition;
            else if (boardZone == GameZoneName.ItemBoard)
            {
                List<ServerCard> equipTargets =
                    state.Simulation.CardCatalog.GatherCardsInZone(
                        delegate(ServerCard candidate) { return true; },
                        player.NetId,
                        GameZoneName.CreatureBoard);
                int e;
                for (e = 0; e < equipTargets.Count; ++e)
                    command.EquipTargets.Add(equipTargets[e].NetUid.Id);
                if (equipTargets.Count > 0)
                    command.ValidCardCommandMask |= (int)CardCommand.PlayToEquip;
                else
                {
                    if (!IsMobilizeCard(baseCard))
                        return false;
                    int laneCount = state.Simulation.CardCatalog.FindLaneCount(
                        GameZoneName.CreatureBoard, player.NetId);
                    for (int lane = 0; lane < laneCount && lane < 2; ++lane)
                        if (!state.Simulation.CardCatalog.IsLaneFull(
                            GameZoneName.CreatureBoard, lane, player.NetId))
                            command.LaneTargets.Add(lane == 0
                                ? CcgRpc.BoardLaneTarget.MyLeftLane
                                : CcgRpc.BoardLaneTarget.MyRightLane);
                    if (command.LaneTargets.Count == 0)
                        return false;
                    command.ValidCardCommandMask |= (int)CardCommand.PlayToLane;
                }
            }

            List<ServerCard> cardTargets = GatherActionCardTargets(
                state, player, card, actionAbility);
            List<CcgRpc.BoardLaneTarget> laneTargets = GatherActionLaneTargets(
                state, player, card, actionAbility);
            if (actionAbility.PlayTargetingType == PlayTargetingType.RequiredTarget &&
                cardTargets.Count == 0 && laneTargets.Count == 0)
                return false;

            int i;
            for (i = 0; i < cardTargets.Count; ++i)
                command.PlayTargets.Add(cardTargets[i].NetUid.Id);
            for (i = 0; i < laneTargets.Count; ++i)
                command.LaneTargets.Add(laneTargets[i]);
            if (laneTargets.Count > 0)
                command.ValidCardCommandMask |= (int)CardCommand.PlayToLane;

            /* This mirrors the surviving tail of
             * FillPlayCardCommandStateNoPrerequisites(): supports/actions are
             * positionless, and a card target adds the positionless targeting
             * branch even when the card also needs a board/equip destination. */
            if (command.ValidCardCommandMask == 0 || cardTargets.Count > 0 ||
                boardZone == GameZoneName.SupportBoard)
                command.ValidCardCommandMask |= (int)CardCommand.PlayPositionless;

            if (CardCommandUtilities.StrongHintPrerequisitesMet(
                state.Simulation.CardCatalog, player, opponent, card))
                command.ValidCardCommandMask |= (int)CardCommand.StrongHint;

            return command.ValidCardCommandMask != 0;
        }

        private static bool ValidateActionDecision(
            HostState state,
            PlayerState player,
            ServerCard card,
            PlayCardAbility ability,
            CcgRpc.Decision decision)
        {
            CardNetUid targetUid = new CardNetUid(decision.TargetCardNetUid);
            bool hasCardDecision = targetUid.IsValid();
            bool hasLaneDecision =
                decision.LaneTarget != CcgRpc.BoardLaneTarget.None;

            if (ability.PlayTargetingType == PlayTargetingType.NoTarget)
                return !hasCardDecision && !hasLaneDecision;

            if (!hasCardDecision && !hasLaneDecision)
                return ability.PlayTargetingType == PlayTargetingType.OptionalTarget;

            if (hasCardDecision)
            {
                List<ServerCard> targets = GatherActionCardTargets(
                    state, player, card, ability);
                int i;
                for (i = 0; i < targets.Count; ++i)
                {
                    if (targets[i].NetUid.Id == targetUid.Id)
                        return true;
                }
                return false;
            }

            List<CcgRpc.BoardLaneTarget> lanes = GatherActionLaneTargets(
                state, player, card, ability);
            return lanes.Contains(decision.LaneTarget);
        }

        private static CardFxAction GetPlayCardFxAction(SPTESL.Card baseCard)
        {
            if (baseCard.HasSubtype(CardSubtype.Item))
                return CardFxAction.PlayCardItem;
            if (baseCard.HasSubtype(CardSubtype.Support))
                return CardFxAction.PlayCardSupport;
            if (baseCard.HasSubtype(CardSubtype.Spell) ||
                baseCard.HasSubtype(CardSubtype.Shout) ||
                baseCard.HasSubtype(CardSubtype.HasAction))
                return CardFxAction.PlayCardAction;
            return CardFxAction.PlayCard;
        }

        private static bool ExecuteSupportedActionPlay(
            HostState state,
            ServerEffectUtilities.TriggerGameState triggerState,
            PlayerState player,
            ServerCard card,
            PlayCardAbility ability,
            CcgRpc.Decision decision,
            out ServerEffectUtilities.TriggerContext context)
        {
            TriggeredEffect effect = new TriggeredEffect();
            effect.EffectGroup = ability.EffectGroup;
            effect.TriggerType = EffectTriggerType.ActionEffect;
            effect.ClientTargetingType = ability.PlayTargetingType;
            effect.TargetSelectors = ability.TargetSelectors;
            effect.CustomUiType = ability.CustomUiType;
            effect.OptionalCustomUiType = ability.OptionalCustomUiType;
            effect.CustomUiEffectGroups = ability.CustomUiEffectGroups;
            effect.PostCustomUiEffectGroup = ability.PostCustomUiEffectGroup;

            context = new ServerEffectUtilities.TriggerContext();
            context.TriggeredEffect = effect;
            context.EffectOwner = card;
            context.TriggerEvent = new ServerEffectUtilities.TriggerEvent(
                EffectTriggerType.ActionEffect, card, state.Simulation.Players);
            context.EffectIndex = player.GetActionLevel(card.CardId);
            context.ActingLaneIndex = decision.LaneIndex;
            context.ActingLaneIndexType = 0;

            StartTriggerEventLogWithMechanicFx(
                state.Simulation.HistoryLog, ref context);
            ServerEffectUtilities.ExecuteOnPlayCardEffects(
                triggerState, context, decision, null);

            if (ability.CustomUiType != CustomCardUiType.None)
            {
                context.DidMainEffectsRun = true;
                if (ServerEffectUtilities.IsPlayerDecisionRequired(
                    context, state.CustomUiCards))
                {
                    /* Keep the nested EventTrigger open.  The second-stage
                     * CustomChoice completes the same retail action effect. */
                    return true;
                }

                /* No legal/required custom choice was generated.  The retail
                 * play executor normally enters the board only after its
                 * second stage; complete that transition without fabricating
                 * an invalid choice index. */
                ServerEffectUtilities.TriggerWhenCardEntersBoard(
                    triggerState, card);
            }

            ServerEffectUtilities.EndTriggerEventLog(triggerState, ref context);
            return false;
        }

        private static void FinalizePlayedCardAfterEffects(
            HostState state,
            ServerEffectUtilities.TriggerGameState triggerState,
            ServerCard card,
            bool boardCard,
            bool supportedAction)
        {
            /* ExecuteOnPlayCardEffects() emits WhenCardEntersBoard after the
             * action effect.  Cards without ActionPlayInformation need that
             * retail transition explicitly. */
            if (boardCard)
            {
                ServerEffectUtilities.TriggerDirectEffectEvent(
                    triggerState,
                    new ServerEffectUtilities.TriggerEvent(
                        EffectTriggerType.Summon,
                        card,
                        state.Simulation.Players));

                /* Plot is a play-time mechanic distinct from Summon and
                 * WhenCardIsPlayed.  The retail graph keeps it as its own
                 * trigger type, so the offline host must emit that event after
                 * the card has entered the board.  NumCardsPlayedThisTurn was
                 * incremented immediately before this method; > 1 therefore
                 * means another card was played earlier in the same turn. */
                PlayerState owner = state.Simulation.Players.Find(
                    delegate(PlayerState candidate)
                    {
                        return candidate.NetId.Id == card.OwnerNetId.Id;
                    });
                if (!Object.ReferenceEquals(owner, null) &&
                    owner.NumCardsPlayedThisTurn > 1)
                {
                    ServerEffectUtilities.TriggerDirectEffectEvent(
                        triggerState,
                        new ServerEffectUtilities.TriggerEvent(
                            EffectTriggerType.Plot,
                            card,
                            state.Simulation.Players));
                    Plugin.Log("plot trigger emitted: card=" + card.CardId
                        + " uid=" + card.NetUid.Id
                        + " cardsPlayed=" + owner.NumCardsPlayedThisTurn);
                }
            }
            ServerEffectUtilities.TriggerEffectEvent(
                triggerState,
                new ServerEffectUtilities.TriggerEvent(
                    EffectTriggerType.WhenCardIsPlayed,
                    card,
                    card,
                    state.Simulation.Players));
            if (boardCard && !supportedAction)
                ServerEffectUtilities.TriggerWhenCardEntersBoard(
                    triggerState, card);
            ElsweyrKhajiitRules.OnPlayed(triggerState, card);
            ServerEffectUtilities.TriggerEffectEvent(
                triggerState,
                new ServerEffectUtilities.TriggerEvent(
                    EffectTriggerType.WhenCardMoveComplete,
                    card,
                    card,
                    state.Simulation.Players));

            if (supportedAction && !boardCard)
            {
                /* Pure action cards leave the board staging zone after
                 * resolution.  Creatures, supports and items can also own
                 * ActionPlayInformation; those remain in their canonical
                 * Board/SupportBoard/ItemBoard zone. */
                ServerCardCatalog.MoveToCemetery(
                    triggerState,
                    card,
                    ServerCardCatalog.MoveOperationType.Untyped);
                state.Simulation.CardCatalog.FlushDeathrattleZone(triggerState);
            }
        }

        private static bool TryAttack(
            HostState state,
            int playerIndex,
            CcgRpc.Decision decision)
        {
            PlayerState player = state.Simulation.Players[playerIndex];
            PlayerState opponent = state.Simulation.Players[playerIndex == 0 ? 1 : 0];
            ServerCard attacker = state.Simulation.CardCatalog.FindDynamicCard(
                new CardNetUid(decision.ActorCardNetUid));
            ServerCard target = state.Simulation.CardCatalog.FindDynamicCard(
                new CardNetUid(decision.TargetCardNetUid));

            if (Object.ReferenceEquals(attacker, null) ||
                Object.ReferenceEquals(target, null) ||
                attacker.OwnerNetId.Id != player.NetId.Id)
            {
                Plugin.Warn("master debug: attack rejected missing/owner actor="
                    + decision.ActorCardNetUid + " target=" + decision.TargetCardNetUid);
                return false;
            }
            bool canAttackNormally = attacker.CanCardAttack(false);
            bool extra = !canAttackNormally && HasMasterOfThievesExtraAttack(state, attacker);
            if (!canAttackNormally && !extra) {
                Plugin.Warn("master debug: attack rejected canAttack=false card="
                    + attacker.CardId + " uid=" + attacker.NetUid.Id);
                return false;
            }

            List<ServerCard> valid = GatherBasicAttackTargets(state, player, opponent, attacker);
            bool found = false;
            int i;
            for (i = 0; i < valid.Count; ++i)
            {
                if (valid[i].NetUid.Id == target.NetUid.Id)
                {
                    found = true;
                    break;
                }
            }
            if (!found)
                return false;

            Plugin.Log((playerIndex == 0 ? "human" : "AI")
                + " attack accepted actorUid=" + attacker.NetUid.Id
                + " actorCard=" + attacker.CardId
                + " targetUid=" + target.NetUid.Id
                + " targetCard=" + target.CardId);
            if (extra)
            {
                int used;
                state.MasterOfThievesExtraAttacksUsed.TryGetValue(
                    attacker.NetUid.Id, out used);
                state.MasterOfThievesExtraAttacksUsed[attacker.NetUid.Id] = used + 1;
                Plugin.Log("master debug: extra attack granted card=" + attacker.CardId
                    + " uid=" + attacker.NetUid.Id + " used=" + (used + 1));
            }

            state.Simulation.HistoryLog.StartEvent(
                CcgRpc.HistoryType.EventAttack,
                player.NetId,
                attacker.NetUid,
                target.NetUid,
                -1);

            /* The retail client does not animate an attack from EventAttack +
             * damage alone. BoardMessageTranslator maps these exact history
             * actions to SequenceCardPrepAttack and SequenceCardAttack. Keep the actor/target on each item as
             * the original wire protocol does. */
            AddAttackPhaseAction(
                state, CcgRpc.HistoryType.ActionPrepAttack, attacker, target);

            /* The host owns the attack-phase transition.  The retail action
             * history alone is not sufficient to wake cards listening for
             * the preparation trigger, so dispatch it before rally/attack
             * resolution begins. */
            ServerEffectUtilities.TriggerEffectEvent(
                TriggerState(state),
                new ServerEffectUtilities.TriggerEvent(
                    EffectTriggerType.WhenCardPreparesAttack,
                    attacker,
                    target,
                    state.Simulation.Players));
            DrainAutomaticTriggerQueues(state, false);

            if (attacker.QueryAttribute(AttributeType.Rally).Value > 0)
                ExecuteRetailRallyAction(
                    state, attacker, target, new CardActions.PrepareRally());

            ServerEffectUtilities.TriggerEffectEvent(
                TriggerState(state),
                new ServerEffectUtilities.TriggerEvent(
                    EffectTriggerType.WhenCardAttacks,
                    attacker,
                    target,
                    state.Simulation.Players));
            DrainAutomaticTriggerQueues(state, false);

            AddAttackPhaseAction(
                state, CcgRpc.HistoryType.ActionDoAttack, attacker, target);

            List<ServerCard> recipients = new List<ServerCard>();
            recipients.Add(target);
            /* The attack sequence already owns the lunge and return. ActionFight
             * loads card_battle, a second card-vs-card motion that also plays
             * against an avatar. Suppress only that visual action for face
             * attacks; Fight still applies retail combat damage and triggers. */
            int battleAssetHash = target.HasSubtype(CardSubtype.Avatar)
                ? -1
                : attacker.FindOverrideOrDefaultAssetHash(CardFxAction.Battle);
            int targetHealthBeforeFight = target.CurrentHealth;
            CardActions.Fight.StaticExecute(
                TriggerState(state),
                attacker,
                recipients,
                battleAssetHash);

            if (target.HasSubtype(CardSubtype.Avatar) &&
                target.CurrentHealth < targetHealthBeforeFight &&
                target.OwnerNetId.Id != attacker.OwnerNetId.Id &&
                attacker.CurrentGameZoneName == GameZoneName.CreatureBoard &&
                !attacker.IsDying())
                ApplyThievesDenPilfer(state, attacker);

            /* SequenceCardAttack already plays card_finish_attack and clears
             * Attacking after the return animation. Retail SequenceCardFinishAttack
             * (ActionEndAttack) plays card_attack AGAIN: emitting both produces
             * the second lunge seen after damage in the Bridge10 runtime log. */

            Plugin.Log("combat resolved: actorUid=" + attacker.NetUid.Id
                + " actorHealth=" + attacker.CurrentHealth
                + " targetUid=" + target.NetUid.Id
                + " targetHealth=" + target.CurrentHealth);

            CheckForRuneBreaks(state);
            if (target.HasSubtype(CardSubtype.Avatar) &&
                state.PendingProphecyPlayerIndex == (playerIndex == 0 ? 1 : 0))
            {
                state.PausedAttackVisualUid = attacker.NetUid.Id;
                state.PausedAttackVisualHeld = true;
                state.PausedAttackVisualComplete = false;
                state.PausedAttackCompletionActor = attacker;
                state.PausedAttackCompletionTarget = target;
                Plugin.Log("avatar attack held for prophecy: attacker="
                    + playerIndex + " defender="
                    + state.PendingProphecyPlayerIndex + " actor="
                    + attacker.NetUid.Id);
            }
            else
                CompleteAttack(state, attacker, target);
            state.Simulation.HistoryLog.EndEvent();
            ResolveDyingNonAvatarCards(state);
            DrainAutomaticTriggerQueues(state, true);

            state.Simulation.HistoryLog.SendUnsentMessages(
                state.Clients,
                GetObserverClients(state.Simulation),
                HistoryLog.FullGameStateSendRule.Exclude);

            Plugin.Log((playerIndex == 0 ? "human" : "AI")
                + " attacked actor=" + attacker.CardId
                + " target=" + target.CardId);

            if (state.PendingProphecyPlayerIndex < 0)
                CheckForAvatarDeath(state);
            if (!IsGameFinished(state) && state.PendingProphecyPlayerIndex == 0)
                SendCommandList(state, 0);
            return true;
        }

        private static void ApplyThievesDenPilfer(HostState state, ServerCard attacker)
        {
            List<ServerCard> dens = state.Simulation.CardCatalog.GatherCardsInZone(
                delegate(ServerCard card)
                {
                    return VariantCardRules.CanonicalId(card.CardId) == 1659589269 &&
                        !card.Silenced && !card.IsDying();
                }, attacker.OwnerNetId, GameZoneName.SupportBoard);
            if (dens.Count == 0)
                return;

            PlayerState owner = state.Simulation.Players.Find(
                delegate(PlayerState player)
                {
                    return player.NetId.Id == attacker.OwnerNetId.Id;
                });
            if (owner == null)
                return;

            foreach (ServerCard den in dens)
            {
                SPTESL.Enchantment buff = new SPTESL.Enchantment {
                    PermanentExpiresOnLeavingBoard = true
                };
                buff.AttributeModifications.Add(new EnchantmentAttributeModification(
                    EnchantmentAttributeModificationType.Add, AttributeType.Attack, 1));
                buff.AttributeModifications.Add(new EnchantmentAttributeModification(
                    EnchantmentAttributeModificationType.Add, AttributeType.MaxHealth, 1));
                CardActions.Buff.StaticExecute(
                    TriggerState(state), den, new List<ServerCard> { attacker },
                    buff, owner, den.FindOverrideOrDefaultAssetHash(CardFxAction.Buff),
                    false);
                Plugin.Log("thieves den pilfer: support=" + den.NetUid.Id
                    + " creature=" + attacker.NetUid.Id);
            }
        }

        private static bool HasActiveThievesDen(HostState state, ServerCard card)
        {
            if (state == null || card == null)
                return false;
            List<ServerCard> dens = state.Simulation.CardCatalog.GatherCardsInZone(
                delegate(ServerCard candidate)
                {
                    return VariantCardRules.CanonicalId(candidate.CardId) == 1659589269 &&
                        !candidate.Silenced && !candidate.IsDying();
                }, card.OwnerNetId, GameZoneName.SupportBoard);
            return dens.Count > 0;
        }

        private static bool HasMasterOfThievesExtraAttack(
            HostState state, ServerCard attacker)
        {
            if (attacker == null)
                return false;
            if (VariantCardRules.CanonicalId(attacker.CardId) == 1046549666)
                return false;
            bool hasPilfer = false;
            SPTESL.Card retailCard = GameManager.Instance.Config.FindCardById(attacker.CardId);
            if (retailCard != null && retailCard.MechanicsEntries != null)
                for (int i = 0; i < retailCard.MechanicsEntries.Count; ++i)
                    if (retailCard.MechanicsEntries[i].MechanicType == UIGameMechanicTypes.Pilfer)
                    { hasPilfer = true; break; }
            hasPilfer = hasPilfer || HasActiveThievesDen(state, attacker);
            Plugin.Log("master debug: attacker card=" + attacker.CardId + " uid="
                + attacker.NetUid.Id + " zone=" + attacker.CurrentGameZoneName
                + " pilfer=" + hasPilfer + " retailDesc="
                + (retailCard == null ? "null" : retailCard.Description));
            if (!hasPilfer)
                return false;
            List<ServerCard> masters = state.Simulation.CardCatalog.GatherCardsInZone(
                c => VariantCardRules.CanonicalId(c.CardId) == 1046549666 && !c.IsDying(),
                attacker.OwnerNetId, GameZoneName.CreatureBoard);
            int usedExtraAttacks;
            state.MasterOfThievesExtraAttacksUsed.TryGetValue(
                attacker.NetUid.Id, out usedExtraAttacks);
            Plugin.Log("master debug: active masters owner=" + attacker.OwnerNetId.Id
                + " count=" + masters.Count + " extraUsed=" + usedExtraAttacks);
            return usedExtraAttacks < masters.Count;
        }

        private static void CompleteAttack(
            HostState state, ServerCard attacker, ServerCard target)
        {
            int coverBefore = attacker.QueryAttribute(AttributeType.Cover).Value;
            int rally = attacker.QueryAttribute(AttributeType.Rally).Value;
            attacker.IncrementActionCount();
            /* Retail Cover created by Shadow and card effects carries
             * ExpiresOnAttack. Cull it for both human and AI attacks at the
             * authoritative completion point, including Charge attacks made
             * on the turn the creature entered play. */
            attacker.CullExpiredEnchantments(
                CullEnchantmentReason.OnAttack,
                TriggerState(state));
            Plugin.Log("attack completion attributes: card=" + attacker.CardId
                + " uid=" + attacker.NetUid.Id + " charge="
                + attacker.QueryAttribute(AttributeType.Charge).Value
                + " rally=" + rally + " cover=" + coverBefore + "->"
                + attacker.QueryAttribute(AttributeType.Cover).Value);
            /* PrepareRally performs the complete buff and emits ActionBuff.
             * Running CardActions.Rally here would apply the same Rally a
             * second time after combat. */
            SharedStatFamilyRules.OnAttackCompleted(TriggerState(state), attacker);
            ServerEffectUtilities.TriggerEffectEvent(
                TriggerState(state),
                new ServerEffectUtilities.TriggerEvent(
                    EffectTriggerType.WhenCardAttackComplete,
                    attacker,
                    target,
                    state.Simulation.Players));
            /* The attack-complete trigger and any continuous aura refresh it
             * causes are part of this attack. Drain while EventAttack is open
             * so ActionFullCardRefresh remains inside a valid history event
             * in both ordinary and prophecy-paused attack paths. */
            DrainAutomaticTriggerQueues(state, false);
        }

        private static void ExecuteRetailRallyAction(
            HostState state, ServerCard attacker, ServerCard target,
            ICardAction action)
        {
            PlayerState owner = null;
            PlayerState opponent = null;
            for (int i = 0; i < state.Simulation.Players.Count; ++i)
            {
                PlayerState candidate = state.Simulation.Players[i];
                if (candidate.NetId == attacker.OwnerNetId)
                    owner = candidate;
                else
                    opponent = candidate;
            }
            if (Object.ReferenceEquals(owner, null))
                return;

            List<ServerCard> handCreatures =
                state.Simulation.CardCatalog.GatherCardsInZone(
                    delegate(ServerCard candidate) {
                        return candidate.HasSubtype(CardSubtype.Creature);
                    }, owner.NetId, GameZoneName.Hand);
            ServerCard rallyTarget = null;
            if (handCreatures.Count > 0)
            {
                int index = state.StartInfo.RandomNumberGenerator.GenerateInRange(
                    0, handCreatures.Count - 1);
                rallyTarget = handCreatures[index];
            }
            if (Object.ReferenceEquals(rallyTarget, null) ||
                rallyTarget.CurrentGameZoneName != GameZoneName.Hand ||
                rallyTarget.OwnerNetId != owner.NetId)
            {
                Plugin.Log("rally prepare skipped: attacker="
                    + attacker.CardId + " uid=" + attacker.NetUid.Id
                    + " reason=no-friendly-creature-in-hand");
                return;
            }

            List<ServerCard> targets = new List<ServerCard>();
            targets.Add(rallyTarget);
            CardConditionalArguments cards = new CardConditionalArguments(
                state.Simulation.CardCatalog, attacker, attacker, target,
                rallyTarget, null, new LaneIdentifier());
            PlayerConditionalArguments players = new PlayerConditionalArguments(
                state.Simulation.CardCatalog, owner, owner, null, null, opponent);
            TriggerConditionalArguments trigger = new TriggerConditionalArguments(
                0, AttributeType.None);
            int assetHash = attacker.FindOverrideOrDefaultAssetHash(CardFxAction.Rally);
            action.Execute(new CardActionArguments(
                TriggerState(state), targets, cards, players, trigger,
                assetHash, assetHash < 0));
            Plugin.Log("rally prepare: attacker=" + attacker.CardId
                + " uid=" + attacker.NetUid.Id + " owner=" + owner.NetId.Id
                + " handTarget=" + rallyTarget.CardId
                + " handUid=" + rallyTarget.NetUid.Id + " fx=" + assetHash);
        }

        private static void CheckForRuneBreaks(HostState state)
        {
            int i;
            for (i = 0; i < state.Simulation.Players.Count; ++i)
                CheckForRuneBreaks(state, state.Simulation.Players[i]);
        }

        private static void CheckForRuneBreaks(
            HostState state,
            PlayerState player)
        {
            ServerCard avatar = state.Simulation.CardCatalog.FindCardInDynamicZone(
                delegate(ServerCard card) { return card.OwnerNetId == player.NetId; },
                player.NetId,
                GameZoneName.AvatarBoard);
            if (Object.ReferenceEquals(avatar, null))
                return;

            int runeHealth = player.QueryAttribute(AttributeType.RuneHealth).Value;
            int numRunes = player.QueryAttribute(AttributeType.NumRunes).Value;
            if (runeHealth <= 0 || numRunes <= 0)
                return;

            ServerEffectUtilities.TriggerGameState triggerState = TriggerState(state);
            while (numRunes > 0 && avatar.CurrentHealth <= numRunes * runeHealth)
            {
                state.Simulation.HistoryLog.StartEvent(
                    CcgRpc.HistoryType.EventRuneBreak,
                    player.NetId,
                    avatar.NetUid,
                    CardNetUid.GetInvalid(),
                    -1);

                numRunes -= 1;
                player.SetAttribute(AttributeType.NumRunes, numRunes);
                player.ModifyAttribute(AttributeType.NumRunesLost, 1);

                Plugin.Log("rune break: player=" + player.NetId.Id
                    + " health=" + avatar.CurrentHealth
                    + " runes=" + (numRunes + 1) + "->" + numRunes
                    + " threshold=" + ((numRunes + 1) * runeHealth));

                HistoryItem runeAttribute = new HistoryItem();
                runeAttribute.Type = CcgRpc.HistoryType.ActionSetPlayerAttribute;
                runeAttribute.AssetHash = -1;
                runeAttribute.ActingPlayerInstanceId = player.NetId.Id;
                runeAttribute.PlayerAttributeName = (int)AttributeType.NumRunesLost;
                runeAttribute.PlayerAttributeValue =
                    player.QueryAttribute(AttributeType.NumRunesLost).Value;
                state.Simulation.HistoryLog.AddInstant(runeAttribute);

                bool prophecyDecisionPending = false;
                int runeHeal = avatar.QueryAttribute(AttributeType.GainHealthFromRuneBreak).Value;
                if (runeHeal > 0)
                {
                    CardActions.Heal.StaticExecute(
                        triggerState, avatar, new List<ServerCard> { avatar },
                        runeHeal, -1, false, false);
                    ServerEffectUtilities.TriggerEffectEvent(
                        triggerState,
                        new ServerEffectUtilities.TriggerEvent(
                            EffectTriggerType.WhenHealthGainedOffRuneBreak,
                            avatar,
                            avatar,
                            state.Simulation.Players));
                }
                else if (player.QueryAttribute(AttributeType.BlockDrawsFromRuneBreak).Value <= 0)
                {
                    ServerCard drawnCard = DrawRuneBreakCard(
                        state, triggerState, player, avatar);
                    if (!Object.ReferenceEquals(drawnCard, null) &&
                        drawnCard.CurrentGameZoneName == GameZoneName.Prophecy)
                    {
                        player.ModifyAttribute(
                            AttributeType.PropheciesFromRuneBreak, 1);
                        ServerEffectUtilities.TriggerEffectEvent(
                            triggerState,
                            new ServerEffectUtilities.TriggerEvent(
                                EffectTriggerType.WhenProphecyDrawnOffRuneBreak,
                                drawnCard,
                                avatar,
                                state.Simulation.Players));

                        state.PendingProphecyPlayerIndex =
                            state.Simulation.Players.IndexOf(player);
                        prophecyDecisionPending = true;
                    }
                }

                ServerEffectUtilities.TriggerEffectEvent(
                    triggerState,
                    new ServerEffectUtilities.TriggerEvent(
                        EffectTriggerType.WhenCardBreaksRune,
                        avatar,
                        avatar,
                        state.Simulation.Players));
                ServerEffectUtilities.TriggerEffectEvent(
                    triggerState,
                    new ServerEffectUtilities.TriggerEvent(
                        EffectTriggerType.WhenBeastFormChange,
                        avatar,
                        avatar,
                        state.Simulation.Players));

                state.Simulation.HistoryLog.EndEvent();

                if (prophecyDecisionPending)
                    return;
            }
        }

        private static ServerCard DrawRuneBreakCard(
            HostState state,
            ServerEffectUtilities.TriggerGameState triggerState,
            PlayerState player,
            ServerCard avatar)
        {
            List<ServerCard> deck = state.Simulation.CardCatalog.GatherCardsInZone(
                delegate(ServerCard candidate) { return true; },
                player.NetId,
                GameZoneName.Deck);

            if (deck.Count == 0)
            {
                DrawCardsWithFallback(
                    state,
                    triggerState,
                    player,
                    1,
                    CcgRpc.HistoryType.ActionMoveCards,
                    "rune-break-empty-deck");
                return null;
            }

            ServerCard card = deck[0];
            if (card.QueryAttribute(AttributeType.Prophecy).Value <= 0)
            {
                /* The retail DrawCards body is stripped in this build and is
                 * already known to return zero during mulligan/turn draws.  A
                 * rune draw must use the same verified Deck -> Hand fallback or
                 * the rune can break visually without producing its card. */
                List<ServerCard> drawn = DrawCardsWithFallback(
                    state,
                    triggerState,
                    player,
                    1,
                    CcgRpc.HistoryType.ActionMoveCards,
                    "rune-break");
                return drawn.Count > 0 ? drawn[0] : null;
            }

            GameZoneOperationResult move = ServerCardCatalog.MoveCard(
                triggerState,
                new GameZoneId(player.NetId, GameZoneType.Deck),
                new GameZoneId(player.NetId, GameZoneType.Prophecy),
                card.InstanceId,
                0,
                CardInsertionType.Bottom,
                ServerCardCatalog.MoveOperationType.DefaultDraw);
            if (move != GameZoneOperationResult.Succeeded)
                return null;

            List<ServerCard> prophecy = new List<ServerCard>();
            prophecy.Add(card);
            PlayerActions.DrawCards.LogDrawnCards(
                CcgRpc.HistoryType.ActionMoveCards,
                state.Simulation.HistoryLog,
                state.Simulation.Players,
                state.Simulation.CardCatalog,
                prophecy,
                card.FindOverrideOrDefaultAssetHash(CardFxAction.DrawProphecy));
            return card;
        }

        private static void ResolveHumanProphecyToHand(
            HostState state,
            CcgRpc.Decision decision)
        {
            PlayerState player = state.Simulation.Players[0];
            ServerCard card = state.Simulation.CardCatalog.FindDynamicCard(
                new CardNetUid(decision.ActorCardNetUid));
            if (Object.ReferenceEquals(card, null) ||
                card.OwnerNetId.Id != player.NetId.Id ||
                card.CurrentGameZoneName != GameZoneName.Prophecy)
            {
                SendCommandList(state, 0);
                return;
            }

            state.Simulation.HistoryLog.StartEvent(
                CcgRpc.HistoryType.EventDeclineProphecy,
                player.NetId,
                card.NetUid,
                CardNetUid.GetInvalid(),
                -1);
            CardActions.ReturnToHand.StaticExecute(
                TriggerState(state),
                card,
                card,
                player,
                false,
                true,
                false,
                card.FindOverrideOrDefaultAssetHash(CardFxAction.ReturnToHand),
                false,
                false);
            state.Simulation.HistoryLog.EndEvent();
            state.PendingProphecyPlayerIndex = -1;

            CheckForRuneBreaks(state, player);
            DrainAutomaticTriggerQueues(state, true);
            state.Simulation.HistoryLog.SendUnsentMessages(
                state.Clients,
                GetObserverClients(state.Simulation),
                HistoryLog.FullGameStateSendRule.Exclude);
            ResumeAfterProphecyDecision(state);
        }

        private static void ResumeAfterProphecyDecision(HostState state)
        {
            if (state.PendingProphecyPlayerIndex == 0)
            {
                SendCommandList(state, 0);
                return;
            }

            if (state.ActivePlayerIndex == 0 &&
                state.PausedAttackVisualUid != 0)
            {
                state.PausedAttackHumanPromptSent = false;
                return;
            }

            if (state.ActivePlayerIndex == 1 && !IsGameFinished(state))
            {
                state.AiActionPending = true;
                state.AiActionFrame = Time.frameCount + 1;
                return;
            }

            if (state.ActivePlayerIndex == 0 && !IsGameFinished(state))
                SendCommandList(state, 0);
        }

        private static void RunAiProphecyIfNeeded(HostState state)
        {
            if (!state.GameInitialized || state.PendingProphecyPlayerIndex != 1)
                return;

            if (state.PendingCustomAction != PendingCustomActionKind.None &&
                state.PendingCustomPlayerIndex == 1)
            {
                ResolveAiPendingCustomAction(state);
                return;
            }

            PlayerState ai = state.Simulation.Players[1];
            List<ServerCard> cards = state.Simulation.CardCatalog.GatherCardsInZone(
                delegate(ServerCard candidate) { return true; },
                ai.NetId, GameZoneName.Prophecy);
            if (cards.Count == 0)
            {
                state.PendingProphecyPlayerIndex = -1;
                ResumeAfterProphecyDecision(state);
                return;
            }

            ServerCard card = cards[0];
            CcgRpc.Decision play;
            bool playable = BuildFirstPlayableDecision(
                state, 1, GameZoneName.Prophecy, true, out play);
            bool played = playable && TryPlayCard(state, 1, play);
            if (played && state.PendingCustomAction != PendingCustomActionKind.None &&
                state.PendingCustomPlayerIndex == 1)
            {
                if (!ResolveAiPendingCustomAction(state))
                    return;
                Plugin.Log("AI prophecy played with custom choice: card="
                    + card.CardId);
                return;
            }

            if (!played)
            {
                state.Simulation.HistoryLog.StartEvent(
                    CcgRpc.HistoryType.EventDeclineProphecy,
                    ai.NetId, card.NetUid, CardNetUid.GetInvalid(), -1);
                CardActions.ReturnToHand.StaticExecute(
                    TriggerState(state), card, card, ai,
                    false, true, false,
                    card.FindOverrideOrDefaultAssetHash(
                        CardFxAction.ActionMoveCard),
                    false, false);
                state.Simulation.HistoryLog.EndEvent();
                Plugin.Log("AI prophecy declined to hand: card=" + card.CardId);
            }
            else
                Plugin.Log("AI prophecy played: card=" + card.CardId);

            state.PendingProphecyPlayerIndex = -1;
            CheckForRuneBreaks(state, ai);
            DrainAutomaticTriggerQueues(state, true);
            if (state.PendingProphecyPlayerIndex < 0)
                CheckForAvatarDeath(state);
            state.Simulation.HistoryLog.SendUnsentMessages(
                state.Clients, GetObserverClients(state.Simulation),
                HistoryLog.FullGameStateSendRule.Exclude);
            if (state.PendingProphecyPlayerIndex < 0)
                ResumeAfterProphecyDecision(state);
        }

        private static void ResolveDyingNonAvatarCards(HostState state)
        {
            ServerEffectUtilities.TriggerGameState triggerState = TriggerState(state);
            List<ServerCard> movedCards = new List<ServerCard>();
            bool standaloneHistoryEvent = false;
            bool moved;
            int guard = 0;
            do
            {
                moved = false;
                List<ServerCard> cards = state.Simulation.CardCatalog.GatherInPlayCardList();
                int i;
                for (i = 0; i < cards.Count; ++i)
                {
                    ServerCard card = cards[i];
                    if (Object.ReferenceEquals(card, null) ||
                        card.HasSubtype(CardSubtype.Avatar) ||
                        !card.ShouldBeDiscarded(state.Simulation.CardCatalog))
                        continue;

                    /* HistoryLog.AddInstant discards/logs any state update
                     * outside an open server event. Several decision paths
                     * resolve deaths after closing their attack/play event;
                     * create a native EventTrigger scope in that case so the
                     * cemetery and RemoveFromGame responses reach the client. */
                    if (!standaloneHistoryEvent &&
                        !HasOpenHistoryEvent(state.Simulation.HistoryLog))
                    {
                        state.Simulation.HistoryLog.StartEvent(
                            CcgRpc.HistoryType.EventTrigger,
                            card.OwnerNetId,
                            card.NetUid,
                            CardNetUid.GetInvalid(),
                            -1);
                        standaloneHistoryEvent = true;
                    }

                    Plugin.Log("death resolution: moving card=" + card.CardId
                        + " uid=" + card.NetUid.Id
                        + " owner=" + card.OwnerNetId.Id
                        + " from=" + card.CurrentGameZoneName
                        + " lane=" + card.CurrentLaneIndex
                        + " health=" + card.CurrentHealth);
                    /* Some retail token effects listen for WhenCardSlays on
                     * the creature that was killed (for example Sweet Roll).
                     * Slay (the killer's keyword event) is emitted by damage,
                     * but the paired victim event must be dispatched while
                     * the killed card is still available to the trigger scan. */
                    if (card.KilledByCard != null &&
                        card.TriggeredEffects.Exists(delegate(TriggeredEffect effect) {
                            return effect != null && effect.TriggerType ==
                                EffectTriggerType.WhenCardSlays;
                        }))
                    {
                        ServerEffectUtilities.TriggerEffectEvent(
                            triggerState,
                            new ServerEffectUtilities.TriggerEvent(
                                EffectTriggerType.WhenCardSlays,
                                card.KilledByCard,
                                card,
                                state.Simulation.Players));
                    }
                    ServerEffectUtilities.TriggerEffectEvent(
                        triggerState,
                        new ServerEffectUtilities.TriggerEvent(
                            EffectTriggerType.BeforeCardLeavesBoard,
                            card,
                            state.Simulation.Players));
                    ObservedCardBatch02.CaptureDeathTarget(triggerState,card);
                    ConsumeAndLastGaspRules.OnDying(triggerState, card);
                    ServerCardCatalog.MoveToDeathrattle(triggerState, card);
                    ServerEffectUtilities.TriggerEffectEvent(
                        triggerState,
                        new ServerEffectUtilities.TriggerEvent(
                            EffectTriggerType.WhenCardLeavesBoard,
                            card,
                            state.Simulation.Players));
                    ServerEffectUtilities.TriggerEffectEvent(
                        triggerState,
                        new ServerEffectUtilities.TriggerEvent(
                            EffectTriggerType.WhenCardDies,
                            card,
                            state.Simulation.Players));
                    ServerEffectUtilities.TriggerEffectEvent(
                        triggerState,
                        new ServerEffectUtilities.TriggerEvent(
                            EffectTriggerType.WhenCardMoveComplete,
                            card,
                            card,
                            state.Simulation.Players));
                    movedCards.Add(card);
                    moved = true;
                }
                guard += 1;
            } while (moved && guard < 8);

            state.Simulation.CardCatalog.FlushDeathrattleZone(triggerState);
            if (movedCards.Count > 0)
            {
                /* MoveToDeathrattle creates the canonical cemetery entry, then
                 * FlushDeathrattleZone purges the original board entity.  The
                 * retail client needs an ActionMoveCards delta for that original
                 * UID as well; ActionLogCardDeath only marks it dead and does
                 * not detach/recycle its lane Card3D. */
                HistoryItem removeDeadCards = new HistoryItem();
                removeDeadCards.Type = CcgRpc.HistoryType.ActionMoveCards;
                /* The Last Gasp damage FX ends at impact. When Brutal
                 * Ashlander's damage kills its target, the subsequent board
                 * removal must carry the retail death FX as well. Scope this
                 * to the victim identified by its native lethal history. */
                removeDeadCards.AssetHash = movedCards.Count == 1 &&
                    WasKilledByBrutalAshlander(state, movedCards[0])
                    ? -521991121 : -1;
                int deadIndex;
                for (deadIndex = 0;
                     deadIndex < movedCards.Count;
                     ++deadIndex)
                {
                    ServerCard deadCard = movedCards[deadIndex];
                    HistoryCard moveDelta = deadCard.HistoryCreate_FullStateCard(
                        state.Simulation.CardCatalog);
                    moveDelta.PreviousGameZoneType =
                        (int)GameZoneType.Deathrattle;
                    removeDeadCards.AffectedCardsArray.Add(moveDelta);
                }
                state.Simulation.HistoryLog.AddInstant(removeDeadCards);
            }
            if (standaloneHistoryEvent)
            {
                state.Simulation.HistoryLog.EndEvent();
                Plugin.Log("death resolution protocol event closed: moved="
                    + movedCards.Count);
            }
            int completedIndex;
            for (completedIndex = 0;
                 completedIndex < movedCards.Count;
                 ++completedIndex)
            {
                ServerCard card = movedCards[completedIndex];
                Plugin.Log("death resolution complete: card=" + card.CardId
                    + " uid=" + card.NetUid.Id
                    + " zone=" + card.CurrentGameZoneName
                    + " zoneType=" + card.CurrentGameZoneType
                    + " health=" + card.CurrentHealth);
            }
        }

        private static readonly FieldInfo HistoryMessagesField = typeof(HistoryLog).GetField(
            "_messageLog", BindingFlags.Instance | BindingFlags.NonPublic);

        private static bool WasKilledByBrutalAshlander(
            HostState state, ServerCard victim)
        {
            List<HistoryItem> history = HistoryMessagesField.GetValue(
                state.Simulation.HistoryLog) as List<HistoryItem>;
            if (Object.ReferenceEquals(history, null))
                return false;
            List<ServerCard> cards = state.Simulation.CardCatalog.GatherDynamicCardList();
            for (int i = history.Count - 1; i >= 0; --i)
            {
                HistoryItem item = history[i];
                if (item.Type != CcgRpc.HistoryType.ActionLogCardDeath ||
                    !item.AffectedCardsArray.Any(delegate(HistoryCard card)
                    {
                        return card.CardNetUid == victim.NetUid.Id;
                    }))
                    continue;
                ServerCard actor = cards.Find(delegate(ServerCard card)
                {
                    return card.NetUid.Id == item.ActorCardNetUid;
                });
                return !Object.ReferenceEquals(actor, null) &&
                    VariantCardRules.CanonicalId(actor.CardId) == -938950495;
            }
            return false;
        }

        private static void StartTriggerEventLogWithMechanicFx(
            HistoryLog log, ref ServerEffectUtilities.TriggerContext context)
        {
            ServerEffectUtilities.StartTriggerEventLog(log, ref context);
            // SequenceEventTrigger plays the original keyword FX only when the
            // header carries its mechanic. The retail server helper omits Expertise.
            if (log == null || HistoryMessagesField == null ||
                context.TriggerEvent.Type != EffectTriggerType.Expertise) return;
            var messages = (List<CcgRpc.HistoryItem>)HistoryMessagesField.GetValue(log);
            if (messages.Count == 0 || Object.ReferenceEquals(context.EffectOwner, null)) return;
            CcgRpc.HistoryItem header = null;
            for (int i = messages.Count - 1; i >= 0; --i)
            {
                var candidate = messages[i];
                if (candidate.Type == CcgRpc.HistoryType.EventTrigger &&
                    candidate.EffectTriggerType == (int)EffectTriggerType.Expertise &&
                    candidate.ActorCardNetUid == context.EffectOwner.NetUid.Id)
                { header = candidate; break; }
            }
            if (header != null &&
                header.MechanicType != (int)UIGameMechanicTypes.Expertise)
            {
                header.MechanicType = (int)UIGameMechanicTypes.Expertise;
                Plugin.Log("Expertise native mechanic FX: actor=" + header.ActorCardNetUid
                    + " owner=" + header.ActingPlayerInstanceId);
            }
        }

        private static void RepairExpertiseMechanicHeaders(
            HostState state, int firstHistoryIndex)
        {
            List<HistoryItem> messages = HistoryMessagesField.GetValue(
                state.Simulation.HistoryLog) as List<HistoryItem>;
            if (Object.ReferenceEquals(messages, null)) return;
            List<ServerCard> cards = state.Simulation.CardCatalog.GatherDynamicCardList();
            for (int i = Math.Max(0, firstHistoryIndex); i < messages.Count; ++i)
            {
                HistoryItem header = messages[i];
                if (header.Type != CcgRpc.HistoryType.EventTrigger ||
                    header.EffectTriggerType != (int)EffectTriggerType.Expertise ||
                    header.MechanicType == (int)UIGameMechanicTypes.Expertise) continue;
                ServerCard source = cards.Find(c => c.NetUid.Id == header.ActorCardNetUid);
                if (Object.ReferenceEquals(source, null) ||
                    !source.TriggeredEffects.Exists(t => t.TriggerType == EffectTriggerType.Expertise)) continue;
                header.MechanicType = (int)UIGameMechanicTypes.Expertise;
            }
        }

        private static bool HasOpenHistoryEvent(HistoryLog historyLog)
        {
            if (Object.ReferenceEquals(historyLog, null) ||
                Object.ReferenceEquals(HistoryOpenHeaderCountField, null))
                return false;

            try
            {
                return (int)HistoryOpenHeaderCountField.GetValue(historyLog) > 0;
            }
            catch (Exception ex)
            {
                Plugin.Warn("could not inspect retail HistoryLog event scope: "
                    + ex.Message);
                return false;
            }
        }

        private static void AddAttackPhaseAction(
            HostState state,
            CcgRpc.HistoryType type,
            ServerCard attacker,
            ServerCard target)
        {
            CcgRpc.HistoryItem item = new CcgRpc.HistoryItem();
            item.Type = type;
            item.ActorCardNetUid = attacker.NetUid.Id;
            item.TargetCardNetUid = target.NetUid.Id;
            state.Simulation.HistoryLog.AddInstant(item);
        }

        private static List<ServerCard> GatherBasicAttackTargets(
            HostState state,
            PlayerState player,
            PlayerState opponent,
            ServerCard attacker)
        {
            List<ServerCard> result = new List<ServerCard>();
            if (player.QueryAttribute(AttributeType.PlayerCannotAttack).Value > 0 ||
                (!attacker.CanCardAttack(false) &&
                 !HasMasterOfThievesExtraAttack(state, attacker)))
                return result;

            List<ServerCard> creatures = state.Simulation.CardCatalog.GatherCardsInZone(
                delegate(ServerCard card) { return true; },
                opponent.NetId,
                GameZoneName.CreatureBoard);
            List<ServerCard> legalCreatures = new List<ServerCard>();
            List<ServerCard> guards = new List<ServerCard>();
            bool canCrossLane =
                attacker.QueryAttribute(AttributeType.CanAttackOtherLane).Value > 0;
            bool ignoreGuard = attacker.CanIgnoreMustBeAttackedFirst();

            int i;
            for (i = 0; i < creatures.Count; ++i)
            {
                ServerCard card = creatures[i];
                if (Object.ReferenceEquals(card, null))
                    continue;
                if (!card.CanBeExplicitlyTargetedByOpponentAttack(attacker))
                    continue;

                bool sameLane = attacker.CurrentLaneIndex == card.CurrentLaneIndex;
                bool reachable = canCrossLane || sameLane ||
                    card.CanBeAttackedFromAnyLane() || card.GuardsBothLanes();
                if (!reachable)
                    continue;

                legalCreatures.Add(card);

                /* Guard/Taunt only blocks attacks when that guard can actually
                 * intercept this attacker. GuardsBothLanes is the retail
                 * exception for a guard outside the attacker's lane. */
                bool guardBlocks = card.MustBeAttackedFirst() && !ignoreGuard &&
                    (sameLane || card.GuardsBothLanes() || canCrossLane);
                if (guardBlocks)
                    guards.Add(card);
            }

            if (guards.Count > 0)
            {
                Plugin.Log("attack targets actorUid=" + attacker.NetUid.Id
                    + " lane=" + attacker.CurrentLaneIndex
                    + " GUARD_BLOCK count=" + guards.Count
                    + " firstGuardUid=" + guards[0].NetUid.Id
                    + " firstGuardLane=" + guards[0].CurrentLaneIndex);
                result.AddRange(guards);
                return result;
            }

            result.AddRange(legalCreatures);

            if (attacker.QueryAttribute(AttributeType.CanOnlyAttackCreatures).Value <= 0)
            {
                ServerCard avatar = state.Simulation.CardCatalog.FindCardInDynamicZone(
                    delegate(ServerCard card) { return true; },
                    opponent.NetId,
                    GameZoneName.AvatarBoard);
                if (!Object.ReferenceEquals(avatar, null) &&
                    avatar.CanBeExplicitlyTargetedByOpponentAttack(attacker))
                    result.Add(avatar);
            }
            return result;
        }

        private static void CheckForAvatarDeath(HostState state)
        {
            if (state.MechanicalHeartRescueInProgress)
                return;

            ResolveMechanicalHeartRescue(state, 0);
            ResolveMechanicalHeartRescue(state, 1);
            List<HistoryItem> deathCheckHistory = HistoryMessagesField.GetValue(
                state.Simulation.HistoryLog) as List<HistoryItem>;
            if (!Object.ReferenceEquals(deathCheckHistory, null))
                state.LastMechanicalHeartHistoryIndex = deathCheckHistory.Count;

            bool firstLost = AvatarIsDead(state, 0) || state.Simulation.Players[0].ForceLoss;
            bool secondLost = AvatarIsDead(state, 1) || state.Simulation.Players[1].ForceLoss;
            if (HasExaltedImmortality(state, 0)) firstLost = false;
            if (HasExaltedImmortality(state, 1)) secondLost = false;
            if (!firstLost && !secondLost)
                return;

            Plugin.RecordLocalMatchResult(firstLost, secondLost);

            if (Object.ReferenceEquals(EndGameMethod, null))
            {
                Plugin.Warn("Simulation.EndGame not found; cannot finish local match");
                return;
            }

            EndGameMethod.Invoke(
                state.Simulation,
                new object[] { firstLost, secondLost });
            state.Simulation.HistoryLog.SendUnsentMessages(
                state.Clients,
                GetObserverClients(state.Simulation),
                HistoryLog.FullGameStateSendRule.Include);
            Plugin.Log("local match ended: firstLost=" + firstLost
                + " secondLost=" + secondLost);
        }

        private static bool HasExaltedImmortality(HostState state, int playerIndex)
        {
            PlayerState player = state.Simulation.Players[playerIndex];
            if (player.QueryAttribute(AttributeType.Immortal).Value > 0)
                return true;
            List<ServerCard> creatures = state.Simulation.CardCatalog.GatherCardsInZone(
                delegate(ServerCard card) {
                    return card.OwnerNetId == player.NetId && !card.Silenced;
                }, player.NetId, GameZoneName.CreatureBoard);
            if (creatures.Count == 0)
                return false;

            bool vivecInPlay = creatures.Exists(delegate(ServerCard card) {
                return VariantCardRules.CanonicalId(card.CardId) == -1597653207;
            });
            bool exaltedCreatureInPlay = creatures.Exists(delegate(ServerCard card) {
                return card.QueryAttribute(AttributeType.Exalted).Value > 0;
            });
            return vivecInPlay && exaltedCreatureInPlay;
        }

        private static void ResolveMechanicalHeartRescue(
            HostState state, int playerIndex)
        {
            List<HistoryItem> history = HistoryMessagesField.GetValue(
                state.Simulation.HistoryLog) as List<HistoryItem>;
            HashSet<int> heartExitSources = new HashSet<int>();
            if (!Object.ReferenceEquals(history, null))
            {
                for (int i = Math.Max(0, state.LastMechanicalHeartHistoryIndex);
                     i < history.Count; ++i)
                {
                    HistoryItem item = history[i];
                    if (item.Type == CcgRpc.HistoryType.ActionPlayCustomFx &&
                        item.AssetHash == 2087980878)
                        heartExitSources.Add(item.ActorCardNetUid);
                }
            }
            PlayerState player = state.Simulation.Players[playerIndex];
            ServerCard avatar = state.Simulation.CardCatalog.FindCardInDynamicZone(
                delegate(ServerCard card) { return card.OwnerNetId == player.NetId; },
                player.NetId, GameZoneName.AvatarBoard);
            if (avatar == null)
                return;

            ServerCard heart = state.Simulation.CardCatalog.GatherCardsInZone(
                delegate(ServerCard card) {
                    return card.CardId == 130262328 && !card.Silenced;
                }, player.NetId, GameZoneName.SupportBoard).FirstOrDefault();
            if (heart == null)
                return;

            /* Native rescue may heal before this final defeat check. Its exit
             * FX proves that the lethal trigger fired; consume the support
             * even when the avatar is already back above zero. */
            if (avatar.CurrentHealth > 0)
            {
                if (heartExitSources.Contains(heart.NetUid.Id) &&
                    heart.CurrentGameZoneName == GameZoneName.SupportBoard)
                {
                    ServerCardCatalog.MoveToCemetery(TriggerState(state), heart);
                    Plugin.Log("Mechanical Heart consumed after native rescue: player="
                        + player.NetId.Id + " heart=" + heart.NetUid.Id);
                }
                return;
            }

            state.MechanicalHeartRescueInProgress = true;
            try
            {
                Plugin.Log("Mechanical Heart rescue begin: player=" + player.NetId.Id
                    + " health=" + avatar.CurrentHealth + " heart=" + heart.NetUid.Id);

                /* The damage/rune path already emits the retail activation and
                 * exit FX. Finish only the server state before EndGame; do not
                 * dispatch the event again or the client would play both twice. */
                if (avatar.CurrentHealth <= 0 &&
                    heart.CurrentGameZoneName == GameZoneName.SupportBoard)
                    CompleteMechanicalHeartRescue(state, player, avatar, heart);
            }
            finally
            {
                state.MechanicalHeartRescueInProgress = false;
            }
        }

        private static void CompleteMechanicalHeartRescue(
            HostState state, PlayerState player, ServerCard avatar, ServerCard heart)
        {
            HistoryLog history = state.Simulation.HistoryLog;
            history.StartEvent(CcgRpc.HistoryType.EventTrigger, player.NetId,
                heart.NetUid, avatar.NetUid,
                heart.FindOverrideOrDefaultAssetHash(CardFxAction.ActivateAbility));

            typeof(ServerCard).GetField("<CurrentHealth>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic).SetValue(avatar, 10);
            HistoryItem heal = new HistoryItem();
            heal.Type = CcgRpc.HistoryType.ActionHeal;
            heal.AssetHash = -1;
            heal.ActorCardNetUid = heart.NetUid.Id;
            HistoryCard healedAvatar = avatar.HistoryCreate_FullStateCard(
                state.Simulation.CardCatalog);
            healedAvatar.CurrentHealth = 10;
            healedAvatar.IsFullState = true;
            heal.AffectedCardsArray.Add(healedAvatar);
            history.AddInstant(heal);

            ServerCardCatalog.MoveToCemetery(TriggerState(state), heart);
            history.EndEvent();
            Plugin.Log("Mechanical Heart rescue complete: player=" + player.NetId.Id
                + " health=" + avatar.CurrentHealth
                + " runes=" + player.QueryAttribute(AttributeType.NumRunes).Value
                + " runesLost=" + player.QueryAttribute(AttributeType.NumRunesLost).Value
                + " heartZone=" + heart.CurrentGameZoneName);
        }

        private static bool AvatarIsDead(HostState state, int playerIndex)
        {
            PlayerState player = state.Simulation.Players[playerIndex];
            ServerCard avatar = state.Simulation.CardCatalog.FindCardInDynamicZone(
                delegate(ServerCard card) { return true; },
                player.NetId,
                GameZoneName.AvatarBoard);
            return !Object.ReferenceEquals(avatar, null) &&
                avatar.IsDyingFromHealthLoss();
        }

        private static bool IsGameFinished(HostState state)
        {
            return GetGameState(state.Simulation).GamePhase == GamePhase.Results;
        }

        private static void EndTurn(HostState state, int playerIndex)
        {
            if (IsGameFinished(state))
                return;

            PlayerState player = state.Simulation.Players[playerIndex];

            /* OnEndTurn may cull Freeze/Shackle and emit
             * ActionEnchantmentsExpired. Keep those instant actions inside the
             * retail turn-end event so the client receives the full-card
             * refresh and removes the chain visual. */
            state.Simulation.HistoryLog.StartEvent(
                CcgRpc.HistoryType.EventTurnEnd,
                player.NetId,
                CardNetUid.GetInvalid(),
                CardNetUid.GetInvalid(),
                -1);

            List<ServerCard> allCards = state.Simulation.CardCatalog.GatherDynamicCardList();
            List<HistoryItem> turnHistory = HistoryMessagesField.GetValue(
                state.Simulation.HistoryLog) as List<HistoryItem>;
            int turnHistoryStart = Object.ReferenceEquals(turnHistory, null)
                ? 0 : turnHistory.Count;
            ServerEffectUtilities.TriggerEffectEvent(
                TriggerState(state),
                new ServerEffectUtilities.TriggerEvent(
                    EffectTriggerType.WhenPlayerTurnEnds, player));
            SharedStatFamilyRules.OnTurnEnds(TriggerState(state), player);
            /* Resolve turn-end effects while their temporary enchantments are
             * still present. Blood Pact Messenger grants its avatar a turn-end
             * sacrifice trigger through an enchantment that OnEndTurn expires. */
            DrainAutomaticTriggerQueues(state, false);
            int c;
            for (c = 0; c < allCards.Count; ++c)
                allCards[c].OnEndTurn(player.NetId, TriggerState(state));
            ServerEffectUtilities.TriggerEffectEvent(
                TriggerState(state),
                new ServerEffectUtilities.TriggerEvent(
                    EffectTriggerType.WhenPlayerTurnEndsCleanup, player));
            ReferenceSummonRules.ExpireDiscounts(TriggerState(state));

            if (playerIndex != state.StartingPlayerIndex &&
                player.QueryAttribute(AttributeType.CanUseRing).Value != 0)
            {
                PlayerActions.SetAttribute.StaticExecute(
                    state.Simulation.HistoryLog,
                    player,
                    AttributeType.CanUseRing,
                    0);
            }

            /* Turn-end effects must remain under EventTurnEnd.  Draining them
             * after publishing that event hid ChangeLane/Damage/Heal from the
             * client, and a lethal result could then arrive before their FX. */
            DrainAutomaticTriggerQueues(state, false);
            RepairExpertiseMechanicHeaders(state, turnHistoryStart);
            state.Simulation.HistoryLog.EndEvent();
            ResolveDyingNonAvatarCards(state);
            state.Simulation.HistoryLog.SendUnsentMessages(
                state.Clients,
                GetObserverClients(state.Simulation),
                HistoryLog.FullGameStateSendRule.Exclude);

            /* Publish the complete effect batch before EndGame.  This lets
             * SequenceMoveCards and the retail damage/heal FX finish before
             * the result screen is queued. */
            CheckForAvatarDeath(state);
            if (IsGameFinished(state))
                return;

            int next = playerIndex == 0 ? 1 : 0;
            BeginTurn(state, next, false);

            if (IsGameFinished(state))
                return;

            if (next == 1)
            {
                state.AiActionPending = true;
                state.AiActionFrame = Time.frameCount + 18;
            }
            else
            {
                /* The retail loop emits a fresh CardCommandList whenever the
                 * human becomes the active player.  Without this, AI EndTurn
                 * leaves the board visually alive but input-locked. */
                SendCommandList(state, 0);
            }
        }

        private static void BeginTurn(
            HostState state,
            int playerIndex,
            bool firstTurn)
        {
            Simulation simulation = state.Simulation;
            PlayerState player = simulation.Players[playerIndex];
            state.MasterOfThievesExtraAttacksUsed.Clear();
            PlayerState opponent = simulation.Players[playerIndex == 0 ? 1 : 0];

            state.ActivePlayerIndex = playerIndex;
            state.AiResumeAttacks = false;
            state.PausedAttackVisualUid = 0;
            state.PausedAttackVisualHeld = false;
            state.PausedAttackVisualComplete = false;
            state.PausedAttackCompletionActor = null;
            state.PausedAttackCompletionTarget = null;
            state.PausedAttackCompletionDone = false;
            state.PausedAttackEndMarkerSent = false;
            state.PausedAttackHumanPromptSent = false;
            SetField(CurrentPlayerField, simulation, player, "CurrentPlayer");
            simulation.CurrentOpponents.Clear();
            simulation.CurrentOpponents.Add(opponent);

            int i;
            for (i = 0; i < simulation.Players.Count; ++i)
                simulation.Players[i].IsMyTurn = i == playerIndex;

            if (playerIndex == 0)
                state.HumanTurns += 1;
            else
                state.AiTurns += 1;

            /* OnStartTurn may cull temporary enchantments and emit
             * ActionEnchantmentsExpired. HistoryLog only accepts those instant
             * actions while an event is open, so the retail turn-start event
             * must begin before cards process their start-of-turn state. */
            simulation.HistoryLog.StartEvent(
                CcgRpc.HistoryType.EventTurnStart,
                player.NetId,
                CardNetUid.GetInvalid(),
                CardNetUid.GetInvalid(),
                -1);

            List<ServerCard> activeCards = simulation.CardCatalog.GatherDynamicCardList();
            int cardIndex;
            for (cardIndex = 0; cardIndex < activeCards.Count; ++cardIndex)
                activeCards[cardIndex].OnStartTurn(player.NetId, TriggerState(state));
            SharedStatFamilyRules.OnTurnStarts(TriggerState(state), player);
            ServerEffectUtilities.TriggerEffectEvent(
                TriggerState(state),
                new ServerEffectUtilities.TriggerEvent(
                    EffectTriggerType.WhenPlayerTurnStartsCleanup, player));

            int maxMana = player.QueryAttribute(AttributeType.MaxMana).Value;
            if (maxMana < 12)
                maxMana += 1;
            player.SetAttribute(AttributeType.MaxMana, maxMana);
            player.SetAttribute(AttributeType.Mana, maxMana);
            player.ModifyAttribute(AttributeType.TurnCount, 1);
            player.NumCardsPlayedThisTurn = 0;

            TurnState turn = new TurnState();
            turn.ActivePlayerIndex = playerIndex;
            turn.TurnPlayerIndex = playerIndex;
            turn.Phase = TurnPhase.PlayCards;
            turn.CustomUICards = new List<CustomDisplayCard>();

            Stack<TurnState> stack = GetTurnStack(simulation);
            stack.Clear();
            stack.Push(turn);

            GameState gameState = GetGameState(simulation);
            gameState.GamePhase = GamePhase.MainLoop;

            /* Keep both pieces of the AP widget inside EventTurnStart.
             * MaxMana is repaired at the client endpoint SetMaxAP(); current
             * mana uses the dedicated ActionSetMana -> SequenceSetMana path. */
            EmitPlayerAttribute(state, player, AttributeType.MaxMana);
            EmitManaState(state, player);

            if (playerIndex != state.StartingPlayerIndex)
            {
                int ringUses = player.QueryAttribute(AttributeType.RingUsesLeft).Value;
                /* Attribute 12 is what actually creates/updates the three
                 * pips in Board3DUI.  Attribute 13 only controls whether the
                 * ring can be clicked. */
                EmitPlayerAttribute(state, player, AttributeType.RingUsesLeft);
                PlayerActions.SetAttribute.StaticExecute(
                    simulation.HistoryLog,
                    player,
                    AttributeType.CanUseRing,
                    ringUses > 0 ? 1 : 0);
                Plugin.Log("ring turn state: player=" + player.NetId.Id
                    + " charges=" + ringUses
                    + " canUse=" + (ringUses > 0 ? 1 : 0));
            }

            if (!firstTurn)
            {
                int deckBefore = simulation.CardCatalog.FindCardCount(
                    GameZoneName.Deck, player.NetId);
                ServerCard avatarBefore = simulation.CardCatalog.FindCardInDynamicZone(
                    delegate(ServerCard candidate) { return true; },
                    player.NetId,
                    GameZoneName.AvatarBoard);
                int healthBefore = Object.ReferenceEquals(avatarBefore, null)
                    ? -1 : avatarBefore.CurrentHealth;
                int runesBefore = player.QueryAttribute(
                    AttributeType.NumRunes).Value;
                List<ServerCard> turnDrawn = DrawCardsWithFallback(
                    state,
                    TriggerState(state),
                    player,
                    1,
                    CcgRpc.HistoryType.ActionMoveCards,
                    "turn-draw");
                if (turnDrawn.Count > 0)
                {
                    PlayerState copier = simulation.Players[playerIndex == 0 ? 1 : 0];
                    bool hasRazumDar = simulation.CardCatalog.GatherCardsInZone(
                        c => c.CardId == -1906674453 && !c.IsDying(),
                        copier.NetId, GameZoneName.CreatureBoard).Count > 0;
                    if (hasRazumDar)
                    {
                        ServerCard copy = ServerCardCatalog.CreateCard(
                            TriggerState(state), turnDrawn[0].CardId, false,
                            GameZoneName.Hand, 0, CardInsertionType.Bottom,
                            copier.NetId, null, null);
                        Plugin.Log("Razum-Dar copied first opponent draw: source="
                            + turnDrawn[0].CardId + " copy="
                            + (copy == null ? 0 : copy.NetUid.Id));
                    }
                }
                ServerCard avatarAfter = simulation.CardCatalog.FindCardInDynamicZone(
                    delegate(ServerCard candidate) { return true; },
                    player.NetId,
                    GameZoneName.AvatarBoard);
                Plugin.Log(
                    "turn draw: player=" + player.NetId.Id
                    + " drawn=" + turnDrawn.Count
                    + " hand="
                    + simulation.CardCatalog.FindCardCount(
                        GameZoneName.Hand, player.NetId)
                    + " deck=" + deckBefore + "->"
                    + simulation.CardCatalog.FindCardCount(
                        GameZoneName.Deck, player.NetId)
                    + " emptyDeckAttempt=" + (deckBefore == 0)
                    + " health=" + healthBefore + "->"
                    + (Object.ReferenceEquals(avatarAfter, null)
                        ? -1 : avatarAfter.CurrentHealth)
                    + " runes=" + runesBefore + "->"
                    + player.QueryAttribute(AttributeType.NumRunes).Value
                    + " runesLost="
                    + player.QueryAttribute(AttributeType.NumRunesLost).Value);
            }

            // Turn-start damage can cross a rune threshold, including damage
            // produced by cleanup effects in the After trigger queue. Drain
            // cleanup effects before evaluating Dementia's lane rule. Publish
            // its damage in this same event, then process rune breaks/Prophecy.
            DrainAutomaticTriggerQueues(state, false);
            DrainAutomaticTriggerQueues(state, true);
            ExperimentalCardRules100.ResolveDementiaTurnStart(
                TriggerState(state), player);
            DrainAutomaticTriggerQueues(state, false);
            DrainAutomaticTriggerQueues(state, true);
            CheckForRuneBreaks(state);
            DrainAutomaticTriggerQueues(state, true);

            simulation.HistoryLog.EndEvent();
            CheckForAvatarDeath(state);
            if (IsGameFinished(state))
                return;

            simulation.HistoryLog.SendUnsentMessages(
                state.Clients,
                GetObserverClients(simulation),
                HistoryLog.FullGameStateSendRule.Exclude);

            Plugin.Log("turn start: player=" + player.NetId.Id
                + " human=" + player.IsHuman
                + " mana=" + player.QueryAttribute(AttributeType.Mana).Value
                + " hand="
                + simulation.CardCatalog.FindCardCount(
                    GameZoneName.Hand, player.NetId));

        }

        private static void RunPausedHumanAttackIfNeeded(HostState state)
        {
            if (!state.GameInitialized || state.ActivePlayerIndex != 0 ||
                state.PausedAttackVisualUid == 0)
                return;

            if (state.PausedAttackEndMarkerSent)
            {
                if (!state.PausedAttackVisualComplete &&
                    Time.frameCount - state.PausedAttackVisualReleasedFrame < 300)
                    return;
                if (!state.PausedAttackVisualComplete)
                    Plugin.Warn("AI prophecy attack visual timed out");
                state.PausedAttackVisualUid = 0;
                state.PausedAttackVisualHeld = false;
                state.PausedAttackVisualComplete = false;
                state.PausedAttackEndMarkerSent = false;
                state.PausedAttackHumanPromptSent = false;
                if (!IsGameFinished(state))
                    SendCommandList(state, 0);
                return;
            }

            if (state.PendingProphecyPlayerIndex >= 0 ||
                state.PausedAttackHumanPromptSent)
                return;

            if (!IsGameFinished(state) && PauseAiForHumanReaction(state))
            {
                state.PausedAttackHumanPromptSent =
                    HasPendingHumanDecision(state);
                return;
            }

            ServerCard attacker = state.PausedAttackCompletionActor;
            ServerCard target = state.PausedAttackCompletionTarget;
            if (Object.ReferenceEquals(attacker, null) ||
                Object.ReferenceEquals(target, null))
                return;

            if (!state.PausedAttackCompletionDone)
            {
                CompleteAttack(state, attacker, target);
                state.PausedAttackCompletionDone = true;
                DrainAutomaticTriggerQueues(state, true);
                CheckForRuneBreaks(state);
                state.Simulation.HistoryLog.SendUnsentMessages(
                    state.Clients, GetObserverClients(state.Simulation),
                    HistoryLog.FullGameStateSendRule.Exclude);
                if (state.PendingProphecyPlayerIndex >= 0)
                    return;
                if (!IsGameFinished(state) && PauseAiForHumanReaction(state))
                {
                    state.PausedAttackHumanPromptSent =
                        HasPendingHumanDecision(state);
                    return;
                }
            }

            SendPausedAttackEndMarker(state, attacker, target);
        }

        private static bool HasPendingHumanDecision(HostState state)
        {
            if (state.PendingProphecyPlayerIndex == 0 ||
                (state.PendingCustomAction != PendingCustomActionKind.None &&
                 state.PendingCustomPlayerIndex == 0))
                return true;
            ServerEffectUtilities.TriggerContext context;
            int playerIndex;
            return TryPeekActiveDecisionTrigger(
                state, out context, out playerIndex) && playerIndex == 0;
        }

        private static void SendPausedAttackEndMarker(
            HostState state, ServerCard attacker, ServerCard target)
        {
            PlayerState owner = state.Simulation.Players[
                state.ActivePlayerIndex];
            state.Simulation.HistoryLog.StartEvent(
                CcgRpc.HistoryType.EventAttack,
                owner.NetId, attacker.NetUid, target.NetUid, -1);
            AddAttackPhaseAction(
                state, CcgRpc.HistoryType.ActionEndAttack,
                attacker, target);
            state.Simulation.HistoryLog.EndEvent();
            state.PausedAttackEndMarkerSent = true;
            state.PausedAttackVisualReleasedFrame = Time.frameCount;
            state.Simulation.HistoryLog.SendUnsentMessages(
                state.Clients, GetObserverClients(state.Simulation),
                HistoryLog.FullGameStateSendRule.Exclude);
            Plugin.Log("AI prophecy attack end marker sent: actor="
                + attacker.NetUid.Id);
        }

        private static void RunAiIfNeeded(HostState state)
        {
            if (!state.GameInitialized || state.ActivePlayerIndex != 1 ||
                !state.AiActionPending || Time.frameCount < state.AiActionFrame)
                return;

            state.AiActionPending = false;

            if (state.PausedAttackVisualUid != 0)
            {
                if (state.PausedAttackVisualHeld)
                {
                    if (PauseAiForHumanReaction(state))
                        return;
                    if (!Object.ReferenceEquals(
                        state.PausedAttackCompletionActor, null))
                    {
                        CompleteAttack(state,
                            state.PausedAttackCompletionActor,
                            state.PausedAttackCompletionTarget);
                        DrainAutomaticTriggerQueues(state, false);
                        state.PausedAttackCompletionActor = null;
                        state.PausedAttackCompletionTarget = null;
                        DrainAutomaticTriggerQueues(state, true);
                        state.Simulation.HistoryLog.SendUnsentMessages(
                            state.Clients,
                            GetObserverClients(state.Simulation),
                            HistoryLog.FullGameStateSendRule.Exclude);
                        if (PauseAiForHumanReaction(state))
                            return;
                    }
                    state.PausedAttackVisualHeld = false;
                    state.PausedAttackVisualReleasedFrame = Time.frameCount;
                    state.AiActionPending = true;
                    state.AiActionFrame = Time.frameCount + 1;
                    return;
                }
                if (!state.PausedAttackVisualComplete &&
                    Time.frameCount - state.PausedAttackVisualReleasedFrame < 300)
                {
                    state.AiActionPending = true;
                    state.AiActionFrame = Time.frameCount + 1;
                    return;
                }
                if (!state.PausedAttackVisualComplete)
                    Plugin.Warn("paused attack visual timed out; resuming AI");
                state.PausedAttackVisualUid = 0;
                state.PausedAttackVisualComplete = false;
            }

            if (!state.AiResumeAttacks)
            {
                CcgRpc.Decision playDecision;
                if (BuildFirstPlayableDecision(state, 1, out playDecision))
                {
                    TryPlayCard(state, 1, playDecision);
                    if (state.PendingCustomAction != PendingCustomActionKind.None &&
                        state.PendingCustomPlayerIndex == 1 &&
                        !ResolveAiPendingCustomAction(state))
                        return;
                }

                /* A queued human choice must finish before the attack phase,
                 * even when the AI had no playable card. */
                if (PauseAiForHumanReaction(state))
                {
                    state.AiResumeAttacks = true;
                    return;
                }
            }

            /* Resume after a prophecy at the attack phase, without playing a
             * fresh card before the interrupted sequence continues. */
            if (PauseAiForHumanReaction(state))
            {
                state.AiResumeAttacks = true;
                return;
            }

            RunBasicAiAttacks(state);
            if (PauseAiForHumanReaction(state))
            {
                state.AiResumeAttacks = true;
                return;
            }

            state.AiResumeAttacks = false;
            Plugin.Log("local AI completed turn");
            EndTurn(state, 1);
        }

        private static bool ResolveAiPendingCustomAction(HostState state)
        {
            if (state.PendingCustomAction == PendingCustomActionKind.None ||
                state.PendingCustomPlayerIndex != 1)
                return true;

            TriggeredEffect effect =
                state.PendingCustomContext.TriggeredEffect;
            if (Object.ReferenceEquals(effect, null))
                return false;

            CcgRpc.Decision decision = new CcgRpc.Decision();
            int choiceCount = GetPendingCustomChoiceCount(state);
            if (choiceCount > 0)
            {
                decision.Type = CcgRpc.DecisionType.CustomChoice;
                decision.CustomChoice = 0;
            }
            else if (effect.OptionalCustomUiType)
            {
                decision.Type = CcgRpc.DecisionType.NoCommand;
            }
            else
            {
                Plugin.Warn("AI custom UI has no legal choice: card="
                    + (Object.ReferenceEquals(state.PendingCustomCard, null)
                        ? 0
                        : state.PendingCustomCard.CardId)
                    + " ui=" + effect.CustomUiType);
                return false;
            }

            return CompletePendingCustomAction(state, decision);
        }

        private static void RunBasicAiAttacks(HostState state)
        {
            PlayerState ai = state.Simulation.Players[1];
            PlayerState human = state.Simulation.Players[0];
            List<ServerCard> attackers = state.Simulation.CardCatalog.GatherCardsInZone(
                delegate(ServerCard card) { return true; },
                ai.NetId,
                GameZoneName.CreatureBoard);

            int i;
            for (i = 0; i < attackers.Count; ++i)
            {
                ServerCard attacker = attackers[i];
                if (!attacker.CanCardAttack(false) &&
                    !HasMasterOfThievesExtraAttack(state, attacker))
                    continue;
                List<ServerCard> targets = GatherBasicAttackTargets(
                    state, ai, human, attacker);
                if (targets.Count == 0)
                    continue;

                /* Prefer the human avatar; otherwise attack the first legal
                 * creature.  This is deterministic and intentionally simple. */
                ServerCard target = targets[0];
                int j;
                for (j = 0; j < targets.Count; ++j)
                {
                    if (targets[j].HasSubtype(CardSubtype.Avatar))
                    {
                        target = targets[j];
                        break;
                    }
                }

                CcgRpc.Decision decision = new CcgRpc.Decision();
                decision.CommandUid = state.ListUid;
                decision.Type = CcgRpc.DecisionType.Attack;
                decision.ActorCardNetUid = attacker.NetUid.Id;
                decision.TargetCardNetUid = target.NetUid.Id;
                TryAttack(state, 1, decision);
                if (PauseAiForHumanReaction(state))
                {
                    state.AiResumeAttacks = true;
                    return;
                }
            }
        }

        private static bool PauseAiForHumanReaction(HostState state)
        {
            if (IsGameFinished(state))
                return true;

            if (state.PendingCustomAction != PendingCustomActionKind.None)
            {
                if (state.PendingCustomPlayerIndex == 0)
                {
                    SendPendingCustomActionCommandList(state);
                    return true;
                }
                if (state.PendingCustomPlayerIndex == 1 &&
                    !ResolveAiPendingCustomAction(state))
                    return true;
            }

            if (state.PendingProphecyPlayerIndex == 0)
            {
                SendCommandList(state, 0);
                return true;
            }

            ServerEffectUtilities.TriggerContext humanContext;
            if (PrepareQueuedEffectsForCommand(state, out humanContext))
            {
                SendTriggeredEffectCommandList(state, humanContext);
                return true;
            }

            if (state.PendingProphecyPlayerIndex == 0)
            {
                SendCommandList(state, 0);
                return true;
            }

            return state.TriggerQueues.Active.Count > 0;
        }

        private static bool BuildFirstPlayableDecision(
            HostState state,
            int playerIndex,
            out CcgRpc.Decision decision)
        {
            return BuildFirstPlayableDecision(
                state, playerIndex, GameZoneName.Hand, false, out decision);
        }

        private static bool BuildFirstPlayableDecision(
            HostState state,
            int playerIndex,
            GameZoneName sourceZone,
            bool ignoreMana,
            out CcgRpc.Decision decision)
        {
            return BuildFirstPlayableDecision(
                state, playerIndex, sourceZone, ignoreMana, 0, out decision);
        }

        private static bool BuildFirstPlayableDecision(
            HostState state,
            int playerIndex,
            GameZoneName sourceZone,
            bool ignoreMana,
            int preferredCardNetUid,
            out CcgRpc.Decision decision)
        {
            decision = null;
            List<string> diagnostics = new List<string>();
            PlayerState player = state.Simulation.Players[playerIndex];
            List<ServerCard> hand = state.Simulation.CardCatalog.GatherCardsInZone(
                delegate(ServerCard card) { return true; },
                player.NetId,
                sourceZone);

            int mana = player.QueryAttribute(AttributeType.Mana).Value;
            int i;
            for (i = 0; i < hand.Count; ++i)
            {
                ServerCard card = hand[i];
                if (preferredCardNetUid != 0 &&
                    card.NetUid.Id != preferredCardNetUid)
                    continue;
                SPTESL.Card baseCard = GameManager.Instance.GetCard(card.CardId);
                if (Object.ReferenceEquals(baseCard, null))
                {
                    diagnostics.Add(card.NetUid.Id + ":card-definition-missing");
                    continue;
                }
                bool isCreature = baseCard.HasSubtype(CardSubtype.Creature);
                bool isSupport = baseCard.HasSubtype(CardSubtype.Support);
                bool isItem = baseCard.HasSubtype(CardSubtype.Item);
                bool boardPlayable = isCreature || isSupport || isItem;
                BoardLaneMask prerequisiteLanes =
                    state.Simulation.CardCatalog.GatherOpenCreatureBoardLanesMask(
                        player.NetId);
                if (!PlayCardPrerequisitesMet(
                    state, player, card, baseCard, ref prerequisiteLanes))
                {
                    diagnostics.Add(card.NetUid.Id + ":play-requirements-failed");
                    continue;
                }
                int creatureLane = 0;
                ServerCard sacrificeTarget = null;
                if (isCreature && !ChooseAiCreaturePlacement(
                    state, player, out creatureLane, out sacrificeTarget))
                {
                    diagnostics.Add(card.NetUid.Id + ":no-creature-lane-or-discardable-sacrifice");
                    continue;
                }
                PlayCardAbility actionAbility;
                bool hasActionAbility =
                    TryGetSupportedActionAbility(
                        player, card, baseCard, out actionAbility);
                if (!boardPlayable && !hasActionAbility)
                {
                    diagnostics.Add(card.NetUid.Id + ":no-supported-board-or-action-play-ability");
                    continue;
                }
                if (!ignoreMana &&
                    card.QueryAttribute(AttributeType.ManaCost).Value > mana)
                {
                    diagnostics.Add(card.NetUid.Id + ":insufficient-mana");
                    continue;
                }
                if (IsForbiddenByOpponentManaLock(state, player, card,
                    card.QueryAttribute(AttributeType.ManaCost).Value))
                {
                    diagnostics.Add(card.NetUid.Id + ":opponent-mana-lock");
                    continue;
                }

                List<ServerCard> actionTargets = new List<ServerCard>();
                List<CcgRpc.BoardLaneTarget> actionLaneTargets =
                    new List<CcgRpc.BoardLaneTarget>();
                if (hasActionAbility &&
                    actionAbility.PlayTargetingType != PlayTargetingType.NoTarget)
                {
                    actionTargets = GatherActionCardTargets(
                        state, player, card, actionAbility);
                    actionLaneTargets = GatherActionLaneTargets(
                        state, player, card, actionAbility);
                    if (actionAbility.PlayTargetingType ==
                            PlayTargetingType.RequiredTarget &&
                        actionTargets.Count == 0 &&
                        actionLaneTargets.Count == 0)
                    {
                        diagnostics.Add(card.NetUid.Id + ":required-action-target-unavailable");
                        continue;
                    }
                }

                ServerCard equipTarget = null;
                if (isItem)
                {
                    List<ServerCard> equipTargets =
                        state.Simulation.CardCatalog.GatherCardsInZone(
                            delegate(ServerCard candidate) { return true; },
                            player.NetId,
                            GameZoneName.CreatureBoard);
                    if (equipTargets.Count == 0)
                    {
                        if (!IsMobilizeCard(baseCard))
                        {
                            diagnostics.Add(card.NetUid.Id + ":no-friendly-creature-to-equip-or-mobilize");
                            continue;
                        }
                        int mobilizeLane = -1;
                        for (int lane = 0; lane < 2; ++lane)
                            if (!state.Simulation.CardCatalog.IsLaneFull(
                                GameZoneName.CreatureBoard, lane, player.NetId))
                            {
                                mobilizeLane = lane;
                                break;
                            }
                        if (mobilizeLane < 0)
                        {
                            diagnostics.Add(card.NetUid.Id + ":mobilize-lanes-full");
                            continue;
                        }
                        decision = new CcgRpc.Decision();
                        decision.CommandUid = state.ListUid;
                        decision.Type = CcgRpc.DecisionType.Play;
                        decision.ActorCardNetUid = card.NetUid.Id;
                        decision.LaneTarget = mobilizeLane == 0
                            ? CcgRpc.BoardLaneTarget.MyLeftLane
                            : CcgRpc.BoardLaneTarget.MyRightLane;
                        decision.LaneIndex = mobilizeLane;
                        return true;
                    }
                    equipTarget = equipTargets[0];
                }

                decision = new CcgRpc.Decision();
                decision.CommandUid = state.ListUid;
                decision.Type = CcgRpc.DecisionType.Play;
                decision.ActorCardNetUid = card.NetUid.Id;
                decision.LaneIndex = isCreature ? creatureLane : 0;
                decision.PositionIndex = 0;
                if (!Object.ReferenceEquals(sacrificeTarget, null))
                    decision.DiscardCardNetUids.Add(sacrificeTarget.NetUid.Id);
                if (!Object.ReferenceEquals(equipTarget, null))
                    decision.EquipCardNetUid = equipTarget.NetUid.Id;
                if (actionTargets.Count > 0)
                    decision.TargetCardNetUid = actionTargets[0].NetUid.Id;
                else if (actionLaneTargets.Count > 0)
                    decision.LaneTarget = actionLaneTargets[0];
                if (baseCard.BetrayRequirements != null &&
                    baseCard.BetrayRequirements.PlayerRequirements != null &&
                    baseCard.BetrayRequirements.PlayerRequirements.Count > 0)
                {
                    List<ServerCard> betrayables =
                        state.Simulation.CardCatalog.GatherCardsInZone(
                            delegate(ServerCard candidate)
                            {
                                return candidate.HasSubtype(CardSubtype.Creature) &&
                                    !candidate.IsDying();
                            }, player.NetId, GameZoneName.CreatureBoard);
                    if (betrayables.Count > 0)
                        decision.DiscardCardNetUids.Add(
                            betrayables[0].NetUid.Id);
                }
                return true;
            }

            if (diagnostics.Count == 0)
                diagnostics.Add("no-card-in-source-zone-or-preferred-card-not-found");
            return false;
        }

        private static bool PlayCardPrerequisitesMet(
            HostState state,
            PlayerState player,
            ServerCard card,
            SPTESL.Card baseCard,
            ref BoardLaneMask legalCreatureBoardLanes)
        {
            PlayerState opponent = state.Simulation.Players[0].NetId.Id ==
                player.NetId.Id
                ? state.Simulation.Players[1]
                : state.Simulation.Players[0];
            PlayerConditionalArguments playerArguments =
                new PlayerConditionalArguments(
                    state.Simulation.CardCatalog,
                    player,
                    player,
                    null,
                    null,
                    opponent);
            try
            {
                return CardCommandUtilities.PlayCardPrerequisitesMet(
                    card,
                    baseCard,
                    ref legalCreatureBoardLanes,
                    playerArguments,
                    false);
            }
            catch (Exception ex)
            {
                Plugin.Warn("play requirements rejected card=" + card.CardId
                    + " uid=" + card.NetUid.Id
                    + " error=" + ex.Message);
                return false;
            }
        }

        private static bool IsForbiddenByOpponentManaLock(
            HostState state, PlayerState player, ServerCard card, int manaCost)
        {
            List<ServerCard> board = state.Simulation.CardCatalog.GatherDynamicCardList()
                .FindAll(delegate(ServerCard candidate)
                {
                    int preventedCost = candidate.QueryAttribute(
                        AttributeType.PreventOpponentManaPlay).Value;
                    return candidate.CurrentGameZoneName == GameZoneName.CreatureBoard &&
                        candidate.OwnerNetId != player.NetId && !candidate.Silenced &&
                        !candidate.IsDying() && preventedCost > 0 &&
                        preventedCost == manaCost;
                });
            return board.Count > 0;
        }

        private static bool ChooseAiCreaturePlacement(
            HostState state,
            PlayerState player,
            out int lane,
            out ServerCard sacrificeTarget)
        {
            lane = -1;
            sacrificeTarget = null;
            ServerCardCatalog catalog = state.Simulation.CardCatalog;
            BoardLaneMask open = catalog.GatherOpenCreatureBoardLanesMask(player.NetId);
            bool leftOpen = BoardLaneMaskUtilities.IsLaneSet(open, 0);
            bool rightOpen = BoardLaneMaskUtilities.IsLaneSet(open, 1);

            if (leftOpen || rightOpen)
            {
                if (leftOpen && !rightOpen)
                    lane = 0;
                else if (!leftOpen && rightOpen)
                    lane = 1;
                else
                {
                    List<ServerCard> creatures = catalog.GatherCardsInZone(
                        delegate(ServerCard candidate) { return true; },
                        player.NetId, GameZoneName.CreatureBoard);
                    int leftCount = 0;
                    int rightCount = 0;
                    int i;
                    for (i = 0; i < creatures.Count; ++i)
                    {
                        if (creatures[i].CurrentLaneIndex == 0)
                            leftCount++;
                        else if (creatures[i].CurrentLaneIndex == 1)
                            rightCount++;
                    }
                    if (leftCount < rightCount)
                        lane = 0;
                    else if (rightCount < leftCount)
                        lane = 1;
                    else
                        lane = Object.ReferenceEquals(
                            state.StartInfo.RandomNumberGenerator, null)
                            ? 0
                            : state.StartInfo.RandomNumberGenerator.GenerateInRange(0, 1);
                }
                return true;
            }

            int laneIndex;
            for (laneIndex = 0; laneIndex < 2; ++laneIndex)
            {
                if (!catalog.IsLaneFull(
                    GameZoneName.CreatureBoard, laneIndex, player.NetId))
                    continue;
                List<ServerCard> discardable =
                    catalog.GatherDiscardableCardsInZone(
                        player.NetId, GameZoneName.CreatureBoard, laneIndex);
                int i;
                for (i = 0; i < discardable.Count; ++i)
                {
                    ServerCard candidate = discardable[i];
                    if (Object.ReferenceEquals(sacrificeTarget, null) ||
                        candidate.QueryAttribute(AttributeType.ManaCost).Value <
                        sacrificeTarget.QueryAttribute(AttributeType.ManaCost).Value)
                    {
                        lane = laneIndex;
                        sacrificeTarget = candidate;
                    }
                }
            }
            return !Object.ReferenceEquals(sacrificeTarget, null);
        }

        private static void AnnounceTurnAndCommands(HostState state)
        {
            if (state.ActivePlayerIndex == 0)
                SendCommandList(state, 0);
            else
            {
                state.AiActionPending = true;
                state.AiActionFrame = Time.frameCount + 18;
            }
        }

        private static void SendFullStateAndCommands(HostState state)
        {
            SendFullStateOnly(state);
            if (state.ActivePlayerIndex == 0)
                SendCommandList(state, 0);
        }

        private static void SendFullStateOnly(HostState state)
        {
            if (Object.ReferenceEquals(SendFullStateMethod, null))
                return;
            PlayerServiceClient client = state.Clients[0];
            if (Object.ReferenceEquals(client, null) ||
                client.GetConnectionStatus() !=
                    PlayerServiceClient.ConnectionStatus.Connected)
                return;

            SendFullStateMethod.Invoke(
                state.Simulation,
                new object[] { client, false });
        }

        private static void SendProphecyCommandList(
            HostState state,
            int playerIndex,
            CardCommandList list)
        {
            PlayerState player = state.Simulation.Players[playerIndex];
            PlayerState opponent =
                state.Simulation.Players[playerIndex == 0 ? 1 : 0];
            List<ServerCard> cards = state.Simulation.CardCatalog.GatherCardsInZone(
                delegate(ServerCard candidate) { return true; },
                player.NetId,
                GameZoneName.Prophecy);
            if (cards.Count == 0)
            {
                state.PendingProphecyPlayerIndex = -1;
                state.OutstandingProphecyListUid = 0;
                SendCommandList(state, playerIndex);
                return;
            }

            ServerCard card = cards[0];
            CardCommandState command = new CardCommandState();
            command.CardNetUid = card.NetUid.Id;
            command.ValidCardCommandMask = (int)CardCommand.PlayToHand;

            SPTESL.Card baseCard = GameManager.Instance.GetCard(card.CardId);
            if (!Object.ReferenceEquals(baseCard, null))
            {
                bool boardPlayable =
                    baseCard.HasSubtype(CardSubtype.Creature) ||
                    baseCard.HasSubtype(CardSubtype.Support) ||
                    baseCard.HasSubtype(CardSubtype.Item);
                PlayCardAbility actionAbility;
                bool supportedAction =
                    TryGetSupportedActionAbility(
                        player, card, baseCard, out actionAbility);
                try
                {
                    if (boardPlayable)
                    {
                        BoardLaneMask legal =
                            state.Simulation.CardCatalog.GatherOpenCreatureBoardLanesMask(
                                player.NetId);
                        BoardLaneMask filled =
                            state.Simulation.CardCatalog.GatherFilledBoardLanesMask(
                                player.NetId, GameZoneName.CreatureBoard);
                        BoardPlacementMasks.ForCard(state.Simulation.CardCatalog,player.NetId,card,ref legal,ref filled);
                        if (supportedAction)
                        {
                            FillReconstructedBoardPlayCommand(
                                state,
                                player,
                                opponent,
                                card,
                                baseCard,
                                actionAbility,
                                legal,
                                filled,
                                command);
                        }
                        else
                        {
                            BoardLaneMask cardLegal=legal,cardFilled=filled;
                            BoardPlacementMasks.ForCard(state.Simulation.CardCatalog,player.NetId,card,ref cardLegal,ref cardFilled);
                            state.Simulation.CardCatalog.
                                FillPlayCardCommandStateNoPrerequisites(
                                    command,
                                    card,
                                    baseCard,
                                    player,
                                    opponent,
                                    state.Simulation.Players,
                                    cardLegal,
                                    cardFilled);
                        }
                        if (IsCreaturePlacementCommand(baseCard, command))
                            NormalizeCreaturePlacementCommand(
                                state, player, command, legal, filled);
                    }
                    else if (supportedAction)
                    {
                        FillSupportedActionCommand(
                            state, player, card, actionAbility, command);
                    }
                    command.ValidCardCommandMask |= (int)CardCommand.PlayToHand;
                }
                catch (Exception ex)
                {
                    Plugin.Warn("prophecy play command limited to hand: " + ex.Message);
                    command.ValidCardCommandMask = (int)CardCommand.PlayToHand;
                }
            }

            list.MustSelectCommand = true;
            list.ValidCommands.Add(command);

            /* Prophecy uses the same full-lane replacement flow as a normal
             * creature play.  The client opens its sacrifice picker from the
             * command-list level FullLaneCardUids collection; without these
             * UIDs it waits forever after a full lane is selected. */
            state.Simulation.CardCatalog.AttachFullLaneCardsToCommandList(
                player, list);

            CardCommandItem item = new CardCommandItem();
            item.CommandList = list;
            CcgRpc.HistoryItem history = new CcgRpc.HistoryItem();
            history.Type = CcgRpc.HistoryType.CardCommandList;
            history.ActingPlayerInstanceId = player.NetId.Id;
            history.CardCommandItem = item;
            state.Simulation.HistoryLog.AddCardCommandItem(history);
            player.LastSentCommandItem = item;
            state.OutstandingProphecyListUid = list.ListUid;
            state.Simulation.HistoryLog.SendUnsentMessages(
                state.Clients,
                GetObserverClients(state.Simulation),
                HistoryLog.FullGameStateSendRule.Exclude);

            Plugin.Log("prophecy CardCommandList sent: uid=" + list.ListUid
                + " card=" + card.CardId
                + " actor=" + card.NetUid.Id
                + " actionRules=" + (Object.ReferenceEquals(baseCard, null) ||
                    Object.ReferenceEquals(baseCard.ActionPlayInformation, null)
                    ? -1 : baseCard.ActionPlayInformation.Count)
                + " triggerRules=" + (Object.ReferenceEquals(baseCard, null) ||
                    Object.ReferenceEquals(baseCard.TriggeredEffects, null)
                    ? -1 : baseCard.TriggeredEffects.Count)
                + " mask=" + command.ValidCardCommandMask
                + " boardLanes=" + command.ValidBoardLanes
                + " fullBoardLanes=" + command.FullBoardLanes
                + " fullLaneCards=[" + JoinIntTargets(
                    list.FullLaneCardUids) + "]"
                + " playTargets=[" + JoinIntTargets(command.PlayTargets) + "]"
                + " laneTargets=[" + JoinLaneTargets(command.LaneTargets) + "]");
        }

        private static string JoinIntTargets(
            Google.Protobuf.Collections.RepeatedField<int> values)
        {
            if (Object.ReferenceEquals(values, null) || values.Count == 0)
                return String.Empty;
            System.Text.StringBuilder builder =
                new System.Text.StringBuilder();
            int i;
            for (i = 0; i < values.Count; ++i)
            {
                if (i > 0)
                    builder.Append(',');
                builder.Append(values[i]);
            }
            return builder.ToString();
        }

        private static string DescribeAttackTargets(List<ServerCard> targets)
        {
            if (Object.ReferenceEquals(targets, null) || targets.Count == 0)
                return String.Empty;
            System.Text.StringBuilder builder =
                new System.Text.StringBuilder();
            int i;
            for (i = 0; i < targets.Count; ++i)
            {
                ServerCard target = targets[i];
                if (i > 0)
                    builder.Append(',');
                builder.Append(target.NetUid.Id)
                    .Append(':').Append(target.CurrentGameZoneName)
                    .Append(":owner=").Append(target.OwnerNetId.Id)
                    .Append(":avatar=")
                    .Append(target.HasSubtype(CardSubtype.Avatar));
            }
            return builder.ToString();
        }

        private static string JoinLaneTargets(
            Google.Protobuf.Collections.RepeatedField<CcgRpc.BoardLaneTarget> values)
        {
            if (Object.ReferenceEquals(values, null) || values.Count == 0)
                return String.Empty;
            System.Text.StringBuilder builder =
                new System.Text.StringBuilder();
            int i;
            for (i = 0; i < values.Count; ++i)
            {
                if (i > 0)
                    builder.Append(',');
                builder.Append(values[i]);
            }
            return builder.ToString();
        }

        private static void SendCommandList(HostState state, int playerIndex)
        {
            if (state.PendingProphecyPlayerIndex == playerIndex &&
                state.OutstandingProphecyListUid != 0)
            {
                Plugin.Log("prophecy command already outstanding: uid="
                    + state.OutstandingProphecyListUid + " player="
                    + state.Simulation.Players[playerIndex].NetId.Id);
                return;
            }
            PlayerState player = state.Simulation.Players[playerIndex];
            PlayerState opponent = state.Simulation.Players[playerIndex == 0 ? 1 : 0];

            CardCommandList list = new CardCommandList();
            state.ListUid += 1;
            list.ListUid = state.ListUid;
            list.MustSelectCommand = false;
            list.IsMulliganPhase = false;

            if (state.PendingProphecyPlayerIndex == playerIndex)
            {
                SendProphecyCommandList(state, playerIndex, list);
                return;
            }

            ServerEffectUtilities.TriggerContext humanTriggerContext;
            if (PrepareQueuedEffectsForCommand(state, out humanTriggerContext))
            {
                SendTriggeredEffectCommandList(state, humanTriggerContext);
                return;
            }
            if (state.TriggerQueues.Active.Count > 0)
            {
                /* Do not let the player issue unrelated turn commands while an
                 * unresolved retail decision context is still active. */
                return;
            }

            /* PrepareQueuedEffectsForCommand may have resolved AI/automatic
             * effects that generated a prophecy or ended the game. */
            if (IsGameFinished(state))
                return;
            if (state.PendingProphecyPlayerIndex == 0)
            {
                SendProphecyCommandList(state, 0, list);
                return;
            }

            List<ServerCard> hand = state.Simulation.CardCatalog.GatherCardsInZone(
                delegate(ServerCard card) { return true; },
                player.NetId,
                GameZoneName.Hand);

            BoardLaneMask legal =
                state.Simulation.CardCatalog.GatherOpenCreatureBoardLanesMask(player.NetId);
            BoardLaneMask filled =
                state.Simulation.CardCatalog.GatherFilledBoardLanesMask(
                    player.NetId, GameZoneName.CreatureBoard);
            int mana = player.QueryAttribute(AttributeType.Mana).Value;

            int i;
            int skippedMana = 0;
            int skippedNoBase = 0;
            for (i = 0; i < hand.Count; ++i)
            {
                ServerCard card = hand[i];
                int cost = card.QueryAttribute(AttributeType.ManaCost).Value;
                if (IsForbiddenByOpponentManaLock(state, player, card, cost))
                {
                    Plugin.Log("command rejected by opponent mana lock: card="
                        + card.CardId + " cost=" + cost);
                    continue;
                }
                if (cost > mana)
                {
                    skippedMana += 1;
                    continue;
                }

                SPTESL.Card baseCard = GameManager.Instance.GetCard(card.CardId);
                if (Object.ReferenceEquals(baseCard, null))
                {
                    skippedNoBase += 1;
                    continue;
                }

                BoardLaneMask prerequisiteLanes = legal;
                if (!PlayCardPrerequisitesMet(
                    state, player, card, baseCard, ref prerequisiteLanes))
                {
                    Plugin.Log("command rejected by retail play requirements: card="
                        + card.CardId + " uid=" + card.NetUid.Id);
                    continue;
                }

                CardCommandState command = new CardCommandState();
                command.CardNetUid = card.NetUid.Id;

                bool boardPlayable =
                    baseCard.HasSubtype(CardSubtype.Creature) ||
                    baseCard.HasSubtype(CardSubtype.Support) ||
                    baseCard.HasSubtype(CardSubtype.Item);
                PlayCardAbility actionAbility;
                bool supportedAction =
                    TryGetSupportedActionAbility(
                        player, card, baseCard, out actionAbility);

                if (boardPlayable)
                {
                    try
                    {
                        if (supportedAction)
                        {
                            if (!FillReconstructedBoardPlayCommand(
                                state,
                                player,
                                opponent,
                                card,
                                baseCard,
                                actionAbility,
                                prerequisiteLanes,
                                filled,
                                command))
                                continue;
                        }
                        else
                        {
                            BoardLaneMask cardLegal=prerequisiteLanes,cardFilled=filled;
                            BoardPlacementMasks.ForCard(state.Simulation.CardCatalog,player.NetId,card,ref cardLegal,ref cardFilled);
                            state.Simulation.CardCatalog.
                                FillPlayCardCommandStateNoPrerequisites(
                                    command,
                                    card,
                                    baseCard,
                                    player,
                                    opponent,
                                    state.Simulation.Players,
                                    cardLegal,
                                    cardFilled);
                        }
                    }
                    catch (Exception ex)
                    {
                        Plugin.Warn("command generation skipped card=" + card.CardId
                            + " error=" + ex.Message);
                        continue;
                    }

                    if (IsCreaturePlacementCommand(baseCard, command))
                        NormalizeCreaturePlacementCommand(
                            state, player, command, legal, filled);
                }
                else if (supportedAction)
                {
                    if (!FillSupportedActionCommand(
                        state, player, card, actionAbility, command))
                        continue;
                }
                else
                {
                    continue;
                }

                if (command.ValidCardCommandMask != 0)
                {
                    Plugin.Log("command detail: card=" + card.CardId
                        + " uid=" + card.NetUid.Id
                        + " mask=" + command.ValidCardCommandMask
                        + " lanes=" + command.ValidBoardLanes
                        + " playTargets=[" + JoinIntTargets(command.PlayTargets) + "]"
                        + " laneTargets=[" + JoinLaneTargets(command.LaneTargets) + "]"
                        + " equipTargets=[" + JoinIntTargets(command.EquipTargets) + "]");
                    list.ValidCommands.Add(command);
                }
            }

            if (hand.Count > 0 && list.ValidCommands.Count == 0)
            {
                System.Text.StringBuilder costs = new System.Text.StringBuilder();
                int hi;
                for (hi = 0; hi < hand.Count && hi < 6; ++hi)
                {
                    if (hi > 0)
                        costs.Append(',');
                    costs.Append(
                        hand[hi].QueryAttribute(AttributeType.ManaCost).Value);
                }
                Plugin.Log(
                    "SendCommandList: hand=" + hand.Count
                    + " mana=" + mana
                    + " costs=[" + costs.ToString() + "]"
                    + " skippedMana=" + skippedMana
                    + " skippedNoBase=" + skippedNoBase
                    + " (no play commands yet — need more mana or EndTurn)");
            }

            List<ServerCard> abilityCards =
                state.Simulation.CardCatalog.GatherCardsInZone(
                    delegate(ServerCard candidate) { return true; },
                    player.NetId,
                    GameZoneName.CreatureBoard);
            List<ServerCard> supportAbilityCards =
                state.Simulation.CardCatalog.GatherCardsInZone(
                    delegate(ServerCard candidate) { return true; },
                    player.NetId,
                    GameZoneName.SupportBoard);
            abilityCards.AddRange(supportAbilityCards);
            List<ServerCard> itemAbilityCards =
                state.Simulation.CardCatalog.GatherCardsInZone(
                    delegate(ServerCard candidate) { return true; },
                    player.NetId,
                    GameZoneName.ItemBoard);
            abilityCards.AddRange(itemAbilityCards);

            int abilityIndex;
            for (abilityIndex = 0;
                 abilityIndex < abilityCards.Count;
                 ++abilityIndex)
            {
                ServerCard abilityCard = abilityCards[abilityIndex];
                bool hasActivatedAbility =
                    abilityCard.HasPotentialActivatedAbility() &&
                    !abilityCard.HasConsumedAllAbilityUsesForThisTurn();
                bool hasExalt = !abilityCard.HasPotentialActivatedAbility() &&
                    CanOfferExalt(player, abilityCard);
                if (!hasActivatedAbility && !hasExalt)
                    continue;

                CardCommandState abilityCommand = null;
                int existingIndex;
                for (existingIndex = 0;
                     existingIndex < list.ValidCommands.Count;
                     ++existingIndex)
                {
                    if (list.ValidCommands[existingIndex].CardNetUid ==
                        abilityCard.NetUid.Id)
                    {
                        abilityCommand = list.ValidCommands[existingIndex];
                        break;
                    }
                }
                if (Object.ReferenceEquals(abilityCommand, null))
                {
                    abilityCommand = new CardCommandState();
                    abilityCommand.CardNetUid = abilityCard.NetUid.Id;
                    bool offered = hasActivatedAbility &&
                        FillActivatedAbilityCommand(
                            state, player, abilityCard, abilityCommand);
                    if (hasExalt)
                        offered = FillExaltCommand(
                            player, abilityCard, abilityCommand) || offered;
                    if (offered)
                    {
                        list.ValidCommands.Add(abilityCommand);
                        Plugin.Log("activated ability command offered: player="
                            + player.NetId.Id + " card=" + abilityCard.CardId
                            + " uid=" + abilityCard.NetUid.Id
                            + " targets=" + JoinIntTargets(
                                abilityCommand.ActiveAbilityTargets)
                            + " lanes=" + JoinLaneTargets(
                                abilityCommand.LaneTargets));
                    }
                    else
                    {
                        Plugin.Log("activated ability command omitted: player="
                            + player.NetId.Id + " card=" + abilityCard.CardId
                            + " uid=" + abilityCard.NetUid.Id
                            + " reason=no-valid-ability-target");
                    }
                }
                else
                {
                    bool offered = hasActivatedAbility &&
                        FillActivatedAbilityCommand(
                            state, player, abilityCard, abilityCommand);
                    if (hasExalt)
                        offered = FillExaltCommand(
                            player, abilityCard, abilityCommand) || offered;
                    if (offered)
                        Plugin.Log("activated ability command offered: player="
                            + player.NetId.Id + " card=" + abilityCard.CardId
                            + " uid=" + abilityCard.NetUid.Id
                            + " targets=" + JoinIntTargets(
                                abilityCommand.ActiveAbilityTargets)
                            + " lanes=" + JoinLaneTargets(
                                abilityCommand.LaneTargets));
                }
            }

            if (player.QueryAttribute(AttributeType.PlayerCannotAttack).Value <= 0)
            {
                List<ServerCard> attackers = state.Simulation.CardCatalog.GatherCardsInZone(
                    delegate(ServerCard card) { return true; },
                    player.NetId,
                    GameZoneName.CreatureBoard);
                for (i = 0; i < attackers.Count; ++i)
                {
                    ServerCard attacker = attackers[i];
                    bool consumed = attacker.HasConsumedAllActionsForThisTurn();
                    bool extraAttack = HasMasterOfThievesExtraAttack(state, attacker);
                    bool sleeping = attacker.IsSleeping();
                    bool canAttack = attacker.CanCardAttack(false);
                    if (consumed && !extraAttack)
                    {
                        list.AlreadyUsedCardUids.Add(attacker.NetUid.Id);
                        Plugin.Log("attack command omitted: player=" + player.NetId.Id
                            + " card=" + attacker.CardId
                            + " uid=" + attacker.NetUid.Id
                            + " reason=actions-consumed");
                        continue;
                    }
                    if (sleeping)
                    {
                        list.NotReadyCardUids.Add(attacker.NetUid.Id);
                        Plugin.Log("attack command omitted: player=" + player.NetId.Id
                            + " card=" + attacker.CardId
                            + " uid=" + attacker.NetUid.Id
                            + " reason=sleeping");
                        continue;
                    }
                    if (!canAttack && !extraAttack)
                    {
                        Plugin.Log("attack command omitted: player=" + player.NetId.Id
                            + " card=" + attacker.CardId
                            + " uid=" + attacker.NetUid.Id
                            + " reason=retail-criteria");
                        continue;
                    }
                    List<ServerCard> targets = GatherBasicAttackTargets(
                        state, player, opponent, attacker);
                    if (targets.Count == 0)
                    {
                        Plugin.Log("attack command omitted: player=" + player.NetId.Id
                            + " card=" + attacker.CardId
                            + " uid=" + attacker.NetUid.Id
                            + " reason=no-targets lane=" + attacker.CurrentLaneIndex);
                        continue;
                    }

                    CardCommandState attackCommand = null;
                    int commandIndex;
                    for (commandIndex = 0;
                         commandIndex < list.ValidCommands.Count;
                         ++commandIndex)
                    {
                        if (list.ValidCommands[commandIndex].CardNetUid ==
                            attacker.NetUid.Id)
                        {
                            attackCommand = list.ValidCommands[commandIndex];
                            break;
                        }
                    }
                    if (Object.ReferenceEquals(attackCommand, null))
                    {
                        attackCommand = new CardCommandState();
                        attackCommand.CardNetUid = attacker.NetUid.Id;
                        list.ValidCommands.Add(attackCommand);
                    }
                    attackCommand.ValidCardCommandMask |= (int)CardCommand.Attack;
                    int t;
                    for (t = 0; t < targets.Count; ++t)
                    {
                        if (!attackCommand.AttackTargets.Contains(
                            targets[t].NetUid.Id))
                            attackCommand.AttackTargets.Add(targets[t].NetUid.Id);
                    }
                    Plugin.Log("attack command offered: player=" + player.NetId.Id
                        + " card=" + attacker.CardId
                        + " uid=" + attacker.NetUid.Id
                        + " mask=" + attackCommand.ValidCardCommandMask
                        + " targets=" + JoinIntTargets(attackCommand.AttackTargets)
                        + " targetKinds=" + DescribeAttackTargets(targets));
                }
            }

            try
            {
                state.Simulation.CardCatalog.AttachFullLaneCardsToCommandList(
                    player, list);
            }
            catch (Exception ex)
            {
                Plugin.Warn("full-lane command metadata skipped: " + ex.Message);
            }

            if (player.QueryAttribute(AttributeType.CanUseRing).Value > 0 &&
                player.QueryAttribute(AttributeType.RingUsesLeft).Value > 0)
            {
                CardCommandState ring = new CardCommandState();
                ring.CardNetUid = 0;
                ring.ValidCardCommandMask = (int)CardCommand.UseRing;
                list.ValidCommands.Add(ring);
            }

            CardCommandState endTurn = new CardCommandState();
            endTurn.CardNetUid = 0;
            endTurn.ValidCardCommandMask = (int)CardCommand.EndTurn;
            list.ValidCommands.Add(endTurn);

            CardCommandItem item = new CardCommandItem();
            item.CommandList = list;

            CcgRpc.HistoryItem history = new CcgRpc.HistoryItem();
            history.Type = CcgRpc.HistoryType.CardCommandList;
            history.ActingPlayerInstanceId = player.NetId.Id;
            history.CardCommandItem = item;

            /* The retail Simulation publishes this beside each command list.
             * BoardState uses it to lift/floats creatures that can attack. */
            CcgRpc.HistoryItem floating = new CcgRpc.HistoryItem();
            floating.Type = CcgRpc.HistoryType.FloatingCards;
            floating.FloatingCardsUpdate =
                state.Simulation.CardCatalog.GenerateFloatingCardList(list);
            state.Simulation.HistoryLog.AddFloatingCardItem(floating);

            state.Simulation.HistoryLog.AddCardCommandItem(history);
            player.LastSentCommandItem = item;
            state.Simulation.HistoryLog.SendUnsentMessages(
                state.Clients,
                GetObserverClients(state.Simulation),
                HistoryLog.FullGameStateSendRule.Exclude);

            Plugin.Log("CardCommandList sent: uid=" + list.ListUid
                + " commands=" + (list.ValidCommands.Count - 1)
                + " notReady=" + list.NotReadyCardUids.Count
                + " used=" + list.AlreadyUsedCardUids.Count
                + " openLaneMask=" + legal.Value
                + " filledLaneMask=" + filled.Value
                + " fullLane=" + list.FullLaneCardUids.Count
                + " floating=" + floating.FloatingCardsUpdate.CardNetUids.Count
                + " + EndTurn");
        }

        private static bool IsCreaturePlacementCommand(
            SPTESL.Card baseCard,
            CardCommandState command)
        {
            return !Object.ReferenceEquals(baseCard, null) &&
                !Object.ReferenceEquals(command, null) &&
                baseCard.HasSubtype(CardSubtype.Creature) &&
                (command.ValidCardCommandMask &
                    (int)CardCommand.PlayToPosition) != 0;
        }

        /* Normal creature placement is encoded by ValidBoardLanes, not by
         * LaneTargets.  Keep the canonical two placement bits explicit while
         * preserving LaneTargets exclusively for card-effect targeting. */
        private static void NormalizeCreaturePlacementCommand(
            HostState state,
            PlayerState player,
            CardCommandState command,
            BoardLaneMask legal,
            BoardLaneMask filled)
        {
            ServerCardCatalog catalog = state.Simulation.CardCatalog;
            BoardLaneMask replaceable = new BoardLaneMask();
            int laneIndex;
            for (laneIndex = 0; laneIndex < 2; ++laneIndex)
            {
                if (catalog.IsLaneFull(
                        GameZoneName.CreatureBoard, laneIndex, player.NetId) &&
                    catalog.GatherDiscardableCardsInZone(
                        player.NetId, GameZoneName.CreatureBoard, laneIndex).Count > 0)
                    BoardLaneMaskUtilities.AddLaneIndexToMask(
                        ref replaceable, laneIndex);
            }
            command.ValidBoardLanes = (legal.Value | replaceable.Value) & 3;
            command.FullBoardLanes = filled.Value & 3;
        }

        private static bool IsDecisionTriggerContext(
            ServerEffectUtilities.TriggerContext context)
        {
            return !Object.ReferenceEquals(context.TriggeredEffect, null) &&
                   !context.DidMainEffectsRun &&
                   context.TriggeredEffect.ClientTargetingType !=
                       PlayTargetingType.NoTarget;
        }

        private static bool IsCustomUiDecisionContext(
            HostState state,
            ServerEffectUtilities.TriggerContext context)
        {
            return !Object.ReferenceEquals(context.TriggeredEffect, null) &&
                   context.DidMainEffectsRun &&
                   context.TriggeredEffect.CustomUiType != CustomCardUiType.None &&
                   ServerEffectUtilities.IsPlayerDecisionRequired(
                       context, state.CustomUiCards);
        }

        private static int GetTriggerDecisionPlayerIndex(
            HostState state,
            ServerEffectUtilities.TriggerContext context)
        {
            int ownerIndex = state.ActivePlayerIndex;
            if (!Object.ReferenceEquals(context.EffectOwner, null))
            {
                int i;
                for (i = 0; i < state.Simulation.Players.Count; ++i)
                {
                    if (state.Simulation.Players[i].NetId.Id ==
                        context.EffectOwner.OwnerNetId.Id)
                    {
                        ownerIndex = i;
                        break;
                    }
                }
            }
            else if (!Object.ReferenceEquals(
                context.TriggerEvent.InciterPlayerState, null))
            {
                int i;
                for (i = 0; i < state.Simulation.Players.Count; ++i)
                {
                    if (state.Simulation.Players[i].NetId.Id ==
                        context.TriggerEvent.InciterPlayerState.NetId.Id)
                    {
                        ownerIndex = i;
                        break;
                    }
                }
            }

            if (context.TriggeredEffect.OpponentChooses)
                return ownerIndex == 0 ? 1 : 0;
            return ownerIndex;
        }

        private static bool TryPeekActiveDecisionTrigger(
            HostState state,
            out ServerEffectUtilities.TriggerContext context,
            out int playerIndex)
        {
            context = new ServerEffectUtilities.TriggerContext();
            playerIndex = -1;
            if (state.TriggerQueues.Active.Count == 0)
                return false;

            context = state.TriggerQueues.Active.Peek();
            if (!IsDecisionTriggerContext(context) &&
                !IsCustomUiDecisionContext(state, context))
                return false;

            playerIndex = GetTriggerDecisionPlayerIndex(state, context);
            return true;
        }

        private static bool PromoteNextTriggerContext(HostState state)
        {
            if (state.TriggerQueues.Active.Count > 0)
                return true;

            if (state.TriggerQueues.Pending.Count > 0)
            {
                state.TriggerQueues.Pending.Sort(
                    ServerEffectUtilities.TriggerContext.SortComparison);
                ServerEffectUtilities.TriggerContext context =
                    state.TriggerQueues.Pending[0];
                state.TriggerQueues.Pending.RemoveAt(0);
                state.TriggerQueues.Active.Enqueue(context);
                return true;
            }

            if (state.TriggerQueues.After.Count > 0)
            {
                state.TriggerQueues.After.Sort(
                    ServerEffectUtilities.TriggerContext.SortComparison);
                ServerEffectUtilities.TriggerContext context =
                    state.TriggerQueues.After[0];
                state.TriggerQueues.After.RemoveAt(0);
                state.TriggerQueues.Active.Enqueue(context);
                return true;
            }
            return false;
        }

        private static ServerCard GetTriggeredEffectTargetingSource(
            HostState state,
            ServerEffectUtilities.TriggerContext context)
        {
            ServerCard source = context.EffectOwner;
            if (Object.ReferenceEquals(source, null) ||
                Object.ReferenceEquals(context.TriggeredEffect, null) ||
                !context.TriggeredEffect.ItemUsesWielderAsEffectSource ||
                !source.ItemTarget.IsValid())
                return source;

            ServerCard wielder = state.Simulation.CardCatalog.FindServerCard(
                source.ItemTarget);
            return Object.ReferenceEquals(wielder, null) ? source : wielder;
        }

        private static List<ServerCard> GatherTriggeredEffectCardTargets(
            HostState state,
            ServerEffectUtilities.TriggerContext context)
        {
            List<ServerCard> result = new List<ServerCard>();
            if (!IsDecisionTriggerContext(context) ||
                !ServerEffectUtilities.TestBeforeTargetingEffectConditions(
                    TriggerState(state), context))
                return result;

            List<DecisionConditionalExpression> selectors =
                context.TriggeredEffect.TargetSelectors;
            bool hasCardSelector = false;
            if (!Object.ReferenceEquals(selectors, null))
            {
                int selectorIndex;
                for (selectorIndex = 0; selectorIndex < selectors.Count;
                     ++selectorIndex)
                {
                    DecisionConditionalExpression selector = selectors[selectorIndex];
                    if (!Object.ReferenceEquals(selector, null) &&
                        selector.DecisionType == SPTESL.DecisionType.Card)
                    {
                        hasCardSelector = true;
                        break;
                    }
                }
            }

            if (!hasCardSelector && !Object.ReferenceEquals(selectors, null) &&
                selectors.Count > 0)
                return result;

            ServerCard targetSource = GetTriggeredEffectTargetingSource(
                state, context);
            List<Effect> effects = Object.ReferenceEquals(
                context.TriggeredEffect.EffectGroup, null)
                ? null
                : context.TriggeredEffect.EffectGroup.Effects;
            List<ServerCard> candidates =
                BoardPlacementMasks.TriggerCandidates(state.Simulation.CardCatalog,hasCardSelector);
            int i;
            for (i = 0; i < candidates.Count; ++i)
            {
                ServerCard target = candidates[i];
                if (Object.ReferenceEquals(target, null))
                    continue;

                /* Explicit target legality normally rejects cards in Hand.
                 * Retail effects such as Cornerclub Gambler intentionally
                 * target a hand card; their retail selectors below are the
                 * legality gate for that hidden-zone choice. */
                bool targetInHand =
                    target.CurrentGameZoneName == GameZoneName.Hand;
                if (!targetInHand &&
                    !Object.ReferenceEquals(targetSource, null) &&
                     !target.CanBeExplicitlyTargetedByCard(
                         targetSource, effects, false))
                    continue;

                if (TriggeredEffectCardSelectorsMatch(
                    state, context, target, selectors))
                    result.Add(target);
            }
            return result;
        }

        private static bool TriggeredEffectCardSelectorsMatch(
            HostState state,
            ServerEffectUtilities.TriggerContext context,
            ServerCard target,
            List<DecisionConditionalExpression> selectors)
        {
            if (Object.ReferenceEquals(selectors, null) || selectors.Count == 0)
                return true;

            PlayerState selfPlayer = null;
            PlayerState opponentPlayer = null;
            if (!Object.ReferenceEquals(context.EffectOwner, null))
            {
                selfPlayer = FindPlayerByUid(
                    state.Simulation.Players,
                    context.EffectOwner.OwnerNetId);
                int i;
                for (i = 0; i < state.Simulation.Players.Count; ++i)
                {
                    if (Object.ReferenceEquals(selfPlayer, null) ||
                        state.Simulation.Players[i].NetId.Id != selfPlayer.NetId.Id)
                    {
                        opponentPlayer = state.Simulation.Players[i];
                        break;
                    }
                }
            }
            PlayerState eventTargetPlayer = Object.ReferenceEquals(
                context.TriggerEvent.Target, null)
                ? null
                : FindPlayerByUid(
                    state.Simulation.Players,
                    context.TriggerEvent.Target.OwnerNetId);
            PlayerState decisionTargetPlayer = FindPlayerByUid(
                state.Simulation.Players, target.OwnerNetId);

            CardConditionalArguments cardArguments = new CardConditionalArguments(
                state.Simulation.CardCatalog,
                context.EffectOwner,
                context.TriggerEvent.Inciter,
                context.TriggerEvent.Target,
                target,
                null,
                LaneIdentifier.GetInvalid());
            PlayerConditionalArguments playerArguments =
                new PlayerConditionalArguments(
                    state.Simulation.CardCatalog,
                    selfPlayer,
                    context.TriggerEvent.InciterPlayerState,
                    eventTargetPlayer,
                    decisionTargetPlayer,
                    opponentPlayer);
            TriggerConditionalArguments triggerArguments =
                new TriggerConditionalArguments(
                    context.TriggerEvent.Value,
                    context.TriggerEvent.AttributeType,
                    context.ActingLaneIndex,
                    context.ActingLaneIndexType);

            bool foundCardSelector = false;
            int selectorIndex;
            for (selectorIndex = 0; selectorIndex < selectors.Count;
                 ++selectorIndex)
            {
                DecisionConditionalExpression selector = selectors[selectorIndex];
                if (Object.ReferenceEquals(selector, null) ||
                    selector.DecisionType != SPTESL.DecisionType.Card)
                    continue;
                foundCardSelector = true;

                bool matches = true;
                if (!Object.ReferenceEquals(
                    selector.CardConditionalExpressions, null))
                {
                    int j;
                    for (j = 0;
                         j < selector.CardConditionalExpressions.Count;
                         ++j)
                    {
                        ICardConditionalExpression expression =
                            selector.CardConditionalExpressions[j];
                        if (Object.ReferenceEquals(expression, null) ||
                            !expression.Evaluate(
                                target,
                                cardArguments,
                                playerArguments,
                                triggerArguments))
                        {
                            matches = false;
                            break;
                        }
                    }
                }
                if (matches)
                    return true;
            }
            return !foundCardSelector;
        }

        private static List<CcgRpc.BoardLaneTarget> GatherTriggeredEffectLaneTargets(
            HostState state,
            ServerEffectUtilities.TriggerContext context)
        {
            List<CcgRpc.BoardLaneTarget> result =
                new List<CcgRpc.BoardLaneTarget>();
            if (!IsDecisionTriggerContext(context) ||
                !ServerEffectUtilities.TestBeforeTargetingEffectConditions(
                    TriggerState(state), context))
                return result;

            List<DecisionConditionalExpression> selectors =
                context.TriggeredEffect.TargetSelectors;
            if (Object.ReferenceEquals(selectors, null) || selectors.Count == 0)
                return result;

            PlayerState selfPlayer = null;
            PlayerState opponentPlayer = null;
            if (!Object.ReferenceEquals(context.EffectOwner, null))
            {
                selfPlayer = FindPlayerByUid(
                    state.Simulation.Players,
                    context.EffectOwner.OwnerNetId);
            }
            if (!Object.ReferenceEquals(selfPlayer, null))
            {
                opponentPlayer = state.Simulation.Players[0].NetId.Id ==
                    selfPlayer.NetId.Id
                    ? state.Simulation.Players[1]
                    : state.Simulation.Players[0];
            }
            else
            {
                selfPlayer = state.Simulation.Players[state.ActivePlayerIndex];
                opponentPlayer = state.Simulation.Players[
                    state.ActivePlayerIndex == 0 ? 1 : 0];
            }

            int selectorIndex;
            for (selectorIndex = 0; selectorIndex < selectors.Count;
                 ++selectorIndex)
            {
                DecisionConditionalExpression selector = selectors[selectorIndex];
                if (Object.ReferenceEquals(selector, null) ||
                    (selector.DecisionType != SPTESL.DecisionType.Lane &&
                     selector.DecisionType != SPTESL.DecisionType.LaneLeftOrRight))
                    continue;

                int ownerIndex;
                for (ownerIndex = 0; ownerIndex < 2; ++ownerIndex)
                {
                    PlayerState laneOwner = ownerIndex == 0
                        ? selfPlayer
                        : opponentPlayer;
                    int laneCount = state.Simulation.CardCatalog.FindLaneCount(
                        GameZoneName.CreatureBoard, laneOwner.NetId);

                    int laneIndex;
                    for (laneIndex = 0;
                         laneIndex < laneCount && laneIndex < 2;
                         ++laneIndex)
                    {
                        LaneIdentifier lane = new LaneIdentifier(
                            GameZoneName.CreatureBoard,
                            laneOwner.NetId,
                            laneIndex);
                        if (!TriggeredEffectLaneSelectorMatches(
                            state,
                            context,
                            selfPlayer,
                            opponentPlayer,
                            laneOwner,
                            lane,
                            selector))
                            continue;

                        CcgRpc.BoardLaneTarget encoded = EncodeLaneTarget(
                            ownerIndex == 0, laneIndex);
                        if (encoded != CcgRpc.BoardLaneTarget.None &&
                            !result.Contains(encoded))
                            result.Add(encoded);
                    }
                }
            }
            return result;
        }

        private static bool TriggeredEffectLaneSelectorMatches(
            HostState state,
            ServerEffectUtilities.TriggerContext context,
            PlayerState selfPlayer,
            PlayerState opponentPlayer,
            PlayerState laneOwner,
            LaneIdentifier lane,
            DecisionConditionalExpression selector)
        {
            PlayerState eventTargetPlayer = Object.ReferenceEquals(
                context.TriggerEvent.Target, null)
                ? null
                : FindPlayerByUid(
                    state.Simulation.Players,
                    context.TriggerEvent.Target.OwnerNetId);
            CardConditionalArguments cardArguments = new CardConditionalArguments(
                state.Simulation.CardCatalog,
                context.EffectOwner,
                context.TriggerEvent.Inciter,
                context.TriggerEvent.Target,
                null,
                null,
                lane);
            PlayerConditionalArguments playerArguments =
                new PlayerConditionalArguments(
                    state.Simulation.CardCatalog,
                    selfPlayer,
                    context.TriggerEvent.InciterPlayerState,
                    eventTargetPlayer,
                    laneOwner,
                    opponentPlayer);
            LaneConditionalArguments laneArguments = new LaneConditionalArguments(
                state.Simulation.CardCatalog,
                LaneIdentifier.GetInvalid(),
                lane);

            if (Object.ReferenceEquals(selector.LaneConditionalExpressions, null))
                return true;
            int i;
            for (i = 0; i < selector.LaneConditionalExpressions.Count; ++i)
            {
                ILaneConditionalExpression expression =
                    selector.LaneConditionalExpressions[i];
                if (Object.ReferenceEquals(expression, null) ||
                    !expression.Evaluate(
                        lane,
                        laneArguments,
                        cardArguments,
                        playerArguments))
                    return false;
            }
            return true;
        }

        private static bool ValidateTriggeredEffectDecision(
            HostState state,
            ServerEffectUtilities.TriggerContext context,
            CcgRpc.Decision decision)
        {
            if (IsCustomUiDecisionContext(state, context))
            {
                if (decision.Type == CcgRpc.DecisionType.NoCommand)
                    return context.TriggeredEffect.OptionalCustomUiType;
                if (decision.Type != CcgRpc.DecisionType.CustomChoice)
                    return false;

                int choiceCount = Math.Max(
                    state.CustomUiCards.Count,
                    context.TriggeredEffect.CustomUiEffectGroups.Count);
                return decision.CustomChoice >= 0 &&
                       decision.CustomChoice < choiceCount;
            }

            if (!IsDecisionTriggerContext(context))
                return false;
            if (decision.Type == CcgRpc.DecisionType.NoCommand)
                return context.TriggeredEffect.ClientTargetingType ==
                    PlayTargetingType.OptionalTarget;
            if (decision.Type != CcgRpc.DecisionType.Activate)
                return false;

            CardNetUid targetUid = new CardNetUid(decision.TargetCardNetUid);
            if (targetUid.IsValid())
            {
                List<ServerCard> targets = GatherTriggeredEffectCardTargets(
                    state, context);
                int i;
                for (i = 0; i < targets.Count; ++i)
                {
                    if (targets[i].NetUid.Id == targetUid.Id)
                        return true;
                }
                return false;
            }

            if (decision.LaneTarget != CcgRpc.BoardLaneTarget.None)
            {
                List<CcgRpc.BoardLaneTarget> lanes =
                    GatherTriggeredEffectLaneTargets(state, context);
                return lanes.Contains(decision.LaneTarget);
            }

            return context.TriggeredEffect.ClientTargetingType ==
                PlayTargetingType.OptionalTarget;
        }

        private static bool ExecuteActiveTriggeredEffectDecision(
            HostState state,
            ServerEffectUtilities.TriggerContext expectedContext,
            CcgRpc.Decision decision)
        {
            if (state.TriggerQueues.Active.Count == 0)
                return false;

            ServerEffectUtilities.TriggerContext context =
                state.TriggerQueues.Active.Peek();
            if (context.QueueAddCounter != expectedContext.QueueAddCounter ||
                !Object.ReferenceEquals(
                    context.TriggeredEffect, expectedContext.TriggeredEffect))
            {
                Plugin.Warn("trigger queue changed while awaiting decision");
                return false;
            }
            state.TriggerQueues.Active.Dequeue();

            ServerEffectUtilities.TriggerGameState triggerState = TriggerState(state);
            bool cornerclubDiscard = !Object.ReferenceEquals(context.EffectOwner, null) &&
                VariantCardRules.CanonicalId(context.EffectOwner.CardId) == -1375017618;
            List<HistoryItem> cornerclubHistory = cornerclubDiscard
                ? HistoryMessagesField.GetValue(state.Simulation.HistoryLog) as List<HistoryItem>
                : null;
            int cornerclubHistoryStart = Object.ReferenceEquals(cornerclubHistory, null)
                ? 0 : cornerclubHistory.Count;
            StartTriggerEventLogWithMechanicFx(
                state.Simulation.HistoryLog, ref context);

            bool customStage = context.DidMainEffectsRun;
            CcgRpc.Decision executionDecision =
                (!customStage && decision.Type == CcgRpc.DecisionType.NoCommand)
                ? null
                : decision;
            ServerEffectUtilities.TestAndExecuteTriggeredEffectFromQueue(
                triggerState, context, executionDecision);
            if (!Object.ReferenceEquals(cornerclubHistory, null))
            {
                for (int h = cornerclubHistoryStart; h < cornerclubHistory.Count; ++h)
                {
                    HistoryItem item = cornerclubHistory[h];
                    if (item.Type != CcgRpc.HistoryType.ActionMoveCards)
                        continue;
                    foreach (HistoryCard card in item.AffectedCardsArray)
                    {
                        if (card.PreviousGameZoneType == (int)GameZoneType.Hand &&
                            !Object.ReferenceEquals(card.CurrentPosition, null) &&
                            card.CurrentPosition.GameZoneName == (int)GameZoneName.Deathrattle &&
                            card.CardId != 0)
                        {
                            card.IsFullState = true;
                            card.IsRevealed = true;
                        }
                    }
                }
            }

            if (!customStage)
            {
                context.DidMainEffectsRun = true;
                if (context.TriggeredEffect.CustomUiType != CustomCardUiType.None &&
                    ServerEffectUtilities.IsPlayerDecisionRequired(
                        context, state.CustomUiCards))
                {
                    /* Preserve StartLogged and the open EventTrigger.  The
                     * second-stage CustomChoice completes the same retail event. */
                    state.TriggerQueues.Active.Enqueue(context);
                    return true;
                }
            }
            else if (context.TriggeredEffect.CustomUiType != CustomCardUiType.None)
            {
                HistoryItem endCustomUi = new HistoryItem();
                endCustomUi.Type = CcgRpc.HistoryType.ActionEndCustomUi;
                endCustomUi.ActorCardNetUid = Object.ReferenceEquals(
                    context.EffectOwner, null)
                    ? 0
                    : context.EffectOwner.NetUid.Id;
                state.Simulation.HistoryLog.AddInstant(endCustomUi);
            }

            ServerEffectUtilities.EndTriggerEventLog(
                triggerState, ref context);
            if (customStage && !Object.ReferenceEquals(context.EffectOwner, null) &&
                (context.TriggeredEffect.TriggerType == EffectTriggerType.PrepareExalt ||
                 context.TriggeredEffect.TriggerType == EffectTriggerType.PrepareExaltWithRing) &&
                context.EffectOwner.QueryAttribute(RareAttributeType.ShouldExalt).Value > 0)
            {
                ServerEffectUtilities.TriggerEffectEvent(
                    triggerState,
                    new ServerEffectUtilities.TriggerEvent(
                        EffectTriggerType.Exalt,
                        context.EffectOwner,
                        state.Simulation.Players));
            }
            state.CustomUiCards.Clear();
            ResolveDyingNonAvatarCards(state);
            CheckForRuneBreaks(state);
            CheckForAvatarDeath(state);
            return true;
        }

        private static bool AdvanceNoTargetCustomUiContext(
            HostState state,
            ServerEffectUtilities.TriggerContext expectedContext)
        {
            if (state.TriggerQueues.Active.Count == 0)
                return false;
            ServerEffectUtilities.TriggerContext context =
                state.TriggerQueues.Active.Peek();
            if (context.QueueAddCounter != expectedContext.QueueAddCounter ||
                !Object.ReferenceEquals(
                    context.TriggeredEffect, expectedContext.TriggeredEffect))
                return false;
            state.TriggerQueues.Active.Dequeue();

            ServerEffectUtilities.TriggerGameState triggerState = TriggerState(state);
            StartTriggerEventLogWithMechanicFx(
                state.Simulation.HistoryLog, ref context);
            ServerEffectUtilities.TestAndExecuteTriggeredEffectFromQueue(
                triggerState, context, null);
            context.DidMainEffectsRun = true;

            if (ServerEffectUtilities.IsPlayerDecisionRequired(
                context, state.CustomUiCards))
            {
                state.TriggerQueues.Active.Enqueue(context);
                return true;
            }

            ServerEffectUtilities.EndTriggerEventLog(
                triggerState, ref context);
            state.CustomUiCards.Clear();
            return true;
        }

        private static void ProcessHumanTriggeredEffectDecision(
            HostState state,
            ServerEffectUtilities.TriggerContext context,
            CcgRpc.Decision decision)
        {
            if (!ValidateTriggeredEffectDecision(state, context, decision))
            {
                Plugin.Warn("invalid triggered-effect decision type="
                    + decision.Type + " target=" + decision.TargetCardNetUid
                    + " lane=" + decision.LaneTarget);
                SendCommandList(state, 0);
                return;
            }

            if (!ExecuteActiveTriggeredEffectDecision(state, context, decision))
            {
                SendCommandList(state, 0);
                return;
            }

            DrainAutomaticTriggerQueues(state, false);
            ResolveDyingNonAvatarCards(state);
            DrainAutomaticTriggerQueues(state, true);
            state.Simulation.HistoryLog.SendUnsentMessages(
                state.Clients,
                GetObserverClients(state.Simulation),
                HistoryLog.FullGameStateSendRule.Exclude);

            if (IsGameFinished(state))
                return;

            if (state.ActivePlayerIndex == 1)
            {
                state.AiActionPending = true;
                state.AiActionFrame = Time.frameCount + 1;
                return;
            }

            if (state.PausedAttackVisualUid != 0)
            {
                state.PausedAttackHumanPromptSent = false;
                if (state.PendingProphecyPlayerIndex == 0)
                    SendCommandList(state, 0);
                return;
            }

            SendCommandList(state, 0);
        }

        private static ServerCard ChooseAiTriggeredEffectTarget(
            ServerEffectUtilities.TriggerContext context, List<ServerCard> targets)
        {
            bool harmful = false, beneficial = false;
            if (context.TriggeredEffect.EffectGroup != null)
                foreach (Effect effect in context.TriggeredEffect.EffectGroup.Effects)
                    foreach (CardEffectAction action in effect.CardActions)
                    {
                        bool targetsDecision = action.Selectors.Exists(selector => {
                            var role = selector as CardConditionals.CardRoleType;
                            return role != null && role.Role == CardRole.DecisionTarget
                                && role.Check == CardConditionals.CardRoleType.Requirement.Is;
                        });
                        if (!targetsDecision) continue;
                        harmful |= action.Action is CardActions.Damage || action.Action is CardActions.Debuff || action.Action is CardActions.Destroy
                            || action.Action is CardActions.Freeze || action.Action is CardActions.Fight;
                        beneficial |= action.Action is CardActions.Buff || action.Action is CardActions.Heal;
                    }
            ServerCard best = targets[0]; int bestScore = Int32.MinValue;
            foreach (ServerCard target in targets)
            {
                bool own = context.EffectOwner != null && target.OwnerNetId == context.EffectOwner.OwnerNetId;
                int score = harmful && !beneficial ? (own ? -10000 : 10000)
                    : beneficial && !harmful ? (own ? 10000 : -10000) : 0;
                if (harmful && !beneficial) score += target.QueryAttribute(AttributeType.Attack).Value;
                if (score > bestScore) { best = target; bestScore = score; }
            }
            return best;
        }

        private static bool ResolveAiTriggeredEffectDecision(
            HostState state,
            ServerEffectUtilities.TriggerContext context)
        {
            CcgRpc.Decision decision = new CcgRpc.Decision();
            if (IsCustomUiDecisionContext(state, context))
            {
                int choiceCount = Math.Max(
                    state.CustomUiCards.Count,
                    context.TriggeredEffect.CustomUiEffectGroups.Count);
                if (choiceCount <= 0)
                    return false;
                decision.Type = CcgRpc.DecisionType.CustomChoice;
                decision.CustomChoice = 0;
                return ExecuteActiveTriggeredEffectDecision(
                    state, context, decision);
            }

            List<ServerCard> cardTargets = GatherTriggeredEffectCardTargets(
                state, context);
            List<CcgRpc.BoardLaneTarget> laneTargets =
                GatherTriggeredEffectLaneTargets(state, context);

            decision.Type = CcgRpc.DecisionType.Activate;
            if (cardTargets.Count > 0)
            {
                ServerCard selected = ChooseAiTriggeredEffectTarget(context, cardTargets);
                decision.TargetCardNetUid = selected.NetUid.Id;
                Plugin.Log("AI effect choice: source=" + (context.EffectOwner==null?0:context.EffectOwner.CardId)
                    + " premium=" + (context.EffectOwner!=null && context.EffectOwner.Premium)
                    + " target=" + selected.CardId + "/" + selected.NetUid.Id
                    + " legalTargets=" + cardTargets.Count);
            }
            else if (laneTargets.Count > 0)
                decision.LaneTarget = laneTargets[0];
            else if (context.TriggeredEffect.ClientTargetingType ==
                     PlayTargetingType.OptionalTarget)
                decision.Type = CcgRpc.DecisionType.NoCommand;
            else
            {
                /* A required effect with no legal target fizzles rather than
                 * selecting an illegal target. */
                decision.Type = CcgRpc.DecisionType.NoCommand;
            }

            return ExecuteActiveTriggeredEffectDecision(
                state, context, decision);
        }

        private static bool PrepareQueuedEffectsForCommand(
            HostState state,
            out ServerEffectUtilities.TriggerContext humanContext)
        {
            humanContext = new ServerEffectUtilities.TriggerContext();
            int guard = 0;
            while (guard < 128)
            {
                DrainAutomaticTriggerQueues(state, false);
                ResolveDyingNonAvatarCards(state);
                DrainAutomaticTriggerQueues(state, true);

                if (!PromoteNextTriggerContext(state))
                    return false;

                ServerEffectUtilities.TriggerContext context =
                    state.TriggerQueues.Active.Peek();
                if (IsAutomaticTriggerContext(context))
                {
                    guard += 1;
                    continue;
                }

                /* Choice-free first stage, choice-bearing second stage. */
                if (!context.DidMainEffectsRun &&
                    !Object.ReferenceEquals(context.TriggeredEffect, null) &&
                    context.TriggeredEffect.ClientTargetingType ==
                        PlayTargetingType.NoTarget &&
                    context.TriggeredEffect.CustomUiType != CustomCardUiType.None)
                {
                    if (!AdvanceNoTargetCustomUiContext(state, context))
                        return false;
                    guard += 1;
                    continue;
                }

                if (!IsDecisionTriggerContext(context) &&
                    !IsCustomUiDecisionContext(state, context))
                {
                    Plugin.Warn("queued effect state not reconstructed: owner="
                        + (Object.ReferenceEquals(context.EffectOwner, null)
                            ? 0
                            : context.EffectOwner.CardId)
                        + " didMain=" + context.DidMainEffectsRun
                        + " targetType="
                        + (Object.ReferenceEquals(context.TriggeredEffect, null)
                            ? PlayTargetingType.NoTarget
                            : context.TriggeredEffect.ClientTargetingType)
                        + " customUi="
                        + (Object.ReferenceEquals(context.TriggeredEffect, null)
                            ? CustomCardUiType.None
                            : context.TriggeredEffect.CustomUiType));
                    return false;
                }

                int chooser = GetTriggerDecisionPlayerIndex(state, context);
                if (chooser == 0)
                {
                    humanContext = context;
                    return true;
                }

                if (!ResolveAiTriggeredEffectDecision(state, context))
                    return false;
                guard += 1;
            }

            Plugin.Warn("trigger decision guard reached");
            return false;
        }

        private static void SendTriggeredEffectCommandList(
            HostState state,
            ServerEffectUtilities.TriggerContext context)
        {
            PlayerState player = state.Simulation.Players[0];
            CardCommandList list = new CardCommandList();
            state.ListUid += 1;
            list.ListUid = state.ListUid;
            list.MustSelectCommand = true;
            list.IsMulliganPhase = false;

            CardCommandState command = new CardCommandState();
            command.CardNetUid = Object.ReferenceEquals(context.EffectOwner, null)
                ? 0
                : context.EffectOwner.NetUid.Id;

            if (IsCustomUiDecisionContext(state, context))
            {
                command.ValidCardCommandMask = (int)CardCommand.EffectGroupChoice;
                if (context.TriggeredEffect.OptionalCustomUiType)
                    command.ValidCardCommandMask |=
                        (int)CardCommand.DeclineToPlay;
                command.CustomCardUiEnum =
                    (int)context.TriggeredEffect.CustomUiType;
                command.NumActionChoices = Math.Max(
                    state.CustomUiCards.Count,
                    context.TriggeredEffect.CustomUiEffectGroups.Count);
                if (!String.IsNullOrEmpty(
                    context.TriggeredEffect.CustomUiPrompt.RawKey))
                    command.CustomPromptLocKey =
                        context.TriggeredEffect.CustomUiPrompt.RawKey;

                int c;
                for (c = 0; c < state.CustomUiCards.Count; ++c)
                {
                    CustomDisplayCard display = state.CustomUiCards[c];
                    if (!Object.ReferenceEquals(display, null) &&
                        !Object.ReferenceEquals(display.HistoryCard, null))
                        command.DisplayCards.Add(display.HistoryCard);
                }
            }
            else
            {
                List<ServerCard> cardTargets = GatherTriggeredEffectCardTargets(
                    state, context);
                List<CcgRpc.BoardLaneTarget> laneTargets =
                    GatherTriggeredEffectLaneTargets(state, context);

                if (context.TriggeredEffect.ClientTargetingType ==
                        PlayTargetingType.RequiredTarget &&
                    cardTargets.Count == 0 && laneTargets.Count == 0)
                {
                    Plugin.Warn(
                        "required triggered effect has no legal target; fizzled");
                    CcgRpc.Decision fizzle = new CcgRpc.Decision();
                    fizzle.Type = CcgRpc.DecisionType.NoCommand;
                    ExecuteActiveTriggeredEffectDecision(state, context, fizzle);
                    state.Simulation.HistoryLog.SendUnsentMessages(
                        state.Clients,
                        GetObserverClients(state.Simulation),
                        HistoryLog.FullGameStateSendRule.Exclude);
                    SendCommandList(state, 0);
                    return;
                }

                ServerCard source = GetTriggeredEffectTargetingSource(
                    state, context);
                command.SourceOverrideNetUid = Object.ReferenceEquals(source, null)
                    ? command.CardNetUid
                    : source.NetUid.Id;
                command.ValidCardCommandMask = (int)CardCommand.ChooseTarget;
                if (context.TriggeredEffect.ClientTargetingType ==
                    PlayTargetingType.OptionalTarget)
                    command.ValidCardCommandMask |=
                        (int)CardCommand.DeclineToPlay;

                int i;
                for (i = 0; i < cardTargets.Count; ++i)
                    command.ActiveAbilityTargets.Add(cardTargets[i].NetUid.Id);
                for (i = 0; i < laneTargets.Count; ++i)
                    command.LaneTargets.Add(laneTargets[i]);
                if (!String.IsNullOrEmpty(
                    context.TriggeredEffect.TargetingPrompt.RawKey))
                    command.TargetingPromptLocKey =
                        context.TriggeredEffect.TargetingPrompt.RawKey;
            }
            list.ValidCommands.Add(command);

            CardCommandItem item = new CardCommandItem();
            item.CommandList = list;
            CcgRpc.HistoryItem history = new CcgRpc.HistoryItem();
            history.Type = CcgRpc.HistoryType.CardCommandList;
            history.ActingPlayerInstanceId = player.NetId.Id;
            history.CardCommandItem = item;
            state.Simulation.HistoryLog.AddCardCommandItem(history);
            player.LastSentCommandItem = item;
            state.Simulation.HistoryLog.SendUnsentMessages(
                state.Clients,
                GetObserverClients(state.Simulation),
                HistoryLog.FullGameStateSendRule.Exclude);

            Plugin.Log("trigger CardCommandList sent: uid=" + list.ListUid
                + " source=" + command.CardNetUid
                + " customUi=" + command.CustomCardUiEnum
                + " targets=" + command.ActiveAbilityTargets.Count
                + " lanes=" + command.LaneTargets.Count
                + " choices=" + command.NumActionChoices);
        }

        private static ServerEffectUtilities.TriggerGameState TriggerState(HostState state)
        {
            return new ServerEffectUtilities.TriggerGameState(
                state.Simulation.HistoryLog,
                state.Simulation.RandomGenerator,
                state.Simulation.CardCatalog,
                state.Simulation.Players,
                state.TriggerQueues,
                state.Simulation.ActiveQuests,
                state.CustomUiCards,
                state.StartInfo.ScenarioId);
        }

        private static bool IsAutomaticTriggerContext(
            ServerEffectUtilities.TriggerContext context)
        {
            if (Object.ReferenceEquals(context.TriggeredEffect, null))
                return false;

            /* A NoTarget effect can still open a second-stage custom UI.
             * Auto-execute only contexts that are choice-free in both stages. */
            return context.TriggeredEffect.ClientTargetingType ==
                       PlayTargetingType.NoTarget &&
                   context.TriggeredEffect.CustomUiType == CustomCardUiType.None;
        }

        private static bool DrainAutomaticTriggerQueues(
            HostState state,
            bool includeAfterQueue)
        {
            ServerEffectUtilities.TriggerGameState triggerState = TriggerState(state);
            int guard = 0;

            while (guard < 256)
            {
                // Refresh this recovered continuous aura before selecting the
                // next effect; native attribute-change triggers join the queue.
                bool auraScope = !HasOpenHistoryEvent(state.Simulation.HistoryLog);
                if (auraScope)
                    state.Simulation.HistoryLog.StartEvent(CcgRpc.HistoryType.EventTrigger,
                        PlayerUniqueId.GetInvalid(), CardNetUid.GetInvalid(), CardNetUid.GetInvalid(), -1);
                try
                {
                    AgilityCardRules.RefreshHiddenTrailAuras(triggerState);
                    NaryuAuraRules.Refresh(triggerState);
                    RecoveredAuraRules.Refresh(triggerState);
                    RecoveredOngoingRules.RefreshPersistentShackles(triggerState);
                    RetailTextSummonCompiler.RefreshFriendlyAuras(triggerState);
                    ObservedAiRecovery.RefreshSupportAuras(triggerState);
                    SharedStatFamilyRules.RefreshContinuousAuras(triggerState);
                    ObservedCardBatch02.RefreshEquipmentAuras(triggerState);
                    ObservedCardBatch03.RefreshAuras(triggerState);
                    ExternalCardRecovery.RefreshAuras(triggerState);
                    ReferenceSummonRules.RefreshDiscounts(triggerState);
                }
                finally
                {
                    if (auraScope) state.Simulation.HistoryLog.EndEvent();
                }
                QueueAutomaticExaltPrompts(state, triggerState);
                ServerEffectUtilities.TriggerContext context =
                    new ServerEffectUtilities.TriggerContext();
                bool haveContext = false;

                if (state.TriggerQueues.Active.Count > 0)
                {
                    context = state.TriggerQueues.Active.Peek();
                    if (!IsAutomaticTriggerContext(context))
                        return false;
                    state.TriggerQueues.Active.Dequeue();
                    haveContext = true;
                }
                else if (state.TriggerQueues.Pending.Count > 0)
                {
                    state.TriggerQueues.Pending.Sort(
                        ServerEffectUtilities.TriggerContext.SortComparison);
                    context = state.TriggerQueues.Pending[0];
                    if (!IsAutomaticTriggerContext(context))
                        return false;
                    state.TriggerQueues.Pending.RemoveAt(0);
                    haveContext = true;
                }
                else if (includeAfterQueue && state.TriggerQueues.After.Count > 0)
                {
                    state.TriggerQueues.After.Sort(
                        ServerEffectUtilities.TriggerContext.SortComparison);
                    context = state.TriggerQueues.After[0];
                    if (!IsAutomaticTriggerContext(context))
                        return false;
                    state.TriggerQueues.After.RemoveAt(0);
                    haveContext = true;
                }
                else
                {
                    break;
                }

                if (!haveContext)
                    break;

                StartTriggerEventLogWithMechanicFx(
                    state.Simulation.HistoryLog, ref context);
                ServerEffectUtilities.TestAndExecuteTriggeredEffectFromQueue(
                    triggerState, context, null);
                ServerEffectUtilities.EndTriggerEventLog(
                    triggerState, ref context);
                guard += 1;
            }

            if (guard >= 256)
                Plugin.Warn("automatic trigger drain guard reached");

            if (state.TriggerQueues.CardDrawInterrupt.Count > 0)
            {
                Plugin.Warn("card-draw interrupt queued; decision reconstruction required");
                return false;
            }

            return state.TriggerQueues.Active.Count == 0 &&
                   state.TriggerQueues.Pending.Count == 0 &&
                   (!includeAfterQueue || state.TriggerQueues.After.Count == 0);
        }

        private static void QueueAutomaticExaltPrompts(
            HostState state,
            ServerEffectUtilities.TriggerGameState triggerState)
        {
            List<ServerCard> cards = state.Simulation.CardCatalog.GatherDynamicCardList();
            HashSet<int> eligible = new HashSet<int>();
            for (int i = 0; i < cards.Count; ++i)
            {
                ServerCard card = cards[i];
                if (card.CurrentGameZoneName != GameZoneName.CreatureBoard ||
                    card.Silenced || card.IsDying())
                    continue;
                int ownerIndex = state.Simulation.Players.FindIndex(delegate(PlayerState p)
                {
                    return p.NetId == card.OwnerNetId;
                });
                if (ownerIndex < 0 || ownerIndex != state.ActivePlayerIndex ||
                    state.StartInfo.Users[ownerIndex].Type != PlayerType.Normal)
                    continue;
                PlayerState owner = state.Simulation.Players[ownerIndex];
                if (card.QueryAttribute(AttributeType.ExaltCost).Value <= 0 ||
                    !CanOfferExalt(owner, card))
                    continue;
                List<TriggeredEffect> prepare = card.TriggeredEffects.FindAll(
                    delegate(TriggeredEffect effect)
                    {
                        return effect.TriggerType == EffectTriggerType.PrepareExalt;
                    });
                if (prepare.Count == 0)
                    continue;
                int uid = card.NetUid.Id;
                eligible.Add(uid);
                if (!state.AutoExaltPrompted.Add(uid))
                    continue;
                ServerEffectUtilities.TriggerEffectEvent(
                    triggerState,
                    new ServerEffectUtilities.TriggerEvent(
                        EffectTriggerType.PrepareExalt,
                        card,
                        state.Simulation.Players));
                Plugin.Log("automatic exalt prompt queued: player=" + owner.NetId.Id
                    + " card=" + card.CardId + " cost="
                    + card.QueryAttribute(AttributeType.ExaltCost).Value);
            }
            state.AutoExaltPrompted.RemoveWhere(delegate(int uid)
            {
                return !eligible.Contains(uid);
            });
        }

        private static GameState GetGameState(Simulation simulation)
        {
            if (Object.ReferenceEquals(GameStateField, null))
                throw new InvalidOperationException("Simulation._gameState not found");
            return (GameState)GameStateField.GetValue(simulation);
        }

        private static Stack<TurnState> GetTurnStack(Simulation simulation)
        {
            if (Object.ReferenceEquals(TurnStateStackField, null))
                throw new InvalidOperationException("Simulation._turnStateStack not found");
            return (Stack<TurnState>)TurnStateStackField.GetValue(simulation);
        }

        private static List<PlayerServiceClient> GetObserverClients(Simulation simulation)
        {
            if (Object.ReferenceEquals(ObserverClientsField, null))
                return new List<PlayerServiceClient>();
            return (List<PlayerServiceClient>)ObserverClientsField.GetValue(simulation);
        }

        internal static void Stop(Simulation simulation)
        {
            if (Object.ReferenceEquals(simulation, null))
                return;
            States.Remove(simulation);
            Plugin.LocalMatchStopped();
        }

        private static void SetField(
            FieldInfo field,
            object instance,
            object value,
            string name)
        {
            if (Object.ReferenceEquals(field, null))
            {
                Plugin.Warn("Simulation field not found: " + name);
                return;
            }
            field.SetValue(instance, value);
        }
    }
}
