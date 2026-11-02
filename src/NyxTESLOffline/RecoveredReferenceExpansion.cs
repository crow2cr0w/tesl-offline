using System;
using System.Collections.Generic;
using SPTESL;
namespace NyxTESLOffline
{
    internal static partial class RecoveredCardRules
    {
        internal static readonly int[] ReferenceExpansionIds = {
            1789830533, -1800265625, -542954561, -462299413, 780292596, 43252412, -522300233,
            694231768, -1192168222
        };
        static bool IsReferenceExpansion(int id) { return Array.IndexOf(ReferenceExpansionIds,id) >= 0 || id == -1192168222; }
        static InsertPosition ShuffledPosition() { return new InsertPosition {
            Type = InsertPosition.LaneChoiceType.Constant, InsertionType = CardInsertionType.Random }; }
        static bool InstallReferenceExpansion(Card card)
        {
            if (!CanInstall(card) || card.Auras.Count != 0) return false;
            int id = card.ResourceId.Id;
            if (id == -542954561) return InstallTargetedAction(card,
                new CardActions.TransformCard { CardReference = new EditorCardReference(404557142) }, TargetSelector(false,false,false));
            if (id == 694231768 || id == -1192168222) {
                var selector = EnemyCreatureSelector();
                selector.CardConditionalExpressions.Add(new CardConditionals.AttributeValue {
                    AttributeName = AttributeType.Attack,
                    Is = CardConditionals.AttributeValue.ComparisonType.LessThanOrEqualTo,
                    Value = new ConditionalValueData(2)
                });
                return InstallTargetedSummon(card,
                    new CardActions.TransformCard { CardReference = new EditorCardReference(-163092025) }, selector);
            }
            if (id == 780292596) return InstallTargetedAction(card,new CardActions.ReturnToHand(),TargetSelector(false,false,false));
            if (id == -522300233) {
                var decision = new DecisionConditionalExpression { DecisionType = DecisionType.Card };
                decision.CardConditionalExpressions.Add(new DismantleTarget());
                return InstallTargetedAction(card,new CardActions.Destroy(),decision);
            }
            if (id == 43252412) {
                var debuff = new CardActions.Debuff(); debuff.Enchantment.ExpiresAtEndOfTurn = true;
                debuff.Enchantment.AttributeModifications.Add(new EnchantmentAttributeModification(
                    EnchantmentAttributeModificationType.Add,AttributeType.Attack,-2));
                if (!InstallTargetedSummon(card,debuff,TargetSelector(false,false,false))) return false;
                AddAction(card.TriggeredEffects[0].EffectGroup.Effects[0],new CardActions.RemoveCover()); return true;
            }
            var effect = new Effect();
            if (id == 1789830533) {
                ExpansionArea(effect,new CardActions.ReturnToDeck { InsertPosition = ShuffledPosition() },new ExpansionTarget { Friendly = true });
                // Native NewOwner chooses the opposite of the targets' owner. Keep groups homogeneous.
                ExpansionArea(effect,new CardActions.ReturnToDeckNewOwner { InsertPosition = ShuffledPosition() },new ExpansionTarget { Friendly = false });
            } else if (id == -1800265625) {
                ExpansionArea(effect,new JourneyReturn(),new ExpansionTarget { Friendly = true, Cemetery = true });
            } else {
                var copy = new CardActions.CopyAllKeywords { RemoveOnCopy = true };
                copy.CopyFromSelectors.Add(new ExpansionTarget { Friendly = false });
                var entry = new CardEffectAction { HashId = -1, Action = copy };
                entry.Selectors.Add(new CardConditionals.CardRoleType { Role = CardRole.Self,Check = CardConditionals.CardRoleType.Requirement.Is });
                effect.CardActions.Add(entry);
            }
            var group = new EffectGroup(); group.Effects.Add(effect);
            if (id == -462299413) card.TriggeredEffects.Add(new TriggeredEffect { TriggerType = EffectTriggerType.Summon,
                SortOrder = TriggerSortOrder.EndOfActiveQueue, ClientTargetingType = PlayTargetingType.NoTarget,EffectGroup = group });
            else card.ActionPlayInformation.Add(new PlayCardAbility { PlayTargetingType = PlayTargetingType.NoTarget,EffectGroup = group });
            return true;
        }
        static void ExpansionArea(Effect effect,ICardAction action,ICardConditionalExpression selector)
        { var entry = new CardEffectAction { HashId = -1,Action = action }; entry.Selectors.Add(selector); effect.CardActions.Add(entry); }
        sealed class ExpansionTarget : ICardConditionalExpression
        {
            public bool Friendly, Cemetery;
            public bool Evaluate(ServerCard card,CardConditionalArguments cards,PlayerConditionalArguments players,TriggerConditionalArguments trigger)
            {
                return card != null && cards.SelfCard != null && card.HasSubtype(CardSubtype.Creature) &&
                    (card.OwnerNetId == cards.SelfCard.OwnerNetId) == Friendly &&
                    card.CurrentGameZoneName == (Cemetery ? GameZoneName.Cemetery : GameZoneName.CreatureBoard) &&
                    (Cemetery || !card.IsDying());
            }
        }
        sealed class DismantleTarget : ICardConditionalExpression
        {
            public bool Evaluate(ServerCard card,CardConditionalArguments cards,PlayerConditionalArguments players,TriggerConditionalArguments trigger)
            {
                return card != null && cards.SelfCard != null && card.OwnerNetId != cards.SelfCard.OwnerNetId && !card.IsDying() &&
                    (card.CurrentGameZoneName == GameZoneName.SupportBoard ||
                     (card.CurrentGameZoneName == GameZoneName.CreatureBoard && card.HasSubtype(CardSubtype.Neutral)));
            }
        }
        sealed class JourneyReturn : ICardAction
        {
            public CardFxAction GetFxActionType() { return CardFxAction.ActionMoveCard; }
            public void Execute(CardActionArguments args)
            {
                // Snapshot selected discard creatures; never buff unrelated cards already in deck.
                var selected = new List<ServerCard>(args.ValidTargetList);
                new CardActions.ReturnToDeck { InsertPosition = ShuffledPosition() }.Execute(args);
                var moved = selected.FindAll(c=>c.CurrentGameZoneName == GameZoneName.Deck && c.OwnerNetId == args.CardArguments.SelfCard.OwnerNetId);
                if (moved.Count == 0) return;
                var buff = new CardActions.Buff();
                foreach (var attribute in new[] {AttributeType.Attack,AttributeType.MaxHealth})
                    buff.Enchantment.AttributeModifications.Add(new EnchantmentAttributeModification(EnchantmentAttributeModificationType.Add,attribute,5));
                buff.Execute(new CardActionArguments(args.TriggerGameState,moved,args.CardArguments,args.PlayerArguments,
                    args.TriggerArguments,args.CardArguments.SelfCard.FindOverrideOrDefaultAssetHash(CardFxAction.Buff),true));
            }
        }
    }
}
