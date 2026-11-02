using System;
using HarmonyLib;
using SPTESL;

namespace NyxTESLOffline.Patches
{
    /*
     * The retail client already loads GameConfiguration from its binary asset
     * bundle during normal startup.  EnsureFullConfigurationLoad() is a source/
     * debug helper that reloads loose JSON from Application.dataPath +
     * /GameConfiguration.  Those source files are not part of the shipped
     * retail depot, so calling it inside StartSinglePlayerMatch() destroys the
     * valid runtime configuration and then throws DirectoryNotFoundException.
     *
     * Suppress only while a Nyx local match is being created/served.  The
     * normal retail startup path remains untouched.
     */
    [HarmonyPatch(typeof(GameConfiguration), "EnsureFullConfigurationLoad")]
    internal static class GameConfigurationRuntimePatch
    {
        private static bool _logged;

        private static bool Prefix()
        {
            if (!Plugin.HasPendingLocalMatch &&
                !Plugin.IsStartingOfflineMatch &&
                Object.ReferenceEquals(Plugin.ActiveLocalMatch, null))
                return true;

            if (!_logged)
            {
                _logged = true;
                Plugin.Log("retail GameConfiguration already loaded; skipping loose-JSON reload");
            }
            return false;
        }
    }
}
