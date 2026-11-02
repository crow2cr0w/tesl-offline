using System;
using System.Reflection;
using HarmonyLib;
using SPTESL;

namespace NyxTESLOffline.Patches
{
    /* Retail's SetPlaymatFromScenario fixes single-player matches to the
     * scenario's PlaymatType. Training and ranked local matches vary only
     * that visual choice; SetRandomPlaymat is the retail selector already
     * used for null and multiplayer scenarios. Campaigns keep their scenario. */
    [HarmonyPatch(typeof(PlaymatSelector), "SetPlaymatFromScenario")]
    internal static class PracticeRandomPlaymatPatch
    {
        private static readonly MethodInfo SetRandomPlaymat =
            AccessTools.Method(
                typeof(PlaymatSelector),
                "SetRandomPlaymat",
                new[] { typeof(BoardState) });
        private static readonly FieldInfo MatchIdField =
            AccessTools.Field(typeof(GameNetworkManager), "_matchId");

        private static bool Prefix(
            PlaymatSelector __instance,
            Scenario scenario,
            BoardState boardState)
        {
            if (!Plugin.RandomizeLocalPlaymat)
                return true;

            if (SetRandomPlaymat == null)
            {
                Plugin.Warn("local playmat randomizer: retail selector unavailable; using scenario playmat");
                return true;
            }

            GameNetworkManager manager = GameNetworkManager.Instance;
            string oldMatchId = MatchIdField == null || manager == null
                ? null
                : MatchIdField.GetValue(manager) as string;
            try
            {
                /* Retail's selector hashes MatchId. StartSinglePlayerMatch
                 * can retain an empty/stale ID, so supply a one-shot unique
                 * seed and restore the network field immediately afterward. */
                if (MatchIdField != null && manager != null)
                    MatchIdField.SetValue(manager, Guid.NewGuid().ToString("N"));

                SetRandomPlaymat.Invoke(__instance, new object[] { boardState });
            }
            catch (Exception ex)
            {
                Plugin.Warn("local playmat randomizer failed; using scenario playmat: " + ex.Message);
                return true;
            }
            finally
            {
                if (MatchIdField != null && manager != null)
                    MatchIdField.SetValue(manager, oldMatchId);
            }

            Plugin.Log("local playmat randomized by retail selector; selected="
                + (boardState == null ? "unknown" : boardState.PlaymatState.Type.ToString())
                + " scenario="
                + (scenario == null ? "<null>" : scenario.ResourceId.UniqueName));
            return false;
        }
    }
}
