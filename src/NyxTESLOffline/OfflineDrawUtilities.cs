using System;
using System.Collections.Generic;
using SPTESL;
namespace NyxTESLOffline
{
    internal static class OfflineDrawUtilities
    {
        internal static void ExecuteAction(
            PlayerActions.DrawCards action,
            PlayerActionArguments args)
        {
            int count = ActionValueData.EvaluateValue(
                action.NumCards,
                args.CardArguments,
                args.PlayerArguments,
                args.TriggerArguments,
                args.TriggerGameState.RandomGenerator);
            foreach (PlayerState target in args.ValidTargetList)
                Draw(args.TriggerGameState, target, count,
                    CcgRpc.HistoryType.ActionMoveCards,
                    "retail-card-effect");
        }

        internal static List<ServerCard> Draw(
            ServerEffectUtilities.TriggerGameState triggerState,
            PlayerState player,
            int count,
            CcgRpc.HistoryType historyType,
            string reason)
        {
            if (count <= 0)
                return new List<ServerCard>();

            List<ServerCard> handBefore = triggerState.CardCatalog.GatherCardsInZone(
                delegate(ServerCard candidate) { return true; },
                player.NetId,
                GameZoneName.Hand);
            HashSet<CardInstanceId> priorHand = new HashSet<CardInstanceId>();
            foreach (ServerCard existing in handBefore)
                priorHand.Add(existing.InstanceId);

            List<ServerCard> viaRetail = null;
            try
            {
                viaRetail = PlayerActions.DrawCards.StaticExecute(
                    triggerState,
                    player,
                    count,
                    PlayerActions.DrawCards.DrawDeck.Self,
                    ServerCardCatalog.MoveOperationType.DefaultDraw,
                    false,
                    true,
                    -1);
            }
            catch (Exception ex)
            {
                Plugin.Warn("DrawCards.StaticExecute threw (" + reason + "): "
                    + ex.Message);
            }

            List<ServerCard> movedByRetail = triggerState.CardCatalog.GatherCardsInZone(
                delegate(ServerCard candidate) {
                    return !priorHand.Contains(candidate.InstanceId);
                },
                player.NetId,
                GameZoneName.Hand);
            bool recoveredMovedCards =
                (Object.ReferenceEquals(viaRetail, null) || viaRetail.Count == 0) &&
                movedByRetail.Count > 0;
            if (recoveredMovedCards &&
                movedByRetail.Count > 0)
                viaRetail = movedByRetail;

            if (!Object.ReferenceEquals(viaRetail, null) && viaRetail.Count >= count)
            {
                if (recoveredMovedCards)
                    PlayerActions.DrawCards.LogDrawnCards(
                        historyType,
                        triggerState.Log,
                        triggerState.Players,
                        triggerState.CardCatalog,
                        viaRetail,
                        -1);
                return viaRetail;
            }

            Plugin.Warn(
                "DrawCards.StaticExecute incomplete (" + reason + ") player="
                + player.NetId.Id
                + " got="
                + (Object.ReferenceEquals(viaRetail, null) ? -1 : viaRetail.Count)
                + " wanted=" + count
                + "; using MoveCard fallback");

            List<ServerCard> inDeck = new List<ServerCard>(
                triggerState.CardCatalog.GatherCardsInZone(
                    delegate(ServerCard candidate) { return true; },
                    player.NetId,
                    GameZoneName.Deck));

            Plugin.Log(
                "draw fallback (" + reason + "): Deck count="
                + inDeck.Count + " player=" + player.NetId.Id);

            List<ServerCard> drawn = new List<ServerCard>();
            if (!Object.ReferenceEquals(viaRetail, null))
                drawn.AddRange(viaRetail);
            int fallbackNeeded = count - drawn.Count;
            int i;
            for (i = 0; i < fallbackNeeded; ++i)
            {
                if (i >= inDeck.Count)
                {
                    Plugin.Log("empty deck draw: applying retail penalty (" + reason
                        + ") player=" + player.NetId.Id);
                    PlayerActions.DrawCards.DrawOutOfCardsCard(triggerState, player);
                    continue;
                }

                ServerCard card = inDeck[i];
                GameZoneOperationResult move = ServerCardCatalog.MoveCard(
                    triggerState,
                    new GameZoneId(player.NetId, GameZoneType.Deck),
                    new GameZoneId(player.NetId, GameZoneType.Hand),
                    card.InstanceId,
                    0,
                    CardInsertionType.Bottom,
                    ServerCardCatalog.MoveOperationType.DefaultDraw);

                if (move != GameZoneOperationResult.Succeeded)
                {
                    Plugin.Warn(
                        "draw fallback MoveCard failed (" + reason
                        + "): cardInstance=" + card.InstanceId.Id
                        + " cardId=" + card.CardId
                        + " result=" + move);
                    continue;
                }
                drawn.Add(card);
                /* The stripped DrawCards implementation does not dispatch
                 * the draw trigger on this host-side Deck -> Hand path.
                 * Keep the native retail path untouched above; emit exactly
                 * once for cards moved by our fallback so cards listening for
                 * WhenCardIsDrawn receive the same authoritative event. */
                ServerEffectUtilities.TriggerEffectEvent(
                    triggerState,
                    new ServerEffectUtilities.TriggerEvent(
                        EffectTriggerType.WhenCardIsDrawn,
                        card,
                        triggerState.Players));
            }

            if (drawn.Count > (Object.ReferenceEquals(viaRetail, null)
                    ? 0 : viaRetail.Count))
            {
                try
                {
                    List<ServerCard> fallbackDrawn = drawn.GetRange(
                        Object.ReferenceEquals(viaRetail, null) ? 0 : viaRetail.Count,
                        drawn.Count - (Object.ReferenceEquals(viaRetail, null)
                            ? 0 : viaRetail.Count));
                    PlayerActions.DrawCards.LogDrawnCards(
                        historyType,
                        triggerState.Log,
                        triggerState.Players,
                        triggerState.CardCatalog,
                        fallbackDrawn,
                        -1);
                }
                catch (Exception ex)
                {
                    Plugin.Warn("LogDrawnCards(" + historyType + ") failed: "
                        + ex.Message);
                }
            }

            return drawn;
        }
    }
}
