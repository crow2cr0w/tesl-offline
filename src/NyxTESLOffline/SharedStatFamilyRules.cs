using System;
using System.Collections.Generic;
using SPTESL;

namespace NyxTESLOffline
{
    // Explicit original compositions over shared selectors, conditions and executors.
    internal static partial class SharedStatFamilyRules
    {
        internal static readonly int[] CardIds = {
            -1399661641, -1563068710, -871299414, 1646495852, -241860300,
            -2074393999, 1287011430, -389657211, -1787424156, 1399500856,
            782334488, -1308684010, -1686480098, -1244835424, -1588632902,
            -1235178647, -266284141, 723060903, -1987707763, 1021793029,
            982836723, -1023199177, 251949038, -76939763, 295816744, 1767221439, -44117704
        };
        enum Targets { Decision, Self, FriendlyBoard, EnemyLane, AllBoard, Wielder, Inciter }
        internal static bool Install(Card card)
        {
            int id = card.ResourceId.Id;
            if (Array.IndexOf(CardIds, id) < 0 || card.TriggeredEffects.Count != 0 ||
                card.ActionPlayInformation.Count != 0 || card.Auras.Count != 0 || card.ActivatedAbility.IsValidAbility()) return false;
            if (InstallAdvanced(card)) return true;
            Targets targets = Targets.Decision;
            EffectTriggerType triggerType = EffectTriggerType.Summon;
            bool spell = card.HasSubtype(CardSubtype.Spell);
            if (id == -1399661641) { targets = Targets.Self; triggerType = EffectTriggerType.WhenCardWardIsBroken; }
            if (id == -1563068710) { targets = Targets.Self; triggerType = EffectTriggerType.Expertise; }
            if (id == -871299414) { targets = Targets.FriendlyBoard; triggerType = EffectTriggerType.Veteran; }
            if (id == -241860300 || id == 1399500856) targets = Targets.EnemyLane;
            if (id == 1399500856 || id == 782334488) triggerType = EffectTriggerType.WhenPlayerTurnStarts;
            if (id == 782334488) targets = Targets.FriendlyBoard;
            if (id == -1686480098) targets = Targets.AllBoard;
            if (id == -1244835424) targets = Targets.Self;
            if (id == -1787424156 || id == -1588632902) targets = Targets.Wielder;
            if (id == 723060903 || id == 1021793029) { targets = Targets.Inciter; triggerType = EffectTriggerType.WhenCardEntersBoard; }
            var group = new EffectGroup();
            var effect = new Effect(); group.Effects.Add(effect);
            if (id == -1244835424) {
                group.Effects.Clear();
                foreach (var attribute in new[] { AttributeType.Attack, AttributeType.MaxHealth }) {
                    var conditional = new Effect();
                    conditional.ExecuteCardRequirements.Add(Requirement(new RuleCondition {
                        Color = attribute == AttributeType.Attack ? CardSubtype.Strength : CardSubtype.Willpower,
                        CheckColor = true
                    }));
                    Add(conditional, Multiply(attribute), targets);
                    group.Effects.Add(conditional);
                }
            } else if (id == -1686480098) {
                // One board-wide resolution, not one generic buff animation per creature.
                var enchantment = new Enchantment { ResetWoundedState = true, PermanentExpiresOnLeavingBoard = true };
                foreach (var attribute in new[] {AttributeType.Attack,AttributeType.MaxHealth})
                    enchantment.AttributeModifications.Add(new EnchantmentAttributeModification(
                        EnchantmentAttributeModificationType.Set,attribute,1));
                Add(effect,new CardActions.SetOnBoardStats { Enchantment = enchantment,SimultaneousFx = true },targets);
                var retailFx = card.CardFxRelations.Find(r=>r.FXAction == CardFxAction.TriggeredEffect && r.EffectTriggerType == EffectTriggerType.ActionEffect);
                if (retailFx != null) effect.CardActions[0].HashId = retailFx.AssetHash;
            } else if (id == -1787424156 || id == -1588632902) Add(effect, new WielderStats(), targets);
            else if (id == -1399661641 || id == -1563068710 || id == 1021793029) {
                var attack = Multiply(AttributeType.Attack);
                Add(effect, attack, targets);
                if (id != -1399661641) {
                    var health = Multiply(AttributeType.MaxHealth);
                    Add(effect, health, targets);
                }
            } else if (id == -871299414 || id == 723060903)
                Add(effect, new CardActions.BuffAttributeByHealth {
                    ToAttributeName = AttributeType.Attack, ModificationType = EnchantmentAttributeModificationType.Set
                }, targets);
            else Add(effect, new SetStats { CardId = id }, targets);

            if (spell) {
                var ability = new PlayCardAbility { EffectGroup = group,
                    PlayTargetingType = targets == Targets.Decision ? PlayTargetingType.RequiredTarget : PlayTargetingType.NoTarget };
                if (targets == Targets.Decision) ability.TargetSelectors.Add(DecisionSelector(id));
                card.ActionPlayInformation.Add(ability);
            } else {
                var trigger = new TriggeredEffect {
                    TriggerType = triggerType, SortOrder = TriggerSortOrder.EndOfActiveQueue, EffectGroup = group,
                    ClientTargetingType = targets == Targets.Decision ? PlayTargetingType.RequiredTarget : PlayTargetingType.NoTarget
                };
                trigger.TriggerCardRequirements.Add(Requirement(new RuleCondition { RequireBoard = true }));
                if (targets == Targets.Decision) trigger.TargetSelectors.Add(DecisionSelector(id));
                if (id == -1399661641 || id == 723060903 || id == 1021793029 || id == 782334488)
                    trigger.TriggerCardRequirements.Add(Requirement(new RuleCondition { CardId = id, CheckEvent = true }));
                if (id == 1646495852) {
                    trigger.BeforeTargetingCardRequirements.Add(Requirement(new RuleCondition { CardId = id }));
                    effect.ExecuteCardRequirements.Add(Requirement(new RuleCondition { CardId = id }));
                }
                card.TriggeredEffects.Add(trigger);
            }
            // Native item enchantment ties this restriction to the item, so unequip removes it.
            if (id == -1588632902) {
                var ability = new PlayCardAbility { PlayTargetingType = PlayTargetingType.NoTarget };
                var itemEffect = new Effect(); ability.EffectGroup.Effects.Add(itemEffect);
                var restriction = new CardActions.Buff();
                restriction.Enchantment.BasicItemEnchantment = true;
                restriction.Enchantment.AttributeModifications.Add(new EnchantmentAttributeModification(
                    EnchantmentAttributeModificationType.Set, AttributeType.CanOnlyAttackCreatures, 1));
                Add(itemEffect, restriction, Targets.Wielder);
                card.ActionPlayInformation.Add(ability);
            }
            return true;
        }

        static CardActions.BuffAttributeToValue Multiply(AttributeType attribute)
        {
            return new CardActions.BuffAttributeToValue { AttributeName = attribute,
                ModificationType = EnchantmentAttributeModificationType.Multiply, Value = new ActionValueData(2) };
        }
        static void Add(Effect effect, ICardAction action, Targets targets)
        {
            var entry = new CardEffectAction { HashId = -1, Action = action };
            entry.Selectors.Add(new TargetCondition { Target = targets });
            effect.CardActions.Add(entry);
        }
        internal static void OnPlayed(ServerEffectUtilities.TriggerGameState state, ServerCard card)
        {
            // Expertise is resolved at the owner's turn end.  The old host
            // armed it only for spells/items/supports, so creature sources
            // such as Master of Thieves never reached the end-turn trigger.
            var player = state.Players.Find(p => p.NetId == card.OwnerNetId);
            if (player != null)
                PlayerActions.SetAttribute.StaticExecute(state.Log, player, AttributeType.ShouldTriggerExpertise, 1);
        }
        internal static void OnAttackCompleted(ServerEffectUtilities.TriggerGameState state, ServerCard card)
        {
            if (card == null || card.IsDying() || card.Silenced ||
                !HasVeteranTrigger(card) ||
                card.QueryAttribute(RareAttributeType.VeteranTracker).Value != 0) return;
            card.SetRareAttribute(RareAttributeType.VeteranTracker, 1);
            HistoryLoggingUtilities.LogFullCardRefresh(state.Log, state.CardCatalog, card, false);
            ServerEffectUtilities.TriggerDirectEffectEvent(state, new ServerEffectUtilities.TriggerEvent(EffectTriggerType.Veteran, card, state.Players));
        }

        internal static bool HasVeteranTrigger(ServerCard card)
        {
            if (card == null)
                return false;
            foreach (TriggeredEffect effect in card.TriggeredEffects)
            {
                if (effect.TriggerType == EffectTriggerType.Veteran ||
                    EffectGroupHasVeteran(effect.EffectGroup) ||
                    EffectGroupHasVeteran(effect.PostCustomUiEffectGroup))
                    return true;
                foreach (EffectGroup group in effect.CustomUiEffectGroups)
                    if (EffectGroupHasVeteran(group))
                        return true;
            }
            return false;
        }

        private static bool EffectGroupHasVeteran(EffectGroup group)
        {
            if (group == null)
                return false;
            foreach (Effect effect in group.Effects)
                foreach (CardEffectAction cardAction in effect.CardActions)
                {
                    CardActions.RunDirectTrigger direct =
                        cardAction.Action as CardActions.RunDirectTrigger;
                    if (direct != null && direct.TriggerType == EffectTriggerType.Veteran)
                        return true;
                }
            return false;
        }
        internal static void OnTurnStarts(ServerEffectUtilities.TriggerGameState state, PlayerState player)
        {
            ServerEffectUtilities.TriggerEffectEvent(state, new ServerEffectUtilities.TriggerEvent(EffectTriggerType.WhenPlayerTurnStarts, player));
        }
        internal static void OnTurnEnds(ServerEffectUtilities.TriggerGameState state, PlayerState player)
        {
            if (player.QueryAttribute(AttributeType.ShouldTriggerExpertise).Value != 0) {
                int activeBefore=state.TriggerQueues.Active.Count;
                int pendingBefore=state.TriggerQueues.Pending.Count;
                ServerEffectUtilities.TriggerEffectEvent(state,
                    new ServerEffectUtilities.TriggerEvent(EffectTriggerType.Expertise, player));
                Plugin.Log("Expertise end-turn queue: player="+player.NetId.Id+" active="+activeBefore+"->"+state.TriggerQueues.Active.Count+" pending="+pendingBefore+"->"+state.TriggerQueues.Pending.Count);
            }
            PlayerActions.SetAttribute.StaticExecute(state.Log, player, AttributeType.ShouldTriggerExpertise, 0);
        }
        static DecisionConditionalExpression DecisionSelector(int id)
        {
            var selector = new DecisionConditionalExpression { DecisionType = DecisionType.Card };
            selector.CardConditionalExpressions.Add(new RuleCondition { CardId = id, CheckTarget = true });
            return selector;
        }
        sealed class RequirementImpl : GenericCardRequirement<RuleCondition> {}
        static ICardRequirement Requirement(RuleCondition condition)
        {
            return new RequirementImpl { LhsCard = CardRole.Self, Conditional = condition };
        }
        sealed class RuleCondition : ICardConditionalExpression
        {
            public int CardId;
            public bool CheckTarget, CheckEvent, CheckColor, RequireBoard;
            public CardSubtype Color;
            public bool Evaluate(ServerCard card, CardConditionalArguments cards, PlayerConditionalArguments players, TriggerConditionalArguments trigger)
            {
                var self = cards.SelfCard;
                if (self == null || self.IsDying()) return false;
                if (RequireBoard) return self.CurrentGameZoneType == GameZoneType.Board;
                if (CheckColor) return cards.CardCatalog.GatherDynamicCardList().Exists(c =>
                    c.OwnerNetId == self.OwnerNetId && c.CurrentGameZoneType == GameZoneType.Board &&
                    !c.IsDying() && c.HasSubtype(Color));
                if (CheckTarget) {
                    if (card == null || card.IsDying() || !card.HasSubtype(CardSubtype.Creature)) return false;
                    if (CardId == -389657211) return card.OwnerNetId == self.OwnerNetId && card.CurrentGameZoneName == GameZoneName.Hand;
                    if (card.CurrentGameZoneName != GameZoneName.CreatureBoard) return false;
                    return CardId != -1308684010 || (card.OwnerNetId == self.OwnerNetId && card.InstanceId != self.InstanceId);
                }
                if (CheckEvent) {
                    if (CardId == 782334488) return players.InciterPlayerState != null && players.InciterPlayerState.NetId == self.OwnerNetId;
                    if (CardId == -1399661641) return cards.InciterCard == self;
                    var inciter = cards.InciterCard;
                    return inciter != null && inciter.CurrentGameZoneName == GameZoneName.CreatureBoard &&
                        inciter.OwnerNetId == self.OwnerNetId && !inciter.IsDying() &&
                        (CardId != 1021793029 || inciter.QueryAttribute(AttributeType.ManaCost).Value >= 7);
                }
                if (CardId == 1646495852) {
                    var creatures = cards.CardCatalog.GatherDynamicCardList();
                    return creatures.Exists(c => c.OwnerNetId == self.OwnerNetId && c.InstanceId != self.InstanceId &&
                        c.CurrentGameZoneName == GameZoneName.CreatureBoard && c.CurrentLaneIndex == 0 && !c.IsDying()) &&
                        creatures.Exists(c => c.OwnerNetId == self.OwnerNetId && c.InstanceId != self.InstanceId &&
                        c.CurrentGameZoneName == GameZoneName.CreatureBoard && c.CurrentLaneIndex == 1 && !c.IsDying());
                }
                return true;
            }
        }
        sealed class TargetCondition : ICardConditionalExpression
        {
            public Targets Target;
            public bool Evaluate(ServerCard card, CardConditionalArguments cards, PlayerConditionalArguments players, TriggerConditionalArguments trigger)
            {
                if (card == null || card.IsDying() || cards.SelfCard == null) return false;
                var self = cards.SelfCard;
                if (Target == Targets.Decision) return card == cards.DecisionTargetCard;
                if (Target == Targets.Self) return card == self;
                if (Target == Targets.Wielder) return card.InstanceId == self.ItemTarget && card.CurrentGameZoneName == GameZoneName.CreatureBoard;
                if (Target == Targets.Inciter) return card == cards.InciterCard && card.CurrentGameZoneName == GameZoneName.CreatureBoard;
                if (card.CurrentGameZoneName != GameZoneName.CreatureBoard) return false;
                if (Target == Targets.AllBoard) return true;
                if (Target == Targets.FriendlyBoard) return card.OwnerNetId == self.OwnerNetId;
                return card.OwnerNetId != self.OwnerNetId && card.CurrentLaneIndex == self.CurrentLaneIndex &&
                    (VariantCardRules.CanonicalId(self.CardId) != 1399500856 || card.QueryAttribute(AttributeType.Attack).Value > 3);
            }
        }
        sealed class WielderStats : ICardAction
        {
            public CardFxAction GetFxActionType() { return CardFxAction.Buff; }
            public void Execute(CardActionArguments args)
            {
                bool hammer = VariantCardRules.CanonicalId(args.CardArguments.SelfCard.CardId) == -1588632902;
                foreach (var target in args.ValidTargetList) {
                    var enchantment = new Enchantment { PermanentExpiresOnLeavingBoard = true };
                    enchantment.AttributeModifications.Add(new EnchantmentAttributeModification(
                        hammer ? EnchantmentAttributeModificationType.Multiply : EnchantmentAttributeModificationType.Set,
                        AttributeType.Attack, hammer ? 2 : target.CurrentHealth));
                    if (hammer) enchantment.AttributeModifications.Add(new EnchantmentAttributeModification(
                        EnchantmentAttributeModificationType.Multiply, AttributeType.MaxHealth, 2));
                    // A Summon bonus belongs to the wielder, not to the equipped item's
                    // continuing bonuses. Native RemoveItemTarget removes only item-source
                    // enchantments. Normal native buffs still expire when the creature leaves.
                    CardActions.Buff.StaticExecute(args.TriggerGameState, target, new List<ServerCard> { target },
                        enchantment, args.PlayerArguments.InciterPlayerState, args.AssetHash, args.UsingDefaultFx);
                }
            }
        }
        sealed class SetStats : ICardAction
        {
            public int CardId;
            public CardFxAction GetFxActionType() { return CardFxAction.Buff; }
            public void Execute(CardActionArguments args)
            {
                var self = args.CardArguments.SelfCard;
                int value = 1;
                if (CardId == -1308684010 || CardId == -389657211) value = self.QueryAttribute(AttributeType.Attack).Value;
                if (CardId == 1399500856) value = 3;
                if (CardId == 782334488) {
                    var pool = new ActionValueData(ActionValueMethod.HighestAttributeValueFromPool) { AttributeName = AttributeType.Attack };
                    pool.Selectors.Add(new TargetCondition { Target = Targets.FriendlyBoard });
                    value = ActionValueData.EvaluateValue(pool, args.CardArguments, args.PlayerArguments, args.TriggerArguments, args.TriggerGameState.RandomGenerator);
                }
                if (SharedFxBatch01.Contains(CardId)) {
                    var enchantment = new Enchantment { ResetWoundedState = true,PermanentExpiresOnLeavingBoard = true };
                    bool powerOnly = CardId == 1646495852 || CardId == -1235178647 || CardId == 1399500856 || CardId == 782334488;
                    bool healthOnly = CardId == -2074393999 || CardId == 1287011430 || CardId == -241860300;
                    if (!healthOnly) enchantment.AttributeModifications.Add(new EnchantmentAttributeModification(EnchantmentAttributeModificationType.Set,AttributeType.Attack,value));
                    if (!powerOnly) enchantment.AttributeModifications.Add(new EnchantmentAttributeModification(EnchantmentAttributeModificationType.Set,AttributeType.MaxHealth,value));
                    enchantment.ResetWoundedState = !powerOnly;
                    SharedFxBatch01.Execute(args,enchantment);
                    return;
                }
                foreach (var target in args.ValidTargetList) {
                    var enchantment = new Enchantment { ResetWoundedState = true, PermanentExpiresOnLeavingBoard = true };
                    bool power = CardId == 1646495852 || CardId == -1235178647 || CardId == -266284141 ||
                        CardId == 1399500856 || CardId == 782334488;
                    bool health = CardId == -2074393999 || CardId == 1287011430 || CardId == -241860300;
                    if (!health) enchantment.AttributeModifications.Add(new EnchantmentAttributeModification(
                        EnchantmentAttributeModificationType.Set, AttributeType.Attack, value));
                    if (!power) enchantment.AttributeModifications.Add(new EnchantmentAttributeModification(
                        EnchantmentAttributeModificationType.Set, AttributeType.MaxHealth, value));
                    // Power-only changes must not heal wounds.
                    enchantment.ResetWoundedState = !power;
                    var one = new CardActionArguments(args.TriggerGameState, new List<ServerCard> { target },
                        args.CardArguments, args.PlayerArguments, args.TriggerArguments, args.AssetHash, args.UsingDefaultFx);
                    new CardActions.SetOnBoardStats { Enchantment = enchantment }.Execute(one);
                }
            }
        }
    }
}
