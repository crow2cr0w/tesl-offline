using System;
using SPTESL;
namespace NyxTESLOffline
{
    // Ten-card presentation batch only. Resolve original relations, never synthesize FX.
    internal static class SharedFxBatch01
    {
        internal static readonly int[] CardIds = { -1686480098,1646495852,-241860300,-2074393999,
            1287011430,-389657211,1399500856,782334488,-1308684010,-1235178647 };
        internal static bool Contains(int id) { return Array.IndexOf(CardIds,id) >= 0; }
        internal static int Hash(ServerCard source)
        {
            if (source.CardId == -1686480098) {
                var original = GameManager.Instance.Config.FindCardById(source.CardId);
                var fx = original.CardFxRelations.Find(r=>r.FXAction == CardFxAction.TriggeredEffect && r.EffectTriggerType == EffectTriggerType.ActionEffect);
                if (fx != null) return fx.AssetHash;
            }
            return source.FindOverrideOrDefaultAssetHash(CardFxAction.Buff);
        }
        internal static void Execute(CardActionArguments args,Enchantment enchantment)
        {
            new CardActions.SetOnBoardStats { Enchantment = enchantment,SimultaneousFx = args.ValidTargetList.Count > 1 }
                .Execute(new CardActionArguments(args.TriggerGameState,args.ValidTargetList,args.CardArguments,
                    args.PlayerArguments,args.TriggerArguments,Hash(args.CardArguments.SelfCard),args.UsingDefaultFx));
        }
    }
}
