using System;
using SPTESL;
namespace NyxTESLOffline
{
    // Auxiliary compositions checked against original retail descriptions.
    internal static partial class ReferenceSummonRules
    {
        internal static readonly int[] CardIds = { -737824156, -1077442028, -1661672729,
            -1432506315, 820258389, -1955645154, 848305548, 1129542058, -2048305816, -1299948782 };
        internal static void Apply(GameConfiguration config)
        {
            ResetDiscounts();
            foreach (int id in CardIds) {
                var card = config.FindCardById(id);
                if (card != null && Install(card)) Plugin.Log("reference summon recovery: " + id);
            }
        }
        internal static bool Install(Card card)
        {
            int id = card.ResourceId.Id;
            if (Array.IndexOf(CardIds, id) < 0 || card.TriggeredEffects.Count != 0 ||
                card.ActionPlayInformation.Count != 0 || card.Auras.Count != 0 || card.ActivatedAbility.IsValidAbility()) return false;
            if (InstallCandidate(card)) return true;
            var effect = new Effect(); var group = new EffectGroup(); group.Effects.Add(effect);
            var trigger = new TriggeredEffect { TriggerType = EffectTriggerType.Summon,
                SortOrder = TriggerSortOrder.EndOfActiveQueue, EffectGroup = group,
                ClientTargetingType = id == -1661672729 ? PlayTargetingType.RequiredTarget : PlayTargetingType.NoTarget };
            if (id == -1077442028) {
                var action = new PlayerEffectAction { HashId = -1, Action = new PlayerActions.CreateToken {
                    CardReference = new EditorCardReference(-92312088), NumTokens = new ActionValueData(1),
                    CreateInZoneType = GameZoneType.Board, InsertPosition = new InsertPosition {
                        Type = InsertPosition.LaneChoiceType.Relative, Lhs = CardRole.Self,
                        Comparison = InsertPosition.PositionComparison.SameLane, InsertionType = CardInsertionType.Bottom
                    }
                }};
                action.Selectors.Add(new PlayerConditionals.PlayerRoleType { Role = PlayerRole.Self,
                    Check = PlayerConditionals.PlayerRoleType.Requirement.Is });
                effect.PlayerActions.Add(action);
            } else {
                var action = new CardEffectAction { HashId = -1 };
                if (id == -737824156) {
                    action.Action = new CardActions.ReturnToHand();
                    action.Selectors.Add(new MulticolorInOwnDeck());
                    action.SelectorSort.Method = CardListSort.SortMethod.Random;
                    action.SelectorSort.NumToReturn = new ActionValueData(1);
                } else {
                    action.Action = new CardActions.Damage { HealthLoss = new ActionValueData(2), DamageSource = CardRole.Self };
                    var selector = new DecisionConditionalExpression { DecisionType = DecisionType.Card };
                    selector.CardConditionalExpressions.Add(new FriendlyCreature()); trigger.TargetSelectors.Add(selector);
                    action.Selectors.Add(new CardConditionals.CardRoleType { Role = CardRole.DecisionTarget,
                        Check = CardConditionals.CardRoleType.Requirement.Is });
                    action.Selectors.Add(new FriendlyCreature());
                }
                effect.CardActions.Add(action);
            }
            card.TriggeredEffects.Add(trigger); return true;
        }
        sealed class FriendlyCreature : ICardConditionalExpression
        {
            public bool Evaluate(ServerCard card, CardConditionalArguments cards, PlayerConditionalArguments players, TriggerConditionalArguments trigger)
            {
                return card != null && cards.SelfCard != null && card.OwnerNetId == cards.SelfCard.OwnerNetId &&
                    card.CurrentGameZoneName == GameZoneName.CreatureBoard && !card.IsDying();
            }
        }
        sealed class MulticolorInOwnDeck : ICardConditionalExpression
        {
            public bool Evaluate(ServerCard card, CardConditionalArguments cards, PlayerConditionalArguments players, TriggerConditionalArguments trigger)
            {
                if (card == null || cards.SelfCard == null || card.OwnerNetId != cards.SelfCard.OwnerNetId || card.CurrentGameZoneName != GameZoneName.Deck) return false;
                int colors = 0;
                foreach (var color in new[] { CardSubtype.Strength, CardSubtype.Intelligence, CardSubtype.Willpower, CardSubtype.Agility, CardSubtype.Endurance })
                    if (card.HasSubtype(color)) ++colors;
                return colors > 1;
            }
        }
    }
}
