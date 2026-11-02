using System.Collections;
using CcgRpc;
using HarmonyLib;
using SPTESL;

namespace NyxTESLOffline.Patches
{
    /* SequenceEnchantmentsExpired already replaces the ClientCard with the
     * authoritative full state.  Force its status animator to consume that
     * refreshed state before the next player input, so expired shackle chains
     * cannot remain visible after Freeze has been removed. */
    [HarmonyPatch(typeof(SequenceEnchantmentsExpired), "Execute")]
    internal static class ExpiredStatusRefreshPatch
    {
        [HarmonyPostfix]
        private static void Postfix(ref IEnumerator __result,
            MatchLogAnimationData matchLogData, BoardState boardState)
        {
            if (__result != null && matchLogData != null && boardState != null)
                __result = RefreshExpiredStatuses(__result, matchLogData, boardState);
        }

        private static IEnumerator RefreshExpiredStatuses(IEnumerator retail,
            MatchLogAnimationData matchLogData, BoardState boardState)
        {
            while (retail.MoveNext())
                yield return retail.Current;

            if (matchLogData.ActionData == null)
                yield break;

            foreach (HistoryCard historyCard in
                matchLogData.ActionData.AffectedCardsArray)
            {
                if (historyCard == null)
                    continue;
                Card3D card = boardState.FindCardByNetUid(historyCard.CardNetUid);
                if (card == null || card.ClientCard == null)
                    continue;

                ClientCard clientCard = card.ClientCard;
                bool stillFrozen = clientCard.GetAttributeValue(AttributeType.Freeze) > 0 ||
                    clientCard.GetAttributeValue(AttributeType.PermFreeze) > 0 ||
                    clientCard.GetAttributeValue(AttributeType.IceFreeze) > 0 ||
                    clientCard.GetAttributeValue(AttributeType.WebFreeze) > 0 ||
                    clientCard.GetAttributeValue(AttributeType.BoneFreeze) > 0;
                if (stillFrozen)
                    continue;

                card.RefreshStatusVisuals();
                card.UpdateStatusVisuals();
                Plugin.Log("expired shackle visuals refreshed: uid=" + card.NetUid.Id);
            }
        }
    }

    /* Equipment can remove a permanent shackle and is reported as a full card
     * refresh. Re-sync status FX after retail has applied the refreshed card,
     * preserving the retail equip animation and history sequence. */
    [HarmonyPatch(typeof(SequenceFullCardRefresh), "Execute")]
    internal static class FullCardStatusRefreshPatch
    {
        [HarmonyPostfix]
        private static void Postfix(ref IEnumerator __result,
            MatchLogAnimationData matchLogData, BoardState boardState)
        {
            if (__result != null && matchLogData != null && boardState != null)
                __result = RefreshStatuses(__result, matchLogData, boardState);
        }

        private static IEnumerator RefreshStatuses(IEnumerator retail,
            MatchLogAnimationData matchLogData, BoardState boardState)
        {
            while (retail.MoveNext())
                yield return retail.Current;

            if (matchLogData.ActionData == null)
                yield break;

            foreach (HistoryCard historyCard in
                matchLogData.ActionData.AffectedCardsArray)
            {
                if (historyCard == null)
                    continue;
                Card3D card = boardState.FindCardByNetUid(historyCard.CardNetUid);
                if (card == null || card.ClientCard == null)
                    continue;

                card.RefreshStatusVisuals();
                card.UpdateStatusVisuals();
                Plugin.Log("full card refresh status visuals synced: uid="
                    + card.NetUid.Id);
            }
        }
    }
}
