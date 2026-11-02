using SPTESL;
namespace NyxTESLOffline
{
    internal static class BoardPlacementMasks
    {
        internal static System.Collections.Generic.List<ServerCard> TriggerCandidates(ServerCardCatalog catalog,bool explicitCardSelector)
        {return explicitCardSelector?catalog.GatherDynamicCardList():catalog.GatherInPlayCardList();}
        // Retail FillPlayCardCommandState uses the destination zone, not always CreatureBoard.
        internal static void ForCard(ServerCardCatalog catalog,PlayerUniqueId owner,ServerCard card,
            ref BoardLaneMask legal,ref BoardLaneMask filled)
        {
            GameZoneName zone=card.FindGameZoneName(GameZoneType.Board);
            if(zone!=GameZoneName.SupportBoard)return;
            legal=catalog.GatherAllBoardLanesMask(owner,zone);
            filled=catalog.GatherFilledBoardLanesMask(owner,zone);
        }
    }
}
