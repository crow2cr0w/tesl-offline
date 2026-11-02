using System;
using SPTESL;
namespace NyxTESLOffline
{
    internal static partial class RecoveredCardRules
    {
        internal static readonly int[] NextPrimitiveIds = {
            -520695966, -967450522, -463613369, 2095683028, 6794648,
            294653485, 1831263143, 1594345279, -1975806074
        };
        static bool IsNextPrimitiveRule(int id) { return Array.IndexOf(NextPrimitiveIds, id) >= 0; }
        static bool InstallNextPrimitiveRule(Card card)
        {
            if (!CanInstall(card) || card.Auras.Count != 0) return false;
            int id = card.ResourceId.Id;
            if (id == -520695966) return InstallTargetedAction(card, new CardActions.Ward(), TargetSelector(false, false, false));
            if (id == 1831263143 || id == 1594345279) {
                var selector = TargetSelector(false, false, false);
                if (id == 1594345279) selector.CardConditionalExpressions.Add(new CardConditionals.AttributeValue {
                    AttributeName = AttributeType.Attack, Is = CardConditionals.AttributeValue.ComparisonType.EqualTo,
                    Value = new ConditionalValueData(0) });
                return InstallTargetedAction(card, new CardActions.Banish(), selector);
            }
            if (id == 6794648) return InstallTargetedAction(card, NativeDebuff(2), TargetSelector(false, false, false));
            if (id == -967450522 || id == -463613369 || id == 2095683028)
                return InstallTargetedSummon(card, NativeDebuff(1), TargetSelector(false, false, false));
            var entry = new CardEffectAction { HashId = -1,
                Action = id == -1975806074 ? (ICardAction)new CardActions.Banish() : NativeDebuff(1) };
            entry.Selectors.Add(new NextPrimitiveArea { Discard = id == -1975806074 });
            var effect = new Effect(); effect.CardActions.Add(entry);
            var group = new EffectGroup(); group.Effects.Add(effect);
            card.TriggeredEffects.Add(new TriggeredEffect { TriggerType = EffectTriggerType.Summon,
                SortOrder = TriggerSortOrder.EndOfActiveQueue, ClientTargetingType = PlayTargetingType.NoTarget,
                EffectGroup = group });
            return true;
        }
        static CardActions.Debuff NativeDebuff(int amount)
        {
            var action = new CardActions.Debuff();
            foreach (var attribute in new[] {AttributeType.Attack, AttributeType.MaxHealth})
                action.Enchantment.AttributeModifications.Add(new EnchantmentAttributeModification(
                    EnchantmentAttributeModificationType.Add, attribute, -amount));
            return action;
        }
        sealed class NextPrimitiveArea : ICardConditionalExpression
        {
            public bool Discard;
            public bool Evaluate(ServerCard card, CardConditionalArguments cards, PlayerConditionalArguments players, TriggerConditionalArguments trigger)
            {
                if (card == null || cards.SelfCard == null || card.OwnerNetId == cards.SelfCard.OwnerNetId) return false;
                if (Discard) return card.CurrentGameZoneName == GameZoneName.Cemetery;
                return card.CurrentGameZoneName == GameZoneName.CreatureBoard && !card.IsDying() &&
                    card.CurrentLaneIndex == cards.SelfCard.CurrentLaneIndex;
            }
        }
    }
}
