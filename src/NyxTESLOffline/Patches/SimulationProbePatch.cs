using System;
using HarmonyLib;
using SPTESL;

namespace NyxTESLOffline.Patches
{
    /* Replace the stripped retail authoritative entry points with the local
     * Simulation host.  The original TCP, protobuf, state and card catalog
     * classes remain in use. */
    [HarmonyPatch(typeof(Simulation), "Start")]
    internal static class SimulationStartPatch
    {
        private static bool Prefix(
            Simulation __instance,
            TcpNetworkServer tcpServer,
            MatchConfiguration.StartInfo startInfo)
        {
            Plugin.Log("Simulation.Start replacement entered");
            try
            {
                Plugin.ApplyPreparedPremiumFlags(startInfo);
                OfflineSimulationHost.Start(__instance, tcpServer, startInfo);
            }
            catch (Exception ex)
            {
                Plugin.Warn("authoritative local Simulation.Start failed: " + ex);
                OfflineSimulationHost.Stop(__instance);
                /* Do not let StartSinglePlayerMatch continue into LoadGameBoard
                 * with a dead authoritative server.  The caller in Plugin.Update
                 * owns a try/catch and will clear the synthetic match request. */
                throw;
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(Simulation), "FixedUpdate")]
    internal static class SimulationFixedUpdatePatch
    {
        private static bool Prefix(Simulation __instance)
        {
            try
            {
                OfflineSimulationHost.Tick(__instance);
            }
            catch (Exception ex)
            {
                /* Keep the TCP session alive.  The old behavior discarded the
                 * host state on the first gameplay exception, which surfaced to
                 * the retail client only as "disconnected from your match". */
                Plugin.Warn("authoritative local Simulation.FixedUpdate failed: " + ex);
                OfflineSimulationHost.ReportOuterTickFailure(__instance, ex);
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(Simulation), "Stop")]
    internal static class SimulationStopPatch
    {
        private static void Prefix(Simulation __instance)
        {
            OfflineSimulationHost.Stop(__instance);
        }
    }
}
