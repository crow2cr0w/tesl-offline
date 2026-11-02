using HarmonyLib;
using SPTESL;

namespace NyxTESLOffline.Patches
{
    /* The retail embedded-match helper asks DNS for a host-interface address.
     * Offline preservation never needs a LAN interface: force the embedded
     * game client/server hop to loopback as well. */
    [HarmonyPatch(typeof(GrpcUtilities), "GetLocalIPAddress")]
    internal static class LoopbackGameServerPatch
    {
        private static bool Prefix(ref string __result)
        {
            __result = "127.0.0.1";
            return false;
        }
    }
}
