using HarmonyLib;
using SPTESL;

namespace NyxTESLOffline.Patches
{
    // Retain normal combat resolution and history/FX, allowing further face
    // damage to accumulate below zero only while Vivec's aura protects Avatar.
    [HarmonyPatch(typeof(ServerCard), "ModifyCurrentHealth")]
    internal static class ExaltedAvatarOverkillPatch
    {
        [HarmonyPrefix]
        private static void Prefix(ServerCard __instance, int modification,
            ref bool allowOverkill)
        {
            if (modification < 0 && __instance.HasSubtype(CardSubtype.Avatar) &&
                __instance.QueryAttribute(AttributeType.Immortal).Value > 0)
                allowOverkill = true;
        }
    }
}
