using System.Text;

namespace NyxTESLOffline
{
    // 16 x 3 + 2 = 50. All identities and attributes come from retail.
    internal static class StrengthDeck
    {
        internal const long Id = 9000000000014L;
        internal static readonly int[] CardIds = {
            195169233, 373560332, 494443404, 631750256, -1871307781,
            -1879063369, -2096820926, -33546008, -424209530, 1106955994,
            1911637876, -371816192, -1951249314, 616508402, -1767873507,
            1970986215, 842097822
        };

        internal static string BuildJson()
        {
            StringBuilder json = new StringBuilder();
            json.Append("{\"id\":").Append(Id);
            json.Append(",\"displayName\":\"Força — 50 cartas retail\",\"hasBeenSeen\":true," +
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
