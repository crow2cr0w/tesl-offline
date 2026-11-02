using System.Text;
namespace NyxTESLOffline
{
    // Offline experiment, not a legal constructed deck. Never relax retail validation.
    internal static class SharedFamilyDeck100
    {
        internal const long Id = 9000000000018L;
        // 29 collectible family identities; Skeever and the internal Infestation variant excluded.
        internal static readonly int[] FamilyIds = {
            1470504272, -553062902, 1978118591, -1237441152,
            -1399661641, -1563068710, -871299414, 1646495852, -241860300,
            -2074393999, 1287011430, -389657211, -1787424156, 1399500856,
            782334488, -1308684010, -1686480098, -1244835424, -1588632902,
            -1235178647, -266284141, 723060903, -1987707763, 1021793029,
            982836723, -1023199177, 251949038, -76939763, -44117704
        };
        // Random sample from the user-confirmed retail inventory, fixed for reproducibility.
        internal static readonly int[] FillerIds = {
            1106955994, 1911637876, 616508402, -1767873507, 1916654634,
            631750256, 2095683028, -2128238625, -1951249314, -33546008,
            1103333086, -545058236, 842097822
        };
        internal static int Copies(int index)
        {
            // Unique flags confirmed in the original retail configuration.
            if(index<FamilyIds.Length)return FamilyIds[index]==723060903 || FamilyIds[index]==-1588632902?1:3;
            // Four freed slots, filled with nonunique cards from the fixed sample.
            return index-FamilyIds.Length<4?2:1;
        }
        internal static string BuildJson()
        {
            var json = new StringBuilder();
            json.Append("{\"id\":").Append(Id);
            json.Append(",\"displayName\":\"Experimental — Família nova 100\",\"hasBeenSeen\":true," +
                "\"isValid\":true,\"isLocked\":false,\"renameFromUser\":false," +
                "\"artTypeHash\":0,\"deckTypeHash\":0,\"cards\":[");
            for (int i = 0; i < FamilyIds.Length + FillerIds.Length; ++i) {
                if (i > 0) json.Append(',');
                bool family = i < FamilyIds.Length;
                json.Append("{\"typeHash\":").Append(family ? FamilyIds[i] : FillerIds[i-FamilyIds.Length]);
                json.Append(",\"numRegular\":").Append(Copies(i)).Append(",\"numPremium\":0}");
            }
            return json.Append("],\"eventId\":0}").ToString();
        }
    }

    internal static class ExperimentalDeck100
    {
        internal const long Id = 9000000000017L;
        internal static readonly int[] CardIds = {
            -1209831649,
            207833797,
            1106955994,
            -987994366,
            171706845,
            -646559647,
            -1865755971,
            41830377,
            -1086320745,
            -710854512,
            1911637876,
            767299130,
            1009056840,
            831893683,
            -751645406,
            1916654634,
            627783150,
            -632269135,
            1744549196,
            -1094245595,
            1405826749,
            -2043856038,
            1076369070,
            -37005376,
            -1871307781,
            -1767873507,
            842097822,
            -1249810974,
            1750084664,
            -456767346,
            -452943738,
            2052829044,
            652289987,
            -1395960197,
            -30507580,
            1462928361,
            -1543808196,
            -587531429,
            775516546,
            -1143757804,
            -967450522,
            2095683028,
            -622045452,
            -1248991397,
            631750256,
            1970986215,
            616508402,
            -1572966262,
            988424426,
            250717238,
            -6629593,
            -1310707462,
            -819919458,
            -1414651238,
            -1018525677,
            870888167,
            -820333952,
            1732951880,
            -2023047694,
            794235553,
            -195158526,
            -1671730907,
            -1245183227,
            -1858757894,
            -171448078,
            1592621682,
            848886119,
            995238228,
            648200194,
            1479393784,
            1283752651,
            1226173086,
            335189488,
            1325078115,
            753906038,
            -867264913,
            -1139008311,
            -561910135,
            1691833255,
            -1294454614,
            1037073850,
            -295361290,
            1300162036,
            -800102561,
            1700638025,
            -926222237,
            1218903077,
            -2104243403,
            -1410566212,
            -340503958,
            -1052005339,
            1791428459,
            1153770705,
            134431766,
            2044718784,
            -1323382381,
            -163473279,
            -1865310602,
            1103333086,
            40142447
        };
        internal static string BuildJson()
        {
            var json = new StringBuilder();
            json.Append("{\"id\":").Append(Id);
            json.Append(",\"displayName\":\"Experimental — Primitivas 100\",\"hasBeenSeen\":true," +
                "\"isValid\":true,\"isLocked\":false,\"renameFromUser\":false," +
                "\"artTypeHash\":0,\"deckTypeHash\":0,\"cards\":[");
            for (int i = 0; i < CardIds.Length; ++i) {
                if (i > 0) json.Append(',');
                json.Append("{\"typeHash\":").Append(CardIds[i]).Append(",\"numRegular\":1,\"numPremium\":0}");
            }
            return json.Append("],\"eventId\":0}").ToString();
        }
    }
}
