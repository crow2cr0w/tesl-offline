using System;
using System.Collections.Generic;
using System.Linq;
using SPTESL;
namespace NyxTESLOffline
{
    // Descriptions/identities from the hash-pinned retail configuration; see batch03 report.
    internal static class ObservedCardBatch03
    {
        internal static readonly int[] Ids={157797006,-1728909427,735287286,-515379928,-543916079,1140811777,85837010,830964466,-938494744,825017246,729361361,-1272405782,767076468,-775439698};
        internal static void Apply(GameConfiguration config)
        {foreach(int id in Ids){var c=config.FindCardById(id);if(c!=null)Install(c);}
            var sly=config.FindCardById(85837010);if(sly!=null)InstallSlyLastGasp(sly);
            var red=config.FindCardById(-543916079);
            SetActionHash(red,delegate(ICardAction action){return action is CardActions.Damage;},485616595);
            AddNamedFx(config,red,CardFxAction.TriggeredEffect,EffectTriggerType.ActionEffect,-1597605898,"Card_RedYear_Board");
            AddNamedFx(config,red,CardFxAction.Damage,EffectTriggerType.Invalid,485616595,"Card_RedYear");
            var shambler=config.FindCardById(-775439698);
            SetActionHash(shambler,delegate(ICardAction action){return action is CardActions.Destroy;},2058413258);
        }
        static void SetActionHash(Card card,Predicate<ICardAction> predicate,int hash)
        {if(card==null)return;foreach(var effect in card.ActionPlayInformation.SelectMany(a=>a.EffectGroup.Effects).Concat(card.TriggeredEffects.SelectMany(t=>t.EffectGroup.Effects)))foreach(var action in effect.CardActions)if(predicate(action.Action))action.HashId=hash;}
        static void AddNamedFx(GameConfiguration config,Card card,CardFxAction action,EffectTriggerType trigger,int hash,string name)
        {
            string found;if(card==null || !config.CardFxDefinitionLookups.TryGetValue(hash,out found) || found!=name || !config.CardFxDefinitions.Exists(f=>f.Name==name && !String.IsNullOrEmpty(f.MainParticleSystem)))return;
            var relation=card.CardFxRelations.Find(r=>r.FXAction==action && r.EffectTriggerType==trigger);
            if(relation==null)card.CardFxRelations.Add(new CardFxRelationData{FXAction=action,EffectTriggerType=trigger,AssetHash=hash,CardFxDefinition=name});
            else{relation.AssetHash=hash;relation.CardFxDefinition=name;}
        }
        static ICardConditionalExpression Role(CardRole r){return new CardConditionals.CardRoleType{Role=r,Check=CardConditionals.CardRoleType.Requirement.Is};}
        static CardEffectAction Add(Effect e,ICardAction a,ICardConditionalExpression s)
        {var entry=new CardEffectAction{HashId=-1,Action=a};entry.Selectors.Add(s);e.CardActions.Add(entry);return entry;}
        static TriggeredEffect Trigger(Card c,EffectTriggerType type)
        {var g=new EffectGroup();g.Effects.Add(new Effect());var t=new TriggeredEffect{TriggerType=type,SortOrder=TriggerSortOrder.EndOfActiveQueue,ClientTargetingType=PlayTargetingType.NoTarget,EffectGroup=g};c.TriggeredEffects.Add(t);return t;}
        static void InstallSlyLastGasp(Card card)
        {
            var trigger=card.TriggeredEffects.Find(t=>t.TriggerType==EffectTriggerType.LastGaspTrigger);
            if(trigger==null || trigger.EffectGroup==null || trigger.EffectGroup.Effects.Count==0)return;
            // The retail player requirement is evaluated against the board avatar
            // by this runtime and can suppress the event before its draw action.
            // Keep its MaxMana >= 7 rule in the server action, where player state
            // is authoritative, and preserve the Last Gasp event/header itself.
            trigger.TriggerPlayerRequirements.Clear();
            var effect=trigger.EffectGroup.Effects[0];
            effect.CardActions.Clear();effect.PlayerActions.Clear();
            Add(effect,new DrawOne{ManaGate=true},Role(CardRole.Self));
            for(int i=trigger.EffectGroup.Effects.Count-1;i>0;i--)trigger.EffectGroup.Effects.RemoveAt(i);
        }
        static CardActions.Buff Buff(int power,int health,AttributeType keyword=AttributeType.None)
        {var b=new CardActions.Buff();if(power!=0)b.Enchantment.AttributeModifications.Add(new EnchantmentAttributeModification(EnchantmentAttributeModificationType.Add,AttributeType.Attack,power));if(health!=0)b.Enchantment.AttributeModifications.Add(new EnchantmentAttributeModification(EnchantmentAttributeModificationType.Add,AttributeType.MaxHealth,health));if(keyword!=AttributeType.None)b.Enchantment.AttributeModifications.Add(new EnchantmentAttributeModification(EnchantmentAttributeModificationType.Set,keyword,1));return b;}
        internal static bool Install(Card c)
        {
            int id=c.ResourceId.Id;if(Array.IndexOf(Ids,id)<0 || VariantCardRules.HasRules(c))return false;
            if(id==1140811777){var aura=new Aura();var enchant=new AuraEnchantment{Enchantment=Buff(0,3,AttributeType.Taunt).Enchantment};enchant.Selectors.Add(Role(CardRole.Self));enchant.Selectors.Add(new CardConditionals.HasItemEquipped{EquipState=CardConditionals.HasItemEquipped.Requirement.HasEquipment});aura.EnchantmentsToApply.Add(enchant);c.Auras.Add(aura);return true;}
            if(id==-515379928 || id==-543916079){var g=new EffectGroup();var e=new Effect();g.Effects.Add(e);
                if(id==-543916079){var damage=Add(e,new CardActions.Damage{DamageSource=CardRole.Self,HealthLoss=new ActionValueData(10)},new BoardTarget());damage.HashId=485616595;}
                else {var debuff=new CardActions.Debuff();debuff.Enchantment.AttributeModifications.Add(new EnchantmentAttributeModification(EnchantmentAttributeModificationType.Set,AttributeType.Taunt,0));Add(e,debuff,new BoardTarget{Enemy=true});}
                c.ActionPlayInformation.Add(new PlayCardAbility{PlayTargetingType=PlayTargetingType.NoTarget,EffectGroup=g,IsDamageOnly=id==-543916079});return true;}
            var trigger=Trigger(c,id==85837010 || id==830964466?EffectTriggerType.LastGaspTrigger:EffectTriggerType.Summon);var effect=trigger.EffectGroup.Effects[0];
            if(id==157797006)Add(effect,new CardActions.Damage{DamageSource=CardRole.Self,HealthLoss=new ActionValueData(2)},new AvatarTarget());
            else if(id==-1728909427 || id==85837010)Add(effect,new DrawOne{ManaGate=id==85837010},Role(CardRole.Self));
            else if(id==735287286 || id==-1272405782){var selector=new BoardTarget{Enemy=id==735287286,SameLane=id==735287286};trigger.ClientTargetingType=PlayTargetingType.RequiredTarget;var decision=new DecisionConditionalExpression{DecisionType=DecisionType.Card};decision.CardConditionalExpressions.Add(selector);trigger.TargetSelectors.Add(decision);
                var entry=Add(effect,id==735287286?(ICardAction)new CardActions.Destroy():new CardActions.Damage{DamageSource=CardRole.Self,HealthLoss=new ActionValueData(ActionValueMethod.ValueOfCardAttribute){CardRole=CardRole.Self,AttributeName=AttributeType.Attack}},Role(CardRole.DecisionTarget));entry.Selectors.Add(selector);}
            else if(id==767076468){ConsumePrimitives.AddChoice(trigger);
                var follow=Trigger(c,EffectTriggerType.WhenCardConsumes);
                follow.TriggerCardRequirements.Add(new CardRequirements.EventRoleRequirement{LhsCard=CardRole.Self,Conditional=new CardConditionals.CardRoleType{Role=CardRole.Inciter,Check=CardConditionals.CardRoleType.Requirement.Is}});
                Add(follow.EffectGroup.Effects[0],new BuffMatchingConsumed(),Role(CardRole.Self));}
            else if(id==-775439698)Add(effect,new MillAndConsume(),Role(CardRole.Self));
            else if(id==729361361){var entry=Add(effect,new CardActions.ReturnToHand{DrawCopies=false},new OwnDiscardOrc());entry.SelectorSort.Method=CardListSort.SortMethod.Random;entry.SelectorSort.NumToReturn=new ActionValueData(1);
                var aura=new Aura();var enchant=new AuraEnchantment{Enchantment=Buff(1,1).Enchantment};enchant.Selectors.Add(new OtherOwnOrc());aura.EnchantmentsToApply.Add(enchant);c.Auras.Add(aura);}
            else Add(effect,new CreateOrTop{Id=id},Role(CardRole.Self));
            return true;
        }
        sealed class BoardTarget:ICardConditionalExpression
        {internal bool Enemy,SameLane;public bool Evaluate(ServerCard c,CardConditionalArguments a,PlayerConditionalArguments p,TriggerConditionalArguments t){return c!=null && a.SelfCard!=null && c.CurrentGameZoneName==GameZoneName.CreatureBoard && !c.IsDying() && (!Enemy || c.OwnerNetId!=a.SelfCard.OwnerNetId) && (!SameLane || c.CurrentLaneIndex==a.SelfCard.CurrentLaneIndex);}}
        sealed class AvatarTarget:ICardConditionalExpression
        {public bool Evaluate(ServerCard c,CardConditionalArguments a,PlayerConditionalArguments p,TriggerConditionalArguments t){return c!=null && c.CurrentGameZoneName==GameZoneName.AvatarBoard;}}
        sealed class OwnDiscardOrc:ICardConditionalExpression
        {public bool Evaluate(ServerCard c,CardConditionalArguments a,PlayerConditionalArguments p,TriggerConditionalArguments t){return c!=null && a.SelfCard!=null && c.OwnerNetId==a.SelfCard.OwnerNetId && c.CurrentGameZoneName==GameZoneName.Cemetery && c.HasSubtype(CardSubtype.Orc);}}
        sealed class OtherOwnOrc:ICardConditionalExpression
        {public bool Evaluate(ServerCard c,CardConditionalArguments a,PlayerConditionalArguments p,TriggerConditionalArguments t){return c!=null && a.SelfCard!=null && c!=a.SelfCard && c.OwnerNetId==a.SelfCard.OwnerNetId && c.CurrentGameZoneName==GameZoneName.CreatureBoard && c.HasSubtype(CardSubtype.Orc) && !c.IsDying();}}
        sealed class DrawOne:ICardAction
        {internal bool ManaGate;public CardFxAction GetFxActionType(){return CardFxAction.CountOf;}public void Execute(CardActionArguments a){var source=a.CardArguments.SelfCard;var owner=a.TriggerGameState.Players.Find(p=>p.NetId==source.OwnerNetId);if(owner!=null && (!ManaGate || owner.QueryAttribute(AttributeType.MaxMana).Value>=7))OfflineDrawUtilities.Draw(a.TriggerGameState,owner,1,CcgRpc.HistoryType.ActionMoveCards,"batch03-"+source.CardId);}}
        sealed class CreateOrTop:ICardAction
        {
            internal int Id;public CardFxAction GetFxActionType(){return Id==825017246?CardFxAction.Buff:CardFxAction.CreateToken;}
            public void Execute(CardActionArguments a)
            {
                var source=a.CardArguments.SelfCard;var state=a.TriggerGameState;
                if(Id==825017246){var top=state.CardCatalog.GetFirstMatchingCard(c=>true,source.OwnerNetId,GameZoneName.Deck);if(top==null)return;
                    var owner=state.Players.Find(p=>p.NetId==source.OwnerNetId);
                    // Native top-deck tests carry the color and original success FX to the client.
                    foreach(var color in new[]{CardSubtype.Strength,CardSubtype.Intelligence,CardSubtype.Willpower,CardSubtype.Agility,CardSubtype.Endurance}){
                        var condition=new PlayerConditionals.TopCardSubtypeInZone{Check=PlayerConditionals.TopCardSubtypeInZone.Requirement.Is,Subtype=color,ZoneName=GameZoneName.Deck,SuppressClientFailMessage=true};
                        var conditionalLog=new PlayerConditionalLog(state.Log,source);
                        bool success=condition.Evaluate(owner,a.PlayerArguments,a.CardArguments,a.TriggerArguments,conditionalLog);condition.ReportResult(conditionalLog,success);
                    }
                    var b=Buff(top.HasSubtype(CardSubtype.Strength)?2:0,top.HasSubtype(CardSubtype.Endurance)?2:0);
                    foreach(var pair in new[]{new KeyValuePair<CardSubtype,AttributeType>(CardSubtype.Intelligence,AttributeType.Ward),new KeyValuePair<CardSubtype,AttributeType>(CardSubtype.Willpower,AttributeType.Drain),new KeyValuePair<CardSubtype,AttributeType>(CardSubtype.Agility,AttributeType.Deathtouch)})if(top.HasSubtype(pair.Key))b.Enchantment.AttributeModifications.Add(new EnchantmentAttributeModification(EnchantmentAttributeModificationType.Set,pair.Value,1));b.Execute(a);return;}
                int lane=0;Card token=null;GameZoneName zone=GameZoneName.Hand;
                if(Id==-938494744){lane=1-source.CurrentLaneIndex;zone=GameZoneName.CreatureBoard;if(state.CardCatalog.IsLaneFull(zone,lane,source.OwnerNetId))return;token=GameManager.Instance.Config.FindCardById(source.CardId);}
                else{var choices=new List<Card>();foreach(var set in GameManager.Instance.Config.CardCollection)foreach(var c in set.Cards)if(c.HasSubtype(CardSubtype.Shout) && c.AlternateOfCard.resourceId==0 && !String.IsNullOrEmpty(c.ExportCode))choices.Add(c);if(choices.Count==0)return;token=choices[TESLEncryption.GenerateIntInRange(0,choices.Count)];}
                var position=state.CardCatalog.FindInsertionPosition(source.OwnerNetId,zone,lane,CardInsertionType.Bottom);
                PlayerActions.CreateToken.StaticExecute(state,source,token,source.Premium,zone==GameZoneName.Hand?GameZoneType.Hand:GameZoneType.Board,position,source.OwnerNetId,1,a.AssetHash,source);
            }
        }
        sealed class BuffMatchingConsumed:ICardAction
        {public CardFxAction GetFxActionType(){return CardFxAction.Buff;}public void Execute(CardActionArguments a){var source=a.CardArguments.SelfCard;var target=a.CardArguments.EventTargetCard;if(source.Silenced || source.IsDying() || source.CurrentGameZoneName!=GameZoneName.CreatureBoard || target==null || target.CurrentGameZoneName!=GameZoneName.RemoveFromGame || !target.HasSubtype(CardSubtype.Creature) || target.OwnerNetId!=source.OwnerNetId)return;var sameName=new CardConditionals.HasSameName{Same=CardConditionals.HasSameName.Requirement.Is,OtherCard=CardRole.EventTarget};var matches=a.TriggerGameState.CardCatalog.GatherDynamicCardList().FindAll(c=>c.OwnerNetId==target.OwnerNetId && c.HasSubtype(CardSubtype.Creature) && sameName.Evaluate(c,a.CardArguments,a.PlayerArguments,a.TriggerArguments) && (c.CurrentGameZoneName==GameZoneName.Hand || c.CurrentGameZoneName==GameZoneName.Deck));Buff(2,2).Execute(new CardActionArguments(a.TriggerGameState,matches,a.CardArguments,a.PlayerArguments,a.TriggerArguments,a.AssetHash,a.UsingDefaultFx));}}
        sealed class MillAndConsume:ICardAction
        {public CardFxAction GetFxActionType(){return CardFxAction.Consume;}public void Execute(CardActionArguments a){var source=a.CardArguments.SelfCard;var state=a.TriggerGameState;var deck=state.CardCatalog.GatherCardsInZone(c=>true,source.OwnerNetId,GameZoneName.Deck);if(deck.Count>3)deck=deck.GetRange(0,3);OfflineDiscardUtilities.Discard(state,deck,2058413258,source);
            var targets=state.CardCatalog.GatherCardsInZone(c=>c.HasSubtype(CardSubtype.Creature) && c.QueryTurnJournalCount(CardJournal.EnteredDiscard)>0,source.OwnerNetId,GameZoneName.Cemetery);
            if(targets.Count==0)return;new CardActions.Consume().Execute(new CardActionArguments(state,targets,a.CardArguments,a.PlayerArguments,a.TriggerArguments,a.AssetHash,a.UsingDefaultFx));Buff(targets.Count,targets.Count).Execute(a);}}
        internal static void RefreshAuras(ServerEffectUtilities.TriggerGameState state)
        {
            var all=state.CardCatalog.GatherDynamicCardList();var chiefs=all.FindAll(c=>VariantCardRules.CanonicalId(c.CardId)==729361361 && !c.Silenced && !c.IsDying() && c.CurrentGameZoneName==GameZoneName.CreatureBoard);
            foreach(var card in all){bool changed=false;for(int i=card.AuraEnchantments.Count-1;i>=0;i--){var e=card.AuraEnchantments[i];if(VariantCardRules.CanonicalId(e.SourceCardId)!=729361361)continue;var source=chiefs.Find(c=>c.InstanceId==e.SourceId);if(source!=null && source!=card && source.OwnerNetId==card.OwnerNetId && card.HasSubtype(CardSubtype.Orc) && card.CurrentGameZoneName==GameZoneName.CreatureBoard && !card.IsDying())continue;card.AuraEnchantments.RemoveAt(i);card.Enchantments.Remove(e);changed=true;}
                foreach(var source in chiefs){if(source==card || source.OwnerNetId!=card.OwnerNetId || !card.HasSubtype(CardSubtype.Orc) || card.CurrentGameZoneName!=GameZoneName.CreatureBoard || card.IsDying() || card.AuraEnchantments.Exists(e=>e.SourceId==source.InstanceId && VariantCardRules.CanonicalId(e.SourceCardId)==729361361))continue;var added=new EnchantmentInstance(Buff(1,1).Enchantment,1,source.CardId,source.InstanceId,card);card.AuraEnchantments.Add(added);card.Enchantments.Add(added);changed=true;}
                if(changed){card.RecalculateEnchantmentsWithTriggers(state,card);HistoryLoggingUtilities.LogFullCardRefresh(state.Log,state.CardCatalog,card,false);}}
        }
    }
}
