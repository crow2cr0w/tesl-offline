using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using SPTESL;

namespace NyxTESLOffline.Patches
{
    // The EventTrigger already plays the original board-wide FX. Playing it
    // again through PlayCardFx's affected-card loop duplicates it N times.
    [HarmonyPatch(typeof(SequenceFxUtils),"PlayCardFx")]
    internal static class AlmsiviBoardFxPatch
    {
        static readonly MethodInfo Update = typeof(SequenceFxUtils).GetMethod(
            "UpdateCardFromHistoryCard",BindingFlags.NonPublic|BindingFlags.Static);
        [HarmonyPrefix]
        static bool Prefix(AnimationSequence sequence,BoardState boardState,MatchLogAnimationData matchLogData,ref IEnumerator __result)
        {
            if(Update==null || !(sequence is SequenceCardBuffSimultaneous) || matchLogData==null ||
                matchLogData.HeaderContext==null || matchLogData.ActionData==null ||
                matchLogData.HeaderContext.AssetHash!=-1406478351 || matchLogData.ActionData.AssetHash!=-1406478351) return true;
            __result=UpdateWithoutRepeatingBoardFx(boardState,matchLogData);return false;
        }
        static IEnumerator UpdateWithoutRepeatingBoardFx(BoardState boardState,MatchLogAnimationData data)
        {
            foreach(var card in data.ActionData.AffectedCardsArray)
                Update.Invoke(null,new object[]{boardState.FindCardByNetUid(card.CardNetUid),card,boardState});
            Plugin.Log("Almsivi board FX repetition suppressed; original trigger FX retained; targets="+data.ActionData.AffectedCardsArray.Count);
            yield break;
        }
    }
}
