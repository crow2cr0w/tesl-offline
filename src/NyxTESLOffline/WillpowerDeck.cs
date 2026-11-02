using System.Text;

namespace NyxTESLOffline
{
    internal static class WillpowerDeck
    {
        // New identity: the previous experimental deck (13) was rolled back.
        internal const long Id = 9000000000015L;
        internal static readonly int[] CardIds = {
            -452943738, 2052829044, 652289987, 207833797, -1395960197,
            -30507580, -37005376, -751645406, 1462928361, -1543808196,
            -587531429, 775516546, -1143757804, -646559647,
            -1410566212, 1076369070, -2104243403
        };

        internal static string BuildJson()
        {
            StringBuilder json = new StringBuilder();
            json.Append("{\"id\":").Append(Id);
            json.Append(",\"displayName\":\"Vontade — Invocação 50\",\"hasBeenSeen\":true," +
                "\"isValid\":true,\"isLocked\":false,\"renameFromUser\":false," +
                "\"artTypeHash\":0,\"deckTypeHash\":0,\"cards\":[");
            for (int i = 0; i < CardIds.Length; ++i)
            {
                if (i > 0) json.Append(',');
                json.Append("{\"typeHash\":").Append(CardIds[i]);
                json.Append(",\"numRegular\":").Append(i == CardIds.Length - 1 ? 2 : 3);
                json.Append(",\"numPremium\":0}");
            }
            return json.Append("],\"eventId\":0}").ToString();
        }
    }
}
