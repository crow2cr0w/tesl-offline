using HarmonyLib;
using Pixelplacement;
using SPTESL;
using UnityEngine.SceneManagement;

namespace NyxTESLOffline.Patches
{
    /*
     * Retail LoadGameBoard() does not load CDGameBoard itself.  It consumes a
     * one-shot delegate installed by InitializationState.  A local match can be
     * fully alive while that delegate is absent/stale, leaving the player in
     * the menu forever.  Preserve the retail delegate when it exists; fall back
     * to the actual retail game-board scene only for an active Nyx match.
     */
    [HarmonyPatch(typeof(GameNetworkManager), "LoadGameBoard")]
    internal static class LocalGameBoardTransitionPatch
    {
        private static bool Prefix(GameNetworkManager __instance)
        {
            if (System.Object.ReferenceEquals(Plugin.ActiveLocalMatch, null))
                return true;

            bool outside = __instance.IsOutsideOfMatch();
            bool hasDelegate = !System.Object.ReferenceEquals(
                __instance.LoadGameBoardDelegate, null);

            Plugin.Log("LoadGameBoard local transition: delegate="
                + hasDelegate + " outside=" + outside);

            /* Best case: keep the retail state-machine transition exactly as
             * shipped.  InitializationState's delegate unlocks the current
             * state and changes to ExitState, which then loads CDGameBoard. */
            if (hasDelegate)
                return true;

            if (outside)
            {
                Plugin.Warn("LoadGameBoard requested for local match but "
                    + "GameNetworkManager is outside match; refusing scene load");
                return true;
            }

            Plugin.Warn("LoadGameBoardDelegate missing; forcing retail "
                + "CDGameBoard scene for active local match");

            Singleton<SettingsManager>.Instance.HasGameLoaded = true;
            SceneManager.LoadScene("CDGameBoard");
            return false;
        }
    }
}
