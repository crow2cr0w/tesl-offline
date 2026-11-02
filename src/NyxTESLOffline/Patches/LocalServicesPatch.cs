using System;
using System.Net;
using System.Reflection;
using HarmonyLib;

namespace NyxTESLOffline.Patches
{
    /* Packages.OnResponse waits for Shop.Refresh's completion callback before
     * the retail loader can leave its packages stage.  Keep that flow active,
     * but supply the empty catalog that Shop.OnResponse otherwise discards. */
    [HarmonyPatch(typeof(SparkleServices.Shop), "OnResponse")]
    internal static class OfflineShopResponsePatch
    {
        private static readonly FieldInfo CatalogField =
            AccessTools.Field(typeof(SparkleServices.Shop), "<ChannelCatalog>k__BackingField");

        private static void Prefix(SparkleServices.Shop __instance,
            SparkleServices.Shop.FetchCatalogResponse response)
        {
            if (__instance.ChannelCatalog == null && response != null &&
                response.catalog != null)
                CatalogField.SetValue(__instance, response.catalog);
        }
    }

    [HarmonyPatch(typeof(SparkleServices.Presence), "Update")]
    internal static class OfflinePresenceUpdatePatch
    {
        private static bool Prefix() { return false; }
    }

    [HarmonyPatch(typeof(SparkleServices.Presence), "PostPresence")]
    internal static class OfflinePresencePostPatch
    {
        private static bool Prefix() { return false; }
    }

    /*
     * The abandoned retail client selects BNETSteam authentication before it
     * reaches the loopback account endpoint.  Without a running Steam client,
     * BNETPlatformAuthInstance returns SteamNotAvailable and the normal local
     * service initialization never begins.
     *
     * Replace only the top-level authentication entry point.  Its original
     * callback is still ServicesManager.HandleAuthResult, so SetAuthData and
     * every downstream service retain the stock initialization path.
     */
    [HarmonyPatch]
    internal static class BnetAuthStartProcessPatch
    {
        private static MethodBase TargetMethod()
        {
            Type type = AccessTools.TypeByName("SparkleServices.BNETAuth");
            Type request = AccessTools.TypeByName("SparkleServices.BNETAuthRequest");
            Type authData = AccessTools.TypeByName("SparkleServices.AuthData");
            if (Object.ReferenceEquals(type, null) ||
                Object.ReferenceEquals(request, null) ||
                Object.ReferenceEquals(authData, null))
                throw new MissingMemberException("SparkleServices offline auth types");

            Type callback = typeof(Action<>).MakeGenericType(authData);
            MethodInfo method = AccessTools.Method(
                type,
                "StartProcess",
                new Type[] { request, callback });
            if (Object.ReferenceEquals(method, null))
                throw new MissingMethodException(type.FullName, "StartProcess(BNETAuthRequest, Action<AuthData>)");

            return method;
        }

        private static bool Prefix(Action<SparkleServices.AuthData> __1)
        {
            SparkleServices.AuthData authData = new SparkleServices.AuthData();
            authData.token = "offline-token";
            authData.channel = "prod-live";
            authData.sessionId = "offline-session";
            authData.sessionType = "offline";
            authData.sessionToken = "offline-session-token";
            authData.buid = "offline-local-user";
            authData.steamId = "0";
            authData.kpiId = "offline";
            authData.deviceGrade = "PC";
            authData.nickname = "Offline";
            authData.bnetCountry = "BR";
            authData.isLimitedAccount = false;
            authData.isAnonymousAccount = false;
            authData.timeToRefresh = 3600000;
            authData.maxRefreshFailures = 3;
            authData.secondsBetweenRefreshFailures = 10.0f;
            authData.allowTP = false;
            authData.unityBundleUrl = String.Empty;
            authData.unityBundleHash = String.Empty;
            authData.buildSetId = 0;
            authData.wasNativeSignOn = false;

            Plugin.Log("offline BNET auth accepted for " + authData.buid);
            __1(authData);
            return false;
        }
    }

    /*
     * Force both FetchConfig candidates to the same loopback endpoint.  The
     * retail implementation first tries localConfigUrl and then configUrl; if
     * only one is patched, a failed local request can still escape to Bethesda.
     */
    [HarmonyPatch]
    internal static class FetchConfigStartProcessPatch
    {
        private static MethodBase TargetMethod()
        {
            Type type = AccessTools.TypeByName("SparkleServices.FetchConfig");
            if (Object.ReferenceEquals(type, null))
                throw new MissingMemberException("SparkleServices.FetchConfig");

            MethodInfo method = AccessTools.Method(type, "StartProcess");
            if (Object.ReferenceEquals(method, null))
                throw new MissingMethodException(type.FullName, "StartProcess");

            return method;
        }

        private static void Prefix(ref string __0, ref string __1, ref bool __2)
        {
            __0 = LocalControlPlane.ConfigUrl;
            __1 = LocalControlPlane.ConfigUrl;
            __2 = true;
            Plugin.Log("FetchConfig pinned to " + LocalControlPlane.ConfigUrl);
        }
    }

    /* PubSub push delivery has no offline function. ServicesManager.SetAuthData
     * still initializes the component, so PubSub.IsReady() succeeds, but we do
     * not let its Update() create a Socket.IO retry loop. */
    [HarmonyPatch]
    internal static class PubSubUpdatePatch
    {
        private static MethodBase TargetMethod()
        {
            Type type = AccessTools.TypeByName("SparkleServices.PubSub");
            if (Object.ReferenceEquals(type, null))
                throw new MissingMemberException("SparkleServices.PubSub");

            MethodInfo method = AccessTools.Method(type, "Update");
            if (Object.ReferenceEquals(method, null))
                throw new MissingMethodException(type.FullName, "Update");

            return method;
        }

        private static bool Prefix()
        {
            return false;
        }
    }

    /*
     * Fail closed for the game's managed HTTP stack.  The local control plane
     * is the only destination accepted.  This also blocks the hard-coded news,
     * privacy and legacy service URLs that are outside ServicesManager's config.
     */
    [HarmonyPatch]
    internal static class BestHttpLoopbackGuardPatch
    {
        private static MethodBase TargetMethod()
        {
            Type manager = AccessTools.TypeByName("BestHTTP.HTTPManager");
            Type request = AccessTools.TypeByName("BestHTTP.HTTPRequest");
            if (Object.ReferenceEquals(manager, null) || Object.ReferenceEquals(request, null))
                throw new MissingMemberException("BestHTTP HTTP types");

            MethodInfo method = AccessTools.Method(
                manager,
                "SendRequest",
                new Type[] { request });

            if (Object.ReferenceEquals(method, null))
                throw new MissingMethodException(manager.FullName, "SendRequest(HTTPRequest)");

            return method;
        }

        private static bool Prefix(BestHTTP.HTTPRequest request, ref BestHTTP.HTTPRequest __result)
        {
            if (request == null)
                return true;

            Uri uri = request.Uri;
            if (uri == null)
                return true;

            if (IsLoopback(uri))
            {
                Plugin.Log("allowed loopback HTTP: " + uri.Scheme + "://"
                    + uri.Host + ":" + uri.Port + uri.AbsolutePath);
                return true;
            }

            Plugin.Warn("blocked non-loopback HTTP request: " + uri.Scheme
                + "://" + uri.Host + ":" + uri.Port + uri.AbsolutePath);

            try { request.Abort(); }
            catch (Exception ex) { Plugin.Warn("HTTP abort failed: " + ex.Message); }

            __result = request;
            return false;
        }

        private static bool IsLoopback(Uri uri)
        {
            if (uri.IsLoopback)
                return true;

            IPAddress address;
            if (IPAddress.TryParse(uri.Host, out address))
                return IPAddress.IsLoopback(address);

            return String.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
        }
    }
}
