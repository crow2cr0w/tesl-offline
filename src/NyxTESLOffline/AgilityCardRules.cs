using SPTESL;
using System.Collections.Generic;
namespace NyxTESLOffline
{
    // Recover only rule-bearing fields absent from the retail binary.
    // Native base attributes, localization, art, voices and FX are untouched.
    internal static class AgilityCardRules
    {
        [System.ThreadStatic] private static bool _refreshing;
        internal static readonly int[] CardIds = { -967450522, 2095683028, -622045452, -1248991397 };
        internal static void Apply(GameConfiguration config)
        {
            foreach (int id in CardIds)
            {
                Card card = config.FindCardById(id);
                if (card != null && Install(card))
                    Plugin.Log("recovered agility summon: card=" + id + " name=" + card.Name);
            }
        }
        internal static bool Install(Card card)
        {
            int id = card.ResourceId.Id;
            if (System.Array.IndexOf(CardIds, id) < 0 || card.TriggeredEffects.Count != 0 ||
                card.ActionPlayInformation.Count != 0 || card.ActivatedAbility.IsValidAbility() || card.Auras.Count != 0)
                return false;
            ICardAction action;
            if (id == -967450522 || id == 2095683028)
            {
                // Murkwater Witch / Bitterfish Witch: give a creature -1/-1.
                CardActions.Debuff buff = new CardActions.Debuff();
                buff.Enchantment.AttributeModifications.Add(new EnchantmentAttributeModification(
                    EnchantmentAttributeModificationType.Add, AttributeType.Attack, -1));
                buff.Enchantment.AttributeModifications.Add(new EnchantmentAttributeModification(
                    EnchantmentAttributeModificationType.Add, AttributeType.MaxHealth, -1));
                action = buff;
            }
            else if (id == -622045452)
                action = new CardActions.GiveKeyword { KeywordAttrib = AttributeType.Deathtouch };
            else
                action = new CardActions.Cover { MultipleTurns = false };

            CardEffectAction entry = new CardEffectAction { HashId = -1, Action = action };
            entry.Selectors.Add(new CardConditionals.CardRoleType {
                Role = CardRole.DecisionTarget, Check = CardConditionals.CardRoleType.Requirement.Is
            });
            Effect effect = new Effect();
            effect.CardActions.Add(entry);
            EffectGroup group = new EffectGroup();
            group.Effects.Add(effect);
            DecisionConditionalExpression selector = new DecisionConditionalExpression { DecisionType = DecisionType.Card };
            selector.CardConditionalExpressions.Add(new RecoveredCardRules.BoardTarget());
            TriggeredEffect trigger = new TriggeredEffect {
                TriggerType = EffectTriggerType.Summon,
                SortOrder = TriggerSortOrder.EndOfActiveQueue,
                ClientTargetingType = PlayTargetingType.RequiredTarget,
                EffectGroup = group
            };
            trigger.TargetSelectors.Add(selector);
            card.TriggeredEffects.Add(trigger);
            return true;
        }

        internal static void RefreshHiddenTrailAuras(ServerEffectUtilities.TriggerGameState state)
        {
            if (_refreshing || state.CardCatalog == null) return;
            _refreshing = true;
            try
            {
                List<ServerCard> cards = state.CardCatalog.GatherDynamicCardList();
                List<ServerCard> sources = cards.FindAll(delegate(ServerCard card) {
                    return VariantCardRules.CanonicalId(card.CardId) == -1086320745 && card.CurrentGameZoneName == GameZoneName.SupportBoard
                        && !card.Silenced && !card.IsDying() && card.Auras.Count > 0;
                });
                foreach (ServerCard target in cards)
                {
                    bool creature = target.CurrentGameZoneName == GameZoneName.CreatureBoard && target.HasSubtype(CardSubtype.Creature);
                    bool changed = false;
                    for (int i = target.AuraEnchantments.Count - 1; i >= 0; --i)
                    {
                        EnchantmentInstance aura = target.AuraEnchantments[i];
                        if (VariantCardRules.CanonicalId(aura.SourceCardId) != -1086320745) continue;
                        bool valid = creature && sources.Exists(delegate(ServerCard source) {
                            return source.InstanceId == aura.SourceId && source.OwnerNetId == target.OwnerNetId;
                        }) && target.Enchantments.Contains(aura);
                        if (valid) continue;
                        target.AuraEnchantments.RemoveAt(i);
                        target.Enchantments.Remove(aura);
                        changed = true;
                    }
                    if (creature)
                        foreach (ServerCard source in sources)
                        {
                            if (source.OwnerNetId != target.OwnerNetId || target.AuraEnchantments.Exists(
                                delegate(EnchantmentInstance aura) { return VariantCardRules.CanonicalId(aura.SourceCardId) == -1086320745 && aura.SourceId == source.InstanceId; })) continue;
                            Enchantment enchantment = source.Auras[0].EnchantmentsToApply[0].Enchantment;
                            EnchantmentInstance added = new EnchantmentInstance(enchantment, 1, source.CardId, source.InstanceId, target);
                            target.AuraEnchantments.Add(added);
                            target.Enchantments.Add(added);
                            changed = true;
                        }
                    if (!changed) continue;
                    if (state.Log != null)
                        state.Log.StartEvent(CcgRpc.HistoryType.EventTrigger, target.OwnerNetId, target.NetUid, target.NetUid, -1);
                    try
                    {
                        target.RecalculateEnchantmentsWithTriggers(state, null);
                        if (state.Log != null)
                        {
                            CcgRpc.HistoryItem item = new CcgRpc.HistoryItem { Type = CcgRpc.HistoryType.ActionFullCardRefresh, AssetHash = -1 };
                            item.AffectedCardsArray.Add(target.HistoryCreate_FullStateCard(state.CardCatalog));
                            state.Log.AddInstant(item);
                        }
                    }
                    finally { if (state.Log != null) state.Log.EndEvent(); }
                }
            }
            finally { _refreshing = false; }
        }
    }
}
