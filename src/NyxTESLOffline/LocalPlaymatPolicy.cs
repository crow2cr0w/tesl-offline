namespace NyxTESLOffline
{
    internal static class LocalPlaymatPolicy
    {
        /* Local matches use a single-player scenario even for retail's
         * VersusBattle/Multi request.  Only campaign scenes retain the
         * scenario's fixed PlaymatType. */
        internal static bool ShouldRandomize(
            int matchType,
            int matchMode,
            bool isCampaign)
        {
            if (isCampaign)
                return false;

            return true;
        }
    }
}
