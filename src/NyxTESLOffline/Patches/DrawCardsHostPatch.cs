using HarmonyLib;
using SPTESL;

namespace NyxTESLOffline.Patches
{
    [HarmonyPatch(typeof(PlayerActions.DrawCards), "Execute")]
    internal static class DrawCardsHostPatch
    {
        private static bool Prefix(
            PlayerActions.DrawCards __instance,
            PlayerActionArguments actionArguments)
        {
            OfflineDrawUtilities.ExecuteAction(__instance, actionArguments);
            return false;
        }
    }
}
