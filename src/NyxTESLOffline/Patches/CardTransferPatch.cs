using System;
using System.Collections;
using SPTESL;

namespace NyxTESLOffline.Patches
{
    /*
     * Source template for the in-place IL rewrite performed by
     * NyxInPlacePatcher.  This class is deliberately NOT a Harmony patch.
     *
     * Assembly-CSharp retail contains:
     *
     *   ServerCardCatalog.TransferCardToZone(...) { return false; }
     *
     * All surviving MoveCard/CreateCard/DrawCards paths eventually reach that
     * primitive.  The release patcher clones TransferDirect() into the retail
     * method body itself, so card-zone movement no longer depends on a runtime
     * detour or on whether the tiny retail stub is JIT-inlined.
     *
     * alpha7 hotfix: do NOT use List<T> indexer (get_Item) in cloned IL.
     * Cecil rebuild of closed generic MethodRefs such as
     * List<ICardLane>.get_Item produced a token that Mono could not resolve
     * at runtime (MissingMethodException), aborting CreatePlayerCards before
     * the board loaded.  Non-generic IList.get_Item + cast is used instead.
     */
    public static class CardTransferPatch
    {
        public static bool TransferDirect(
            ServerEffectUtilities.TriggerGameState gameState,
            CardInstanceId cardInstanceId,
            GameZone fromZone,
            GameZone toZone,
            PlayerUniqueId toOwnerNetworkId,
            GameZone.Position insertionPosition,
            ServerCardCatalog.MoveOperationType operationType,
            bool suppressLaneChangeTriggers)
        {
            if (Object.ReferenceEquals(gameState.CardCatalog, null) ||
                Object.ReferenceEquals(fromZone, null) ||
                Object.ReferenceEquals(toZone, null))
                return false;

            ServerCard card = gameState.CardCatalog.FindServerCard(cardInstanceId);
            if (Object.ReferenceEquals(card, null))
                return false;

            if (insertionPosition.LaneIndex < 0 ||
                insertionPosition.LaneIndex >= LaneCount(toZone))
                return false;

            PlayerState newOwner = FindPlayer(gameState, toOwnerNetworkId);
            if (Object.ReferenceEquals(newOwner, null))
                return false;

            GameZone.Position oldPosition = fromZone.FindCardPosition(cardInstanceId);
            if (oldPosition.LaneIndex < 0 || oldPosition.CardIndex < 0)
                return false;

            /* Same-zone operations only change slot/lane. */
            if (Object.ReferenceEquals(fromZone, toZone) &&
                card.OwnerNetId.Id == toOwnerNetworkId.Id)
            {
                if (!fromZone.RepositionCard(cardInstanceId, insertionPosition))
                    return false;

                ICardLane lane = LaneAt(toZone, insertionPosition.LaneIndex);
                if (Object.ReferenceEquals(lane, null))
                    return false;

                card.ChangeLane(
                    lane.GetCardLaneType(),
                    lane.GetExtraCardLaneType(),
                    insertionPosition.LaneIndex);
                if (!suppressLaneChangeTriggers &&
                    fromZone.Name == GameZoneName.CreatureBoard &&
                    oldPosition.LaneIndex != insertionPosition.LaneIndex)
                {
                    ServerEffectUtilities.TriggerEffectEvent(
                        gameState,
                        new ServerEffectUtilities.TriggerEvent(
                            EffectTriggerType.WhenCardChangesLane,
                            card,
                            gameState.Players));
                }
                return true;
            }

            if (!toZone.IsPositionFree(insertionPosition))
                return false;

            if (!fromZone.RemoveCard(cardInstanceId))
                return false;

            if (!toZone.InsertCard(cardInstanceId, insertionPosition))
            {
                /* Keep the zone mutation transactional. */
                fromZone.InsertCard(cardInstanceId, oldPosition);
                return false;
            }

            ICardLane destinationLane = LaneAt(toZone, insertionPosition.LaneIndex);
            if (Object.ReferenceEquals(destinationLane, null))
            {
                fromZone.InsertCard(cardInstanceId, oldPosition);
                toZone.RemoveCard(cardInstanceId);
                return false;
            }

            /* ServerCard.ChangeZone refreshes NetUid only when the PREVIOUS
             * zone is Hidden (see the surviving retail implementation).  Feed
             * it a fresh opaque id exactly for that transition. */
            CardNetUid netUid = card.NetUid;
            if (fromZone.Visibility == GameZone.VisibilityRule.Hidden)
                netUid = ServerCardCatalog.GenerateNewCardNetUid(
                    gameState.CardCatalog);

            bool transformed = operationType ==
                ServerCardCatalog.MoveOperationType.TransformCard;
            int oldOwnerId = card.OwnerNetId.Id;

            card.ChangeZone(
                toZone,
                destinationLane.GetCardLaneType(),
                destinationLane.GetExtraCardLaneType(),
                newOwner,
                insertionPosition.LaneIndex,
                netUid,
                transformed);

            if (oldOwnerId != newOwner.NetId.Id)
            {
                ServerEffectUtilities.TriggerEffectEvent(
                    gameState,
                    new ServerEffectUtilities.TriggerEvent(
                        EffectTriggerType.WhenCardChangesOwnership,
                        card,
                        gameState.Players));
            }

            if (!suppressLaneChangeTriggers &&
                fromZone.Name == GameZoneName.CreatureBoard &&
                toZone.Name == GameZoneName.CreatureBoard &&
                oldPosition.LaneIndex != insertionPosition.LaneIndex)
            {
                ServerEffectUtilities.TriggerEffectEvent(
                    gameState,
                    new ServerEffectUtilities.TriggerEvent(
                        EffectTriggerType.WhenCardChangesLane,
                        card,
                        gameState.Players));
            }

            /* Retail double cards split as they are drawn.  The composite is
             * replaced in-place by its two retail face cards and moved to
             * Purgatory by SplitDoubleCard, which also records the native
             * ActionSplitDoubleCard history entry and links the faces. */
            Card baseDefinition = GameManager.Instance.GetCard(card.CardId);
            if (fromZone.Name == GameZoneName.Deck &&
                toZone.Name == GameZoneName.Hand &&
                baseDefinition != null &&
                baseDefinition.HasSubtype(CardSubtype.Double) &&
                baseDefinition.DoubleCardReferences != null &&
                baseDefinition.DoubleCardReferences.Count >= 2)
            {
                CardActions.SplitDoubleCard.StaticExecute(
                    gameState, card, gameState.Log);
            }

            return true;
        }

        /*
         * Cloned into ServerCardCatalog as Nyx_FindPlayerForTransfer.
         * Uses non-generic IList to avoid List<PlayerState>.get_Item tokens.
         */
        private static PlayerState FindPlayer(
            ServerEffectUtilities.TriggerGameState gameState,
            PlayerUniqueId uid)
        {
            if (Object.ReferenceEquals(gameState, null) ||
                Object.ReferenceEquals(gameState.Players, null))
                return null;

            IList players = gameState.Players;
            int count = players.Count;
            int i;
            for (i = 0; i < count; ++i)
            {
                PlayerState player = players[i] as PlayerState;
                if (!Object.ReferenceEquals(player, null) &&
                    player.NetId.Id == uid.Id)
                    return player;
            }
            return null;
        }

        /*
         * Cloned into ServerCardCatalog as Nyx_LaneAtForTransfer.
         * Uses non-generic IList.get_Item so the in-place IL clone does not
         * emit a closed List<ICardLane>::get_Item MethodRef.
         */
        private static ICardLane LaneAt(GameZone zone, int laneIndex)
        {
            if (Object.ReferenceEquals(zone, null) ||
                Object.ReferenceEquals(zone.LaneList, null))
                return null;
            if (laneIndex < 0)
                return null;

            IList lanes = zone.LaneList;
            if (laneIndex >= lanes.Count)
                return null;

            return lanes[laneIndex] as ICardLane;
        }

        private static int LaneCount(GameZone zone)
        {
            if (Object.ReferenceEquals(zone, null) ||
                Object.ReferenceEquals(zone.LaneList, null))
                return 0;
            IList lanes = zone.LaneList;
            return lanes.Count;
        }
    }
}
