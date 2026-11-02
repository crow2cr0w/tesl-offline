using System;
using System.Collections.Generic;
using CcgRpc;
using SPTESL;

namespace NyxTESLOffline
{
    internal static class NaryuAuraRules
    {
        private const int NaryuId = 164091185;
        private static bool refreshing;

        internal static void Refresh(ServerEffectUtilities.TriggerGameState state)
        {
            if (refreshing || state.CardCatalog == null)
                return;
            refreshing = true;
            try
            {
                List<ServerCard> cards = state.CardCatalog.GatherDynamicCardList();
                List<ServerCard> sources = cards.FindAll(delegate(ServerCard card)
                {
                    return VariantCardRules.CanonicalId(card.CardId) == NaryuId &&
                        card.CurrentGameZoneName == GameZoneName.CreatureBoard &&
                        !card.Silenced && !card.IsDying() && card.Auras.Count > 0 &&
                        card.Auras[0].EnchantmentsToApply.Count > 0;
                });

                foreach (ServerCard target in cards)
                {
                    bool creature = target.CurrentGameZoneName == GameZoneName.CreatureBoard &&
                        target.HasSubtype(CardSubtype.Creature) && !target.IsDying();
                    bool changed = false;
                    for (int i = target.AuraEnchantments.Count - 1; i >= 0; --i)
                    {
                        EnchantmentInstance aura = target.AuraEnchantments[i];
                        if (VariantCardRules.CanonicalId(aura.SourceCardId) != NaryuId)
                            continue;
                        bool valid = creature && target.Enchantments.Contains(aura) &&
                            sources.Exists(delegate(ServerCard source)
                            {
                                return source.InstanceId == aura.SourceId &&
                                    source.OwnerNetId == target.OwnerNetId;
                            });
                        if (valid)
                            continue;
                        target.AuraEnchantments.RemoveAt(i);
                        target.Enchantments.Remove(aura);
                        changed = true;
                    }

                    if (creature)
                    {
                        foreach (ServerCard source in sources)
                        {
                            if (source.OwnerNetId != target.OwnerNetId ||
                                target.AuraEnchantments.Exists(delegate(EnchantmentInstance aura)
                                {
                                    return aura.SourceId == source.InstanceId &&
                                        VariantCardRules.CanonicalId(aura.SourceCardId) == NaryuId;
                                }))
                                continue;
                            SPTESL.Enchantment enchantment =
                                source.Auras[0].EnchantmentsToApply[0].Enchantment;
                            EnchantmentInstance added = new EnchantmentInstance(
                                enchantment, 1, source.CardId, source.InstanceId, target);
                            target.AuraEnchantments.Add(added);
                            target.Enchantments.Add(added);
                            changed = true;
                        }
                    }

                    if (!changed)
                        continue;
                    if (state.Log != null)
                        state.Log.StartEvent(HistoryType.EventTrigger, target.OwnerNetId,
                            target.NetUid, target.NetUid, -1);
                    try
                    {
                        target.RecalculateEnchantmentsWithTriggers(state, null);
                        if (state.Log != null)
                            HistoryLoggingUtilities.LogFullCardRefresh(
                                state.Log, state.CardCatalog, target, false);
                    }
                    finally
                    {
                        if (state.Log != null)
                            state.Log.EndEvent();
                    }
                }
            }
            finally
            {
                refreshing = false;
            }
        }
    }
}
