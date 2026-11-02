using System;
using System.Collections.Generic;
using SPTESL;

namespace NyxTESLOffline
{
    internal static partial class SharedStatFamilyRules
    {
        // Unity's retail .NET 2 profile has no ConditionalWeakTable.
        // Receipts last only until Summon resolves, and are cleared at new-match setup.
        static readonly Dictionary<ServerCard, int> PaidCosts = new Dictionary<ServerCard, int>();
        internal static void ResetRuntime() { PaidCosts.Clear(); }
        static bool InstallAdvanced(Card card)
        {
            int id = card.ResourceId.Id;
            if (id == 982836723) {
                // Continuous self aura, rebuilt at the existing authoritative aura checkpoint.
                var aura = new Aura(); var stats = new AuraEnchantment();
                stats.Selectors.Add(new CardConditionals.CardRoleType { Role = CardRole.Self, Check = CardConditionals.CardRoleType.Requirement.Is });
                int health = 0;
                foreach (var attribute in card.EntityAttributes) if (attribute.Name == AttributeType.MaxHealth) health = ((IntAttribute)attribute).Value;
                stats.Enchantment.AttributeModifications.Add(new EnchantmentAttributeModification(
                    EnchantmentAttributeModificationType.Set, AttributeType.Attack, health));
                aura.EnchantmentsToApply.Add(stats); card.Auras.Add(aura);
                return true;
            }
            if (id == -44117704) {
                var effect = new Effect();
                var prepare = new CardEffectAction { HashId = -1, Action = new CardActions.PrepareChooseFromGraveyard() };
                prepare.Selectors.Add(new CemeteryCreature()); effect.CardActions.Add(prepare);
                var group = new EffectGroup(); group.Effects.Add(effect);
                var trigger = new TriggeredEffect { TriggerType = EffectTriggerType.Pilfer,
                    SortOrder = TriggerSortOrder.EndOfActiveQueue, ClientTargetingType = PlayTargetingType.NoTarget,
                    CustomUiType = CustomCardUiType.TargetCemetery, EffectGroup = group };
                trigger.TriggerCardRequirements.Add(Requirement(new RuleCondition { RequireBoard = true }));
                var chosenGroup = new EffectGroup(); var chosen = new Effect(); chosenGroup.Effects.Add(chosen);
                chosen.ExecuteCardRequirements.Add(new CardRequirements.IsDyingRequirement {
                    LhsCard = CardRole.Self, Conditional = new CardConditionals.IsDying { Dying = CardConditionals.IsDying.Requirement.IsNot }
                });
                foreach (var attribute in new[] { AttributeType.Attack, AttributeType.MaxHealth })
                    Add(chosen, new CardActions.BuffAttributeToValue { AttributeName = attribute,
                        ModificationType = EnchantmentAttributeModificationType.Add,
                        Value = new ActionValueData(ActionValueMethod.ValueOfCardAttribute) {
                            AttributeName = attribute, CardRole = CardRole.CustomUITarget
                        }
                    }, Targets.Self);
                var consume = new CardEffectAction { HashId = -1, Action = new CardActions.Consume() };
                consume.Selectors.Add(new CardConditionals.CardRoleType { Role = CardRole.CustomUITarget,
                    Check = CardConditionals.CardRoleType.Requirement.Is });
                consume.Selectors.Add(new CemeteryCreature()); chosen.CardActions.Add(consume);
                trigger.CustomUiEffectGroups.Add(chosenGroup); card.TriggeredEffects.Add(trigger);
                return true;
            }
            if (id != -1023199177 && id != 251949038 && id != -76939763 && id != 295816744 && id != 1767221439) return false;
            var advancedEffect = new Effect(); Add(advancedEffect, new AdvancedStats { CardId = id }, Targets.Self);
            var advancedGroup = new EffectGroup(); advancedGroup.Effects.Add(advancedEffect);
            if (card.HasSubtype(CardSubtype.Spell)) {
                var ability = new PlayCardAbility {
                    PlayTargetingType = id == 251949038 ? PlayTargetingType.RequiredTarget : PlayTargetingType.NoTarget, EffectGroup = advancedGroup
                };
                if (id == 251949038) {
                    var lane = new DecisionConditionalExpression { DecisionType = DecisionType.Lane };
                    lane.LaneConditionalExpressions.Add(new FriendlyOpenLane());
                    ability.TargetSelectors.Add(lane);
                }
                card.ActionPlayInformation.Add(ability);
            }
            else {
                var trigger = new TriggeredEffect {
                    TriggerType = id == 1767221439 ? EffectTriggerType.WhenCardEntersBoard : EffectTriggerType.Summon,
                    SortOrder = TriggerSortOrder.EndOfActiveQueue, ClientTargetingType = PlayTargetingType.NoTarget, EffectGroup = advancedGroup
                };
                trigger.TriggerCardRequirements.Add(Requirement(new RuleCondition { RequireBoard = true }));
                if (id == 1767221439) trigger.TriggerCardRequirements.Add(new CardRequirements.EventRoleRequirement {
                    LhsCard = CardRole.Self, Conditional = new CardConditionals.CardRoleType {
                        Role = CardRole.Inciter, Check = CardConditionals.CardRoleType.Requirement.Is
                    }
                });
                card.TriggeredEffects.Add(trigger);
            }
            return true;
        }
        sealed class CemeteryCreature : ICardConditionalExpression
        {
            public bool Evaluate(ServerCard card, CardConditionalArguments cards, PlayerConditionalArguments players, TriggerConditionalArguments trigger)
            {
                return card != null && cards.SelfCard != null && !cards.SelfCard.IsDying() && card.OwnerNetId == cards.SelfCard.OwnerNetId &&
                    card.CurrentGameZoneName == GameZoneName.Cemetery && card.HasSubtype(CardSubtype.Creature);
            }
        }
        sealed class FriendlyOpenLane : ILaneConditionalExpression
        {
            public bool Evaluate(LaneIdentifier lane, LaneConditionalArguments lanes, CardConditionalArguments cards, PlayerConditionalArguments players)
            {
                return cards.SelfCard != null && lane.ZonePlayer == cards.SelfCard.OwnerNetId &&
                    lane.ZoneName == GameZoneName.CreatureBoard &&
                    !lanes.CardCatalog.IsLaneFull(lane.ZoneName, lane.LaneIndex, lane.ZonePlayer);
            }
        }
        internal static int ManaCost(ServerCard card, int normalCost, int available)
        {
            return VariantCardRules.CanonicalId(card.CardId) == -1023199177 ? Math.Max(normalCost, available) : normalCost;
        }
        internal static void RecordPaidCost(ServerCard card, int cost)
        {
            if (VariantCardRules.CanonicalId(card.CardId) != -1023199177) return;
            PaidCosts[card] = cost;
        }
        internal static void RefreshContinuousAuras(ServerEffectUtilities.TriggerGameState state)
        {
            foreach (var card in state.CardCatalog.GatherDynamicCardList()) {
                if (VariantCardRules.CanonicalId(card.CardId) != 982836723 || card.CurrentGameZoneName != GameZoneName.CreatureBoard || card.Silenced || card.IsDying()) continue;
                var aura = card.AuraEnchantments.Find(a => a.SourceCardId == card.CardId && a.SourceId == card.InstanceId);
                if (aura == null) {
                    var enchantment = new Enchantment();
                    enchantment.AttributeModifications.Add(new EnchantmentAttributeModification(
                        EnchantmentAttributeModificationType.Set, AttributeType.Attack, card.CurrentHealth));
                    aura = new EnchantmentInstance(enchantment, 1, card.CardId, card.InstanceId, card);
                    card.AuraEnchantments.Add(aura); card.Enchantments.Add(aura);
                }
                if (card.QueryAttribute(AttributeType.Attack).Value == card.CurrentHealth) continue;
                aura.Enchantment.AttributeModifications[0].Value = card.CurrentHealth;
                // A continuous equality has priority over ordinary power buffs/items.
                card.Enchantments.Remove(aura); card.Enchantments.Add(aura);
                card.RecalculateEnchantmentsWithTriggers(state, card);
                HistoryLoggingUtilities.LogFullCardRefresh(state.Log, state.CardCatalog, card, false);
            }
        }
        sealed class AdvancedStats : ICardAction
        {
            public int CardId;
            public CardFxAction GetFxActionType() { return CardFxAction.Buff; }
            public void Execute(CardActionArguments args)
            {
                var source = args.CardArguments.SelfCard;
                var state = args.TriggerGameState;
                var owner = state.Players.Find(p => p.NetId == source.OwnerNetId);
                int value = owner.QueryAttribute(RareAttributeType.SkeeverInfestationCount).Value;
                if (CardId == -1023199177) {
                    int paid;
                    if (!PaidCosts.TryGetValue(source, out paid)) return;
                    value = paid; PaidCosts.Remove(source);
                } else if (CardId == 251949038) {
                    var pool = new ActionValueData(ActionValueMethod.HighestAttributeValueFromPool) { AttributeName = AttributeType.Attack };
                    pool.Selectors.Add(new TargetCondition { Target = Targets.FriendlyBoard });
                    value = ActionValueData.EvaluateValue(pool, args.CardArguments, args.PlayerArguments, args.TriggerArguments, state.RandomGenerator);
                } else if (CardId != 1767221439) {
                    value++;
                    owner.SetRareAttribute(RareAttributeType.SkeeverInfestationCount, value);
                }
                var stats = new Enchantment { ResetWoundedState = true, PermanentExpiresOnLeavingBoard = true };
                foreach (var attribute in new[] { AttributeType.Attack, AttributeType.MaxHealth })
                    stats.AttributeModifications.Add(new EnchantmentAttributeModification(EnchantmentAttributeModificationType.Set, attribute, value));
                if (CardId == -1023199177 || CardId == 1767221439) {
                    new CardActions.SetOnBoardStats { Enchantment = stats }.Execute(args);
                    return;
                }
                int tokenId = CardId == 251949038 ? 1609998338 : 1767221439;
                int firstLane = CardId == 251949038 && args.CardArguments.LaneDecision.IsValid() ? args.CardArguments.LaneDecision.LaneIndex : 0;
                for (int lane = firstLane; lane <= (CardId == 251949038 ? firstLane : 1); lane++) {
                    var position = state.CardCatalog.FindInsertionPosition(owner.NetId, GameZoneName.CreatureBoard, lane, CardInsertionType.Bottom);
                    PlayerActions.CreateToken.StaticExecuteWithBuffs(state, source, GameManager.Instance.Config.FindCardById(tokenId),
                        UniqueResourceId.GetInvalid(), source.Premium, GameZoneType.Board, position, owner.NetId, stats, null,
                        1, source.FindOverrideOrDefaultAssetHash(CardFxAction.CreateToken), false, source);
                }
                if (CardId != 251949038) {
                    for (int copy = 0; copy < 2; copy++) {
                    var position = state.CardCatalog.FindInsertionPosition(owner.NetId, GameZoneName.Deck, 0, CardInsertionType.Random);
                    PlayerActions.CreateToken.StaticExecuteWithBuffs(state, source, GameManager.Instance.Config.FindCardById(source.CardId),
                        UniqueResourceId.GetInvalid(), source.Premium, GameZoneType.Deck, position, owner.NetId, null, null,
                        1, source.FindOverrideOrDefaultAssetHash(CardFxAction.CreateToken), false, source);
                    }
                }
            }
        }
    }
}
