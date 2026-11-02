using HarmonyLib;
using SPTESL;

namespace NyxTESLOffline.Patches
{
    // Turn and death triggers can reach the retail transform primitive without
    // an inciter. Its history writer dereferences that value before transforming.
    [HarmonyPatch(typeof(CardActions.TransformCard), "StaticExecute")]
    internal static class TransformCardHostPatch
    {
        private static void Prefix(ref ServerCard inciterCard, ServerCard fromServerCard)
        {
            if (inciterCard == null)
                inciterCard = fromServerCard;
        }
    }
}
