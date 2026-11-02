using System;
using System.Collections.Generic;
using HarmonyLib;
using SPTESL;
using CcgRpc;
namespace NyxTESLOffline
{
    // Retail DiscardUtilities.DiscardCards is stripped in the local runtime.
    [HarmonyPatch(typeof(DiscardUtilities), "DiscardCards")]
    internal static class OfflineDiscardUtilities
    {
        [HarmonyPrefix]
        private static bool DiscardCardsPrefix(
            ServerEffectUtilities.TriggerGameState triggerGameState,
            List<ServerCard> discards,
            int assetHash,
            bool isFullHandDiscard)
        {
            if (Object.ReferenceEquals(Plugin.ActiveLocalMatch, null))
                return true;

            Discard(triggerGameState, discards, assetHash, null);
            return false;
        }

        internal static void Discard(
            ServerEffectUtilities.TriggerGameState state,
            List<ServerCard> cards,
            int assetHash,
            ServerCard source)
        {
            CcgRpc.HistoryItem moved = new CcgRpc.HistoryItem();
            moved.Type = CcgRpc.HistoryType.ActionMoveCards;
            moved.AssetHash = assetHash;
            CcgRpc.HistoryItem added = new CcgRpc.HistoryItem();
            added.Type = CcgRpc.HistoryType.ActionAddToDiscard;
            added.AssetHash = assetHash;
            if (!Object.ReferenceEquals(source, null))
            {
                moved.ActorCardNetUid = source.NetUid.Id;
                moved.ActingPlayerInstanceId = source.OwnerNetId.Id;
                added.ActorCardNetUid = source.NetUid.Id;
                added.ActingPlayerInstanceId = source.OwnerNetId.Id;
            }

            foreach (ServerCard card in cards)
            {
                if (Object.ReferenceEquals(card, null) ||
                    (card.CurrentGameZoneName != GameZoneName.Deck &&
                     card.CurrentGameZoneName != GameZoneName.Hand))
                    continue;

                int visibleUid = card.NetUid.Id;
                /* MoveCard can leave hand/deck discards in Deathrattle.
                 * Use the catalog's complete discard transition, as retail
                 * callers do, before emitting the client history/FX items. */
                ServerCardCatalog.MoveToCemetery(state, card);
                if (card.CurrentGameZoneName != GameZoneName.Cemetery)
                    throw new InvalidOperationException(
                        "Discard transition failed for " + card.CardId);

                CcgRpc.HistoryCard movement =
                    card.HistoryCreate_FullStateCard(state.CardCatalog);
                movement.CardNetUid = visibleUid;
                /* SequenceMoveCards only recycles an already-visible hand
                 * card when the destination is Deathrattle. A direct
                 * Cemetery destination merely inserts a graveyard thumbnail
                 * and leaves the hand Card3D stranded on screen. Deathrattle
                 * is the client's transient discard-animation route; the
                 * following ActionAddToDiscard publishes the actual cemetery
                 * entry. Keep the authoritative card in Cemetery. */
                movement.CurrentPosition.GameZoneName =
                    (int)GameZoneName.Deathrattle;
                movement.CurrentPosition.GameZoneType =
                    (int)GameZoneType.Deathrattle;
                movement.IsFullState = false;
                Plugin.Log("discard visual protocol: uid=" + visibleUid
                    + " card=" + card.CardId
                    + " owner=" + card.OwnerNetId.Id
                    + " premium=" + card.Premium
                    + " previousType=" + movement.PreviousGameZoneType
                    + " animationZone=Deathrattle finalZone="
                    + card.CurrentGameZoneName + " asset=" + assetHash);
                moved.AffectedCardsArray.Add(movement);
                added.AffectedCardsArray.Add(
                    card.HistoryCreate_FullStateCard(state.CardCatalog));
            }

            if (moved.AffectedCardsArray.Count == 0)
                return;

            state.Log.PlayerDiffLogger.DiffDeckCountsAndAddToHistoryItem(
                state.Players, state.CardCatalog, moved);
            /* MoveCards must reach the client while the card still exists in
             * its hand visual. AddToDiscard then updates the graveyard list. */
            state.Log.AddInstant(moved);
            state.Log.AddInstant(added);
        }
    }

}
