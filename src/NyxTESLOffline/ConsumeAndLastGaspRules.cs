using System;
using SPTESL;
namespace NyxTESLOffline
{
    internal static class ConsumeAndLastGaspRules
    {
        internal static readonly int[] CardIds={-1179285674,768890407,190391314,-384331916,-6743431,-679792433};
        internal static void Apply(GameConfiguration config){foreach(int id in CardIds){var card=config.FindCardById(id);if(card!=null && Install(card))Plugin.Log("Consume/Last Gasp recovery: "+id);}}
        internal static void OnDying(ServerEffectUtilities.TriggerGameState state,ServerCard card)
        {
            if(!card.TriggeredEffects.Exists(t=>t.TriggerType==EffectTriggerType.LastGaspTrigger) || card.CurrentGameZoneName!=GameZoneName.CreatureBoard || !card.IsDying() || card.Silenced)return;
            foreach(var queued in state.TriggerQueues.Active)
                if(queued.EffectOwner==card && queued.TriggerEvent.Type==EffectTriggerType.LastGaspTrigger)return;
            ServerEffectUtilities.TriggerDirectEffectEvent(state,new ServerEffectUtilities.TriggerEvent(EffectTriggerType.LastGaspTrigger,card,state.Players));
        }
        internal static bool Install(Card card)
        {
            if(Array.IndexOf(CardIds,card.ResourceId.Id)<0 || card.TriggeredEffects.Count!=0 || card.ActionPlayInformation.Count!=0 || card.Auras.Count!=0 || card.ActivatedAbility.IsValidAbility())return false;
            if(card.ResourceId.Id==-6743431) {
                var effect=new Effect();var group=new EffectGroup();group.Effects.Add(effect);
                var mana=new PlayerEffectAction {HashId=-1,Action=new PlayerActions.ModifyMana {Amount=new ActionValueData(1)}};
                mana.Selectors.Add(new PlayerConditionals.PlayerRoleType {Role=PlayerRole.Self,Check=PlayerConditionals.PlayerRoleType.Requirement.Is});effect.PlayerActions.Add(mana);
                card.ActionPlayInformation.Add(new PlayCardAbility {PlayTargetingType=PlayTargetingType.NoTarget,EffectGroup=group});return true;
            }
            if(card.ResourceId.Id==-384331916 || card.ResourceId.Id==-679792433) {
                var trigger=NewTrigger(EffectTriggerType.LastGaspTrigger);
                var token=new PlayerEffectAction {HashId=-1,Action=new PlayerActions.CreateToken {CardReference=new EditorCardReference(card.ResourceId.Id==-679792433?844499977:-6743431),CreateInZoneType=GameZoneType.Hand,NumTokens=new ActionValueData(1),InsertPosition=new InsertPosition {Type=InsertPosition.LaneChoiceType.Constant,Lane=LaneIndexEnum.Left,InsertionType=CardInsertionType.Bottom}}};
                token.Selectors.Add(new PlayerConditionals.PlayerRoleType {Role=card.ResourceId.Id==-679792433?PlayerRole.Self:PlayerRole.Opponent,Check=PlayerConditionals.PlayerRoleType.Requirement.Is});
                trigger.EffectGroup.Effects[0].PlayerActions.Add(token);card.TriggeredEffects.Add(trigger);return true;
            }
            if(card.ResourceId.Id==768890407) {
                var trigger=NewTrigger(EffectTriggerType.LastGaspTrigger);
                var buff=new CardActions.Buff();buff.Enchantment.AttributeModifications.Add(new EnchantmentAttributeModification(
                    EnchantmentAttributeModificationType.Add,AttributeType.ManaCost,-1));
                var action=new CardEffectAction {HashId=-1,Action=buff};action.Selectors.Add(new OwnHandDragon());
                action.SelectorSort.Method=CardListSort.SortMethod.Random;action.SelectorSort.NumToReturn=new ActionValueData(1);
                trigger.EffectGroup.Effects[0].CardActions.Add(action);card.TriggeredEffects.Add(trigger);return true;
            }
            var summon=NewTrigger(EffectTriggerType.Summon);
            ConsumePrimitives.AddChoice(summon);
            card.TriggeredEffects.Add(summon);
            // Native Consume emits this only after the chosen cemetery card moves.
            // Queue a separate enemy decision; no free Shackle without consumption.
            var follow=NewTrigger(EffectTriggerType.WhenCardConsumes);
            follow.TriggerCardRequirements.Add(new CardRequirements.EventRoleRequirement {LhsCard=CardRole.Self,
                Conditional=new CardConditionals.CardRoleType {Role=CardRole.Inciter,Check=CardConditionals.CardRoleType.Requirement.Is}});
            follow.TriggerCardRequirements.Add(new SourceRequirement {LhsCard=CardRole.Self,Conditional=new LivingSource()});
            if(card.ResourceId.Id==190391314) {
                var buff=new CardActions.Buff();
                buff.Enchantment.AttributeModifications.Add(new EnchantmentAttributeModification(EnchantmentAttributeModificationType.Add,AttributeType.Attack,1));
                buff.Enchantment.AttributeModifications.Add(new EnchantmentAttributeModification(EnchantmentAttributeModificationType.Add,AttributeType.MaxHealth,1));
                var action=new CardEffectAction {HashId=-1,Action=buff};
                action.Selectors.Add(new CardConditionals.CardRoleType {Role=CardRole.Self,Check=CardConditionals.CardRoleType.Requirement.Is});
                follow.EffectGroup.Effects[0].CardActions.Add(action);card.TriggeredEffects.Add(follow);return true;
            }
            follow.ClientTargetingType=PlayTargetingType.RequiredTarget;
            var selector=new DecisionConditionalExpression {DecisionType=DecisionType.Card};selector.CardConditionalExpressions.Add(new EnemyCreature());follow.TargetSelectors.Add(selector);
            var freeze=new CardEffectAction {HashId=-1,Action=new CardActions.Freeze {PermanentFreeze=false,Type=EnchantmentUtilities.FreezeType.Normal}};
            freeze.Selectors.Add(new CardConditionals.CardRoleType {Role=CardRole.DecisionTarget,Check=CardConditionals.CardRoleType.Requirement.Is});
            freeze.Selectors.Add(new EnemyCreature());follow.EffectGroup.Effects[0].CardActions.Add(freeze);card.TriggeredEffects.Add(follow);return true;
        }
        static TriggeredEffect NewTrigger(EffectTriggerType type){var group=new EffectGroup();group.Effects.Add(new Effect());return new TriggeredEffect {
            TriggerType=type,SortOrder=TriggerSortOrder.EndOfActiveQueue,ClientTargetingType=PlayTargetingType.NoTarget,EffectGroup=group};}
        sealed class SourceRequirement:GenericCardRequirement<LivingSource>{}
        sealed class LivingSource:ICardConditionalExpression {
            public bool Evaluate(ServerCard card,CardConditionalArguments cards,PlayerConditionalArguments players,TriggerConditionalArguments trigger)
            {return card!=null && card.CurrentGameZoneName==GameZoneName.CreatureBoard && !card.IsDying();}
        }
        sealed class OwnHandDragon:ICardConditionalExpression {
            public bool Evaluate(ServerCard card,CardConditionalArguments cards,PlayerConditionalArguments players,TriggerConditionalArguments trigger)
            {return card!=null && cards.SelfCard!=null && card.OwnerNetId==cards.SelfCard.OwnerNetId && card.CurrentGameZoneName==GameZoneName.Hand && card.HasSubtype(CardSubtype.Dragon);}
        }
        sealed class EnemyCreature:ICardConditionalExpression {
            public bool Evaluate(ServerCard card,CardConditionalArguments cards,PlayerConditionalArguments players,TriggerConditionalArguments trigger)
            {return card!=null && cards.SelfCard!=null && card.OwnerNetId!=cards.SelfCard.OwnerNetId && card.CurrentGameZoneName==GameZoneName.CreatureBoard && card.HasSubtype(CardSubtype.Creature) && !card.IsDying();}
        }
    }
    // Shared composition: the same native preparation/UI/Consume used by the
    // existing Senche-raht Graveprowler, without duplicating the banish executor.
    internal static class ConsumePrimitives
    {
        internal static void AddChoice(TriggeredEffect trigger)
        {
            trigger.CustomUiType=CustomCardUiType.TargetCemetery;
            var prepare=new CardEffectAction {HashId=-1,Action=new CardActions.PrepareChooseFromGraveyard()};prepare.Selectors.Add(new OwnCemeteryCreature());
            trigger.EffectGroup.Effects[0].CardActions.Add(prepare);
            var selected=new EffectGroup();var effect=new Effect();selected.Effects.Add(effect);
            var consume=new CardEffectAction {HashId=-1,Action=new CardActions.Consume()};
            consume.Selectors.Add(new CardConditionals.CardRoleType {Role=CardRole.CustomUITarget,Check=CardConditionals.CardRoleType.Requirement.Is});
            consume.Selectors.Add(new OwnCemeteryCreature());effect.CardActions.Add(consume);trigger.CustomUiEffectGroups.Add(selected);
        }
        sealed class OwnCemeteryCreature:ICardConditionalExpression {
            public bool Evaluate(ServerCard card,CardConditionalArguments cards,PlayerConditionalArguments players,TriggerConditionalArguments trigger)
            {return card!=null && cards.SelfCard!=null && !cards.SelfCard.IsDying() && card.OwnerNetId==cards.SelfCard.OwnerNetId && card.CurrentGameZoneName==GameZoneName.Cemetery && card.HasSubtype(CardSubtype.Creature);}
        }
    }
}
