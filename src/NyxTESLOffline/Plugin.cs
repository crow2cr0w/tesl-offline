using System.Collections.Generic;
using HarmonyLib;
using Pixelplacement;
using SparkleServices;
using SPTESL;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NyxTESLOffline
{
    public sealed class Plugin : MonoBehaviour
    {
        internal static Plugin Instance;

        private Harmony _harmony;
        private LocalControlPlane _controlPlane;
        private LocalContentStore _contentStore;
        private bool _offlineReady;
        private bool _startIssued;
        private bool _contentReported;
        private bool _ruleOverlayStarted;
        private float _localMatchStartedAt = -1f;
        private bool _boardFallbackIssued;
        private static OfflineMatchRequest _pendingLocalMatch;
        private static OfflineMatchRequest _activeLocalMatch;
        private static List<bool> _preparedHumanPremiumFlags;
        private static bool _startingOfflineMatch;
        private static readonly object CampaignProgressSync = new object();
        private static int _pendingCompletedCampaignEventId;
        private static bool _pendingCompletedCampaignIsMastery;
        private static bool _pendingCampaignVictory;
        private static readonly System.Reflection.FieldInfo MatchingRequestIdField =
            AccessTools.Field(typeof(Matching), "_currMatchRequestId");
        private static readonly System.Reflection.FieldInfo MatchingPoolField =
            AccessTools.Field(typeof(Matching), "_currMatchPool");
        private static readonly System.Reflection.FieldInfo MatchingReadyField =
            AccessTools.Field(typeof(Matching), "_matchReady");
        private static readonly System.Reflection.MethodInfo EndConnectionToServerMethod =
            AccessTools.Method(typeof(GameNetworkManager), "EndConnectionToServer");

        internal static bool HasPendingLocalMatch
        {
            get { return !System.Object.ReferenceEquals(_pendingLocalMatch, null); }
        }

        internal static bool IsStartingOfflineMatch
        {
            get { return _startingOfflineMatch; }
        }

        internal static OfflineMatchRequest ActiveLocalMatch
        {
            get { return _activeLocalMatch; }
        }

        internal static bool RandomizeLocalPlaymat
        {
            get
            {
                if (System.Object.ReferenceEquals(_activeLocalMatch, null))
                    return false;

                return LocalPlaymatPolicy.ShouldRandomize(
                    (int)_activeLocalMatch.MatchType,
                    (int)_activeLocalMatch.MatchMode,
                    _activeLocalMatch.MatchMode == Matching.IMatchMode.Campaign);
            }
        }

        internal static string SelectedCardBack
        {
            get
            {
                if (System.Object.ReferenceEquals(Instance, null) ||
                    System.Object.ReferenceEquals(Instance._contentStore, null))
                    return "cardback_legend_alt_premium";
                return Instance._contentStore.SelectedCardBack;
            }
        }

        internal static string RandomCardBack(RandomIntegerGenerator random)
        {
            if (System.Object.ReferenceEquals(Instance, null) ||
                System.Object.ReferenceEquals(Instance._contentStore, null))
                return "cardback_legend_alt_premium";
            return Instance._contentStore.RandomCardBack(random);
        }

        private void Awake()
        {
            try {
                string logDir=System.IO.Path.Combine(UnityEngine.Application.dataPath,"Managed/NyxTESLOffline-data");
                string logPath=System.IO.Path.Combine(logDir,"nyx-inplace.log");
                if(System.IO.File.Exists(logPath))System.IO.File.Delete(logPath);
            } catch { }
            Instance = this;
            Log("INPLACE-MARKER 0.6.0-alpha14-bridge18 bootstrap reached (semantic-recovery-batch03; log="+(DiagnosticLogPolicy.Verbose?"verbose":"compact")+")");

            /*
             * Bootstrap is intentionally transactional:
             *   1. load/validate local data;
             *   2. bind 127.0.0.1:3000;
             *   3. only then patch FetchConfig/HTTP/service behavior.
             *
             * The old 0.4.0 order patched FetchConfig first.  A content-store
             * failure therefore redirected the retail client to a listener that
             * did not exist and caused FailedConfigFetch.  Never install routing
             * patches until the local endpoint is actually available.
             */
            try
            {
                _contentStore = new LocalContentStore();
                _contentStore.Load();

                _controlPlane = new LocalControlPlane(_contentStore);
                _controlPlane.Start();

                if (!_controlPlane.IsRunning)
                    throw new System.InvalidOperationException(
                        "loopback control plane failed to enter running state");

                _harmony = new Harmony("org.nyx.tesl.offline");
                _harmony.PatchAll(typeof(Plugin).Assembly);

                _offlineReady = true;
            }
            catch (System.Exception ex)
            {
                _offlineReady = false;

                if (!System.Object.ReferenceEquals(_harmony, null))
                {
                    try { _harmony.UnpatchSelf(); }
                    catch { }
                    _harmony = null;
                }

                if (!System.Object.ReferenceEquals(_controlPlane, null))
                {
                    try { _controlPlane.Dispose(); }
                    catch { }
                    _controlPlane = null;
                }

                UnityEngine.Debug.LogError(
                    "[NYX-OFFLINE] offline bootstrap failed before patches were installed: " + ex);
                UnityEngine.Debug.LogError(
                    "[NYX-OFFLINE] plugin disabled for this run; F8 and local routing are inactive");
                return;
            }

            Log("loaded; original ServicesManager flow retained on loopback");
            Log("managed HTTP guard: non-loopback destinations are blocked");
            Log("0.6.0-alpha14: retail rule-data overlay + canonical card placement + targeting active");
            Log("bridge18: ten-card independent-reference test batch active; prior rules active");
            Log("cards/campaigns/decks remain local; F8 remains as a direct diagnostic match shortcut");
        }

        private void OnDestroy()
        {
            _offlineReady = false;

            if (!System.Object.ReferenceEquals(_harmony, null))
                _harmony.UnpatchSelf();

            if (!System.Object.ReferenceEquals(_controlPlane, null))
                _controlPlane.Dispose();
        }

        private void Update()
        {
            if (!_offlineReady)
                return;

            ReportContentReadiness();
            EnsureRetailRuleOverlay();
            ProcessPendingLocalMatch();

            /* Pump TCP attach / InitClient every frame while a local match is
             * live.  Simulation.FixedUpdate alone is not enough: the host
             * MonoBehaviour may not receive FixedUpdate until CDGameBoard is
             * active, which previously produced RejectTimeout with a connected
             * TCP client and no InitClient. */
            if (!System.Object.ReferenceEquals(_activeLocalMatch, null))
            {
                try
                {
                    OfflineSimulationHost.TickAllActiveHosts();
                }
                catch (System.Exception ex)
                {
                    Warn("TickAllActiveHosts from Plugin.Update failed: " + ex);
                }
            }

            EnsureLocalGameBoardTransition();

            if (!_startIssued && Input.GetKeyDown(KeyCode.F8))
            {
                _startIssued = true;
                StartOfflineMatch();
            }
        }


        private void EnsureLocalGameBoardTransition()
        {
            if (System.Object.ReferenceEquals(_activeLocalMatch, null) ||
                _localMatchStartedAt < 0f)
                return;

            string sceneName = SceneManager.GetActiveScene().name;
            if (sceneName == "CDGameBoard")
            {
                if (!_boardFallbackIssued)
                {
                    _boardFallbackIssued = true;
                    Log("CDGameBoard scene active for local match"
                        + " hosts=" + OfflineSimulationHost.ActiveHostCount);
                }
                return;
            }

            if (_boardFallbackIssued)
                return;

            float elapsed =
                UnityEngine.Time.realtimeSinceStartup - _localMatchStartedAt;

            /* ExitState normally waits ~0.5 s and may also wait for an audio
             * fade.  Give the retail transition a short window, then force. */
            if (elapsed < 1.5f)
                return;

            GameNetworkManager manager = GameNetworkManager.Instance;
            if (System.Object.ReferenceEquals(manager, null))
                return;

            if (manager.IsOutsideOfMatch())
            {
                _boardFallbackIssued = true;
                Warn("board transition watchdog: local request is active but "
                    + "GameNetworkManager is outside match; scene not forced"
                    + " scene=" + sceneName
                    + " elapsed=" + elapsed.ToString("0.00"));
                return;
            }

            _boardFallbackIssued = true;
            Warn("board transition watchdog: retail transition did not reach "
                + "CDGameBoard; forcing scene now"
                + " scene=" + sceneName
                + " elapsed=" + elapsed.ToString("0.00")
                + " hosts=" + OfflineSimulationHost.ActiveHostCount);
            try
            {
                if (!System.Object.ReferenceEquals(
                        Singleton<SettingsManager>.Instance, null))
                    Singleton<SettingsManager>.Instance.HasGameLoaded = true;
            }
            catch { }
            SceneManager.LoadScene("CDGameBoard");
        }

        private void ReportContentReadiness()
        {
            if (_contentReported)
                return;

            ServicesManager services = Singleton<ServicesManager>.Instance;
            if (System.Object.ReferenceEquals(services, null) ||
                System.Object.ReferenceEquals(services.Inventory, null) ||
                System.Object.ReferenceEquals(services.Campaign, null))
                return;

            if (!services.Inventory.IsReady() || !services.Campaign.IsReady())
                return;

            int cards = 0;
            int decks = 0;
            int campaigns = 0;

            if (!System.Object.ReferenceEquals(services.Inventory.InventoryDescription, null))
            {
                if (!System.Object.ReferenceEquals(
                        services.Inventory.InventoryDescription.cardTypes, null))
                    cards = services.Inventory.InventoryDescription.cardTypes.Count;

                if (!System.Object.ReferenceEquals(
                        services.Inventory.InventoryDescription.decks, null))
                    decks = services.Inventory.InventoryDescription.decks.Count;
            }

            if (!System.Object.ReferenceEquals(services.Campaign.CampaignDatas, null))
                campaigns = services.Campaign.CampaignDatas.Count;

            _contentReported = true;
            Log("local content accepted by retail client: cards=" + cards
                + " decks=" + decks + " campaigns=" + campaigns);
        }

        internal static void QueueLocalMatch(OfflineMatchRequest request)
        {
            _pendingLocalMatch = request;
        }

        internal static void CancelPendingLocalMatch()
        {
            _pendingLocalMatch = null;
        }

        internal static void LocalMatchStopped()
        {
            _pendingLocalMatch = null;
            _activeLocalMatch = null;
            _preparedHumanPremiumFlags = null;
            if (!System.Object.ReferenceEquals(Instance, null))
            {
                Instance._localMatchStartedAt = -1f;
                Instance._boardFallbackIssued = false;
            }

            ServicesManager services = Singleton<ServicesManager>.Instance;
            if (System.Object.ReferenceEquals(services, null) ||
                System.Object.ReferenceEquals(services.Matching, null))
                return;

            if (!System.Object.ReferenceEquals(MatchingRequestIdField, null))
                MatchingRequestIdField.SetValue(services.Matching, null);
            if (!System.Object.ReferenceEquals(MatchingPoolField, null))
                MatchingPoolField.SetValue(services.Matching, null);
            if (!System.Object.ReferenceEquals(MatchingReadyField, null))
                MatchingReadyField.SetValue(services.Matching, null);
        }

        internal static void RecordLocalMatchResult(bool firstLost, bool secondLost)
        {
            OfflineMatchRequest request = _activeLocalMatch;
            lock (CampaignProgressSync)
            {
                _pendingCampaignVictory = false;
                _pendingCompletedCampaignEventId = 0;
                _pendingCompletedCampaignIsMastery = false;

                if (request == null || request.MatchMode != Matching.IMatchMode.Campaign ||
                    request.CampaignData == null)
                    return;

                _pendingCompletedCampaignEventId = request.CampaignData.chapterEventId;
                _pendingCompletedCampaignIsMastery = request.CampaignData.isMastery;
                _pendingCampaignVictory = !firstLost && secondLost;
                Log("campaign match result recorded: event=" + _pendingCompletedCampaignEventId
                    + " mastery=" + _pendingCompletedCampaignIsMastery
                    + " humanVictory=" + _pendingCampaignVictory);
            }
        }

        internal static void CheckCampaignProgress(string body)
        {
            long campaignId;
            if (!LocalContentStore.TryReadInt64Field(body, "campaignId", out campaignId))
            {
                Log("campaign progress check ignored: campaignId missing");
                return;
            }

            lock (CampaignProgressSync)
            {
                if (!_pendingCampaignVictory || Instance == null || Instance._contentStore == null)
                {
                    Log("campaign progress check: no pending local campaign victory");
                    return;
                }

                bool updated = Instance._contentStore.MarkCampaignEventComplete(
                    campaignId,
                    _pendingCompletedCampaignEventId,
                    _pendingCompletedCampaignIsMastery);
                Log("campaign progress check applied: campaign=" + campaignId
                    + " event=" + _pendingCompletedCampaignEventId
                    + " updated=" + updated);
                if (updated)
                    _pendingCampaignVictory = false;
            }
        }

        private void EnsureRetailRuleOverlay()
        {
            if (_ruleOverlayStarted || RetailRuleDataOverlay.IsSettled)
                return;

            if (System.Object.ReferenceEquals(GameManager.Instance, null) ||
                System.Object.ReferenceEquals(GameManager.Instance.Config, null) ||
                !GameManager.Instance.Config.initialized)
                return;

            _ruleOverlayStarted = true;
            StartCoroutine(RetailRuleDataOverlay.Load(GameManager.Instance.Config));
        }

        private void ProcessPendingLocalMatch()
        {
            OfflineMatchRequest request = _pendingLocalMatch;
            if (System.Object.ReferenceEquals(request, null))
                return;

            /* PlayWin writes LastRetryMatchRequestData immediately after
             * Matching.MatchRequestAdd() returns. Do not change scenes from
             * inside the Harmony prefix; defer at least one Unity frame. */
            if (UnityEngine.Time.frameCount <= request.QueuedFrame)
                return;

            /* Do not materialize ServerCard instances until the optional retail
             * source-rule overlay has either completed or proved unavailable.
             * ServerCard copies TriggeredEffects/ActionPlayInformation during
             * construction, so merging the source config after Simulation.Start
             * would be too late for the current match. */
            if (!RetailRuleDataOverlay.IsSettled)
                return;

            ServicesManager services = Singleton<ServicesManager>.Instance;
            if (System.Object.ReferenceEquals(services, null) || !services.IsReady())
                return;

            GameNetworkManager manager = GameNetworkManager.Instance;
            if (System.Object.ReferenceEquals(manager, null) || !manager.IsOutsideOfMatch())
                return;

            _pendingLocalMatch = null;
            _activeLocalMatch = request;

            try
            {
                ConfigureScenario(manager, request);
                PrepareLocalDeckSelections(manager, request);

                Log("starting local AI match from UI request=" + request.RequestId
                    + " mode=" + request.MatchMode
                    + " type=" + request.MatchType);

                manager.StartSinglePlayerMatch(false, false, false);
                _localMatchStartedAt = UnityEngine.Time.realtimeSinceStartup;
                _boardFallbackIssued = false;
            }
            catch (System.Exception ex)
            {
                Warn("local AI match start failed: " + ex);

                /* StartSinglePlayerMatch creates the TCP listener/client before
                 * Simulation.Start.  If authoritative startup throws, close
                 * those half-created endpoints immediately instead of letting
                 * the retail connection timer reach the disconnect popup. */
                if (!System.Object.ReferenceEquals(EndConnectionToServerMethod, null))
                {
                    try
                    {
                        EndConnectionToServerMethod.Invoke(manager, null);
                        Log("aborted half-created local TCP match after startup failure");
                    }
                    catch (System.Exception cleanupEx)
                    {
                        Warn("failed to abort half-created local match: " + cleanupEx);
                    }
                }

                LocalMatchStopped();
            }
        }

        private static void ConfigureScenario(
            GameNetworkManager manager,
            OfflineMatchRequest request)
        {
            if (request.MatchMode != Matching.IMatchMode.Campaign ||
                System.Object.ReferenceEquals(request.CampaignData, null) ||
                System.String.IsNullOrEmpty(request.CampaignData.scenarioName))
            {
                manager.UseDefaultScenario();
                return;
            }

            string wanted = request.CampaignData.scenarioName;
            Scenario found = null;
            int i;
            for (i = 0; i < GameManager.Instance.Config.Scenarios.Count; ++i)
            {
                Scenario candidate = GameManager.Instance.Config.Scenarios[i];
                if (!System.Object.ReferenceEquals(candidate, null) &&
                    candidate.ResourceId.UniqueName == wanted)
                {
                    found = candidate;
                    break;
                }
            }

            if (!System.Object.ReferenceEquals(found, null))
            {
                manager.DebugScenarioId = found.ResourceId;
                Log("campaign scenario selected locally: " + wanted
                    + " id=" + found.ResourceId.Id);
                return;
            }

            UniqueResourceId scenarioId = new UniqueResourceId();
            scenarioId.UniqueName = wanted;
            scenarioId.CompletionType = ResourceCompletionType.Placeholder;
            scenarioId.RefreshId();
            if (!System.Object.ReferenceEquals(
                    GameManager.Instance.FindScenario(scenarioId.Id), null))
            {
                manager.DebugScenarioId = scenarioId;
                Log("campaign scenario selected by hash: " + wanted
                    + " id=" + scenarioId.Id);
                return;
            }

            Warn("campaign scenario not found locally: " + wanted
                + "; falling back to Default");
            manager.UseDefaultScenario();
        }

        private static void PrepareLocalDeckSelections(
            GameNetworkManager manager,
            OfflineMatchRequest request)
        {
            GameManager game = GameManager.Instance;
            if (System.Object.ReferenceEquals(game, null) ||
                System.Object.ReferenceEquals(game.Config, null))
                throw new System.InvalidOperationException("GameManager/config not ready");

            Scenario scenario = null;
            if (manager.DebugScenarioId.Id != UniqueResourceId.GetInvalid().Id)
                scenario = game.FindScenario(manager.DebugScenarioId.Id);

            /* The retail UIDeckUtils conversion drops numPremium entirely.
             * Rebuild the exact user-selected inventory deck here from the
             * matchmaking deckId, preserving every regular/premium copy. */
            _preparedHumanPremiumFlags = null;
            List<bool> premiumFlags;
            Deck exactHuman = BuildInventoryDeck(request.DeckId, out premiumFlags);
            if (!System.Object.ReferenceEquals(exactHuman, null))
            {
                game.SelectedDeck = exactHuman;
                _preparedHumanPremiumFlags = premiumFlags;
                Log("local human inventory deck restored: id=" + request.DeckId
                    + " name=" + exactHuman.Name
                    + " cards=" + exactHuman.Size
                    + " premium=" + CountTrue(premiumFlags));
            }
            else if (System.Object.ReferenceEquals(game.SelectedDeck, null))
            {
                Deck human = BuildScenarioDeck(scenario, true, request);
                if (System.Object.ReferenceEquals(human, null))
                    human = BuildPracticeDeck(request, "NyxHumanFallback", null);
                if (!System.Object.ReferenceEquals(human, null))
                {
                    game.SelectedDeck = human;
                    Log("local human fallback deck prepared in memory: " + human.Name
                        + " cards=" + human.Size);
                }
            }

            Deck ai = BuildScenarioDeck(scenario, false, request);
            if (System.Object.ReferenceEquals(ai, null) ||
                DecksEquivalent(ai, game.SelectedDeck))
                ai = BuildPracticeDeck(request, "NyxAiFallback", game.SelectedDeck);

            /* Retail PresetDeck.Cards is empty in this depot.  Never leave the
             * AI deck null here: LoadPlayerDescription(PlayerType.Ai) would then
             * fall through to LoadDeckFromDisk("StandardDeck"), whose embedded
             * Resources asset is absent in this install and dereferences null.
             * BuildSyntheticAiDeck already uses the loaded retail CardCollection,
             * so it is the correct no-disk fallback. */
            if (System.Object.ReferenceEquals(ai, null) ||
                DecksEquivalent(ai, game.SelectedDeck))
            {
                ai = BuildSyntheticAiDeck(game.SelectedDeck, "NyxAiSynthetic");
            }

            if (System.Object.ReferenceEquals(ai, null))
                throw new System.InvalidOperationException(
                    "unable to build a non-null AI deck from retail CardCollection");

            game.AiOpponentSelectedDeck = ai;
            Log("local AI deck prepared in memory: " + ai.Name
                + " cards=" + ai.Size + " (disk fallback bypassed)");
        }

        internal static void ApplyPreparedPremiumFlags(
            MatchConfiguration.StartInfo startInfo)
        {
            if (System.Object.ReferenceEquals(startInfo, null) ||
                System.Object.ReferenceEquals(startInfo.Users, null) ||
                startInfo.Users.Length == 0 ||
                System.Object.ReferenceEquals(_preparedHumanPremiumFlags, null))
                return;

            MatchConfiguration.User human = startInfo.Users[0];
            if (human.Type != PlayerType.Normal ||
                System.Object.ReferenceEquals(human.CustomDeck, null))
                return;

            if (_preparedHumanPremiumFlags.Count != human.CustomDeck.Deck.Count)
            {
                Warn("premium metadata size mismatch: flags="
                    + _preparedHumanPremiumFlags.Count
                    + " deck=" + human.CustomDeck.Deck.Count
                    + "; preserving retail flags");
                return;
            }

            human.CustomDeck.Premium.Clear();
            int i;
            for (i = 0; i < _preparedHumanPremiumFlags.Count; ++i)
                human.CustomDeck.Premium.Add(_preparedHumanPremiumFlags[i]);
            startInfo.Users[0] = human;

            Log("human premium metadata restored before Simulation.Start: cards="
                + human.CustomDeck.Deck.Count
                + " premium=" + CountTrue(_preparedHumanPremiumFlags));
        }

        private static Deck BuildInventoryDeck(long deckId, out List<bool> premiumFlags)
        {
            premiumFlags = null;

            ServicesManager services = Singleton<ServicesManager>.Instance;
            if (System.Object.ReferenceEquals(services, null) ||
                System.Object.ReferenceEquals(services.Inventory, null) ||
                System.Object.ReferenceEquals(services.Inventory.InventoryDescription, null) ||
                System.Object.ReferenceEquals(services.Inventory.InventoryDescription.decks, null))
                return null;

            Inventory.Deck source = null;
            if (deckId != 0)
            {
                source = services.Inventory.InventoryDescription.decks.Find(
                    delegate(Inventory.Deck candidate)
                    {
                        return !System.Object.ReferenceEquals(candidate, null) &&
                            candidate.id == deckId;
                    });
            }

            /* Some UI paths post deckId=0 even though SelectedDeck already
             * identifies the inventory deck by its display name.  Recover that
             * case rather than accepting UIDeckUtils' premium-truncated deck. */
            if (System.Object.ReferenceEquals(source, null) &&
                !System.Object.ReferenceEquals(GameManager.Instance.SelectedDeck, null))
            {
                string selectedName = GameManager.Instance.SelectedDeck.Name;
                if (!System.String.IsNullOrEmpty(selectedName))
                {
                    source = services.Inventory.InventoryDescription.decks.Find(
                        delegate(Inventory.Deck candidate)
                        {
                            return !System.Object.ReferenceEquals(candidate, null) &&
                                candidate.DisplayName == selectedName;
                        });
                }
            }

            if (System.Object.ReferenceEquals(source, null) ||
                System.Object.ReferenceEquals(source.cards, null))
                return null;

            Deck deck = new Deck();
            deck.Name = source.DisplayName;
            deck.AvatarCardId = 1264418727;
            premiumFlags = new List<bool>();

            int i;
            for (i = 0; i < source.cards.Count; ++i)
            {
                Inventory.Deck.DeckCard card = source.cards[i];
                if (System.Object.ReferenceEquals(card, null))
                    continue;

                int n;
                for (n = 0; n < card.numRegular; ++n)
                {
                    deck.AddCard(card.typeHash);
                    premiumFlags.Add(false);
                }
                for (n = 0; n < card.numPremium; ++n)
                {
                    deck.AddCard(card.typeHash);
                    premiumFlags.Add(true);
                }
            }

            return deck;
        }

        private static int CountTrue(List<bool> values)
        {
            if (System.Object.ReferenceEquals(values, null))
                return 0;
            int count = 0;
            int i;
            for (i = 0; i < values.Count; ++i)
                if (values[i])
                    count += 1;
            return count;
        }

        private static Deck BuildScenarioDeck(
            Scenario scenario,
            bool human,
            OfflineMatchRequest request)
        {
            if (System.Object.ReferenceEquals(scenario, null) ||
                System.Object.ReferenceEquals(scenario.DeckOverrides, null) ||
                System.Object.ReferenceEquals(GameManager.Instance.Config.CustomDecks, null))
                return null;

            int bits = human
                ? ((int)PlayerSlotMask.Human | (int)PlayerSlotMask.FirstPlayer)
                : ((int)PlayerSlotMask.Ai | (int)PlayerSlotMask.SecondPlayer);
            DeckDifficulty wanted = RequestedDeckDifficulty(request);
            DeckOverride chosen = null;
            int i;

            for (i = 0; i < scenario.DeckOverrides.Count; ++i)
            {
                DeckOverride item = scenario.DeckOverrides[i];
                if (System.Object.ReferenceEquals(item, null) ||
                    System.Object.ReferenceEquals(item.Override, null) ||
                    ((((int)item.SlotMask) & bits) == 0))
                    continue;

                if (System.Object.ReferenceEquals(chosen, null))
                    chosen = item;
                if (item.Difficulty == wanted)
                {
                    chosen = item;
                    break;
                }
            }

            if (System.Object.ReferenceEquals(chosen, null))
                return null;

            CustomDeck custom = FindCustomDeck(chosen.Override.resourceId);
            if (System.Object.ReferenceEquals(custom, null))
                return null;

            PresetDeck preset = FindPresetDeck(custom, wanted);
            if (System.Object.ReferenceEquals(preset, null))
                return null;

            /* PresetDeck.Cards is [NonSerialized] in the retail binary.  Do not
             * replace a usable local/fallback deck with an empty historical
             * preset merely because the scenario still references its ID. */
            if (System.Object.ReferenceEquals(preset.Cards, null) ||
                preset.Cards.Count == 0)
            {
                Log("scenario preset deck card list absent in retail binary: "
                    + custom.ResourceId.UniqueName
                    + "; keeping local fallback deck");
                return null;
            }

            Deck deck = UIDeckUtils.CreateGameConfigDeckFromCustomDeck(
                preset,
                custom.ResourceId.UniqueName);
            Log("scenario deck prepared before StartSinglePlayerMatch: side="
                + (human ? "human" : "ai")
                + " deck=" + custom.ResourceId.UniqueName
                + " difficulty=" + preset.Difficulty);
            return deck;
        }

        private static Deck BuildPracticeDeck(
            OfflineMatchRequest request,
            string fallbackName,
            Deck excludedDeck)
        {
            GameConfiguration config = GameManager.Instance.Config;
            if (System.Object.ReferenceEquals(config, null) ||
                System.Object.ReferenceEquals(config.CustomDecks, null))
                return null;

            DeckDifficulty wanted = RequestedDeckDifficulty(request);
            int pass;
            for (pass = 0; pass < 2; ++pass)
            {
                List<Deck> choices = new List<Deck>();
                int i;
                for (i = 0; i < config.CustomDecks.Count; ++i)
                {
                    CustomDeck candidate = config.CustomDecks[i];
                    if (System.Object.ReferenceEquals(candidate, null))
                        continue;
                    if (pass == 0 && !candidate.AvailableInPractice)
                        continue;

                    PresetDeck preset = FindPresetDeck(candidate, wanted);
                    if (System.Object.ReferenceEquals(preset, null) ||
                        System.Object.ReferenceEquals(preset.Cards, null) ||
                        preset.Cards.Count == 0)
                        continue;

                    string name = candidate.ResourceId.UniqueName;
                    if (System.String.IsNullOrEmpty(name))
                        name = fallbackName;
                    Deck deck = UIDeckUtils.CreateGameConfigDeckFromCustomDeck(
                        preset, name);
                    if (System.Object.ReferenceEquals(deck, null) ||
                        DecksEquivalent(deck, excludedDeck))
                        continue;
                    choices.Add(deck);
                }

                if (choices.Count > 0)
                {
                    RandomIntegerGenerator rng =
                        new RandomIntegerGenerator(System.DateTime.UtcNow.GetHashCode());
                    int index = rng.GenerateInRange(0, choices.Count - 1);
                    Deck selected = choices[index];
                    Log("random retail practice deck selected: " + selected.Name
                        + " choices=" + choices.Count
                        + " difficulty=" + wanted);
                    return selected;
                }
            }

            return null;
        }

        private static Deck BuildSyntheticAiDeck(Deck excludedDeck, string name)
        {
            GameConfiguration config = GameManager.Instance.Config;
            ServicesManager services = Singleton<ServicesManager>.Instance;
            if (System.Object.ReferenceEquals(config, null) ||
                System.Object.ReferenceEquals(config.CardCollection, null) ||
                System.Object.ReferenceEquals(services, null) ||
                System.Object.ReferenceEquals(services.Inventory, null) ||
                System.Object.ReferenceEquals(services.Inventory.InventoryDescription, null) ||
                System.Object.ReferenceEquals(
                    services.Inventory.InventoryDescription.cardTypes, null))
                return null;

            System.Collections.Generic.HashSet<int> inventoryCardIds =
                new System.Collections.Generic.HashSet<int>();
            foreach (Inventory.CardType inventoryCard in
                services.Inventory.InventoryDescription.cardTypes)
            {
                if (!System.Object.ReferenceEquals(inventoryCard, null))
                    inventoryCardIds.Add(inventoryCard.TypeHash);
            }

            System.Collections.Generic.Dictionary<int, bool> excluded =
                new System.Collections.Generic.Dictionary<int, bool>();
            if (!System.Object.ReferenceEquals(excludedDeck, null))
            {
                int i;
                for (i = 0; i < excludedDeck.Cards.Count; ++i)
                {
                    DeckCardInfo info = excludedDeck.Cards[i];
                    if (!System.Object.ReferenceEquals(info, null))
                        excluded[info.Id] = true;
                }
            }

            int targetSize = 40;
            if (!System.Object.ReferenceEquals(excludedDeck, null) &&
                excludedDeck.Size > targetSize)
                targetSize = excludedDeck.Size;

            List<int> candidates = new List<int>();
            int setIndex;
            for (setIndex = 0; setIndex < config.CardCollection.Count; ++setIndex)
            {
                CardSet set = config.CardCollection[setIndex];
                if (System.Object.ReferenceEquals(set, null) ||
                    System.Object.ReferenceEquals(set.Cards, null))
                    continue;

                int cardIndex;
                for (cardIndex = 0; cardIndex < set.Cards.Count; ++cardIndex)
                {
                    Card card = set.Cards[cardIndex];
                    if (System.Object.ReferenceEquals(card, null) ||
                        !card.Production ||
                        card.IneligibleForRandomPools ||
                        card.IsPremiumOnly ||
                        System.String.IsNullOrEmpty(card.ExportCode) ||
                        !card.HasSubtype(CardSubtype.Creature))
                        continue;

                    int id = card.ResourceId.Id;
                    if (id == UniqueResourceId.GetInvalid().Id ||
                        !inventoryCardIds.Contains(id) ||
                        excluded.ContainsKey(id))
                        continue;
                    candidates.Add(id);
                }
            }

            if (candidates.Count < targetSize)
                return null;

            RandomIntegerGenerator rng =
                new RandomIntegerGenerator(System.DateTime.UtcNow.GetHashCode());
            int iShuffle;
            for (iShuffle = candidates.Count - 1; iShuffle > 0; --iShuffle)
            {
                int j = rng.GenerateInRange(0, iShuffle);
                int tmp = candidates[iShuffle];
                candidates[iShuffle] = candidates[j];
                candidates[j] = tmp;
            }

            Deck deck = new Deck();
            deck.Name = name;
            deck.Type = !System.Object.ReferenceEquals(excludedDeck, null)
                ? excludedDeck.Type
                : DeckType.Standard;
            deck.AvatarCardId = !System.Object.ReferenceEquals(excludedDeck, null)
                ? excludedDeck.AvatarCardId
                : 1264418727;

            int iAdd;
            for (iAdd = 0; iAdd < targetSize; ++iAdd)
                deck.AddCard(candidates[iAdd]);

            Log("synthetic local AI deck randomized from retail CardCollection: "
                + deck.Name + " cards=" + deck.Size
                + " pool=" + candidates.Count);
            return deck;
        }

        private static CustomDeck FindRetailAiIdentityDeck(OfflineMatchRequest request)
        {
            GameConfiguration config = GameManager.Instance.Config;
            if (System.Object.ReferenceEquals(config, null) ||
                System.Object.ReferenceEquals(config.CustomDecks, null))
                return null;

            CustomDeck fallback = null;
            int i;
            for (i = 0; i < config.CustomDecks.Count; ++i)
            {
                CustomDeck candidate = config.CustomDecks[i];
                if (System.Object.ReferenceEquals(candidate, null) ||
                    System.Object.ReferenceEquals(candidate.ValidAvatars, null) ||
                    candidate.ValidAvatars.Count == 0)
                    continue;

                if (System.Object.ReferenceEquals(fallback, null))
                    fallback = candidate;
                if (candidate.AvailableInPractice)
                    return candidate;
            }
            return fallback;
        }

        private static CustomDeck FindCustomDeck(int resourceId)
        {
            if (System.Object.ReferenceEquals(GameManager.Instance.Config.CustomDecks, null))
                return null;
            int i;
            for (i = 0; i < GameManager.Instance.Config.CustomDecks.Count; ++i)
            {
                CustomDeck deck = GameManager.Instance.Config.CustomDecks[i];
                if (!System.Object.ReferenceEquals(deck, null) &&
                    deck.ResourceId.Id == resourceId)
                    return deck;
            }
            return null;
        }

        private static PresetDeck FindPresetDeck(CustomDeck deck, DeckDifficulty wanted)
        {
            if (System.Object.ReferenceEquals(deck, null) ||
                System.Object.ReferenceEquals(deck.Decks, null) || deck.Decks.Count == 0)
                return null;
            int i;
            for (i = 0; i < deck.Decks.Count; ++i)
            {
                PresetDeck preset = deck.Decks[i];
                if (!System.Object.ReferenceEquals(preset, null) &&
                    preset.Difficulty == wanted)
                    return preset;
            }
            return deck.Decks[0];
        }

        private static DeckDifficulty RequestedDeckDifficulty(OfflineMatchRequest request)
        {
            if (System.Object.ReferenceEquals(request, null))
                return DeckDifficulty.Normal;
            if (request.AiDifficulty == Matching.IAiDifficulty.Expert)
                return DeckDifficulty.Hardest;
            if (request.AiDifficulty == Matching.IAiDifficulty.Adept)
                return DeckDifficulty.Hard;
            return DeckDifficulty.Normal;
        }

        private static bool DecksEquivalent(Deck a, Deck b)
        {
            if (System.Object.ReferenceEquals(a, null) ||
                System.Object.ReferenceEquals(b, null))
                return false;
            if (a.Cards.Count != b.Cards.Count)
                return false;

            System.Collections.Generic.Dictionary<int, int> counts =
                new System.Collections.Generic.Dictionary<int, int>();
            int i;
            for (i = 0; i < a.Cards.Count; ++i)
            {
                DeckCardInfo info = a.Cards[i];
                if (System.Object.ReferenceEquals(info, null))
                    continue;
                int oldCount;
                counts.TryGetValue(info.Id, out oldCount);
                counts[info.Id] = oldCount + info.Count;
            }

            for (i = 0; i < b.Cards.Count; ++i)
            {
                DeckCardInfo info = b.Cards[i];
                if (System.Object.ReferenceEquals(info, null))
                    continue;
                int oldCount;
                if (!counts.TryGetValue(info.Id, out oldCount))
                    return false;
                oldCount -= info.Count;
                if (oldCount < 0)
                    return false;
                counts[info.Id] = oldCount;
            }

            foreach (System.Collections.Generic.KeyValuePair<int, int> pair in counts)
            {
                if (pair.Value != 0)
                    return false;
            }
            return true;
        }

        private void StartOfflineMatch()
        {
            if (!_offlineReady)
            {
                Warn("F8 ignored: offline bootstrap is not active");
                _startIssued = false;
                return;
            }

            ServicesManager services = Singleton<ServicesManager>.Instance;
            if (System.Object.ReferenceEquals(services, null) || !services.IsReady() ||
                System.Object.ReferenceEquals(services.Endpoints, null))
            {
                Warn("F8 ignored: local ServicesManager initialization is not complete yet");
                _startIssued = false;
                return;
            }

            GameNetworkManager manager = GameNetworkManager.Instance;
            if (System.Object.ReferenceEquals(manager, null))
            {
                Warn("F8 ignored: GameNetworkManager.Instance is null");
                _startIssued = false;
                return;
            }

            try
            {
                _preparedHumanPremiumFlags = null;
                manager.UseDefaultScenario();
                Log("starting embedded StartSinglePlayerMatch() path");
                _startingOfflineMatch = true;
                try
                {
                    manager.StartSinglePlayerMatch(false, false, false);
                }
                finally
                {
                    _startingOfflineMatch = false;
                }
            }
            catch (System.Exception ex)
            {
                Warn("StartSinglePlayerMatch failed: " + ex);
                _startIssued = false;
            }
        }

        public static void Bootstrap()
        {
            if (!System.Object.ReferenceEquals(Instance, null))
                return;

            GameObject host = new GameObject("NyxTESLOffline.InPlace");
            UnityEngine.Object.DontDestroyOnLoad(host);
            host.AddComponent<Plugin>();
        }

        private static readonly object TraceLock = new object();

        private static void WriteTrace(string level, string text)
        {
            try
            {
                string dir = System.IO.Path.Combine(
                    UnityEngine.Application.dataPath,
                    "Managed/NyxTESLOffline-data");
                if (!System.IO.Directory.Exists(dir))
                    return;

                string path = System.IO.Path.Combine(dir, "nyx-inplace.log");
                string line = System.DateTime.UtcNow.ToString(
                    "yyyy-MM-ddTHH:mm:ss.fffZ") + " [" + level + "] "
                    + text + System.Environment.NewLine;
                lock (TraceLock)
                {
                    // Preserve an existing large log, never erase the external test evidence.
                    if(System.IO.File.Exists(path) && new System.IO.FileInfo(path).Length>4*1024*1024) {
                        string archive=System.IO.Path.Combine(dir,"log-backups");System.IO.Directory.CreateDirectory(archive);
                        System.IO.File.Move(path,System.IO.Path.Combine(archive,"nyx-inplace-"+System.DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff")+".log"));
                    }
                    System.IO.File.AppendAllText(path, line);
                }
            }
            catch
            {
                /* Diagnostics must never alter gameplay. */
            }
        }

        internal static void Log(string text)
        {
            if(!DiagnosticLogPolicy.Keep(text,DiagnosticLogPolicy.Verbose))return;
            if(!DiagnosticLogPolicy.Verbose && text!=null && text.Length>1200)text=text.Substring(0,1200)+" [truncated; NYX_VERBOSE_LOG=1 for details]";
            UnityEngine.Debug.Log("[NYX-OFFLINE] " + text);
            WriteTrace("INFO", text);
        }

        internal static void Warn(string text)
        {
            UnityEngine.Debug.LogWarning("[NYX-OFFLINE] " + text);
            WriteTrace("WARN", text);
        }
    }
}
