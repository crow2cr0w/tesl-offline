using System;
using System.Collections.Generic;
using System.Linq;
using SPTESL;

namespace NyxTESLOffline
{
    // Explicit identities from the PC catalog and named PC FX definitions.
    // The installed executor supplies the FX action; existing relations win.
    internal static class RetailNamedFxRecovery
    {
        sealed class Binding
        {
            internal int Id; internal string Executor, Definition;
            internal Binding(int id,string executor,string definition)
            {Id=id;Executor=executor;Definition=definition;}
        }
        static readonly Binding[] Bindings = {
            new Binding(1916654634,"Damage","Card_ArrowInTheKnee"),
            new Binding(780292596,"ReturnToHand","Card_CastOut"),
            new Binding(294653485,"Debuff","Card_ChaurusReaper"),
            new Binding(-37005376,"Destroy","Card_Execute"),
            new Binding(-1571294504,"Damage","Card_IceStorm"),
            new Binding(878764871,"Damage","Card_IndorilArchmage"),
            new Binding(41830377,"Damage","Card_LightningBolt"),
            new Binding(1565188079,"Freeze","Card_MaceOfEncumbrance"),
            new Binding(575791787,"Freeze","Card_MaceOfEncumbrance"),
            new Binding(-542954561,"TransformCard","Card_Mummify"),
            new Binding(-751645406,"Destroy","Card_PiercingJavelin"),
            new Binding(-452943738,"Buff","Card_SunholdMedic"),
            new Binding(820258389,"Damage","Card_Trebuchet"),
            new Binding(-987994366,"Ward","Card_Wardcrafter"),
            new Binding(-1563068710,"BuffAttributeToValue","Card_Fighters_Guild_Elite"),
            new Binding(2095683028,"Debuff","Card_Bitterfish_Witch"),
            new Binding(881505215,"Damage","Card_IcyShambles"),
            new Binding(-1932999038,"Damage","Card_IcyShambles"),
            new Binding(1831263143,"Banish","Card_CastIntoTime"),
            new Binding(744779288,"Damage","Card_CradlecrushGiant"),
            new Binding(1790691369,"Buff","Card_Felldew"),
            new Binding(-89673891,"GiveKeyword","Card_ManicMutation"),
            new Binding(888500878,"Damage","Card_Tiny_Dragon"),
            new Binding(133401086,"ReturnToHand","Card_CloseCall"),
            new Binding(-1787424156,"WielderStats","Card_RingOfImaginaryMight"),
            new Binding(-1588632902,"WielderStats","Card_StendarrsHammer"),
            new Binding(183499429,"Damage","Card_BlastBones"),
            new Binding(-456767346,"Freeze","Card_IcewingDragon"),
            new Binding(-602835722,"Damage","Card_MudcrabAnklesnapper")
        };
        internal static void Apply(GameConfiguration config)
        {
            int restored=0;
            foreach(var binding in Bindings) {
                var card=config.FindCardById(binding.Id);
                if(card==null)continue;
                var definition=config.CardFxDefinitions.Find(d=>d.Name==binding.Definition);
                if(definition==null || String.IsNullOrEmpty(definition.MainParticleSystem))continue;
                var hashes=config.CardFxDefinitionLookups.Where(p=>p.Value==binding.Definition).ToList();
                if(hashes.Count!=1)continue;
                var groups=card.TriggeredEffects.Select(t=>t.EffectGroup)
                    .Concat(card.ActionPlayInformation.Select(p=>p.EffectGroup))
                    .Concat(card.ActivatedAbility.AbilityGroups.Select(g=>g.EffectGroup));
                var entries=groups.Where(g=>g!=null).SelectMany(g=>g.Effects)
                    .SelectMany(e=>e.CardActions).Where(a=>a.Action!=null &&
                        (a.Action.GetType().Name==binding.Executor ||
                         binding.Executor=="Ward" && a.Action is WardWithPresentation)).ToList();
                if(entries.Count==0)continue;
                var action=entries[0].Action;
                // Destroy.GetFxActionType returns CountOf. Its native executor
                // independently resolves PreDestroy, which is the emitted FX.
                var fxAction=action is CardActions.Destroy?CardFxAction.PreDestroy:action.GetFxActionType();
                if(binding.Executor=="Ward")foreach(var entry in entries)
                    if(entry.Action is CardActions.Ward)entry.Action=new WardWithPresentation();
                if(card.CardFxRelations.Exists(r=>r.FXAction==fxAction))continue;
                card.CardFxRelations.Add(new CardFxRelationData {
                    FXAction=fxAction,EffectTriggerType=EffectTriggerType.Invalid,
                    AssetHash=hashes[0].Key,CardFxDefinition=binding.Definition
                });
                restored++;
                Plugin.Log("retail named FX restored: card="+binding.Id+" action="+fxAction+
                    " definition="+binding.Definition+" hash="+hashes[0].Key);
            }
            Plugin.Log("retail named FX associations restored="+restored+"; client playback pending");
        }
        sealed class WardWithPresentation : ICardAction
        {
            public CardFxAction GetFxActionType(){return CardFxAction.Ward;}
            public void Execute(CardActionArguments args)
            {
                // Retail Ward ignores the supplied asset hash and only logs a
                // state refresh. Keep its gameplay, then log the native Buff
                // presentation with the exact post-Ward state and named asset.
                new CardActions.Ward().Execute(args);
                var source=args.CardArguments.SelfCard;
                if(source==null || args.AssetHash==-1)return;
                var owner=args.TriggerGameState.Players.Find(p=>p.NetId==source.OwnerNetId);
                CardActions.Buff.LogToHistory(args.TriggerGameState,args.ValidTargetList,owner,
                    args.AssetHash,false,false,CcgRpc.SanitizeRule.Default);
            }
        }
    }
}
