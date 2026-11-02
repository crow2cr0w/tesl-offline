using System;
using CcgRpc;
using HarmonyLib;
using SPTESL;

namespace NyxTESLOffline.Patches
{
    /*
     * Alpha10 is deliberately protocol-first.  These patches do not mutate the
     * retail visual state; they only trace the exact hand-off points between
     * history ingestion, sequence queueing and the retail animation routines.
     *
     * The previous alpha9 SequenceCardPlay/SequenceCardDamage replacements were
     * removed.  If the client receives a correct HistoryLog, the stock sequence
     * objects and data-driven CardFx pipeline must own the animation.
     */
    internal static class HistoryProtocolTrace
    {
        internal static bool IsInteresting(HistoryType type)
        {
            if(!DiagnosticLogPolicy.Verbose)return false;
            int value = (int)type;
            return (value >= (int)HistoryType.EventFirst &&
                    value <= (int)HistoryType.EventLast) ||
                   (value >= (int)HistoryType.ActionFirst &&
                    value <= (int)HistoryType.ActionLast) ||
                   (value >= (int)HistoryType.ConditionFirst &&
                    value <= (int)HistoryType.ConditionLast);
        }

        internal static string DescribeCard(HistoryCard card)
        {
            if (Object.ReferenceEquals(card, null))
                return "card=null";

            string position = "pos=null";
            if (!Object.ReferenceEquals(card.CurrentPosition, null))
            {
                position = "owner=" + card.CurrentPosition.OwningPlayerUid
                    + " zoneName=" + card.CurrentPosition.GameZoneName
                    + " zoneType=" + card.CurrentPosition.GameZoneType
                    + " lane=" + card.CurrentPosition.LaneIndex
                    + " pos=" + card.CurrentPosition.PositionIndex;
            }

            string attributes = String.Empty;
            for (int i = 0; i < card.AffectedAttributeName.Count; ++i)
            {
                if (i > 0) attributes += ",";
                attributes += ((AttributeType)card.AffectedAttributeName[i]).ToString()
                    + "=" + (i < card.AffectedAttributeValue.Count
                        ? card.AffectedAttributeValue[i].ToString() : "missing");
            }

            return "cardId=" + card.CardId
                + " uid=" + card.CardNetUid
                + " full=" + card.IsFullState
                + " revealed=" + card.IsRevealed
                + " premium=" + card.IsPremium
                + " prevType=" + card.PreviousGameZoneType
                + " useHealth=" + card.UseCurrentHealth
                + " health=" + card.CurrentHealth
                + " attrs=" + card.AffectedAttributeName.Count
                + " attrValues=[" + attributes + "]"
                + " " + position;
        }

        internal static void LogMatchData(string stage, MatchLogAnimationData data)
        {
            if (Object.ReferenceEquals(data, null))
            {
                Plugin.Log(stage + ": matchLogData=null");
                return;
            }

            HistoryItem header = data.HeaderContext;
            HistoryItem action = data.ActionData;
            HistoryType type = !Object.ReferenceEquals(action, null)
                ? action.Type
                : (!Object.ReferenceEquals(header, null) ? header.Type : (HistoryType)0);

            string message = stage
                + ": seqType=" + type
                + " header=" + (!Object.ReferenceEquals(header, null)
                    ? header.Type.ToString() : "null")
                + " action=" + (!Object.ReferenceEquals(action, null)
                    ? action.Type.ToString() : "null");

            if (!Object.ReferenceEquals(action, null))
            {
                message += " actor=" + action.ActorCardNetUid
                    + " target=" + action.TargetCardNetUid
                    + " asset=" + action.AssetHash
                    + " affected=" + action.AffectedCardsArray.Count;

                if (action.Type == HistoryType.ActionSetPlayerAttribute)
                {
                    message += " player=" + action.ActingPlayerInstanceId
                        + " attr=" + action.PlayerAttributeName
                        + " value=" + action.PlayerAttributeValue;
                }

                for (int cardIndex = 0; cardIndex < action.AffectedCardsArray.Count; ++cardIndex)
                    message += " [" + DescribeCard(action.AffectedCardsArray[cardIndex]) + "]";
            }

            Plugin.Log(message);
        }
    }


    [HarmonyPatch(typeof(ExitState), "OnEnable")]
    internal static class ExitStateOnEnableTracePatch
    {
        [HarmonyPrefix]
        private static void Prefix()
        {
            if (!Object.ReferenceEquals(Plugin.ActiveLocalMatch, null))
                Plugin.Log("visual protocol lifecycle: ExitState.OnEnable reached");
        }
    }

    [HarmonyPatch(typeof(BoardMessageTranslator), "Awake")]
    internal static class BoardMessageTranslatorAwakeTracePatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            if (!Object.ReferenceEquals(Plugin.ActiveLocalMatch, null))
                Plugin.Log("visual protocol: BoardMessageTranslator.Awake completed");
        }
    }

    [HarmonyPatch(typeof(BoardConductor), "Awake")]
    internal static class BoardConductorAwakeTracePatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            if (!Object.ReferenceEquals(Plugin.ActiveLocalMatch, null))
                Plugin.Log("visual protocol: BoardConductor.Awake completed");
        }
    }

    [HarmonyPatch(typeof(ClientCardCatalog), "Update")]
    internal static class ClientCardCatalogUpdateTracePatch
    {
        [HarmonyPrefix]
        private static void Prefix(HistoryItem item, bool isPlayHistory)
        {
            if (Object.ReferenceEquals(Plugin.ActiveLocalMatch, null) ||
                Object.ReferenceEquals(item, null) ||
                !HistoryProtocolTrace.IsInteresting(item.Type))
                return;

            string message = "visual protocol client-catalog: type=" + item.Type
                + " playHistory=" + isPlayHistory
                + " affected=" + item.AffectedCardsArray.Count;
            for (int cardIndex = 0; cardIndex < item.AffectedCardsArray.Count; ++cardIndex)
                message += " [" + HistoryProtocolTrace.DescribeCard(
                    item.AffectedCardsArray[cardIndex]) + "]";
            Plugin.Log(message);
        }
    }


    [HarmonyPatch(typeof(BoardMessageTranslator), "CollectSequenceData")]
    internal static class BoardMessageTranslatorCollectTracePatch
    {
        [HarmonyPrefix]
        private static void Prefix(
            HistoryItem item,
            HistoryItem header,
            bool useAiDelay)
        {
            if (Object.ReferenceEquals(Plugin.ActiveLocalMatch, null) ||
                Object.ReferenceEquals(item, null) ||
                !HistoryProtocolTrace.IsInteresting(item.Type))
                return;

            Plugin.Log("visual protocol translate: item=" + item.Type
                + " header=" + (!Object.ReferenceEquals(header, null)
                    ? header.Type.ToString() : "null")
                + " aiDelay=" + useAiDelay
                + " affected=" + item.AffectedCardsArray.Count);
        }
    }

    [HarmonyPatch(typeof(BoardInputController), "ReceiveNewActionSequence")]
    internal static class BoardInputControllerSequenceTracePatch
    {
        [HarmonyPrefix]
        private static void Prefix(ActionSequenceInstance sequenceInstance)
        {
            if (Object.ReferenceEquals(Plugin.ActiveLocalMatch, null) ||
                Object.ReferenceEquals(sequenceInstance, null) ||
                Object.ReferenceEquals(sequenceInstance.SequenceTemplate, null))
                return;

            HistoryType type = sequenceInstance.SequenceTemplate.GetHistoryType();
            if (!HistoryProtocolTrace.IsInteresting(type))
                return;

            Plugin.Log("visual protocol input: template="
                + sequenceInstance.SequenceTemplate.GetType().Name
                + " historyType=" + type);
            HistoryProtocolTrace.LogMatchData(
                "visual protocol input-data", sequenceInstance.MatchLogData);
    }

    [HarmonyPatch(typeof(PlayerInfoController), "SetAllCards")]
    internal static class AvatarBindingTracePatch
    {
        [HarmonyPostfix]
        private static void Postfix(
            PlayerInfoController __instance,
            BoardState boardState,
            ClientCard[] clientCards)
        {
            if (Object.ReferenceEquals(Plugin.ActiveLocalMatch, null) ||
                Object.ReferenceEquals(boardState, null) ||
                Object.ReferenceEquals(clientCards, null) ||
                clientCards.Length == 0)
                return;

            int uid = clientCards[0].NetUid.Id;
            Card3D found = boardState.FindCardByNetUid(uid);
            Plugin.Log("avatar visual bound: uid=" + uid
                + " owner=" + clientCards[0].OwnerNetId.Id
                + " found=" + !Object.ReferenceEquals(found, null)
                + " portrait=" + Object.ReferenceEquals(found, __instance.PlayerAvatar)
                + " active=" + (!Object.ReferenceEquals(found, null) &&
                    found.gameObject.activeInHierarchy));
        }
    }

    [HarmonyPatch(typeof(SequenceCardAttack), "Execute")]
    internal static class SequenceCardAttackPreflightTracePatch
    {
        [HarmonyPrefix]
        private static void Prefix(
            MatchLogAnimationData matchLogData,
            BoardState boardState)
        {
            if (Object.ReferenceEquals(Plugin.ActiveLocalMatch, null) ||
                Object.ReferenceEquals(matchLogData, null) ||
                Object.ReferenceEquals(matchLogData.HeaderContext, null) ||
                Object.ReferenceEquals(boardState, null))
                return;

            int actorUid = matchLogData.HeaderContext.ActorCardNetUid;
            int targetUid = matchLogData.HeaderContext.TargetCardNetUid;
            Card3D actor = boardState.FindCardByNetUid(actorUid);
            Card3D target = boardState.FindCardByNetUid(targetUid);
            Plugin.Log("attack animation preflight: actorUid=" + actorUid
                + " actorFound=" + !Object.ReferenceEquals(actor, null)
                + " actorCard=" + (!Object.ReferenceEquals(actor, null) &&
                    !Object.ReferenceEquals(actor.ClientCard, null)
                        ? actor.ClientCard.CardId.ToString() : "null")
                + " targetUid=" + targetUid
                + " targetFound=" + !Object.ReferenceEquals(target, null)
                + " targetCard=" + (!Object.ReferenceEquals(target, null) &&
                    !Object.ReferenceEquals(target.ClientCard, null)
                        ? target.ClientCard.CardId.ToString() : "null")
                + " targetAvatar=" + (!Object.ReferenceEquals(target, null) &&
                    !Object.ReferenceEquals(target.ClientCard, null) &&
                    target.ClientCard.HasCardSubtype(CardSubtype.Avatar)));
        }
    }
}

    [HarmonyPatch(typeof(BoardConductor), "ReceiveNewActionSequence")]
    internal static class BoardConductorSequenceTracePatch
    {
        [HarmonyPrefix]
        private static void Prefix(ActionSequenceInstance sequenceInstance)
        {
            if (Object.ReferenceEquals(Plugin.ActiveLocalMatch, null) ||
                Object.ReferenceEquals(sequenceInstance, null) ||
                Object.ReferenceEquals(sequenceInstance.MatchLogData, null))
                return;

            HistoryItem action = sequenceInstance.MatchLogData.ActionData;
            HistoryItem header = sequenceInstance.MatchLogData.HeaderContext;
            HistoryType type = !Object.ReferenceEquals(action, null)
                ? action.Type
                : (!Object.ReferenceEquals(header, null) ? header.Type : (HistoryType)0);
            if (!HistoryProtocolTrace.IsInteresting(type))
                return;

            string template = Object.ReferenceEquals(
                sequenceInstance.SequenceTemplate, null)
                ? "null"
                : sequenceInstance.SequenceTemplate.GetType().Name;

            Plugin.Log("visual protocol queue: template=" + template
                + " historyType=" + type);
            HistoryProtocolTrace.LogMatchData(
                "visual protocol queue-data", sequenceInstance.MatchLogData);
        }
    }

    [HarmonyPatch(typeof(SequenceCardPlay), "Execute")]
    internal static class SequenceCardPlayTracePatch
    {
        [HarmonyPrefix]
        private static void Prefix(MatchLogAnimationData matchLogData)
        {
            if (Object.ReferenceEquals(Plugin.ActiveLocalMatch, null))
                return;
            HistoryProtocolTrace.LogMatchData(
                "visual protocol SequenceCardPlay.Execute", matchLogData);
        }
    }

    [HarmonyPatch(typeof(SequenceCardPlay), "PlayCardToBoard")]
    internal static class SequenceCardPlayToBoardTracePatch
    {
        [HarmonyPrefix]
        private static void Prefix(
            MatchLogAnimationData matchLogData,
            ClientCard clientCard,
            GameZoneName toZone,
            int positionIndex,
            int laneIndex,
            bool isRevealed)
        {
            if (Object.ReferenceEquals(Plugin.ActiveLocalMatch, null))
                return;

            Plugin.Log("visual protocol PlayCardToBoard: to=" + toZone
                + " lane=" + laneIndex
                + " pos=" + positionIndex
                + " revealed=" + isRevealed
                + " clientCard=" + (!Object.ReferenceEquals(clientCard, null)
                    ? (clientCard.CardId + "/" + clientCard.NetUid.Id) : "null"));
            HistoryProtocolTrace.LogMatchData(
                "visual protocol PlayCardToBoard-data", matchLogData);
        }
    }

    [HarmonyPatch(typeof(SequenceCardDamage), "Execute")]
    internal static class SequenceCardDamageTracePatch
    {
        [HarmonyPrefix]
        private static void Prefix(MatchLogAnimationData matchLogData)
        {
            if (Object.ReferenceEquals(Plugin.ActiveLocalMatch, null))
                return;
            HistoryProtocolTrace.LogMatchData(
                "visual protocol SequenceCardDamage.Execute", matchLogData);
        }
    }


    [HarmonyPatch(typeof(SequenceFxUtils), "PlayCardFx")]
    internal static class SequenceFxPlayCardTracePatch
    {
        [HarmonyPrefix]
        private static void Prefix(
            AnimationSequence sequence,
            MatchLogAnimationData matchLogData)
        {
            if (Object.ReferenceEquals(Plugin.ActiveLocalMatch, null))
                return;

            string name = Object.ReferenceEquals(sequence, null)
                ? "null" : sequence.GetType().Name;
            Plugin.Log("visual protocol FX: sequence=" + name);
            HistoryProtocolTrace.LogMatchData(
                "visual protocol FX-data", matchLogData);
        }
    }

    [HarmonyPatch(typeof(SequenceSetPlayerAttribute), "Execute")]
    internal static class SequenceSetPlayerAttributeTracePatch
    {
        [HarmonyPrefix]
        private static void Prefix(MatchLogAnimationData matchLogData)
        {
            if (Object.ReferenceEquals(Plugin.ActiveLocalMatch, null))
                return;
            HistoryProtocolTrace.LogMatchData(
                "visual protocol SequenceSetPlayerAttribute.Execute", matchLogData);
        }
    }

    [HarmonyPatch(typeof(PlayerInfoController), "SetHealth")]
    internal static class PlayerInfoSetHealthTracePatch
    {
        [HarmonyPrefix]
        private static void Prefix(int value, bool showHealFx)
        {
            if (!Object.ReferenceEquals(Plugin.ActiveLocalMatch, null))
                Plugin.Log("visual protocol PlayerInfo.SetHealth: value=" + value
                    + " healFx=" + showHealFx);
        }
    }

    [HarmonyPatch(typeof(PlayerInfoController), "UpdateRunesLost")]
    internal static class PlayerInfoRunesTracePatch
    {
        [HarmonyPrefix]
        private static void Prefix(int value)
        {
            if (!Object.ReferenceEquals(Plugin.ActiveLocalMatch, null))
                Plugin.Log("visual protocol PlayerInfo.UpdateRunesLost: value=" + value);
        }
    }
    [HarmonyPatch(typeof(HistoryLog), "AddInstant")]
    internal static class HistoryLogAddInstantTracePatch
    {
        [HarmonyPrefix]
        private static void Prefix(HistoryLog __instance, HistoryItem instantItem)
        {
            OfflineSimulationHost.NormalizeActionDamageFxOrigin(__instance, instantItem);
            if(!DiagnosticLogPolicy.Verbose) {
                if(!Object.ReferenceEquals(Plugin.ActiveLocalMatch,null) && instantItem!=null &&
                    (instantItem.Type==HistoryType.ActionDamage || instantItem.Type==HistoryType.ActionBuff || instantItem.Type==HistoryType.ActionChangeCard || instantItem.Type==HistoryType.ActionPreDestroy || instantItem.Type==HistoryType.ActionFreeze || instantItem.Type==HistoryType.ActionCreateToken || instantItem.Type==HistoryType.ActionLink))
                    Plugin.Log("RULE-HISTORY type="+instantItem.Type+" actor="+instantItem.ActorCardNetUid+" target="+instantItem.TargetCardNetUid+" fx="+instantItem.AssetHash);
                return;
            }
            if (Object.ReferenceEquals(Plugin.ActiveLocalMatch, null) ||
                Object.ReferenceEquals(instantItem, null) ||
                !HistoryProtocolTrace.IsInteresting(instantItem.Type))
                return;

            string message = "history emit: type=" + instantItem.Type
                + " actor=" + instantItem.ActorCardNetUid
                + " target=" + instantItem.TargetCardNetUid
                + " asset=" + instantItem.AssetHash
                + " affected=" + instantItem.AffectedCardsArray.Count;
            if (instantItem.Type == HistoryType.ActionSetPlayerAttribute)
            {
                message += " player=" + instantItem.ActingPlayerInstanceId
                    + " attr=" + instantItem.PlayerAttributeName
                    + " value=" + instantItem.PlayerAttributeValue;
            }
            for (int cardIndex = 0; cardIndex < instantItem.AffectedCardsArray.Count; ++cardIndex)
                message += " [" + HistoryProtocolTrace.DescribeCard(
                    instantItem.AffectedCardsArray[cardIndex]) + "]";
            Plugin.Log(message);
        }
    }

    [HarmonyPatch(typeof(BoardMessageTranslator), "AddHistoryQueue")]
    internal static class BoardMessageTranslatorQueueTracePatch
    {
        [HarmonyPrefix]
        private static void Prefix(HistoryQueue queue)
        {
            if (Object.ReferenceEquals(Plugin.ActiveLocalMatch, null) ||
                Object.ReferenceEquals(queue, null) ||
                queue.Queue.Count == 0)
                return;

            bool interesting = false;
            string types = String.Empty;
            int i;
            for (i = 0; i < queue.Queue.Count; ++i)
            {
                HistoryItem item = queue.Queue[i];
                if (Object.ReferenceEquals(item, null))
                    continue;
                if (types.Length > 0)
                    types += ",";
                types += item.Type.ToString();
                if (HistoryProtocolTrace.IsInteresting(item.Type))
                    interesting = true;
            }

            if (interesting)
                Plugin.Log("visual protocol raw history queue: count="
                    + queue.Queue.Count + " types=[" + types + "]");
        }
    }

    [HarmonyPatch(typeof(Board3DUI), "ShowRingUsesLeft")]
    internal static class Board3DUIRingUsesTracePatch
    {
        [HarmonyPrefix]
        private static void Prefix(PlayerControllerType controllerType, int value)
        {
            if (!Object.ReferenceEquals(Plugin.ActiveLocalMatch, null))
                Plugin.Log("visual protocol ring charges: owner="
                    + controllerType + " value=" + value);
        }
    }

    [HarmonyPatch(typeof(Board3DUI), "SetRingCanUseState")]
    internal static class Board3DUIRingUsableTracePatch
    {
        [HarmonyPrefix]
        private static void Prefix(PlayerControllerType controllerType, bool canUse)
        {
            if (!Object.ReferenceEquals(Plugin.ActiveLocalMatch, null))
                Plugin.Log("visual protocol ring usable: owner="
                    + controllerType + " canUse=" + canUse);
        }
    }


}
