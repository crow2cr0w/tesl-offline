using System.Collections;
using System.Collections.Generic;
using CcgRpc;
using HarmonyLib;
using SPTESL;
using UnityEngine;

namespace NyxTESLOffline
{
    /* The retail attack sequence starts its return FX immediately after the
     * lunge. A rune prophecy needs the history queue to keep moving so the
     * player can make a decision, while the attacker stays at the target.
     * A face attack with a pending prophecy uses this delayed retail return FX. */
    [HarmonyPatch(typeof(SequenceCardAttack), "Execute")]
    internal static class ProphecyAttackPausePatch
    {
        private static bool Prefix(
            MatchLogAnimationData matchLogData,
            BoardState boardState,
            ref IEnumerator __result)
        {
            int actorUid = matchLogData.HeaderContext.ActorCardNetUid;
            if (!OfflineSimulationHost.IsAttackVisualHeld(actorUid))
                return true;
            __result = ExecutePausedAttack(matchLogData, boardState, actorUid);
            return false;
        }

        private static IEnumerator ExecutePausedAttack(
            MatchLogAnimationData data,
            BoardState board,
            int actorUid)
        {
            Card3D attacker = board.FindCardByNetUid(actorUid);
            Card3D target = board.FindCardByNetUid(
                data.HeaderContext.TargetCardNetUid);
            if (attacker == null || target == null)
            {
                if (attacker != null)
                {
                    attacker.Attacking = false;
                    attacker.Animating = false;
                }
                OfflineSimulationHost.MarkAttackVisualComplete(actorUid);
                yield break;
            }

            PlayerControllerType ownerType =
                GameNetworkManager.Instance.GetPlayerControllerType(
                    data.HeaderContext.ActingPlayerInstanceId);
            VisualDirector mainVisuals = null;
            VisualDirector endVisuals = null;
            CardFxDefinition fxDef =
                SequenceDataLookup.FindFxDefinitionByName("card_attack");
            CardFxDefinition fxEndDef =
                SequenceDataLookup.FindFxDefinitionByName("card_finish_attack");
            foreach (Coroutine load in new List<Coroutine> {
                CoroutineHandler.Start(SequenceFxUtils.LoadCardFx(
                    repeatableInstanceHandle: new MatchFxTracker.RepeatableFxInstanceHandle(),
                    boardState: board, fxDefinition: fxEndDef,
                    source: attacker.ClientCard, target: target.ClientCard,
                    variantOverrides: default(FxVariantTestOverrides),
                    del: delegate(FxDefinitionLoadResult loaded) {
                        if (loaded != null) endVisuals = loaded.Visual;
                    })),
                CoroutineHandler.Start(SequenceFxUtils.LoadCardFx(
                    repeatableInstanceHandle: new MatchFxTracker.RepeatableFxInstanceHandle(),
                    boardState: board, fxDefinition: fxDef,
                    source: attacker.ClientCard, target: target.ClientCard,
                    variantOverrides: default(FxVariantTestOverrides),
                    del: delegate(FxDefinitionLoadResult loaded) {
                        if (loaded != null) mainVisuals = loaded.Visual;
                    }))
            })
                yield return load;

            if (mainVisuals == null)
            {
                attacker.Attacking = false;
                OfflineSimulationHost.MarkAttackVisualComplete(actorUid);
                yield break;
            }

            mainVisuals.SetData(board, ownerType, attacker, target);
            mainVisuals.Play();
            while (!mainVisuals.IsTimelineComplete())
                yield return 0;

            /* Run separately: blocking this history sequence would also block
             * the prophecy draw and its command prompt behind it. */
            CoroutineHandler.Start(ReturnAfterProphecy(
                board, attacker, endVisuals, actorUid));
        }

        private static IEnumerator ReturnAfterProphecy(
            BoardState board,
            Card3D attacker,
            VisualDirector endVisuals,
            int actorUid)
        {
            while (OfflineSimulationHost.IsAttackVisualHeld(actorUid))
                yield return 0;

            if (attacker != null && endVisuals != null)
            {
                IClientGameZone zone = board.FindGameZoneWithCard(
                    attacker.NetUid.Id);
                if (zone != null)
                {
                    Vector3 from = attacker.transform.position;
                    Vector3 to = zone.FindCardPosition(attacker.NetUid.Id);
                    endVisuals.SetData(attacker);
                    endVisuals.SetPosition(VisualDirectorPosition.Source, from);
                    endVisuals.SetPosition(VisualDirectorPosition.Target, to);
                    endVisuals.Play();
                    while (endVisuals.CurrentlyPlaying())
                        yield return 0;
                }
            }
            if (attacker != null)
                attacker.Attacking = false;
            OfflineSimulationHost.MarkAttackVisualComplete(actorUid);
        }
    }

    /* The server queues ActionEndAttack after the AI prophecy has resolved.
     * Its retail sequence would play a second lunge, so use it only as an
     * ordered release marker for the waiting return coroutine. */
    [HarmonyPatch(typeof(SequenceCardFinishAttack), "Execute")]
    internal static class ProphecyAttackEndMarkerPatch
    {
        private static bool Prefix(
            MatchLogAnimationData matchLogData,
            ref IEnumerator __result)
        {
            int actorUid = matchLogData.HeaderContext.ActorCardNetUid;
            if (!OfflineSimulationHost.ReleaseAttackVisualAtEndMarker(actorUid))
                return true;
            __result = EmptySequence();
            return false;
        }

        private static IEnumerator EmptySequence()
        {
            yield break;
        }
    }
}
