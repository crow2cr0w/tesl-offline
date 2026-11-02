using HarmonyLib;
using SPTESL;
namespace NyxTESLOffline.Patches
{
    // This retail build's general aura updater is a stripped stub.
    [HarmonyPatch(typeof(EnchantmentUtilities), "RecalculateAuras")]
    internal static class HiddenTrailAuraPatch
    {
        [HarmonyPostfix]
        private static void Postfix(ServerEffectUtilities.TriggerGameState gameState)
        {
            AgilityCardRules.RefreshHiddenTrailAuras(gameState);
            NaryuAuraRules.Refresh(gameState);
            RecoveredAuraRules.Refresh(gameState);
        }
    }
}
