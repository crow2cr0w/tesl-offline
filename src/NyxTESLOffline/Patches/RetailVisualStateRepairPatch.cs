using System;
using System.Collections;
using CcgRpc;
using HarmonyLib;
using SPTESL;
using UnityEngine;
using Object = System.Object;

namespace NyxTESLOffline.Patches
{
    /*
     * The retail client already owns card placement, summon/play FX, VO and
     * per-card visual behavior.  The local host only repairs the missing
     * source visual invariant before handing control back to
     * SequenceCardPlay.PlayCardToBoard().
     *
     * In the retail online path the visual card referenced by CardNetUid is
     * still reachable from an IClientGameZone when ActionPlayToBoard executes.
     * With the local loopback/predictive input path that assumption is not
     * stable: the hand card can be detached before the authoritative history
     * item is consumed, and hidden opponent cards can have no addressable hand
     * visual.  Stock SequenceCardPlay silently exits when currentGameZone is
     * null.  That is the exact failure observed in alpha10: Execute() runs but
     * PlayCardToBoard() never runs.
     */
    [HarmonyPatch(typeof(SequenceCardPlay), "Execute")]
    internal static class RetailSequenceCardPlayRepairPatch
    {
        /*
         * The Cloud/nyxdec body of SequenceCardPlay.Execute has one decisive
         * gate: PlayCardToBoard() is reached only when the source zone still
         * returns a concrete Card3D.  In the local client the input/prediction
         * path and ClientCardCatalog can move the logical card before the
         * authoritative EventPlayCard is consumed, so the stock iterator has
         * repeatedly returned without ever calling PlayCardToBoard().
         *
         * For persistent board cards only, mirror that surviving retail body
         * exactly enough to restore its precondition, then enter the ORIGINAL
         * PlayCardToBoard coroutine.  Spells/actions and equipped items remain
         * entirely on the stock Execute path (PlaySpell/PlayItem).
         */
        [HarmonyPrefix]
        private static bool Prefix(
            SequenceCardPlay __instance,
            MatchLogAnimationData matchLogData,
            BoardState boardState,
            ref IEnumerator __result)
        {
            if (Object.ReferenceEquals(Plugin.ActiveLocalMatch, null) ||
                Object.ReferenceEquals(__instance, null) ||
                Object.ReferenceEquals(boardState, null) ||
                Object.ReferenceEquals(matchLogData, null) ||
                Object.ReferenceEquals(matchLogData.ActionData, null) ||
                matchLogData.ActionData.Type != HistoryType.ActionPlayToBoard ||
                matchLogData.ActionData.AffectedCardsArray.Count <= 0)
                return true;

            HistoryCard historyCard =
                matchLogData.ActionData.AffectedCardsArray[0];
            if (Object.ReferenceEquals(historyCard, null) ||
                Object.ReferenceEquals(historyCard.CurrentPosition, null))
                return true;

            GameZoneName targetZone =
                (GameZoneName)historyCard.CurrentPosition.GameZoneName;

            /* Retail Execute owns these branches and they do not produce a
             * persistent lane Card3D. */
            if (targetZone == GameZoneName.Board ||
                targetZone == GameZoneName.ItemBoard ||
                matchLogData.ActionData.EquipCardNetUid !=
                    CardNetUid.GetInvalid().Id)
                return true;

            __result = ExecutePersistentRetailPlay(
                __instance, matchLogData, boardState, historyCard, targetZone);
            return false;
        }

        private static IEnumerator ExecutePersistentRetailPlay(
            SequenceCardPlay sequence,
            MatchLogAnimationData matchLogData,
            BoardState boardState,
            HistoryCard historyCard,
            GameZoneName targetZone)
        {
            int laneIndex = historyCard.CurrentPosition.LaneIndex;
            int positionIndex = historyCard.CurrentPosition.PositionIndex;
            PlayerControllerType playerType =
                GameNetworkManager.Instance.GetPlayerControllerType(
                    historyCard.CurrentPosition.OwningPlayerUid);

            IClientGameZone currentZone =
                boardState.FindGameZoneWithCard(historyCard.CardNetUid);
            Card3D animateCard = null;

            if (!Object.ReferenceEquals(currentZone, null))
            {
                int currentIndex = currentZone.FindIndexForCard(
                    historyCard.CardNetUid);
                Plugin.Log("visual play canonical preflight: uid="
                    + historyCard.CardNetUid
                    + " source=" + currentZone.GameZoneName
                    + " sourceLane=" + currentZone.ClientGameZonePosition
                    + " sourceIndex=" + currentIndex
                    + " target=" + targetZone
                    + " targetLane=" + laneIndex
                    + " targetIndex=" + positionIndex);

                animateCard = currentZone.RemoveAndGetCard(
                    boardState, historyCard.CardNetUid);
            }

            IClientGameZone hand = boardState.FindLane(
                GameZoneName.Hand,
                playerType,
                ClientGameZonePosition.Left);

            /* The authoritative UID can be absent from the visible hand after
             * local input detached it.  For the local player, recover the
             * matching physical hand card by CardId.  For the opponent,
             * PlayCardToBoard can allocate the correct lane visual from the
             * retail MatchCardPool once it has the authoritative ClientCard. */
            if (Object.ReferenceEquals(animateCard, null) &&
                !Object.ReferenceEquals(hand, null) &&
                playerType == PlayerControllerType.Player)
            {
                Card3D[] cards = hand.AllCards;
                int i;
                for (i = 0; i < cards.Length; ++i)
                {
                    Card3D candidate = cards[i];
                    if (Object.ReferenceEquals(candidate, null) ||
                        Object.ReferenceEquals(candidate.ClientCard, null))
                        continue;

                    if (candidate.ClientCard.NetUid.Id == historyCard.CardNetUid ||
                        (historyCard.CardId != 0 &&
                         candidate.ClientCard.CardId == historyCard.CardId))
                    {
                        animateCard = hand.RemoveAndGetCard(
                            boardState, candidate.ClientCard.NetUid.Id);
                        break;
                    }
                }
            }

            ClientCard clientCard = null;
            bool wasRevealed = historyCard.IsRevealed;
            if (!Object.ReferenceEquals(animateCard, null) &&
                !Object.ReferenceEquals(animateCard.ClientCard, null))
            {
                /* A card in the opponent's hand is hidden before it is played.
                 * The authoritative ActionPlayToBoard explicitly reveals it;
                 * keep that public delta instead of overwriting it with the
                 * source hand visual's old hidden state. */
                wasRevealed = historyCard.IsRevealed ||
                    animateCard.IsRevealed();
                ClientCard handClientCard = animateCard.ClientCard;
                bool revealedCardMissingBase = historyCard.IsRevealed &&
                    historyCard.CardId != UniqueResourceId.GetInvalid().Id &&
                    (handClientCard.CardId != historyCard.CardId ||
                     Object.ReferenceEquals(handClientCard.BaseCard, null));
                if (revealedCardMissingBase)
                {
                    /* Hidden opponent hand entries intentionally have no
                     * BaseCard. CreateBestClientCard prefers the existing
                     * Card3D and UpdateWithMinimalData only changes owner/zone,
                     * so it can never hydrate the newly revealed card ID. */
                    clientCard = SequenceUtils.CreateClientCard(historyCard);
                    Plugin.Log("visual play rebuilt revealed hand placeholder: card="
                        + historyCard.CardId
                        + " uid=" + historyCard.CardNetUid
                        + " base=" + (!Object.ReferenceEquals(
                            clientCard, null) && !Object.ReferenceEquals(
                                clientCard.BaseCard, null)));
                }
                else
                {
                    clientCard = SequenceUtils.CreateBestClientCard(
                        historyCard, animateCard, null);
                }
            }
            else
            {
                /* CreateBestClientCard(null source) is the exact retail
                 * fallback.  Because our minimal authoritative history includes
                 * CardId, the ClientCard constructor can still bind BaseCard. */
                clientCard = SequenceUtils.CreateBestClientCard(
                    historyCard, null, null);
            }

            if (Object.ReferenceEquals(clientCard, null) ||
                Object.ReferenceEquals(clientCard.BaseCard, null))
            {
                Plugin.Warn("visual play canonical abort: missing ClientCard/BaseCard"
                    + " card=" + historyCard.CardId
                    + " uid=" + historyCard.CardNetUid);
                yield break;
            }

            /* If a local prediction already inserted the Card3D into the
             * destination lane, rewind its transform through the actual Hand
             * layout before the authoritative animation.  This prevents the
             * common 'card starts at portrait / vanishes' artifact while still
             * leaving PlayCardToBoard in charge of the board transition. */
            if (!Object.ReferenceEquals(animateCard, null) &&
                !Object.ReferenceEquals(hand, null))
            {
                clientCard.GameZoneName = GameZoneName.Hand;
                animateCard.ClientCard = clientCard;
                int handIndex = hand.GetCardCount();
                Vector3 handPosition = hand.AddCard(animateCard, handIndex);
                animateCard.transform.position = handPosition;
                animateCard = hand.RemoveAndGetCard(
                    boardState, historyCard.CardNetUid);
            }

            /* Restore the authoritative public destination after the hand
             * transform rewind.  PlayCardToBoard will set/refresh the visual
             * and AddCardAnimating() will own final placement. */
            clientCard.UpdateWithMinimalData(historyCard);

            Plugin.Log("visual play canonical dispatch: card="
                + historyCard.CardId
                + " uid=" + historyCard.CardNetUid
                + " owner=" + playerType
                + " animate=" + (!Object.ReferenceEquals(animateCard, null))
                + " to=" + targetZone
                + " lane=" + laneIndex
                + " pos=" + positionIndex);

            IEnumerator retail = sequence.PlayCardToBoard(
                matchLogData,
                animateCard,
                clientCard,
                boardState,
                playerType,
                targetZone,
                positionIndex,
                laneIndex,
                wasRevealed);

            if (!Object.ReferenceEquals(retail, null))
            {
                while (retail.MoveNext())
                    yield return retail.Current;
            }

            IClientGameZone finalZone =
                boardState.FindGameZoneWithCard(historyCard.CardNetUid);
            if (Object.ReferenceEquals(finalZone, null) ||
                finalZone.GameZoneName != targetZone)
            {
                Plugin.Warn("visual play canonical postcondition failed: card="
                    + historyCard.CardId
                    + " uid=" + historyCard.CardNetUid
                    + " expected=" + targetZone
                    + " actual=" + (Object.ReferenceEquals(finalZone, null)
                        ? "null" : finalZone.GameZoneName.ToString()));
            }
            else
            {
                Plugin.Log("visual play canonical complete: card="
                    + historyCard.CardId
                    + " uid=" + historyCard.CardNetUid
                    + " zone=" + finalZone.GameZoneName
                    + " lane=" + finalZone.ClientGameZonePosition
                    + " index=" + finalZone.FindIndexForCard(
                        historyCard.CardNetUid));
            }
        }
    }

    /*
     * MaxMana is carried by the local host as ActionSetPlayerAttribute(23).
     * Retail SequenceSetPlayerAttribute does not update the AP widget for that
     * attribute; SetMaxAP is normally reached through avatar history deltas.
     * Mirror that exact UI endpoint so the displayed value remains X/Y while
     * keeping current mana on SequenceSetMana/SequenceModifyMana.
     */
    [HarmonyPatch(typeof(SequenceSetPlayerAttribute), "UpdatePlayerAttribute")]
    internal static class RetailMaxManaVisualPatch
    {
        [HarmonyPostfix]
        private static void Postfix(
            BoardState boardState,
            int attributeType,
            int attributeValue,
            PlayerControllerType controllerType)
        {
            if (attributeType != (int)AttributeType.MaxMana ||
                Object.ReferenceEquals(boardState, null))
                return;

            PlayerInfoController info =
                boardState.FindLane<PlayerInfoController>(
                    GameZoneName.AvatarBoard,
                    controllerType,
                    ClientGameZonePosition.Left);
            if (Object.ReferenceEquals(info, null))
                return;

            info.SetMaxAP(attributeValue);
            Plugin.Log("visual protocol PlayerInfo.SetMaxAP: owner="
                + controllerType + " value=" + attributeValue);
        }
    }

    /*
     * SequenceFxUtils.UpdateForAvatar() requires the damage target Card3D to be
     * discoverable by NetUid.  Alpha10 proves the history delta reaches
     * SequenceCardDamage/PlayCardFx, but PlayerInfoController.SetHealth() never
     * follows.  Resolve the portrait by CurrentPosition owner when the avatar
     * visual lookup fails, then apply the same authoritative CurrentHealth that
     * the retail UpdateForAvatar path would consume.
     */
    [HarmonyPatch(typeof(SequenceCardDamage), "Execute")]
    internal static class RetailAvatarHealthRepairPatch
    {
        [HarmonyPostfix]
        private static void Postfix(
            MatchLogAnimationData matchLogData,
            BoardState boardState,
            ref IEnumerator __result)
        {
            __result = ReconcileAfterRetailDamage(
                __result, matchLogData, boardState);
        }

        private static IEnumerator ReconcileAfterRetailDamage(
            IEnumerator original,
            MatchLogAnimationData matchLogData,
            BoardState boardState)
        {
            if (!Object.ReferenceEquals(original, null))
            {
                while (original.MoveNext())
                    yield return original.Current;
            }

            if (Object.ReferenceEquals(matchLogData, null) ||
                Object.ReferenceEquals(matchLogData.ActionData, null) ||
                Object.ReferenceEquals(boardState, null))
                yield break;

            int i;
            for (i = 0;
                 i < matchLogData.ActionData.AffectedCardsArray.Count;
                 ++i)
            {
                HistoryCard update =
                    matchLogData.ActionData.AffectedCardsArray[i];
                if (Object.ReferenceEquals(update, null))
                    continue;

                Card3D target = boardState.FindCardByNetUid(update.CardNetUid);
                if (!Object.ReferenceEquals(target, null) &&
                    !Object.ReferenceEquals(target.ClientCard, null))
                {
                    bool mismatch = update.UseCurrentHealth &&
                        target.ClientCard.CurrentHealth != update.CurrentHealth;
                    int a;
                    for (a = 0;
                         !mismatch && a < update.AffectedAttributeName.Count;
                         ++a)
                    {
                        AttributeType type =
                            (AttributeType)update.AffectedAttributeName[a];
                        mismatch = target.ClientCard.GetAttributeValue(type) !=
                            update.AffectedAttributeValue[a];
                    }
                    if (mismatch)
                        SequenceFxUtils.UpdateModifiedAttributes(
                            boardState, matchLogData, target, update);
                }

                if (!update.UseCurrentHealth ||
                    Object.ReferenceEquals(update.CurrentPosition, null) ||
                    (GameZoneName)update.CurrentPosition.GameZoneName !=
                        GameZoneName.AvatarBoard)
                    continue;

                PlayerControllerType owner =
                    GameNetworkManager.Instance.GetPlayerControllerType(
                        update.CurrentPosition.OwningPlayerUid);
                PlayerInfoController info =
                    boardState.FindLane<PlayerInfoController>(
                        GameZoneName.AvatarBoard,
                        owner,
                        ClientGameZonePosition.Left);
                if (Object.ReferenceEquals(info, null))
                {
                    Plugin.Warn("avatar health repair: PlayerInfoController missing"
                        + " owner=" + owner + " uid=" + update.CardNetUid);
                    continue;
                }

                int clientHealthBefore = -1;
                if (!Object.ReferenceEquals(info.PlayerAvatar, null) &&
                    !Object.ReferenceEquals(info.PlayerAvatar.ClientCard, null))
                {
                    clientHealthBefore = info.PlayerAvatar.ClientCard.CurrentHealth;
                    info.PlayerAvatar.ClientCard.CurrentHealth =
                        update.CurrentHealth;
                    int startingHealth =
                        info.PlayerAvatar.ClientCard.GetAttributeValue(
                            AttributeType.StartingHealth);
                    info.ToggleHealthAboveMax(
                        update.CurrentHealth > startingHealth);
                }

                Plugin.Log("avatar health reconcile: owner=" + owner
                    + " uid=" + update.CardNetUid
                    + " clientBefore=" + clientHealthBefore
                    + " authoritative=" + update.CurrentHealth);

                /* Always route the authoritative value through the retail
                 * presenter.  Even if another path happened to update the
                 * number first, SetHealth also owns the red cover material,
                 * individual health ticks and damage feedback.  Reapplying the
                 * same value is idempotent for those transitions. */
                info.SetHealth(update.CurrentHealth, true);
            }
        }
    }
}
