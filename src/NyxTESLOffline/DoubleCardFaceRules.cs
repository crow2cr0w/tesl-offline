using System;
using System.Collections.Generic;
using SPTESL;

namespace NyxTESLOffline
{
    // Compositions restored for action faces of Isle of Madness doubles.
    // The face text and IDs come from the retail GameConfiguration; all
    // effects use retail CardActions so their history and FX remain native.
    internal static class DoubleCardFaceRules
    {
        internal static readonly int[] CardIds = { -1445693535, 1790691369, -89673891 };

        internal static void Apply(GameConfiguration config)
        {
            foreach (int id in CardIds)
            {
                Card card = config.FindCardById(id);
                if (card != null && Install(card))
                    Plugin.Log("double-card action face composition restored: " + id);
            }
        }

        internal static bool Install(Card card)
        {
            int id = card.ResourceId.Id;
            if (Array.IndexOf(CardIds, id) < 0 || card.TriggeredEffects.Count != 0 ||
                card.ActionPlayInformation.Count != 0 || card.Auras.Count != 0 ||
                card.ActivatedAbility.IsValidAbility())
                return false;

            ICardAction action;
            bool friendlyOnly = id == -89673891;
            if (id == -1445693535)
                action = new CardActions.Heal { FullHeal = true };
            else if (id == 1790691369)
            {
                var enchantment = new Enchantment { ExpiresAtEndOfTurn = true };
                enchantment.AttributeModifications.Add(new EnchantmentAttributeModification(
                    EnchantmentAttributeModificationType.Set, AttributeType.Deathtouch, 1));
                action = new CardActions.Buff { Enchantment = enchantment };
            }
            else
                action = new CardActions.GiveKeyword { RandomKeyword = true };

            var effect = new Effect();
            var entry = new CardEffectAction { HashId = -1, Action = action };
            entry.Selectors.Add(new CardConditionals.CardRoleType {
                Role = CardRole.DecisionTarget,
                Check = CardConditionals.CardRoleType.Requirement.Is
            });
            effect.CardActions.Add(entry);
            var group = new EffectGroup();
            group.Effects.Add(effect);

            var selector = new DecisionConditionalExpression { DecisionType = DecisionType.Card };
            selector.CardConditionalExpressions.Add(new CreatureTarget());
            if (friendlyOnly)
                selector.CardConditionalExpressions.Add(new CardConditionals.Relationship {
                    OtherCard = CardRole.Self,
                    Are = CardConditionals.Relationship.RelationshipType.Friends
                });
            card.ActionPlayInformation.Add(new PlayCardAbility {
                PlayTargetingType = PlayTargetingType.RequiredTarget,
                EffectGroup = group,
                TargetSelectors = { selector }
            });
            return true;
        }

        sealed class CreatureTarget : ICardConditionalExpression
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
