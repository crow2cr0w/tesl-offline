using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using SPTESL;

namespace NyxTESLOffline.Patches
{
    /*
     * Detailed, local-match-only trace of retail rule execution.  This records
     * the trigger that fired, condition results, resolved actor/target roles,
     * action configuration, FX selection and before/after target state.  It is
     * observational: no rule data or game state is modified here.
     */
    internal sealed class CardRuleActionTraceState
    {
        internal bool Compact;
        internal int Id;
        internal string ActionName;
        internal ServerCard[] Targets;
        internal string[] Before;
    }

    internal sealed class PlayerRuleActionTraceState
    {
        internal int Id;
        internal string ActionName;
        internal PlayerState[] Targets;
        internal string[] Before;
    }

    internal static class CardRuleCallTrace
    {
        private static int _nextActionId;
        private const BindingFlags InstanceFields =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        internal static bool IsActive
        {
            get { return DiagnosticLogPolicy.Verbose && !Object.ReferenceEquals(Plugin.ActiveLocalMatch, null); }
        }

        internal static string DescribeCard(ServerCard card)
        {
            if (Object.ReferenceEquals(card, null))
                return "null";

            string attrs = String.Empty;
            AttributeType[] watched = new AttributeType[] {
                AttributeType.Charge, AttributeType.Taunt, AttributeType.Cover,
                AttributeType.Deathtouch, AttributeType.CannotAttack,
                AttributeType.Freeze, AttributeType.PermFreeze,
                AttributeType.Prophecy, AttributeType.Breakthrough,
                AttributeType.Ward, AttributeType.Drain, AttributeType.Exalted,
                AttributeType.Immortal
            };
            int i;
            for (i = 0; i < watched.Length; ++i)
            {
                int value = card.QueryAttribute(watched[i]).Value;
                if (value == 0)
                    continue;
                if (attrs.Length > 0) attrs += ",";
                attrs += watched[i] + "=" + value;
            }

            return "card=" + card.CardId
                + " uid=" + card.NetUid.Id
                + " owner=" + card.OwnerNetId.Id
                + " zone=" + card.CurrentGameZoneName
                + " lane=" + card.CurrentLaneIndex
                + " atk=" + card.QueryAttribute(AttributeType.Attack).Value
                + " hp=" + card.CurrentHealth
                + " attrs=[" + attrs + "]";
        }

        internal static string DescribeCardList(IList<ServerCard> cards)
        {
            if (Object.ReferenceEquals(cards, null))
                return "null";
            string result = "count=" + cards.Count + " [";
            int limit = Math.Min(cards.Count, 20);
            int i;
            for (i = 0; i < limit; ++i)
            {
                if (i > 0) result += " | ";
                result += DescribeCard(cards[i]);
            }
            if (cards.Count > limit)
                result += " | ...";
            return result + "]";
        }

        internal static string DescribeConfiguredAction(ICardAction action)
        {
            string fx = "unknown";
            try { fx = action.GetFxActionType().ToString(); }
            catch (Exception ex) { fx = "error:" + ex.GetType().Name; }

            return action.GetType().FullName
                + " fx=" + fx
                + " config={" + DescribeFields(action, 0) + "}";
        }

        internal static string DescribeActionArguments(
            CardActionArguments args)
        {
            CardConditionalArguments cards = args.CardArguments;
            PlayerConditionalArguments players = args.PlayerArguments;
            TriggerConditionalArguments trigger = args.TriggerArguments;
            return "asset=" + args.AssetHash
                + " defaultFx=" + args.UsingDefaultFx
                + " self={" + DescribeCard(cards.SelfCard) + "}"
                + " inciter={" + DescribeCard(cards.InciterCard) + "}"
                + " eventTarget={" + DescribeCard(cards.EventTargetCard) + "}"
                + " decisionTarget={" + DescribeCard(cards.DecisionTargetCard) + "}"
                + " customTarget={" + DescribeCard(cards.CustomUITargetCard) + "}"
                + " validTargets={" + DescribeCardList(args.ValidTargetList) + "}"
                + " players={self:" + DescribePlayer(players.SelfPlayerState)
                + ",inciter:" + DescribePlayer(players.InciterPlayerState)
                + ",eventTarget:" + DescribePlayer(players.EventTargetPlayerState)
                + ",decisionTarget:" + DescribePlayer(players.DecisionTargetPlayerState)
                + ",opponent:" + DescribePlayer(players.OpponentPlayerState) + "}"
                + " triggerArgs={value:" + trigger.TriggerValue
                + ",attribute:" + trigger.TriggerAttribute
                + ",lane:" + trigger.ActingLaneIndex
                + ",laneType:" + trigger.ActingLaneType + "}";
        }

        internal static string DescribePlayerActionArguments(
            PlayerActionArguments args)
        {
            CardConditionalArguments cards = args.CardArguments;
            PlayerConditionalArguments players = args.PlayerArguments;
            TriggerConditionalArguments trigger = args.TriggerArguments;
            return "asset=" + args.AssetHash
                + " selfCard={" + DescribeCard(cards.SelfCard) + "}"
                + " inciterCard={" + DescribeCard(cards.InciterCard) + "}"
                + " eventTargetCard={" + DescribeCard(cards.EventTargetCard) + "}"
                + " decisionTargetCard={" + DescribeCard(cards.DecisionTargetCard) + "}"
                + " validPlayers={" + DescribePlayersList(args.ValidTargetList) + "}"
                + " players={self:" + DescribePlayer(players.SelfPlayerState)
                + ",inciter:" + DescribePlayer(players.InciterPlayerState)
                + ",eventTarget:" + DescribePlayer(players.EventTargetPlayerState)
                + ",decisionTarget:" + DescribePlayer(players.DecisionTargetPlayerState)
                + ",opponent:" + DescribePlayer(players.OpponentPlayerState) + "}"
                + " triggerArgs={value:" + trigger.TriggerValue
                + ",attribute:" + trigger.TriggerAttribute
                + ",lane:" + trigger.ActingLaneIndex
                + ",laneType:" + trigger.ActingLaneType + "}";
        }

        internal static string DescribeTriggerContext(
            ServerEffectUtilities.TriggerContext context)
        {
            TriggeredEffect effect = context.TriggeredEffect;
            if (Object.ReferenceEquals(effect, null))
                return "context=null";

            string owner = DescribeCard(context.EffectOwner);
            string inciter = DescribeCard(context.TriggerEvent.Inciter);
            string target = DescribeCard(context.TriggerEvent.Target);
            EffectGroup group = effect.EffectGroup;
            int actionCount = 0;
            if (!Object.ReferenceEquals(group, null) && group.Effects != null)
            {
                for (int i = 0; i < group.Effects.Count; ++i)
                {
                    Effect item = group.Effects[i];
                    if (!Object.ReferenceEquals(item, null) && item.CardActions != null)
                        actionCount += item.CardActions.Count;
                }
            }

            return "owner={" + owner + "} trigger=" + effect.TriggerType
                + " event=" + context.TriggerEvent.Type
                + " eventValue=" + context.TriggerEvent.Value
                + " eventAttr=" + context.TriggerEvent.AttributeType
                + " fxOverride=" + context.TriggerEvent.TypeFxOverride
                + " inciter={" + inciter + "} eventTarget={" + target + "}"
                + " queue=" + context.QueueAddCounter
                + " effectIndex=" + context.EffectIndex
                + " lane=" + context.ActingLaneIndex
                + "/" + context.ActingLaneIndexType
                + " didMain=" + context.DidMainEffectsRun
                + " targeting=" + effect.ClientTargetingType
                + " selectors=" + DescribeTypeList(effect.TargetSelectors)
                + " triggerCardReq=" + DescribeObjectList(effect.TriggerCardRequirements)
                + " triggerPlayerReq=" + DescribeObjectList(effect.TriggerPlayerRequirements)
                + " beforeTargetCardReq=" + DescribeObjectList(effect.BeforeTargetingCardRequirements)
                + " beforeTargetPlayerReq=" + DescribeObjectList(effect.BeforeTargetingPlayerRequirements)
                + " configuredCardActions=" + actionCount
                + " customUi=" + effect.CustomUiType;
        }

        internal static string DescribeTriggerEvent(
            ServerEffectUtilities.TriggerEvent triggerEvent)
        {
            return "type=" + triggerEvent.Type
                + " value=" + triggerEvent.Value
                + " attribute=" + triggerEvent.AttributeType
                + " fxOverride=" + triggerEvent.TypeFxOverride
                + " inciter={" + DescribeCard(triggerEvent.Inciter) + "}"
                + " target={" + DescribeCard(triggerEvent.Target) + "}"
                + " inciterPlayer={" + DescribePlayer(triggerEvent.InciterPlayerState) + "}";
        }

        internal static string DescribePlayer(PlayerState player)
        {
            if (Object.ReferenceEquals(player, null))
                return "null";
            return "id=" + player.NetId.Id
                + " human=" + player.IsHuman
                + " turn=" + player.IsMyTurn
                + " hand=" + player.FindHandSize()
                + " runes=" + player.QueryAttribute(AttributeType.NumRunes).Value
                + " runesLost=" + player.QueryAttribute(AttributeType.NumRunesLost).Value;
        }

        internal static string DescribeDecision(CcgRpc.Decision decision)
        {
            if (Object.ReferenceEquals(decision, null))
                return "null";
            return "type=" + decision.Type
                + " commandUid=" + decision.CommandUid
                + " actorUid=" + decision.ActorCardNetUid
                + " targetUid=" + decision.TargetCardNetUid
                + " equipUid=" + decision.EquipCardNetUid
                + " laneIndex=" + decision.LaneIndex
                + " position=" + decision.PositionIndex
                + " lane=" + decision.LaneTarget
                + " customChoice=" + decision.CustomChoice;
        }

        internal static string DescribeObjectList<T>(IList<T> values)
        {
            if (Object.ReferenceEquals(values, null))
                return "null";
            string result = "count=" + values.Count + " [";
            int limit = Math.Min(values.Count, 12);
            int i;
            for (i = 0; i < limit; ++i)
            {
                if (i > 0) result += " | ";
                object value = values[i];
                result += Object.ReferenceEquals(value, null)
                    ? "null" : value.GetType().Name + "{" + DescribeFields(value, 0) + "}";
            }
            if (values.Count > limit) result += " | ...";
            return result + "]";
        }

        internal static string DescribePlayersList(IList<PlayerState> players)
        {
            if (Object.ReferenceEquals(players, null))
                return "null";
            string result = "count=" + players.Count + " [";
            int limit = Math.Min(players.Count, 12);
            for (int i = 0; i < limit; ++i)
            {
                if (i > 0) result += " | ";
                result += DescribePlayer(players[i]);
            }
            if (players.Count > limit) result += " | ...";
            return result + "]";
        }

        internal static string DescribeFieldsForTrace(object value)
        {
            return DescribeFields(value, 0);
        }

        private static string DescribeTypeList<T>(IList<T> values)
        {
            if (Object.ReferenceEquals(values, null))
                return "null";
            string result = "count=" + values.Count + " [";
            int limit = Math.Min(values.Count, 12);
            int i;
            for (i = 0; i < limit; ++i)
            {
                if (i > 0) result += " | ";
                object value = values[i];
                result += Object.ReferenceEquals(value, null)
                    ? "null" : value.GetType().Name + "{"
                        + DescribeFields(value, 0) + "}";
            }
            if (values.Count > limit) result += " | ...";
            return result + "]";
        }

        private static string DescribeFields(object value, int depth)
        {
            if (Object.ReferenceEquals(value, null))
                return "null";
            if (depth >= 2)
                return value.GetType().Name;

            FieldInfo[] fields = value.GetType().GetFields(InstanceFields);
            string result = String.Empty;
            int shown = 0;
            int i;
            for (i = 0; i < fields.Length && shown < 16; ++i)
            {
                if (fields[i].IsStatic)
                    continue;
                object fieldValue;
                try { fieldValue = fields[i].GetValue(value); }
                catch { continue; }
                if (result.Length > 0) result += ",";
                result += fields[i].Name + "=" + DescribeValue(fieldValue, depth + 1);
                shown += 1;
            }
            if (fields.Length > shown)
                result += (result.Length > 0 ? "," : String.Empty) + "...";
            return result;
        }

        private static string DescribeValue(object value, int depth)
        {
            if (Object.ReferenceEquals(value, null))
                return "null";
            Type type = value.GetType();
            if (type.IsEnum || type.IsPrimitive || value is string || value is decimal)
                return value.ToString();
            if (value is ServerCard)
                return DescribeCard((ServerCard)value);
            if (value is PlayerState)
                return DescribePlayer((PlayerState)value);
            if (value is IList)
            {
                IList list = (IList)value;
                string itemTypes = String.Empty;
                int limit = Math.Min(list.Count, 8);
                int i;
                for (i = 0; i < limit; ++i)
                {
                    if (i > 0) itemTypes += "|";
                    object item = list[i];
                    itemTypes += Object.ReferenceEquals(item, null)
                        ? "null" : item.GetType().Name + "{"
                            + DescribeFields(item, depth + 1) + "}";
                }
                if (list.Count > limit) itemTypes += "|...";
                return type.Name + " count=" + list.Count + " [" + itemTypes + "]";
            }
            if (type.Namespace == "SPTESL" && depth < 2)
                return type.Name + "{" + DescribeFields(value, depth) + "}";
            return type.Name;
        }

        internal static CardRuleActionTraceState BeginActionTrace(
            ICardAction action,
            CardActionArguments args)
        {
            if (!IsActive)
            {
                if(Object.ReferenceEquals(Plugin.ActiveLocalMatch,null) || action==null)return null;
                var source=args.CardArguments.SelfCard;
                return new CardRuleActionTraceState {Compact=true,ActionName=action.GetType().Name+" source="+(source==null?"null":source.CardId+"/"+source.NetUid.Id+" owner="+source.OwnerNetId.Id)+" fx="+args.AssetHash,
                    Targets=args.ValidTargetList==null?new ServerCard[0]:args.ValidTargetList.ToArray()};
            }

            CardRuleActionTraceState state = new CardRuleActionTraceState();
            state.Id = Interlocked.Increment(ref _nextActionId);
            state.ActionName = action.GetType().FullName;
            List<ServerCard> targets = new List<ServerCard>();
            AddUnique(targets, args.CardArguments.SelfCard);
            AddUnique(targets, args.CardArguments.InciterCard);
            AddUnique(targets, args.CardArguments.EventTargetCard);
            AddUnique(targets, args.CardArguments.DecisionTargetCard);
            AddUnique(targets, args.CardArguments.CustomUITargetCard);
            if (!Object.ReferenceEquals(args.ValidTargetList, null))
                for (int i = 0; i < args.ValidTargetList.Count; ++i)
                    AddUnique(targets, args.ValidTargetList[i]);
            state.Targets = targets.ToArray();
            state.Before = new string[state.Targets.Length];
            for (int i = 0; i < state.Targets.Length; ++i)
                state.Before[i] = DescribeCard(state.Targets[i]);

            Plugin.Log("[CARD-RULE] ACTION BEGIN id=" + state.Id
                + " " + DescribeConfiguredAction(action)
                + " " + DescribeActionArguments(args));
            return state;
        }

        internal static void EndActionTrace(
            CardRuleActionTraceState state, Exception exception)
        {
            if (Object.ReferenceEquals(state, null))
                return;
            if(state.Compact) {
                string message="RULE action="+state.ActionName;
                foreach(var target in state.Targets)message+=" target="+target.CardId+"/"+target.NetUid.Id+" atk="+target.QueryAttribute(AttributeType.Attack).Value+" hp="+target.CurrentHealth;
                if(exception!=null)Plugin.Warn(message+" exception="+exception);else Plugin.Log(message);
                return;
            }
            string targets = String.Empty;
            for (int i = 0; i < state.Targets.Length; ++i)
            {
                if (i > 0) targets += " | ";
                targets += "before{" + state.Before[i] + "} after{"
                    + DescribeCard(state.Targets[i]) + "}";
            }
            Plugin.Log("[CARD-RULE] ACTION "
                + (Object.ReferenceEquals(exception, null) ? "END" : "THREW")
                + " id=" + state.Id
                + " action=" + state.ActionName
                + " targets=[" + targets + "]"
                + (!Object.ReferenceEquals(exception, null)
                    ? " exception=" + exception : String.Empty));
        }

        internal static PlayerRuleActionTraceState BeginPlayerActionTrace(
            IPlayerAction action,
            PlayerActionArguments args)
        {
            if (!IsActive)
                return null;

            PlayerRuleActionTraceState state = new PlayerRuleActionTraceState();
            state.Id = Interlocked.Increment(ref _nextActionId);
            state.ActionName = action.GetType().FullName;
            List<PlayerState> targets = new List<PlayerState>();
            if (!Object.ReferenceEquals(args.ValidTargetList, null))
            {
                for (int i = 0; i < args.ValidTargetList.Count; ++i)
                    AddUnique(targets, args.ValidTargetList[i]);
            }
            AddUnique(targets, args.PlayerArguments.SelfPlayerState);
            AddUnique(targets, args.PlayerArguments.InciterPlayerState);
            AddUnique(targets, args.PlayerArguments.EventTargetPlayerState);
            AddUnique(targets, args.PlayerArguments.DecisionTargetPlayerState);
            AddUnique(targets, args.PlayerArguments.OpponentPlayerState);
            state.Targets = targets.ToArray();
            state.Before = new string[state.Targets.Length];
            for (int i = 0; i < state.Targets.Length; ++i)
                state.Before[i] = DescribePlayer(state.Targets[i]);

            string fx = "unknown";
            try { fx = action.GetFxActionType().ToString(); }
            catch (Exception ex) { fx = "error:" + ex.GetType().Name; }
            Plugin.Log("[CARD-RULE] PLAYER ACTION BEGIN id=" + state.Id
                + " action=" + state.ActionName
                + " fx=" + fx
                + " config={" + DescribeFields(action, 0) + "}"
                + " " + DescribePlayerActionArguments(args));
            return state;
        }

        internal static void EndPlayerActionTrace(
            PlayerRuleActionTraceState state, Exception exception)
        {
            if (Object.ReferenceEquals(state, null))
                return;
            string targets = String.Empty;
            for (int i = 0; i < state.Targets.Length; ++i)
            {
                if (i > 0) targets += " | ";
                targets += "before{" + state.Before[i] + "} after{"
                    + DescribePlayer(state.Targets[i]) + "}";
            }
            Plugin.Log("[CARD-RULE] PLAYER ACTION "
                + (Object.ReferenceEquals(exception, null) ? "END" : "THREW")
                + " id=" + state.Id
                + " action=" + state.ActionName
                + " players=[" + targets + "]"
                + (!Object.ReferenceEquals(exception, null)
                    ? " exception=" + exception : String.Empty));
        }

        private static void AddUnique(List<ServerCard> cards, ServerCard card)
        {
            if (Object.ReferenceEquals(card, null))
                return;
            for (int i = 0; i < cards.Count; ++i)
                if (Object.ReferenceEquals(cards[i], card))
                    return;
            cards.Add(card);
        }

        private static void AddUnique(List<PlayerState> players, PlayerState player)
        {
            if (Object.ReferenceEquals(player, null))
                return;
            for (int i = 0; i < players.Count; ++i)
                if (Object.ReferenceEquals(players[i], player))
                    return;
            players.Add(player);
        }
    }

    [HarmonyPatch]
    internal static class RetailCardActionCallTracePatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            Type[] types;
            try { types = typeof(ICardAction).Assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types; }

            if (Object.ReferenceEquals(types, null))
                yield break;

            Type[] signature = new Type[] { typeof(CardActionArguments) };
            for (int i = 0; i < types.Length; ++i)
            {
                Type type = types[i];
                if (Object.ReferenceEquals(type, null) || type.IsInterface ||
                    type.IsAbstract || !typeof(ICardAction).IsAssignableFrom(type))
                    continue;

                MethodInfo[] methods = type.GetMethods(
                    BindingFlags.Instance | BindingFlags.Public |
                    BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                for (int methodIndex = 0; methodIndex < methods.Length; ++methodIndex)
                {
                    MethodInfo method = methods[methodIndex];
                    if (method.Name == "Execute" && method.ReturnType == typeof(void) &&
                        method.GetParameters().Length == 1 &&
                        method.GetParameters()[0].ParameterType == signature[0])
                        yield return method;
                }
            }
        }

        [HarmonyPrefix]
        private static void Prefix(
            ICardAction __instance,
            CardActionArguments actionArguments,
            out CardRuleActionTraceState __state)
        {
            __state = CardRuleCallTrace.BeginActionTrace(
                __instance, actionArguments);
        }

        [HarmonyFinalizer]
        private static Exception Finalizer(
            Exception __exception,
            CardRuleActionTraceState __state)
        {
            CardRuleCallTrace.EndActionTrace(__state, __exception);
            return __exception;
        }
    }

    [HarmonyPatch]
    internal static class RetailPlayerActionCallTracePatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            Type[] types;
            try { types = typeof(IPlayerAction).Assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types; }

            if (Object.ReferenceEquals(types, null))
                yield break;

            Type argumentType = typeof(PlayerActionArguments);
            for (int i = 0; i < types.Length; ++i)
            {
                Type type = types[i];
                if (Object.ReferenceEquals(type, null) || type.IsInterface ||
                    type.IsAbstract || !typeof(IPlayerAction).IsAssignableFrom(type))
                    continue;

                MethodInfo[] methods = type.GetMethods(
                    BindingFlags.Instance | BindingFlags.Public |
                    BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                for (int methodIndex = 0; methodIndex < methods.Length; ++methodIndex)
                {
                    MethodInfo method = methods[methodIndex];
                    ParameterInfo[] parameters = method.GetParameters();
                    if (method.Name == "Execute" && method.ReturnType == typeof(void) &&
                        parameters.Length == 1 &&
                        parameters[0].ParameterType == argumentType)
                        yield return method;
                }
            }
        }

        [HarmonyPrefix]
        private static void Prefix(
            IPlayerAction __instance,
            PlayerActionArguments actionArguments,
            out PlayerRuleActionTraceState __state)
        {
            __state = CardRuleCallTrace.BeginPlayerActionTrace(
                __instance, actionArguments);
        }

        [HarmonyFinalizer]
        private static Exception Finalizer(
            Exception __exception,
            PlayerRuleActionTraceState __state)
        {
            CardRuleCallTrace.EndPlayerActionTrace(__state, __exception);
            return __exception;
        }
    }

    [HarmonyPatch(typeof(ServerEffectUtilities), "TriggerEffectEvent")]
    internal static class RetailTriggerEventCallTracePatch
    {
        [HarmonyPrefix]
        private static void Prefix(
            ServerEffectUtilities.TriggerGameState gameState,
            ServerEffectUtilities.TriggerEvent triggerEvent)
        {
            if (!CardRuleCallTrace.IsActive)
                return;
            Plugin.Log("[CARD-RULE] TRIGGER DISPATCH "
                + CardRuleCallTrace.DescribeTriggerEvent(triggerEvent)
                + " players={" + CardRuleCallTrace.DescribePlayersList(gameState.Players)
                + "}");
        }
    }

    [HarmonyPatch(typeof(ServerEffectUtilities), "TestTriggerEffectConditions")]
    internal static class RetailTriggerConditionTracePatch
    {
        [HarmonyPrefix]
        private static void Prefix(
            ServerEffectUtilities.TriggerContext context,
            out string __state)
        {
            __state = CardRuleCallTrace.IsActive
                ? CardRuleCallTrace.DescribeTriggerContext(context)
                : null;
        }

        [HarmonyPostfix]
        private static void Postfix(bool __result, string __state)
        {
            if (__state != null)
                Plugin.Log("[CARD-RULE] TRIGGER CONDITIONS result="
                    + __result + " " + __state);
        }
    }

    /* Slay is a card-owned combat trigger, not a global reaction to any
     * creature dying. The stripped host broadcasts the event to every card;
     * scope accepted effects back to the creature that dealt the lethal hit.
     * This also lets aura-granted Slay effects on Astrid/Skooma beneficiaries
     * run normally while preventing a dying Jiub from triggering his own Slay. */
    [HarmonyPatch(typeof(ServerEffectUtilities), "TestTriggerEffectConditions")]
    internal static class SlayTriggerInciterScopePatch
    {
        [HarmonyPostfix]
        private static void Postfix(
            ServerEffectUtilities.TriggerContext context,
            ref bool __result)
        {
            if (Object.ReferenceEquals(Plugin.ActiveLocalMatch, null) ||
                !__result || Object.ReferenceEquals(context, null) ||
                context.TriggerEvent.Type != EffectTriggerType.Slay ||
                Object.ReferenceEquals(context.EffectOwner, context.TriggerEvent.Inciter))
                return;

            Plugin.Log("Slay trigger rejected: effectOwner="
                + (context.EffectOwner == null ? -1 : context.EffectOwner.NetUid.Id)
                + " inciter=" + (context.TriggerEvent.Inciter == null
                    ? -1 : context.TriggerEvent.Inciter.NetUid.Id)
                + " target=" + (context.TriggerEvent.Target == null
                    ? -1 : context.TriggerEvent.Target.NetUid.Id));
            __result = false;
        }
    }

    [HarmonyPatch(typeof(ServerEffectUtilities), "TestBeforeTargetingEffectConditions")]
    internal static class RetailBeforeTargetConditionTracePatch
    {
        [HarmonyPrefix]
        private static void Prefix(
            ServerEffectUtilities.TriggerContext context,
            out string __state)
        {
            __state = CardRuleCallTrace.IsActive
                ? CardRuleCallTrace.DescribeTriggerContext(context)
                : null;
        }

        [HarmonyPostfix]
        private static void Postfix(bool __result, string __state)
        {
            if (__state != null)
                Plugin.Log("[CARD-RULE] BEFORE-TARGET CONDITIONS result="
                    + __result + " " + __state);
        }
    }

    [HarmonyPatch(typeof(ServerEffectUtilities), "TestConditions")]
    internal static class RetailIndividualConditionsTracePatch
    {
        [HarmonyPrefix]
        private static void Prefix(
            List<ITriggerRequirement> triggerRequirements,
            TriggerConditionalArguments triggerArguments,
            List<ICardRequirement> cardRequirements,
            CardConditionalArguments cardArguments,
            List<IPlayerRequirement> playerRequirements,
            PlayerConditionalArguments playerArguments,
            List<ILaneRequirement> laneRequirements,
            LaneConditionalArguments laneArguments,
            out string __state)
        {
            if (!CardRuleCallTrace.IsActive)
            {
                __state = null;
                return;
            }

            __state = "triggerReq="
                + CardRuleCallTrace.DescribeObjectList(triggerRequirements)
                + " triggerArgs={" + CardRuleCallTrace.DescribeFieldsForTrace(triggerArguments) + "}"
                + " cardReq=" + CardRuleCallTrace.DescribeObjectList(cardRequirements)
                + " cardArgs={" + CardRuleCallTrace.DescribeFieldsForTrace(cardArguments) + "}"
                + " playerReq=" + CardRuleCallTrace.DescribeObjectList(playerRequirements)
                + " playerArgs={" + CardRuleCallTrace.DescribeFieldsForTrace(playerArguments) + "}"
                + " laneReq=" + CardRuleCallTrace.DescribeObjectList(laneRequirements)
                + " laneArgs={" + CardRuleCallTrace.DescribeFieldsForTrace(laneArguments) + "}";
        }

        [HarmonyPostfix]
        private static void Postfix(bool __result, string __state)
        {
            if (__state != null)
                Plugin.Log("[CARD-RULE] REQUIREMENTS result=" + __result
                    + " " + __state);
        }
    }

    [HarmonyPatch(typeof(ServerEffectUtilities), "TestAndExecuteTriggeredEffectFromQueue")]
    internal static class RetailQueuedEffectTracePatch
    {
        [HarmonyPrefix]
        private static void Prefix(
            ServerEffectUtilities.TriggerContext context,
            CcgRpc.Decision playerDecision,
            out string __state)
        {
            __state = CardRuleCallTrace.IsActive
                ? CardRuleCallTrace.DescribeTriggerContext(context)
                    + " decision={" + CardRuleCallTrace.DescribeDecision(playerDecision) + "}"
                : null;
            if (__state != null)
                Plugin.Log("[CARD-RULE] QUEUED EFFECT BEGIN " + __state);
        }

        [HarmonyPostfix]
        private static void Postfix(string __state)
        {
            if (__state != null)
                Plugin.Log("[CARD-RULE] QUEUED EFFECT END " + __state);
        }
    }
}
