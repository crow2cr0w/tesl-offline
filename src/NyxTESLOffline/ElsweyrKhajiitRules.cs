using System;
using SPTESL;

namespace NyxTESLOffline
{
    // Batch one: original Sage descriptions; native lunar condition and executors.
    internal static class ElsweyrKhajiitRules
    {
        internal static readonly int[] CardIds = {245321285,426473688,-578290460,-980791589,
            636947392,1937298575,672756275,1920337229,1244028749,1547461038};
        internal static void Apply(GameConfiguration config)
        {
            foreach (int id in CardIds) {
                Card card = config.FindCardById(id);
                if (card != null && Install(card)) Plugin.Log("Elsweyr Khajiit recovery: " + id);
            }
        }
        internal static bool Install(Card card)
        {
            int id = card.ResourceId.Id;
            if (Array.IndexOf(CardIds,id)<0 || card.TriggeredEffects.Count!=0 ||
                card.ActionPlayInformation.Count!=0 || card.Auras.Count!=0 || card.ActivatedAbility.IsValidAbility()) return false;
            var wax = Trigger(card,EffectTriggerType.Wax);
            var wane = Trigger(card,EffectTriggerType.Wane);
            if (id==245321285) { Self(wax,Buff(1,1)); Target(wane,new CardActions.Fight(),new CreatureTarget(true,false));
                ((CardActions.Fight)wane.EffectGroup.Effects[0].CardActions[0].Action).InciterSelectors.Add(Role(CardRole.Self)); }
            if (id==426473688) { Self(wax,Buff(3,0)); Self(wane,Buff(0,0,AttributeType.Charge)); }
            if (id==-578290460) { Draw(wax,1); Self(wane,Buff(1,1,AttributeType.Taunt)); }
            if (id==-980791589) { Self(wax,Buff(2,2)); Target(wane,new CardActions.Silence(),new CreatureTarget(false,false)); }
            if (id==636947392) { Target(wax,Buff(2,2),new CreatureTarget(false,true)); Self(wane,Buff(0,0,AttributeType.Taunt,AttributeType.Ward)); }
            if (id==1937298575) { Self(wax,Buff(1,1,AttributeType.Drain)); Draw(wane,1); }
            if (id==672756275) { Self(wax,Buff(2,0)); Target(wane,new CardActions.Freeze {
                PermanentFreeze=false,Type=EnchantmentUtilities.FreezeType.Normal},new CreatureTarget(true,false)); }
            if (id==1920337229) { Draw(wax,2); Target(wane,new CardActions.Destroy(),new CreatureTarget(false,false)); }
            if (id==1244028749) { var action = new PlayerEffectAction {HashId=-1,Action=new PlayerActions.CreateToken {
                CardReference=new EditorCardReference(id),NumTokens=new ActionValueData(1),CreateInZoneType=GameZoneType.Board,
                InsertPosition=new InsertPosition {Type=InsertPosition.LaneChoiceType.Relative,Lhs=CardRole.Self,
                    Comparison=InsertPosition.PositionComparison.OtherLane,InsertionType=CardInsertionType.Bottom}}};
                action.Selectors.Add(new PlayerConditionals.PlayerRoleType {Role=PlayerRole.Self,
                    Check=PlayerConditionals.PlayerRoleType.Requirement.Is}); wax.EffectGroup.Effects[0].PlayerActions.Add(action);
                Self(wane,Buff(1,2,AttributeType.Taunt)); }
            if (id==1547461038) { Self(wax,Buff(0,4)); var heal=new CardActions.Heal {HealthGain=new ActionValueData(4)};
                heal.DrainFxSelector.Add(Role(CardRole.Self)); Add(wane,heal,new OwnerAvatar()); }
            return true;
        }
        internal static void OnPlayed(ServerEffectUtilities.TriggerGameState state,ServerCard source)
        {
            if (Array.IndexOf(CardIds,VariantCardRules.CanonicalId(source.CardId))<0 || source.CurrentGameZoneName!=GameZoneName.CreatureBoard) return;
            PlayerState player=null; foreach(var p in state.Players) if(p.NetId==source.OwnerNetId) player=p;
            if(player==null) return;
            var cards=new CardConditionalArguments(state.CardCatalog,source,source,null,null,null,LaneIdentifier.GetInvalid());
            var players=new PlayerConditionalArguments(state.CardCatalog,player,player,null,null,null);
            foreach(var phase in new[]{EffectTriggerType.Wax,EffectTriggerType.Wane}) {
                var condition=new PlayerConditionals.WaxWaneCheck {Is=phase==EffectTriggerType.Wax ?
                    PlayerConditionals.WaxWaneCheck.Requirement.Wax : PlayerConditionals.WaxWaneCheck.Requirement.Wane};
                if(condition.Evaluate(player,players,cards,new TriggerConditionalArguments(),new PlayerConditionalLog()))
                    ServerEffectUtilities.TriggerDirectEffectEvent(state,new ServerEffectUtilities.TriggerEvent(phase,source,state.Players));
            }
        }
        static TriggeredEffect Trigger(Card card,EffectTriggerType phase)
        {
            var effect=new Effect(); var group=new EffectGroup();group.Effects.Add(effect);
            var trigger=new TriggeredEffect {TriggerType=phase,SortOrder=TriggerSortOrder.EndOfActiveQueue,
                ClientTargetingType=PlayTargetingType.NoTarget,EffectGroup=group};card.TriggeredEffects.Add(trigger);return trigger;
        }
        static CardConditionals.CardRoleType Role(CardRole role) {return new CardConditionals.CardRoleType {
            Role=role,Check=CardConditionals.CardRoleType.Requirement.Is};}
        static CardActions.Buff Buff(int power,int health,params AttributeType[] keywords)
        {
            var buff=new CardActions.Buff();
            if(power!=0)buff.Enchantment.AttributeModifications.Add(new EnchantmentAttributeModification(EnchantmentAttributeModificationType.Add,AttributeType.Attack,power));
            if(health!=0)buff.Enchantment.AttributeModifications.Add(new EnchantmentAttributeModification(EnchantmentAttributeModificationType.Add,AttributeType.MaxHealth,health));
            foreach(var k in keywords)buff.Enchantment.AttributeModifications.Add(new EnchantmentAttributeModification(EnchantmentAttributeModificationType.Set,k,1));
            return buff;
        }
        static void Self(TriggeredEffect trigger,ICardAction action) {Add(trigger,action,Role(CardRole.Self));}
        static void Add(TriggeredEffect trigger,ICardAction action,ICardConditionalExpression selector)
        {
            var entry=new CardEffectAction {HashId=-1,Action=action};entry.Selectors.Add(selector);trigger.EffectGroup.Effects[0].CardActions.Add(entry);
        }
        static void Target(TriggeredEffect trigger,ICardAction action,ICardConditionalExpression selector)
        {
            trigger.ClientTargetingType=PlayTargetingType.RequiredTarget;
            var decision=new DecisionConditionalExpression {DecisionType=DecisionType.Card};decision.CardConditionalExpressions.Add(selector);
            trigger.TargetSelectors.Add(decision);Add(trigger,action,Role(CardRole.DecisionTarget));
            trigger.EffectGroup.Effects[0].CardActions[0].Selectors.Add(selector);
        }
        static void Draw(TriggeredEffect trigger,int count)
        {
            var action=new PlayerEffectAction {HashId=-1,Action=new PlayerActions.DrawCards {NumCards=new ActionValueData(count)}};
            action.Selectors.Add(new PlayerConditionals.PlayerRoleType {Role=PlayerRole.Self,Check=PlayerConditionals.PlayerRoleType.Requirement.Is});
            trigger.EffectGroup.Effects[0].PlayerActions.Add(action);
        }
        sealed class CreatureTarget : ICardConditionalExpression
        {
            readonly bool enemy,hand;internal CreatureTarget(bool enemy,bool hand){this.enemy=enemy;this.hand=hand;}
            public bool Evaluate(ServerCard card,CardConditionalArguments cards,PlayerConditionalArguments players,TriggerConditionalArguments trigger)
            {return card!=null && cards.SelfCard!=null && card.HasSubtype(CardSubtype.Creature) && !card.IsDying() &&
                card.CurrentGameZoneName==(hand?GameZoneName.Hand:GameZoneName.CreatureBoard) &&
                (!hand || card.OwnerNetId==cards.SelfCard.OwnerNetId) && (!enemy || card.OwnerNetId!=cards.SelfCard.OwnerNetId);}
        }
        sealed class OwnerAvatar : ICardConditionalExpression
        {
            public bool Evaluate(ServerCard card,CardConditionalArguments cards,PlayerConditionalArguments players,TriggerConditionalArguments trigger)
            {return card!=null && cards.SelfCard!=null && card.OwnerNetId==cards.SelfCard.OwnerNetId && card.CurrentGameZoneName==GameZoneName.AvatarBoard;}
        }
    }
}
