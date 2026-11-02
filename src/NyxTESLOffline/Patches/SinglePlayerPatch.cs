using HarmonyLib;
using SPTESL;

namespace NyxTESLOffline.Patches
{
    [HarmonyPatch(typeof(GameNetworkManager), "StartSinglePlayerMatch")]
    internal static class StartSinglePlayerMatchPatch
    {
        private static void Prefix(bool skipIntro, bool disableAi, bool showAll)
        {
            Plugin.Log("StartSinglePlayerMatch prefix: skipIntro=" + skipIntro
                + " disableAi=" + disableAi + " showAll=" + showAll);
        }

        private static void Postfix(GameNetworkManager __instance)
        {
            MatchConfiguration.StartInfo info = __instance._matchStartInfo;
            if (info == null)
            {
                Plugin.Warn("StartSinglePlayerMatch produced null StartInfo");
                return;
            }

            Plugin.Log("StartInfo: scenario=" + info.ScenarioId
                + " mode=" + info.MatchMode
                + " type=" + info.MatchType
                + " users=" + (info.Users == null ? 0 : info.Users.Length));
        }
    }
}
