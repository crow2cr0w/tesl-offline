using System.Collections.Generic;
using SPTESL;
namespace NyxTESLOffline
{
    internal static partial class ReferenceSummonRules
    {
        // Reconstructed one-shot player benefit; retail enchantments expose the
        // current cost to both client and AI. Not just an invisible payment hook.
        sealed class Discount
        {
            public ServerCard Source;
            public int Amount;
            public Dictionary<ServerCard, EnchantmentInstance> Applied = new Dictionary<ServerCard, EnchantmentInstance>();
        }
        static readonly Dictionary<PlayerUniqueId, Discount> Discounts = new Dictionary<PlayerUniqueId, Discount>();
        internal static void ResetDiscounts() { Discounts.Clear(); }
        static bool InstallMarshal(Card card)
        {
            var group = new EffectGroup(); var effect = new Effect(); group.Effects.Add(effect);
            effect.ExecutePlayerRequirements.Add(new PlayerRequirements.PlayerHealthComparisonRequirement {
                LhsPlayer = PlayerRole.Self, Conditional = new PlayerConditionals.HealthComparison {
                    Is = PlayerConditionals.HealthComparison.ComparisonType.GreaterThan, CompareTo = PlayerRole.Opponent
                }
            });
            var action = new PlayerEffectAction { HashId = -1, Action = new GrantDiscount() };
            action.Selectors.Add(new PlayerConditionals.PlayerRoleType { Role = PlayerRole.Self,
                Check = PlayerConditionals.PlayerRoleType.Requirement.Is }); effect.PlayerActions.Add(action);
            var trigger = new TriggeredEffect { TriggerType = EffectTriggerType.Summon,
                SortOrder = TriggerSortOrder.EndOfActiveQueue, ClientTargetingType = PlayTargetingType.NoTarget, EffectGroup = group };
            trigger.TriggerCardRequirements.Add(new CandidateRequirement { LhsCard = CardRole.Self,
                Conditional = new CandidateSource { Id = -2048305816 } });
            card.TriggeredEffects.Add(trigger); return true;
        }
        sealed class GrantDiscount : IPlayerAction
        {
            public CardFxAction GetFxActionType() { return CardFxAction.Buff; }
            public void Execute(PlayerActionArguments args)
            {
                foreach (var player in args.ValidTargetList) {
                    Discount discount;
                    if (!Discounts.TryGetValue(player.NetId, out discount)) {
                        discount = new Discount { Source = args.CardArguments.SelfCard };
                        Discounts.Add(player.NetId, discount);
                    }
                    discount.Amount += 6;
                }
                RefreshDiscounts(args.TriggerGameState);
            }
        }
        static void RemoveDiscount(ServerEffectUtilities.TriggerGameState state, ServerCard card, EnchantmentInstance enchantment)
        {
            if (!card.Enchantments.Remove(enchantment)) return;
            card.RecalculateEnchantmentsWithTriggers(state, card);
            HistoryLoggingUtilities.LogFullCardRefresh(state.Log, state.CardCatalog, card, false);
        }
        internal static void RefreshDiscounts(ServerEffectUtilities.TriggerGameState state)
        {
            foreach (var entry in Discounts) {
                var discount = entry.Value;
                foreach (var card in new List<ServerCard>(discount.Applied.Keys)) {
                    var enchantment = discount.Applied[card];
                    if (card.CurrentGameZoneName != GameZoneName.Hand || card.OwnerNetId != entry.Key ||
                        !card.Enchantments.Contains(enchantment)) {
                        RemoveDiscount(state, card, enchantment); discount.Applied.Remove(card);
                    }
                }
                foreach (var card in state.CardCatalog.GatherCardsInZone(c => true, entry.Key, GameZoneName.Hand)) {
                    EnchantmentInstance instance;
                    if (discount.Applied.TryGetValue(card, out instance)) {
                        if (instance.Enchantment.AttributeModifications[0].Value == -discount.Amount) continue;
                        instance.Enchantment.AttributeModifications[0].Value = -discount.Amount;
                    } else {
                        var enchantment = new Enchantment();
                        enchantment.AttributeModifications.Add(new EnchantmentAttributeModification(
                            EnchantmentAttributeModificationType.Add, AttributeType.ManaCost, -discount.Amount));
                        instance = new EnchantmentInstance(enchantment, 1, discount.Source.CardId, discount.Source.InstanceId, card);
                        card.Enchantments.Add(instance); discount.Applied.Add(card, instance);
                    }
                    card.RecalculateEnchantmentsWithTriggers(state, discount.Source);
                    HistoryLoggingUtilities.LogFullCardRefresh(state.Log, state.CardCatalog, card, false);
                }
            }
        }
        internal static void ConsumeDiscount(ServerEffectUtilities.TriggerGameState state, PlayerUniqueId owner)
        {
            Discount discount;
            if (!Discounts.TryGetValue(owner, out discount)) return;
            Discounts.Remove(owner);
            foreach (var applied in discount.Applied) RemoveDiscount(state, applied.Key, applied.Value);
        }
        internal static void ExpireDiscounts(ServerEffectUtilities.TriggerGameState state)
        {
            // "This turn" ends with the active turn, including an opponent-turn summon.
            foreach (var owner in new List<PlayerUniqueId>(Discounts.Keys)) ConsumeDiscount(state, owner);
        }
    }
}
