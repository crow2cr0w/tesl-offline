using System;
using System.Reflection;
using HarmonyLib;
using SparkleServices;

namespace NyxTESLOffline.Patches
{
    /*
     * Retail matchmaking is a service boundary, not game logic.  Offline mode
     * turns every locally requested match into an embedded Player-vs-AI match.
     * The callback still receives the same successful ClientMatchRequestResult
     * expected by PlayWin, but no reserve/add/refresh HTTP loop is started.
     */
    [HarmonyPatch(typeof(Matching), "MatchRequestAdd")]
    internal static class LocalMatchmakingAddPatch
    {
        private static readonly FieldInfo CurrentRequestIdField =
            AccessTools.Field(typeof(Matching), "_currMatchRequestId");
        private static readonly FieldInfo CurrentPoolField =
            AccessTools.Field(typeof(Matching), "_currMatchPool");
        private static readonly FieldInfo RefreshPeriodField =
            AccessTools.Field(typeof(Matching), "_currMatchRefreshPeriodSeconds");
        private static readonly FieldInfo EstimatedWaitField =
            AccessTools.Field(typeof(Matching), "_estimatedWaitSeconds");
        private static readonly FieldInfo MatchReadyField =
            AccessTools.Field(typeof(Matching), "_matchReady");

        private static bool Prefix(
            Matching __instance,
            long deckId,
            long eventId,
            Matching.IMatchType matchType,
            Matching.IMatchMode matchMode,
            Matching.IAiDifficulty aiDifficulty,
            Matching.ICampaignMatchDesc campaignData,
            string region,
            int matchSeed,
            int arenaMatchOrderIndex,
            string friendBuid,
            bool useChallengeTurnTimers,
            Action<Matching.ClientMatchRequestResult> callback)
        {
            string requestId = "nyx-local-" + DateTime.UtcNow.Ticks.ToString();

            Set(CurrentRequestIdField, __instance, requestId);
            Set(CurrentPoolField, __instance, "nyx-local-ai");
            Set(RefreshPeriodField, __instance, 0);
            Set(EstimatedWaitField, __instance, 0);
            Set(MatchReadyField, __instance, null);

            OfflineMatchRequest request = new OfflineMatchRequest();
            request.DeckId = deckId;
            request.EventId = eventId;
            request.MatchType = matchType;
            request.MatchMode = matchMode;
            request.AiDifficulty = aiDifficulty;
            request.CampaignData = campaignData;
            request.Region = region;
            request.MatchSeed = matchSeed;
            request.ArenaMatchOrderIndex = arenaMatchOrderIndex;
            request.FriendBuid = friendBuid;
            request.UseChallengeTurnTimers = useChallengeTurnTimers;
            request.RequestId = requestId;
            request.QueuedFrame = UnityEngine.Time.frameCount;

            Plugin.QueueLocalMatch(request);

            if (!Object.ReferenceEquals(callback, null))
            {
                Matching.ClientMatchRequestResult result =
                    new Matching.ClientMatchRequestResult();
                result.resultCode = ResultCode.Ok;
                result.requestId = requestId;
                result.poolName = "nyx-local-ai";
                result.estimatedWaitSeconds = 0;
                result.refreshPeriodSeconds = 0;
                callback.Invoke(result);
            }

            Plugin.Log("matchmaking bypass: request=" + requestId
                + " mode=" + matchMode
                + " type=" + matchType
                + " ai=" + aiDifficulty
                + " campaign=" + CampaignName(campaignData));

            return false;
        }

        private static string CampaignName(Matching.ICampaignMatchDesc data)
        {
            if (Object.ReferenceEquals(data, null))
                return "-";
            if (String.IsNullOrEmpty(data.scenarioName))
                return "-";
            return data.scenarioName;
        }

        private static void Set(FieldInfo field, object instance, object value)
        {
            if (!Object.ReferenceEquals(field, null))
                field.SetValue(instance, value);
        }
    }


    [HarmonyPatch(typeof(Matching), "Update")]
    internal static class LocalMatchmakingUpdatePatch
    {
        private static bool Prefix()
        {
            /* A local match never participates in the retail reserve/add/refresh
             * polling loop.  Keep CurrentMatchRequestId populated for the UI,
             * but suppress the service poll until the embedded match is gone. */
            return !Plugin.HasPendingLocalMatch &&
                System.Object.ReferenceEquals(Plugin.ActiveLocalMatch, null);
        }
    }

    [HarmonyPatch(typeof(Matching), "MatchRequestCancel")]
    internal static class LocalMatchmakingCancelPatch
    {
        private static bool Prefix(Matching __instance, string friendBuid)
        {
            if (!Plugin.HasPendingLocalMatch &&
                Object.ReferenceEquals(Plugin.ActiveLocalMatch, null))
                return true;

            if (Plugin.HasPendingLocalMatch)
            {
                Plugin.CancelPendingLocalMatch();
                Plugin.Log("pending local match cancelled");
            }
            return false;
        }
    }
}
