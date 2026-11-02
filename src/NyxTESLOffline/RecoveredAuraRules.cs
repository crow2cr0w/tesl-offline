using System;
using System.Collections.Generic;
using CcgRpc;
using SPTESL;

namespace NyxTESLOffline
{
    internal static class RecoveredAuraRules
    {
        private const int MournholdTaskmasterId = 1082778386;
        private const int DivineFervorId = -1653188426;
        private const int CrazedHungerFoMId = 198068342;
        private const int CrazedHungerId = 2124177191;
        private const int VarenAquilariosId = 372165261;
        private const int VivecId = -1597653207;
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
                    int id = VariantCardRules.CanonicalId(card.CardId);
                    return IsAuraSource(id) && IsOnBoard(card) && !card.Silenced &&
                        !card.IsDying() && card.Auras.Count > 0 &&
                        card.Auras[0].EnchantmentsToApply.Count > 0 &&
                        IsAuraActive(state, id, card);
                });

                List<ServerCard> avatarSources = cards.FindAll(delegate(ServerCard card)
                {
                    int id = VariantCardRules.CanonicalId(card.CardId);
                    return (id == VarenAquilariosId || id == VivecId) &&
                        IsOnBoard(card) && !card.Silenced && !card.IsDying() &&
                        card.Auras.Count > 0 && card.Auras[0].EnchantmentsToApply.Count > 0 &&
                        (id != VivecId || HasExaltedAlly(cards, card.OwnerNetId));
                });

                foreach (ServerCard target in cards)
                {
                    bool creature = target.CurrentGameZoneName == GameZoneName.CreatureBoard &&
                        target.HasSubtype(CardSubtype.Creature) && !target.IsDying();
                    bool avatar = target.CurrentGameZoneName == GameZoneName.AvatarBoard;
                    bool changed = false;
                    for (int i = target.AuraEnchantments.Count - 1; i >= 0; --i)
                    {
                        EnchantmentInstance aura = target.AuraEnchantments[i];
                        int auraSourceId = VariantCardRules.CanonicalId(aura.SourceCardId);
                        if (!IsAuraSource(auraSourceId) && !IsAvatarAuraSource(auraSourceId))
                            continue;
                        bool valid = target.Enchantments.Contains(aura) &&
                            ((IsAuraSource(auraSourceId) && creature && IsAuraTarget(auraSourceId, target) &&
                            sources.Exists(delegate(ServerCard source)
                            {
                                return source.InstanceId == aura.SourceId &&
                                    source.OwnerNetId == target.OwnerNetId &&
                                    VariantCardRules.CanonicalId(source.CardId) == auraSourceId;
                            })) || (IsAvatarAuraSource(auraSourceId) && avatar &&
                            avatarSources.Exists(delegate(ServerCard source)
                            {
                                return source.InstanceId == aura.SourceId &&
                                    source.OwnerNetId == target.OwnerNetId &&
                                    VariantCardRules.CanonicalId(source.CardId) == auraSourceId;
                            })));
                        if (valid)
                            continue;
                        target.AuraEnchantments.RemoveAt(i);
                        target.Enchantments.Remove(aura);
                        changed = true;
                    }

                    if (creature || avatar)
                    {
                        foreach (ServerCard source in avatar ? avatarSources : sources)
                        {
                            int sourceId = VariantCardRules.CanonicalId(source.CardId);
                            if (source.OwnerNetId != target.OwnerNetId ||
                                (avatar ? !IsAvatarAuraSource(sourceId) : !IsAuraTarget(sourceId, target)) ||
                                target.AuraEnchantments.Exists(delegate(EnchantmentInstance aura)
                                {
                                    return aura.SourceId == source.InstanceId &&
                                        VariantCardRules.CanonicalId(aura.SourceCardId) == sourceId;
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

        private static bool IsAuraSource(int id)
        {
            return id == MournholdTaskmasterId || id == DivineFervorId ||
                id == CrazedHungerFoMId || id == CrazedHungerId;
        }

        private static bool IsAvatarAuraSource(int id)
        {
            return id == VarenAquilariosId || id == VivecId;
        }

        private static bool HasExaltedAlly(List<ServerCard> cards, PlayerUniqueId owner)
        {
            return cards.Exists(delegate(ServerCard card)
            {
                return card.OwnerNetId == owner &&
                    card.CurrentGameZoneName == GameZoneName.CreatureBoard &&
                    card.HasSubtype(CardSubtype.Creature) && !card.Silenced &&
                    !card.IsDying() && card.QueryAttribute(AttributeType.Exalted).Value > 0;
            });
        }

        private static bool IsAuraActive(
            ServerEffectUtilities.TriggerGameState state, int id, ServerCard source)
        {
            if (id != CrazedHungerFoMId && id != CrazedHungerId)
                return true;
            return state.Players.Exists(delegate(PlayerState player)
            {
                return player.NetId == source.OwnerNetId && player.IsMyTurn;
            });
        }

        private static bool IsOnBoard(ServerCard card)
        {
            return card.CurrentGameZoneName == GameZoneName.CreatureBoard ||
                card.CurrentGameZoneName == GameZoneName.SupportBoard;
        }

        private static bool IsAuraTarget(int sourceId, ServerCard target)
        {
            return sourceId == DivineFervorId ||
                ((sourceId == CrazedHungerFoMId || sourceId == CrazedHungerId) &&
                    VariantCardRules.CanonicalId(target.CardId) == sourceId) ||
                (sourceId == MournholdTaskmasterId &&
                    SharedStatFamilyRules.HasVeteranTrigger(target));
        }
    }
}
