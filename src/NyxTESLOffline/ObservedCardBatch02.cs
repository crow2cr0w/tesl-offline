using System;
using System.Collections.Generic;
using SPTESL;
namespace NyxTESLOffline
{
    internal static class ObservedCardBatch02
    {
        internal static readonly int[] Ids={1741863602,-801177855,-355724207,536764784,1507002611,-867455886,-1709661502,16891283,1783048624,1307441161,-811885009};
        static readonly Dictionary<ServerCard,ServerCard> DyingChoices=new Dictionary<ServerCard,ServerCard>();
        internal static void Apply(GameConfiguration config){DyingChoices.Clear();foreach(int id in Ids){var c=config.FindCardById(id);if(c!=null)Install(c);}}
        internal static void CaptureDeathTarget(ServerEffectUtilities.TriggerGameState state,ServerCard card)
        {
            if(VariantCardRules.CanonicalId(card.CardId)!=-867455886 || card.Silenced || !card.IsDying() || card.CurrentGameZoneName!=GameZoneName.CreatureBoard || DyingChoices.ContainsKey(card))return;
            var linked=card.GetLinkedCards(LinkType.BerneClan,LinkedCard.LinkRole.Source);
            if(linked.Count>0){var target=state.CardCatalog.FindServerCard(linked[0]);if(target!=null)DyingChoices[card]=target;}
        }
        static TriggeredEffect Trigger(Card c,EffectTriggerType type)
        {var group=new EffectGroup();group.Effects.Add(new Effect());var t=new TriggeredEffect{TriggerType=type,SortOrder=TriggerSortOrder.EndOfActiveQueue,ClientTargetingType=PlayTargetingType.NoTarget,EffectGroup=group};c.TriggeredEffects.Add(t);return t;}
        static CardEffectAction Add(Effect e,ICardAction a,ICardConditionalExpression selector)
        {var entry=new CardEffectAction{HashId=-1,Action=a};entry.Selectors.Add(selector);e.CardActions.Add(entry);return entry;}
        static ICardConditionalExpression Self(){return new CardConditionals.CardRoleType{Role=CardRole.Self,Check=CardConditionals.CardRoleType.Requirement.Is};}
        static CardActions.Buff PowerBuff()
        {var a=new CardActions.Buff();a.Enchantment.AttributeModifications.Add(new EnchantmentAttributeModification(EnchantmentAttributeModificationType.Add,AttributeType.Attack,3));a.Enchantment.AttributeModifications.Add(new EnchantmentAttributeModification(EnchantmentAttributeModificationType.Set,AttributeType.Breakthrough,1));return a;}
        internal static bool Install(Card c)
        {
            int id=c.ResourceId.Id;if(Array.IndexOf(Ids,id)<0 || VariantCardRules.HasRules(c))return false;
            if(id==-801177855) {
                var aura=new Aura();var enchant=new AuraEnchantment();enchant.Enchantment=PowerBuff().Enchantment;
                enchant.Selectors.Add(Self());enchant.Selectors.Add(new CardConditionals.HasItemEquipped{EquipState=CardConditionals.HasItemEquipped.Requirement.HasEquipment});aura.EnchantmentsToApply.Add(enchant);c.Auras.Add(aura);return true;
            }
            if(id==1507002611) {
                var group=new EffectGroup();var e=new Effect();group.Effects.Add(e);
                var count=new ActionValueData(ActionValueMethod.ValueOfPlayerAttribute){AttributeName=AttributeType.NumRunesLost,PlayerRole=PlayerRole.Opponent};
                Add(e,new CardActions.Damage{DamageSource=CardRole.Self,HealthLoss=count},new Enemy{SelectedLane=true});
                var ability=new PlayCardAbility{PlayTargetingType=PlayTargetingType.RequiredTarget,EffectGroup=group,IsDamageOnly=true};
                var target=new DecisionConditionalExpression{DecisionType=DecisionType.Lane};target.LaneConditionalExpressions.Add(new EnemyLane());ability.TargetSelectors.Add(target);c.ActionPlayInformation.Add(ability);return true;
            }
            var t=Trigger(c,EffectTriggerType.Summon);var effect=t.EffectGroup.Effects[0];
            if(id==1741863602) {
                effect.ExecuteCardRequirements.Add(new Requirement{LhsCard=CardRole.Self,Conditional=new TopStrength()});Add(effect,PowerBuff(),Self());
            } else if(id==-355724207) {
                var count=new ActionValueData(ActionValueMethod.CountOfSelector);count.Selectors.Add(new OwnDiscardCreature());
                foreach(var attr in new[]{AttributeType.Attack,AttributeType.MaxHealth})
                    Add(effect,new CardActions.BuffAttributeToValue{AttributeName=attr,ModificationType=EnchantmentAttributeModificationType.Add,Value=count},Self());
            } else if(id==536764784 || id==-867455886) {
                t.ClientTargetingType=PlayTargetingType.RequiredTarget;var d=new DecisionConditionalExpression{DecisionType=DecisionType.Card};d.CardConditionalExpressions.Add(new Enemy());t.TargetSelectors.Add(d);
                ICardAction action;
                if(id==536764784){var fight=new CardActions.Fight();fight.InciterSelectors.Add(Self());action=fight;}
                else action=new CardActions.CreateLink{LinkType=LinkType.BerneClan,Behavior=new CardActions.CreateLink.LinkBehavior{HiddenFromOpponent=true,SourceLeavingBoardException=true,TargetStealException=true,SourceStealException=true}};
                var entry=Add(effect,action,new CardConditionals.CardRoleType{Role=CardRole.DecisionTarget,Check=CardConditionals.CardRoleType.Requirement.Is});entry.Selectors.Add(new Enemy());
                if(id==-867455886) {
                    var gasp=Trigger(c,EffectTriggerType.LastGaspTrigger);
                    // Host flushes native links before draining the death queue.
                    // Use the exact instance captured at death, not a newly chosen victim.
                    Add(gasp.EffectGroup.Effects[0],new DestroySelected(),Self());
                }
            } else {
                var damage=Add(effect,new CardActions.Damage{DamageSource=CardRole.Self,HealthLoss=new ActionValueData(6)},new Enemy{Avatar=true});damage.SelectorSort.Method=CardListSort.SortMethod.Random;damage.SelectorSort.NumToReturn=new ActionValueData(1);
                if(id==-1709661502) {
                    var start=Trigger(c,EffectTriggerType.WhenPlayerTurnStarts);start.TriggerCardRequirements.Add(new TurnRequirement{LhsCard=CardRole.Self,Conditional=new OwnTurn()});
                    Add(start.EffectGroup.Effects[0],new SummonDaughter(),Self());
                }
            }
            return true;
        }
        internal static void RefreshEquipmentAuras(ServerEffectUtilities.TriggerGameState state)
        {
            foreach(var card in state.CardCatalog.GatherDynamicCardList()) {
                int id=VariantCardRules.CanonicalId(card.CardId);if(id!=-801177855 && id!=1140811777 && id!=1366329424)continue;
                bool equipped=state.CardCatalog.GatherCardsInZone(i=>i.ItemTarget==card.InstanceId,card.OwnerNetId,GameZoneName.ItemBoard).Count>0;
                bool active=!card.Silenced && !card.IsDying() && card.CurrentGameZoneName==GameZoneName.CreatureBoard && (id==1366329424?!equipped:equipped);
                var enchant=card.AuraEnchantments.Find(a=>a.SourceId==card.InstanceId && VariantCardRules.CanonicalId(a.SourceCardId)==id);
                if(active==(enchant!=null))continue;
                if(active){var buff=id==-801177855?PowerBuff():new CardActions.Buff();
                    if(id==1366329424) {
                        buff.Enchantment.AttributeModifications.Add(new EnchantmentAttributeModification(EnchantmentAttributeModificationType.Set,AttributeType.Freeze,1));
                        buff.Enchantment.AttributeModifications.Add(new EnchantmentAttributeModification(EnchantmentAttributeModificationType.Set,AttributeType.PermFreeze,1));
                    }
                    if(id==1140811777){buff.Enchantment.AttributeModifications.Add(new EnchantmentAttributeModification(EnchantmentAttributeModificationType.Add,AttributeType.MaxHealth,3));buff.Enchantment.AttributeModifications.Add(new EnchantmentAttributeModification(EnchantmentAttributeModificationType.Set,AttributeType.Taunt,1));}enchant=new EnchantmentInstance(buff.Enchantment,1,card.CardId,card.InstanceId,card);card.AuraEnchantments.Add(enchant);card.Enchantments.Add(enchant);}
                else{card.AuraEnchantments.Remove(enchant);card.Enchantments.Remove(enchant);}
                card.RecalculateEnchantmentsWithTriggers(state,card);HistoryLoggingUtilities.LogFullCardRefresh(state.Log,state.CardCatalog,card,false);
            }
        }
        sealed class Requirement:GenericCardRequirement<TopStrength>{}
        sealed class DestroySelected:ICardAction
        {
            public CardFxAction GetFxActionType(){return new CardActions.Destroy().GetFxActionType();}
            public void Execute(CardActionArguments a)
            {
                var source=a.CardArguments.SelfCard;ServerCard target;
                if(!DyingChoices.TryGetValue(source,out target)) {
                    var links=source.GetLinkedCards(LinkType.BerneClan,LinkedCard.LinkRole.Source);target=links.Count==0?null:a.TriggerGameState.CardCatalog.FindServerCard(links[0]);
                }
                DyingChoices.Remove(source);
                if(target!=null && target.CurrentGameZoneName==GameZoneName.CreatureBoard && !target.IsDying())
                    CardActions.Destroy.StaticExecuteSingleTarget(a.TriggerGameState,target,source,a.AssetHash,false);
            }
        }
        sealed class TopStrength:ICardConditionalExpression
        {public bool Evaluate(ServerCard c,CardConditionalArguments a,PlayerConditionalArguments p,TriggerConditionalArguments t){if(c==null)return false;var top=a.CardCatalog.GetFirstMatchingCard(x=>true,c.OwnerNetId,GameZoneName.Deck);return top!=null && top.HasSubtype(CardSubtype.Strength);}}
        sealed class OwnDiscardCreature:ICardConditionalExpression
        {public bool Evaluate(ServerCard c,CardConditionalArguments a,PlayerConditionalArguments p,TriggerConditionalArguments t){return c!=null && a.SelfCard!=null && c.OwnerNetId==a.SelfCard.OwnerNetId && c.CurrentGameZoneName==GameZoneName.Cemetery && c.HasSubtype(CardSubtype.Creature);}}
        sealed class LivingBoard:ICardConditionalExpression
        {public bool Evaluate(ServerCard c,CardConditionalArguments a,PlayerConditionalArguments p,TriggerConditionalArguments t){return c!=null && c.CurrentGameZoneName==GameZoneName.CreatureBoard && !c.IsDying();}}
        sealed class Enemy:ICardConditionalExpression
        {
            internal bool Avatar,SelectedLane;
            public bool Evaluate(ServerCard c,CardConditionalArguments a,PlayerConditionalArguments p,TriggerConditionalArguments t)
            {return c!=null && a.SelfCard!=null && c.OwnerNetId!=a.SelfCard.OwnerNetId && !c.IsDying() && (c.CurrentGameZoneName==GameZoneName.CreatureBoard || (Avatar && c.CurrentGameZoneName==GameZoneName.AvatarBoard)) && (!SelectedLane || (a.LaneDecision.IsValid() && c.CurrentLaneIndex==a.LaneDecision.LaneIndex));}
        }
        sealed class EnemyLane:ILaneConditionalExpression
        {public bool Evaluate(LaneIdentifier l,LaneConditionalArguments lanes,CardConditionalArguments c,PlayerConditionalArguments p){return c.SelfCard!=null && l.ZoneName==GameZoneName.CreatureBoard && l.ZonePlayer!=c.SelfCard.OwnerNetId;}}
        sealed class TurnRequirement:GenericCardRequirement<OwnTurn>{}
        sealed class OwnTurn:ICardConditionalExpression
        {public bool Evaluate(ServerCard c,CardConditionalArguments a,PlayerConditionalArguments p,TriggerConditionalArguments t){return c!=null && !c.Silenced && !c.IsDying() && c.CurrentGameZoneName==GameZoneName.CreatureBoard && p.InciterPlayerState!=null && p.InciterPlayerState.NetId==c.OwnerNetId;}}
        sealed class SummonDaughter:ICardAction
        {
            static readonly int[] Daughters={16891283,1783048624,1307441161,-811885009};
            public CardFxAction GetFxActionType(){return CardFxAction.CreateToken;}
            public void Execute(CardActionArguments a)
            {
                var c=a.CardArguments.SelfCard;int index=c.QueryAttribute(RareAttributeType.FyrSummonerTracker).Value;
                int lane=c.CurrentLaneIndex;var state=a.TriggerGameState;if(state.CardCatalog.IsLaneFull(GameZoneName.CreatureBoard,lane,c.OwnerNetId))lane=1-lane;
                if(state.CardCatalog.IsLaneFull(GameZoneName.CreatureBoard,lane,c.OwnerNetId))return;
                var position=state.CardCatalog.FindInsertionPosition(c.OwnerNetId,GameZoneName.CreatureBoard,lane,CardInsertionType.Bottom);
                PlayerActions.CreateToken.StaticExecute(state,c,GameManager.Instance.Config.FindCardById(Daughters[index%4]),c.Premium,GameZoneType.Board,position,c.OwnerNetId,1,a.AssetHash,c);
                c.SetRareAttribute(RareAttributeType.FyrSummonerTracker,(index+1)%4);
            }
        }
    }
}
