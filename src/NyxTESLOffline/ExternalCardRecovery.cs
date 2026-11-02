using System;
using System.Collections.Generic;
using System.Linq;
using SPTESL;

namespace NyxTESLOffline
{
    // Retail catalog IDs and descriptions: EXTERNAL-CARD-REPORT-2026-10-04.md.
    internal static class ExternalCardRecovery
    {
        internal static readonly int[] Ids = {935076603,-554006585,-822105628,489838961,951179139,-214646135,-2036935735,681793781,-1527076437,-647209492,-1370043641};
        internal static void Apply(GameConfiguration config)
        {
            foreach(int id in Ids) {var card=config.FindCardById(id);if(card!=null)Install(card);}
            var instructor=config.FindCardById(-1527076437);
            if(instructor!=null)foreach(var effect in instructor.TriggeredEffects.Where(t=>t.TriggerType==EffectTriggerType.Expertise))
                if(!effect.TriggerCardRequirements.OfType<Requirement>().Any(r=>r.Conditional!=null && r.Conditional.Kind==4))
                    effect.TriggerCardRequirements.Add(new Requirement {LhsCard=CardRole.Self,Conditional=new EventCondition {Kind=4}});
            string beastFx;
            if(config.CardFxDefinitionLookups.TryGetValue(-1439761022,out beastFx) && beastFx=="Card_BeastForm")
                foreach(int id in new[]{935076603,-2036935735}) {
                    var card=config.FindCardById(id);
                    if(card!=null && !card.CardFxRelations.Exists(r=>r.FXAction==CardFxAction.TriggeredEffect && r.EffectTriggerType==EffectTriggerType.WhenBeastFormChange))
                        card.CardFxRelations.Add(new CardFxRelationData{FXAction=CardFxAction.TriggeredEffect,EffectTriggerType=EffectTriggerType.WhenBeastFormChange,AssetHash=-1439761022,CardFxDefinition=beastFx});
                }
            var aela=config.FindCardById(-567567049);
            if(aela!=null && aela.TriggeredEffects.Count==0) {
                var finish=Trigger(aela,EffectTriggerType.WhenCardChanges);
                finish.TriggerCardRequirements.Add(new CardRequirements.EventRoleRequirement {LhsCard=CardRole.Self,Conditional=new CardConditionals.CardRoleType {Role=CardRole.Inciter,Check=CardConditionals.CardRoleType.Requirement.Is}});
                Target(finish,new CardActions.Destroy(),true);
            }
        }
        static TriggeredEffect Trigger(Card card,EffectTriggerType type)
        {
            var group=new EffectGroup();group.Effects.Add(new Effect());
            var t=new TriggeredEffect {TriggerType=type,SortOrder=TriggerSortOrder.EndOfActiveQueue,ClientTargetingType=PlayTargetingType.NoTarget,EffectGroup=group};
            card.TriggeredEffects.Add(t);return t;
        }
        static void Add(Effect effect,ICardAction action,ICardConditionalExpression selector)
        {var a=new CardEffectAction {HashId=-1,Action=action};a.Selectors.Add(selector);effect.CardActions.Add(a);}
        static ICardConditionalExpression Role(CardRole role)
        {return new CardConditionals.CardRoleType {Role=role,Check=CardConditionals.CardRoleType.Requirement.Is};}
        static CardActions.Buff Buff(int attack,int health)
        {
            var b=new CardActions.Buff();
            if(attack!=0)b.Enchantment.AttributeModifications.Add(new EnchantmentAttributeModification(EnchantmentAttributeModificationType.Add,AttributeType.Attack,attack));
            if(health!=0)b.Enchantment.AttributeModifications.Add(new EnchantmentAttributeModification(EnchantmentAttributeModificationType.Add,AttributeType.MaxHealth,health));
            return b;
        }
        static void Token(Effect effect,EditorCardReference reference,GameZoneType zone,int count,bool otherLane)
        {
            var a=new PlayerEffectAction {HashId=-1,Action=new PlayerActions.CreateToken {CardReference=reference,CreateInZoneType=zone,NumTokens=new ActionValueData(count),
                InsertPosition=new InsertPosition {Type=otherLane?InsertPosition.LaneChoiceType.Relative:InsertPosition.LaneChoiceType.Constant,
                    Lhs=CardRole.Inciter,Comparison=InsertPosition.PositionComparison.OtherLane,Lane=LaneIndexEnum.Left,
                    InsertionType=zone==GameZoneType.Deck?CardInsertionType.Random:CardInsertionType.Bottom}}};
            a.Selectors.Add(new PlayerConditionals.PlayerRoleType {Role=PlayerRole.Self,Check=PlayerConditionals.PlayerRoleType.Requirement.Is});effect.PlayerActions.Add(a);
        }
        static void Target(TriggeredEffect trigger,ICardAction action,bool wounded)
        {
            trigger.ClientTargetingType=PlayTargetingType.RequiredTarget;
            var d=new DecisionConditionalExpression {DecisionType=DecisionType.Card};d.CardConditionalExpressions.Add(new LegalTarget {Wounded=wounded});trigger.TargetSelectors.Add(d);
            var a=new CardEffectAction {HashId=-1,Action=action};a.Selectors.Add(Role(CardRole.DecisionTarget));a.Selectors.Add(new LegalTarget {Wounded=wounded});trigger.EffectGroup.Effects[0].CardActions.Add(a);
        }
        internal static bool Install(Card card)
        {
            if(Array.IndexOf(Ids,card.ResourceId.Id)<0 || card.TriggeredEffects.Count!=0)return false;
            int id=card.ResourceId.Id;
            if(id==-1370043641 && card.Auras.Count>0)return false;
            if(id==935076603 || id==-2036935735) {
                if(id==-2036935735)Target(Trigger(card,EffectTriggerType.Summon),new CardActions.Damage {HealthLoss=new ActionValueData(1),DamageSource=CardRole.Self},false);
                var beast=Trigger(card,EffectTriggerType.WhenBeastFormChange);
                beast.TriggerCardRequirements.Add(new Requirement {LhsCard=CardRole.Self,Conditional=new EventCondition {Kind=3}});
                Add(beast.EffectGroup.Effects[0],new CardActions.ChangeCard {CardReference=new EditorCardReference(id==935076603?1836094150:-567567049),ChangeType=CardActions.ChangeCard.ChangeCardType.BeastForm},Role(CardRole.Self));
            } else if(id==-554006585) {
                Add(Trigger(card,EffectTriggerType.Summon).EffectGroup.Effects[0],new CardActions.Damage {HealthLoss=new ActionValueData(2),DamageSource=CardRole.Self},new EnemyAvatar());
            } else if(id==-647209492) {
                var t=Trigger(card,EffectTriggerType.LastGaspTrigger);
                t.EffectGroup.Effects[0].CardActions.Add(new CardEffectAction {HashId=-1,Action=new DrawAndIncreaseDrawnCost()});
            } else if(id==-1370043641) {
                var aura=new Aura();
                var enchant=new AuraEnchantment {Enchantment=Buff(5,0).Enchantment};
                enchant.Selectors.Add(Role(CardRole.Self));
                aura.EnchantmentsToApply.Add(enchant);
                card.Auras.Add(aura);
            } else if(id==-822105628) {
                var t=Trigger(card,EffectTriggerType.LastGaspTrigger);Add(t.EffectGroup.Effects[0],Buff(3,3),new FriendlyOther());
                var a=t.EffectGroup.Effects[0].CardActions[0];a.SelectorSort.Method=CardListSort.SortMethod.Random;a.SelectorSort.NumToReturn=new ActionValueData(1);
            } else if(id==489838961 || id==-214646135) {
                var t=Trigger(card,id==489838961?EffectTriggerType.WhenCardAttacks:EffectTriggerType.WhenCardEntersBoard);
                t.TriggerCardRequirements.Add(new Requirement {LhsCard=CardRole.Self,Conditional=new EventCondition {Kind=id==489838961?1:2}});
                Add(t.EffectGroup.Effects[0],id==489838961?(ICardAction)Buff(1,0):new CardActions.Freeze(),Role(CardRole.Self));
                if(id==-214646135)Add(t.EffectGroup.Effects[0],new RefreshStatus(),Role(CardRole.Self));
            } else if(id==-1527076437) {
                var t=Trigger(card,EffectTriggerType.Expertise);
                t.TriggerCardRequirements.Add(new Requirement {LhsCard=CardRole.Self,Conditional=new EventCondition {Kind=4}});
                Token(t.EffectGroup.Effects[0],new EditorCardReference(1475387303),GameZoneType.Board,1,false);
                var token=(PlayerActions.CreateToken)card.TriggeredEffects[0].EffectGroup.Effects[0].PlayerActions[0].Action;
                token.InsertPosition.Type=InsertPosition.LaneChoiceType.Relative;token.InsertPosition.Lhs=CardRole.Self;token.InsertPosition.Comparison=InsertPosition.PositionComparison.SameLane;
            } else if(id==951179139) {
                Token(Trigger(card,EffectTriggerType.Summon).EffectGroup.Effects[0],new EditorCardReference(-1220535708),GameZoneType.Deck,3,false);
                var t=Trigger(card,EffectTriggerType.WhenCardEntersBoard);
                t.TriggerCardRequirements.Add(new Requirement {LhsCard=CardRole.Self,Conditional=new EventCondition {Kind=4}});
                Token(t.EffectGroup.Effects[0],new EditorCardReference(CardRole.Inciter),GameZoneType.Board,1,true);
            } else {
                var t=Trigger(card,EffectTriggerType.Summon);t.CustomUiType=CustomCardUiType.AspectOfHircine;
                var build=new PlayerActions.BuildCustomUiCards {RandomizeOrder=false};
                build.Choices.Add(new NamedEditorCardReference {Name="Strength",CardReference=new EditorCardReference(-463403605)});
                build.Choices.Add(new NamedEditorCardReference {Name="Speed",CardReference=new EditorCardReference(486979035)});
                var a=new PlayerEffectAction {HashId=-1,Action=build};a.Selectors.Add(new PlayerConditionals.PlayerRoleType {Role=PlayerRole.Self,Check=PlayerConditionals.PlayerRoleType.Requirement.Is});t.EffectGroup.Effects[0].PlayerActions.Add(a);
                foreach(int choice in new[]{-463403605,486979035}) {
                    var group=new EffectGroup();var effect=new Effect();group.Effects.Add(effect);
                    Add(effect,new CardActions.ChangeCard {CardReference=new EditorCardReference(choice)},Role(CardRole.Self));t.CustomUiEffectGroups.Add(group);
                }
            }
            return true;
        }
        sealed class Requirement:GenericCardRequirement<EventCondition> {}
        sealed class RefreshStatus:ICardAction
        {
            public CardFxAction GetFxActionType(){return CardFxAction.Freeze;}
            public void Execute(CardActionArguments a)
            {foreach(var c in a.ValidTargetList)HistoryLoggingUtilities.LogFullCardRefresh(a.TriggerGameState.Log,a.TriggerGameState.CardCatalog,c,false);}
        }
        sealed class EventCondition:ICardConditionalExpression
        {
            internal int Kind;
            public bool Evaluate(ServerCard card,CardConditionalArguments cards,PlayerConditionalArguments players,TriggerConditionalArguments trigger)
            {
                var inciter=cards.InciterCard;
                if(card==null || card.Silenced || card.IsDying() || card.CurrentGameZoneName!=GameZoneName.CreatureBoard)return false;
                if(Kind==4)return players.InciterPlayerState!=null && card.OwnerNetId==players.InciterPlayerState.NetId;
                if(inciter==null)return false;
                if(Kind==3)return inciter.HasSubtype(CardSubtype.Avatar) && inciter.OwnerNetId!=card.OwnerNetId;
                if(inciter==card || inciter.CurrentGameZoneName!=GameZoneName.CreatureBoard || inciter.IsDying())return false;
                if(Kind==2)return inciter.OwnerNetId!=card.OwnerNetId;
                if(inciter.OwnerNetId!=card.OwnerNetId)return false;
                return Kind==1 || inciter.HasSubtype(CardSubtype.Atronach);
            }
        }
        sealed class DrawAndIncreaseDrawnCost:ICardAction
        {
            public CardFxAction GetFxActionType(){return CardFxAction.CountOf;}
            public void Execute(CardActionArguments a)
            {
                var source=a.CardArguments.SelfCard;
                var owner=a.TriggerGameState.Players.Find(p=>p.NetId==source.OwnerNetId);
                if(owner==null)return;
                var cards=OfflineDrawUtilities.Draw(a.TriggerGameState,owner,1,CcgRpc.HistoryType.ActionMoveCards,"karthspire-scout-"+source.NetUid.Id);
                if(cards==null || cards.Count==0)return;
                var drawn=cards[cards.Count-1];
                var enchantment=new Enchantment();
                enchantment.AttributeModifications.Add(new EnchantmentAttributeModification(EnchantmentAttributeModificationType.Add,AttributeType.ManaCost,1));
                var instance=new EnchantmentInstance(enchantment,1,source.CardId,source.InstanceId,drawn);
                drawn.Enchantments.Add(instance);
                drawn.RecalculateEnchantmentsWithTriggers(a.TriggerGameState,source);
                HistoryLoggingUtilities.LogFullCardRefresh(a.TriggerGameState.Log,a.TriggerGameState.CardCatalog,drawn,false);
            }
        }
        sealed class FriendlyOther:ICardConditionalExpression
        {public bool Evaluate(ServerCard c,CardConditionalArguments a,PlayerConditionalArguments p,TriggerConditionalArguments t){return c!=null && a.SelfCard!=null && c!=a.SelfCard && c.OwnerNetId==a.SelfCard.OwnerNetId && c.CurrentGameZoneName==GameZoneName.CreatureBoard && !c.IsDying();}}
        internal static void RefreshAuras(ServerEffectUtilities.TriggerGameState state)
        {
            var cards=state.CardCatalog.GatherDynamicCardList();
            foreach(var card in cards) {
                if(VariantCardRules.CanonicalId(card.CardId)!=-1370043641)continue;
                bool active=card.CurrentGameZoneName==GameZoneName.CreatureBoard && !card.IsDying() && !card.Silenced && state.Players.Any(p=>p.NetId==card.OwnerNetId && p.IsMyTurn);
                bool changed=false;
                for(int i=card.AuraEnchantments.Count-1;i>=0;i--) {
                    var aura=card.AuraEnchantments[i];
                    if(VariantCardRules.CanonicalId(aura.SourceCardId)!=-1370043641 || aura.SourceId!=card.InstanceId)continue;
                    if(active && card.Enchantments.Contains(aura))continue;
                    card.AuraEnchantments.RemoveAt(i);card.Enchantments.Remove(aura);changed=true;
                }
                if(active && !card.AuraEnchantments.Exists(a=>VariantCardRules.CanonicalId(a.SourceCardId)==-1370043641 && a.SourceId==card.InstanceId)) {
                    var definition=GameManager.Instance.Config.FindCardById(card.CardId);
                    if(definition!=null && definition.Auras.Count>0) {
                        var added=new EnchantmentInstance(definition.Auras[0].EnchantmentsToApply[0].Enchantment,1,card.CardId,card.InstanceId,card);
                        card.AuraEnchantments.Add(added);card.Enchantments.Add(added);changed=true;
                    }
                }
                if(changed){card.RecalculateEnchantmentsWithTriggers(state,null);HistoryLoggingUtilities.LogFullCardRefresh(state.Log,state.CardCatalog,card,false);}
            }
        }
        sealed class EnemyAvatar:ICardConditionalExpression
        {public bool Evaluate(ServerCard c,CardConditionalArguments a,PlayerConditionalArguments p,TriggerConditionalArguments t){return c!=null && a.SelfCard!=null && c.HasSubtype(CardSubtype.Avatar) && c.OwnerNetId!=a.SelfCard.OwnerNetId;}}
        sealed class LegalTarget:ICardConditionalExpression
        {
            internal bool Wounded;
            public bool Evaluate(ServerCard c,CardConditionalArguments a,PlayerConditionalArguments p,TriggerConditionalArguments t)
            {return c!=null && !c.IsDying() && (c.CurrentGameZoneName==GameZoneName.CreatureBoard || (!Wounded && c.CurrentGameZoneName==GameZoneName.AvatarBoard)) && (!Wounded || c.QueryAttribute(AttributeType.Wounded).Value>0);}
        }
    }
}
