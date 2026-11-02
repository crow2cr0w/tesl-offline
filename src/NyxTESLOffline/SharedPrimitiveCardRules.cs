using System;
using SPTESL;

namespace NyxTESLOffline
{
    // Reconstructed compositions, not a recovered original server rule graph.
    // Keep retail card data and let its executors own attributes/history/FX.
    internal static class SharedPrimitiveCardRules
    {
        internal static readonly int[] CardIds = {
            1470504272, -553062902, 1978118591, -1237441152
        };

        internal static void Apply(GameConfiguration config)
        {
            SharedStatFamilyRules.ResetRuntime();
            foreach (int id in CardIds) {
                Card card = config.FindCardById(id);
                if (card != null && Install(card)) Plugin.Log("shared primitive recovery: " + id);
            }
            foreach (int id in SharedStatFamilyRules.CardIds) {
                Card card = config.FindCardById(id);
                if (card != null && SharedStatFamilyRules.Install(card)) Plugin.Log("shared stat family recovery: " + id);
            }
        }

        internal static bool Install(Card card)
        {
            int id = card.ResourceId.Id;
            if (Array.IndexOf(CardIds, id) < 0 || card.TriggeredEffects.Count != 0 ||
                card.ActionPlayInformation.Count != 0 || card.Auras.Count != 0 ||
                card.ActivatedAbility.IsValidAbility()) return false;

            bool self = id == 1978118591 || id == -1237441152;
            var effect = new Effect();
            if (id == 1470504272) // Hatchery Meddler: swap a creature's stats, including self.
                Add(effect, new CardActions.SwapCombatStats(), self);
            else if (id == -1237441152) // Blades Stalwart: Slay adds CURRENT health to power.
                Add(effect, new CardActions.BuffAttributeByHealth {
                    ToAttributeName = AttributeType.Attack,
                    ModificationType = EnchantmentAttributeModificationType.Add
                }, self);
            else foreach (var attribute in new[] { AttributeType.Attack, AttributeType.MaxHealth }) {
                // Multiplication stores current health when wounded (retail EnchantmentInstance).
                Add(effect, new CardActions.BuffAttributeToValue {
                    AttributeName = attribute, Value = new ActionValueData(2),
                    ModificationType = EnchantmentAttributeModificationType.Multiply
                }, self);
            }
            var group = new EffectGroup(); group.Effects.Add(effect);
            var trigger = new TriggeredEffect {
                TriggerType = id == 1978118591 ? EffectTriggerType.Pilfer :
                    id == -1237441152 ? EffectTriggerType.Slay : EffectTriggerType.Summon,
                SortOrder = TriggerSortOrder.EndOfActiveQueue,
                ClientTargetingType = self ? PlayTargetingType.NoTarget : PlayTargetingType.RequiredTarget,
                EffectGroup = group
            };
            if (!self) {
                var selector = new DecisionConditionalExpression { DecisionType = DecisionType.Card };
                selector.CardConditionalExpressions.Add(new CreatureOnBoard());
                trigger.TargetSelectors.Add(selector);
            }
            card.TriggeredEffects.Add(trigger);
            return true;
        }

        private static void Add(Effect effect, ICardAction action, bool self)
        {
            var entry = new CardEffectAction { HashId = -1, Action = action };
            entry.Selectors.Add(new CardConditionals.CardRoleType {
                Role = self ? CardRole.Self : CardRole.DecisionTarget,
                Check = CardConditionals.CardRoleType.Requirement.Is
            });
            entry.Selectors.Add(new CardConditionals.IsDying { Dying = CardConditionals.IsDying.Requirement.IsNot });
            effect.CardActions.Add(entry);
        }

        private sealed class CreatureOnBoard : ICardConditionalExpression
        {
            public bool Evaluate(ServerCard card, CardConditionalArguments cards,
                PlayerConditionalArguments players, TriggerConditionalArguments trigger)
            {
                return card != null && card.CurrentGameZoneName == GameZoneName.CreatureBoard &&
                    card.HasSubtype(CardSubtype.Creature) && !card.IsDying();
            }
        }
    }
}
