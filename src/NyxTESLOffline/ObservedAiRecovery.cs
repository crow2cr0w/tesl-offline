using System;
using System.Collections.Generic;
using System.Linq;
using SPTESL;
namespace NyxTESLOffline
{
    internal static class ObservedAiRecovery
    {
        internal static readonly int[] CardIds={-805663268,-296994425,-642921813,-474818882,723253705,343268120};
        internal static void Apply(GameConfiguration config) {foreach(int id in CardIds){var card=config.FindCardById(id);if(card!=null && Install(card))Plugin.Log("observed AI recovery: "+id);}}
        internal static bool Install(Card card)
        {
            int id=card.ResourceId.Id;
            if(Array.IndexOf(CardIds,id)<0 || card.TriggeredEffects.Count!=0 || card.ActionPlayInformation.Count!=0 || card.Auras.Count!=0 || card.ActivatedAbility.IsValidAbility())return false;
            var effect=new Effect();var group=new EffectGroup();group.Effects.Add(effect);
            if(id==343268120) {
                var buff=new CardEffectAction {HashId=-1,Action=new CardActions.BuffAttributeToValue {AttributeName=AttributeType.TotalUsesAllowed,ModificationType=EnchantmentAttributeModificationType.Add,Value=new ActionValueData(1)}};
                buff.Selectors.Add(new OwnActivatedSupport());effect.CardActions.Add(buff);
                var aura=new Aura();var enchant=new AuraEnchantment();enchant.Selectors.Add(new OwnActivatedSupport());
                enchant.Enchantment.AttributeModifications.Add(new EnchantmentAttributeModification(EnchantmentAttributeModificationType.Add,AttributeType.AbilityUsesPerTurn,1));
                aura.EnchantmentsToApply.Add(enchant);card.Auras.Add(aura);
            }else if(id==-474818882) {
                AddToken(effect,939372786,GameZoneType.Hand,null,null);
            }else if(id==723253705) {
                var action=new CardEffectAction {HashId=-1,Action=new CardActions.Damage {
                    HealthLoss=new ActionValueData(2),DamageSource=CardRole.Self }};
                action.Selectors.Add(new CardConditionals.CardRoleType {Role=CardRole.DecisionTarget,Check=CardConditionals.CardRoleType.Requirement.Is});
                // Retail patch history broadened the Summon target from an
                // enemy creature to any living creature. Keep the old external
                // catalogue wording out of the legal-target predicate.
                action.Selectors.Add(new LivingCreature());effect.CardActions.Add(action);
            }else if(id==-296994425) {
                var action=new PlayerEffectAction {HashId=-1,Action=new PlayerActions.DrawCards {NumCards=new ActionValueData(1)}};
                action.Selectors.Add(new PlayerConditionals.PlayerRoleType {Role=PlayerRole.Self,Check=PlayerConditionals.PlayerRoleType.Requirement.Is});effect.PlayerActions.Add(action);
            }else if(id==-805663268) {
                AddToken(effect,-2130654737,GameZoneType.Hand,null,CardSubtype.Strength);
                AddToken(effect,-1858021931,GameZoneType.Hand,null,CardSubtype.Intelligence);
            }else{
                // Summon only. The separate full-lanes victory ability remains pending.
                AddToken(effect,-1539142932,GameZoneType.Board,LaneIndexEnum.Left,null);
                AddToken(effect,1218903077,GameZoneType.Board,LaneIndexEnum.Right,null);
            }
            var summon=new TriggeredEffect {TriggerType=id==-296994425?EffectTriggerType.Slay:EffectTriggerType.Summon,
                SortOrder=TriggerSortOrder.EndOfActiveQueue,ClientTargetingType=id==723253705?PlayTargetingType.RequiredTarget:PlayTargetingType.NoTarget,EffectGroup=group};
            if(id==723253705) {
                var target=new DecisionConditionalExpression {DecisionType=DecisionType.Card};target.CardConditionalExpressions.Add(new LivingCreature());summon.TargetSelectors.Add(target);
            }
            card.TriggeredEffects.Add(summon);
            if(id==-642921813) {
                var victory=new Effect();
                var win=new PlayerEffectAction {HashId=-1,Action=new PlayerActions.WinGame()};
                win.Selectors.Add(new OwnerWithColor());victory.PlayerActions.Add(win);
                var victoryGroup=new EffectGroup();victoryGroup.Effects.Add(victory);
                var start=new TriggeredEffect {TriggerType=EffectTriggerType.WhenPlayerTurnStarts,
                    SortOrder=TriggerSortOrder.EndOfActiveQueue,ClientTargetingType=PlayTargetingType.NoTarget,
                    EffectGroup=victoryGroup};
                start.TriggerCardRequirements.Add(new FullLanesAtOwnTurn {LhsCard=CardRole.Self});
                card.TriggeredEffects.Add(start);
            }
            if(id==723253705) {
                var slayEffect=new Effect();var slayGroup=new EffectGroup();slayGroup.Effects.Add(slayEffect);
                var token=new PlayerEffectAction {HashId=-1,Action=new PlayerActions.CreateToken {CardReference=new EditorCardReference(-1918265206),CreateInZoneType=GameZoneType.Board,NumTokens=new ActionValueData(1),InsertPosition=new InsertPosition {Type=InsertPosition.LaneChoiceType.Relative,Lhs=CardRole.Self,Comparison=InsertPosition.PositionComparison.SameLane,InsertionType=CardInsertionType.Bottom}}};
                token.Selectors.Add(new OwnerWithColor());slayEffect.PlayerActions.Add(token);
                var slay=new TriggeredEffect {TriggerType=EffectTriggerType.Slay,SortOrder=TriggerSortOrder.EndOfActiveQueue,ClientTargetingType=PlayTargetingType.NoTarget,EffectGroup=slayGroup};
                slay.TriggerCardRequirements.Add(new CardRequirements.EventRoleRequirement {LhsCard=CardRole.Self,Conditional=new CardConditionals.CardRoleType {Role=CardRole.Inciter,Check=CardConditionals.CardRoleType.Requirement.Is}});
                card.TriggeredEffects.Add(slay);
            }
            return true;
        }
        sealed class LivingCreature:ICardConditionalExpression
        {
            public bool Evaluate(ServerCard card,CardConditionalArguments cards,PlayerConditionalArguments players,TriggerConditionalArguments trigger)
            {return card!=null && card.CurrentGameZoneName==GameZoneName.CreatureBoard && !card.IsDying();}
        }
        sealed class FullLanesAtOwnTurn:GenericCardRequirement<FullLanesCondition>{}
        sealed class FullLanesCondition:ICardConditionalExpression
        {
            public bool Evaluate(ServerCard card,CardConditionalArguments cards,PlayerConditionalArguments players,TriggerConditionalArguments trigger)
            {
                return card!=null && card==cards.SelfCard && !card.Silenced && !card.IsDying() &&
                    card.CurrentGameZoneName==GameZoneName.CreatureBoard &&
                    players.InciterPlayerState!=null && players.InciterPlayerState.NetId==card.OwnerNetId &&
                    cards.CardCatalog.FindLaneCount(GameZoneName.CreatureBoard,card.OwnerNetId)==2 &&
                    cards.CardCatalog.IsLaneFull(GameZoneName.CreatureBoard,0,card.OwnerNetId) &&
                    cards.CardCatalog.IsLaneFull(GameZoneName.CreatureBoard,1,card.OwnerNetId);
            }
        }
        sealed class OwnActivatedSupport:ICardConditionalExpression
        {
            public bool Evaluate(ServerCard card,CardConditionalArguments cards,PlayerConditionalArguments players,TriggerConditionalArguments trigger)
            {return card!=null && cards.SelfCard!=null && card.OwnerNetId==cards.SelfCard.OwnerNetId && card.CurrentGameZoneName==GameZoneName.SupportBoard && card.HasPotentialActivatedAbility();}
        }
        internal static void RefreshSupportAuras(ServerEffectUtilities.TriggerGameState state)
        {
            var cards=state.CardCatalog.GatherDynamicCardList();
            var sources=cards.FindAll(c=>VariantCardRules.CanonicalId(c.CardId)==343268120 && c.CurrentGameZoneName==GameZoneName.CreatureBoard && !c.IsDying() && !c.Silenced && c.Auras.Count>0);
            foreach(var target in cards) {
                bool eligible=target.CurrentGameZoneName==GameZoneName.SupportBoard && target.HasPotentialActivatedAbility();
                bool changed=false;
                for(int i=target.AuraEnchantments.Count-1;i>=0;i--) {
                    var aura=target.AuraEnchantments[i];if(VariantCardRules.CanonicalId(aura.SourceCardId)!= 343268120)continue;
                    if(eligible && target.Enchantments.Contains(aura) && sources.Exists(s=>s.InstanceId==aura.SourceId && s.OwnerNetId==target.OwnerNetId))continue;
                    target.AuraEnchantments.RemoveAt(i);target.Enchantments.Remove(aura);changed=true;
                }
                if(eligible)foreach(var source in sources) {
                    if(source.OwnerNetId!=target.OwnerNetId || target.AuraEnchantments.Exists(a=>VariantCardRules.CanonicalId(a.SourceCardId)==343268120 && a.SourceId==source.InstanceId))continue;
                    var added=new EnchantmentInstance(source.Auras[0].EnchantmentsToApply[0].Enchantment,1,source.CardId,source.InstanceId,target);
                    target.AuraEnchantments.Add(added);target.Enchantments.Add(added);changed=true;
                }
                if(changed) {
                    if(state.Log!=null)state.Log.StartEvent(CcgRpc.HistoryType.EventTrigger,target.OwnerNetId,target.NetUid,target.NetUid,-1);
                    try {
                    target.RecalculateEnchantmentsWithTriggers(state,null);
                    if(state.Log!=null) {
                        var item=new CcgRpc.HistoryItem {Type=CcgRpc.HistoryType.ActionFullCardRefresh,AssetHash=-1};
                        item.AffectedCardsArray.Add(target.HistoryCreate_FullStateCard(state.CardCatalog));state.Log.AddInstant(item);
                    }
                    }finally {if(state.Log!=null)state.Log.EndEvent();}
                }
            }
        }
        static void AddToken(Effect effect,int id,GameZoneType zone,LaneIndexEnum? lane,CardSubtype? color)
        {
            var action=new PlayerEffectAction {HashId=-1,Action=new PlayerActions.CreateToken {
                CardReference=new EditorCardReference(id),CreateInZoneType=zone,NumTokens=new ActionValueData(1),
                InsertPosition=new InsertPosition {Type=InsertPosition.LaneChoiceType.Constant,Lane=lane ?? LaneIndexEnum.Left,InsertionType=CardInsertionType.Bottom}}};
            action.Selectors.Add(new OwnerWithColor {Color=color});effect.PlayerActions.Add(action);
        }
        sealed class OwnerWithColor:IPlayerConditionalExpression
        {
            internal CardSubtype? Color;
            public bool Evaluate(PlayerState player,PlayerConditionalArguments players,CardConditionalArguments cards,TriggerConditionalArguments trigger,PlayerConditionalLog log)
            {
                bool ownerMatch=player!=null && cards.SelfCard!=null && player.NetId==cards.SelfCard.OwnerNetId;
                if(!ownerMatch)return false;
                return !Color.HasValue || cards.CardCatalog.GatherCardsInZone(c=>c.HasSubtype(Color.Value) && !c.IsDying(),player.NetId,GameZoneName.CreatureBoard).Count>0 ||
                    cards.CardCatalog.GatherCardsInZone(c=>c.HasSubtype(Color.Value),player.NetId,GameZoneName.SupportBoard).Count>0 ||
                    cards.CardCatalog.GatherCardsInZone(c=>c.HasSubtype(Color.Value),player.NetId,GameZoneName.ItemBoard).Count>0;
            }
            public void ReportResult(PlayerConditionalLog log,bool result){}
        }
    }
}
