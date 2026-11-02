namespace NyxTESLOffline
{
    internal static class CompetitiveKhajiitDecks
    {
        internal const long Deck1Id = 9000000000040L;
        internal const string Deck1Name = "Khajiit — Khamira Competitivo";
        internal const string Deck1SourceCode = "SPADwWwpeDAEwcfiwdwUANnMebwXmDlYwVwRwovMfhwHdhim";
        internal static readonly CardEntry[] Deck1Cards = { new CardEntry(-2108351942,1),new CardEntry(-2022683313,1),new CardEntry(-1653188426,1),new CardEntry(-1396315262,2),new CardEntry(-9440141,2),new CardEntry(267621894,2),new CardEntry(1547461038,2),new CardEntry(-1661653182,3),new CardEntry(-1572966262,3),new CardEntry(-971628364,3),new CardEntry(-756429718,3),new CardEntry(-751645406,3),new CardEntry(601116895,3),new CardEntry(1244028749,3),new CardEntry(1287927542,3),new CardEntry(1309242071,3),new CardEntry(1597455647,3),new CardEntry(1937298575,3),new CardEntry(481123016,3),new CardEntry(-1410566212,3) };
        internal const long Deck2Id = 9000000000041L;
        internal const string Deck2Name = "Khajiit — Pilfer Monk OG";
        internal const string Deck2SourceCode = "SPABrgACqjoQAPermDebmzlYjPqkknfxfWcxnMfifhgs";
        internal static readonly CardEntry[] Deck2Cards = { new CardEntry(-1834255118,1),new CardEntry(-1750689038,2),new CardEntry(-2048305816,2),new CardEntry(83714140,3),new CardEntry(-756429718,3),new CardEntry(-1572966262,3),new CardEntry(-6629593,3),new CardEntry(-751645406,3),new CardEntry(207833797,3),new CardEntry(-2128238625,3),new CardEntry(1046549666,3),new CardEntry(-212193127,3),new CardEntry(-37005376,3),new CardEntry(-835052994,3),new CardEntry(-1661653182,3),new CardEntry(-9440141,3),new CardEntry(1597455647,3),new CardEntry(-163473279,3) };
        internal const long Deck3Id = 9000000000042L;
        internal const string Deck3Name = "Khajiit — Pilfer Aldmeri 2024";
        internal const string Deck3SourceCode = "SPALsxajczaAynqcdSbqabefwjAOxSbxrCdroEydwCdyumsHwuoecupVAMBmsUsyknqAhnlYvKuAsAswoy";
        internal static readonly CardEntry[] Deck3Cards = { new CardEntry(1152951782,1),new CardEntry(-462299413,1),new CardEntry(578476645,1),new CardEntry(755762199,1),new CardEntry(573251342,1),new CardEntry(2115746825,1),new CardEntry(50577249,1),new CardEntry(536540544,1),new CardEntry(-781718501,1),new CardEntry(1828055257,1),new CardEntry(-2138210071,1),new CardEntry(-1676793170,2),new CardEntry(-849519621,2),new CardEntry(-987994366,2),new CardEntry(652289987,2),new CardEntry(85837010,2),new CardEntry(-1052005339,2),new CardEntry(351483326,2),new CardEntry(-1375017618,2),new CardEntry(1638037568,2),new CardEntry(-1399785843,2),new CardEntry(-212283771,2),new CardEntry(-710854512,2),new CardEntry(-587717065,2),new CardEntry(2074906612,2),new CardEntry(-579219134,3),new CardEntry(-286267637,3),new CardEntry(-1272845130,3),new CardEntry(1046549666,3),new CardEntry(1659589269,3),new CardEntry(-2104243403,3),new CardEntry(-751645406,3),new CardEntry(-1640102389,3),new CardEntry(916842555,3),new CardEntry(-710221525,3),new CardEntry(1736426356,3),new CardEntry(-1670772407,3) };
        internal sealed class CardEntry { internal readonly int Id; internal readonly int Count; internal CardEntry(int id,int count){Id=id;Count=count;} }
        internal static string BuildJson(long id,string name,CardEntry[] cards)
        {
            var json=new System.Text.StringBuilder(); json.Append("{\"id\":").Append(id);
            json.Append(",\"displayName\":\"").Append(name).Append("\",\"hasBeenSeen\":true,\"isValid\":true,\"isLocked\":false,\"renameFromUser\":false,\"artTypeHash\":0,\"deckTypeHash\":0,\"cards\":[");
            for(int i=0;i<cards.Length;i++){if(i>0)json.Append(',');json.Append("{\"typeHash\":").Append(cards[i].Id).Append(",\"numRegular\":").Append(cards[i].Count).Append(",\"numPremium\":0}");}
            return json.Append("],\"eventId\":0}").ToString();
        }
    }
}
