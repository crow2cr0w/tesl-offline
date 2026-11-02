using System;
using SparkleServices;
using SPTESL;

namespace NyxTESLOffline
{
    internal sealed class OfflineMatchRequest
    {
        internal long DeckId;
        internal long EventId;
        internal Matching.IMatchType MatchType;
        internal Matching.IMatchMode MatchMode;
        internal Matching.IAiDifficulty AiDifficulty;
        internal Matching.ICampaignMatchDesc CampaignData;
        internal string Region;
        internal int MatchSeed;
        internal int ArenaMatchOrderIndex;
        internal string FriendBuid;
        internal bool UseChallengeTurnTimers;
        internal string RequestId;
        internal int QueuedFrame;
    }
}
