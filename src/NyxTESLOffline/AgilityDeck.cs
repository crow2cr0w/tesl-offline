using System.Text;
namespace NyxTESLOffline
{
    internal static class AgilityDeck
    {
        internal const long Id = 9000000000016L;
        internal static readonly int[] CardIds = {
            2044718784, -1323382381, -163473279, 1103333086, 481123016,
            40142447, -581948221, -2128238625, -545058236,
            627783150, 1405826749, 1916654634,
            -967450522, 2095683028, -622045452, -1248991397, -1086320745
        };
        internal static string BuildJson()
        {
            StringBuilder json = new StringBuilder();
            json.Append("{\"id\":").Append(Id);
            json.Append(",\"displayName\":\"Agilidade — 50 cartas retail\",\"hasBeenSeen\":true," +
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
