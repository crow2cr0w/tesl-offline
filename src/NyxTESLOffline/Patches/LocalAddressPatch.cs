using HarmonyLib;
using SPTESL;

namespace NyxTESLOffline.Patches
{
    /* StartSinglePlayerMatch() calls this before creating TcpNetworkClient.
     * Pinning it to loopback prevents accidental LAN routing. */
    [HarmonyPatch(typeof(GrpcUtilities), "GetLocalIPAddress")]
    internal static class LocalAddressPatch
    {
        private static bool Prefix(ref string __result)
        {
            __result = "127.0.0.1";
            return false;
        }
    }
}
