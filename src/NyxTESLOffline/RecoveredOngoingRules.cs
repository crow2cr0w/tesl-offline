using System;
using System.Collections.Generic;
using CcgRpc;
using SPTESL;

namespace NyxTESLOffline
{
    internal static class RecoveredOngoingRules
    {
        private const int PrisonShipId = 278121258;
        private const int VigilantAncestorId = -1173040636;
        private static bool refreshingShackles;

        internal static void RefreshPersistentShackles(ServerEffectUtilities.TriggerGameState state)
        {
            if (refreshingShackles || state.CardCatalog == null)
                return;
            refreshingShackles = true;
            try
            {
                List<ServerCard> cards = state.CardCatalog.GatherDynamicCardList();
                List<ServerCard> ships = cards.FindAll(delegate(ServerCard card)
                {
                    return VariantCardRules.CanonicalId(card.CardId) == PrisonShipId &&
                        card.CurrentGameZoneName == GameZoneName.CreatureBoard &&
                        !card.Silenced && !card.IsDying() && card.Auras.Count > 0 &&
                        card.Auras[0].EnchantmentsToApply.Count > 0;
                });

                foreach (ServerCard target in cards)
                {
                    bool validTarget = target.CurrentGameZoneName == GameZoneName.CreatureBoard &&
                        target.HasSubtype(CardSubtype.Creature) && !target.IsDying() &&
                        target.QueryAttribute(AttributeType.ImmuneToFreeze).Value == 0;
                    bool ancestorActive = validTarget &&
                        VariantCardRules.CanonicalId(target.CardId) == VigilantAncestorId &&
                        !target.Silenced && target.Auras.Count > 0 &&
                        target.Auras[0].EnchantmentsToApply.Count > 0;
                    bool changed = false;
                    for (int i = target.AuraEnchantments.Count - 1; i >= 0; --i)
                    {
                        EnchantmentInstance aura = target.AuraEnchantments[i];
                        int sourceCardId = VariantCardRules.CanonicalId(aura.SourceCardId);
                        if (sourceCardId == VigilantAncestorId)
                        {
                            if (ancestorActive && aura.SourceId == target.InstanceId &&
                                target.Enchantments.Contains(aura))
                                continue;
                            target.AuraEnchantments.RemoveAt(i);
                            target.Enchantments.Remove(aura);
                            changed = true;
                            continue;
                        }
                        if (sourceCardId != PrisonShipId)
                            continue;
                        bool active = validTarget && target.Enchantments.Contains(aura) &&
                            ships.Exists(delegate(ServerCard ship)
                            {
                                return ship.InstanceId == aura.SourceId &&
                                    ship.OwnerNetId != target.OwnerNetId &&
                                    HasPrisonShipLink(state, ship, target);
                            });
                        if (active)
                            continue;
                        target.AuraEnchantments.RemoveAt(i);
                        target.Enchantments.Remove(aura);
                        changed = true;
                    }

                    if (validTarget)
                        foreach (ServerCard ship in ships)
                        {
                            if (ship.OwnerNetId == target.OwnerNetId ||
                                !HasPrisonShipLink(state, ship, target) ||
                                target.AuraEnchantments.Exists(delegate(EnchantmentInstance aura)
                                {
                                    return aura.SourceId == ship.InstanceId &&
                                        VariantCardRules.CanonicalId(aura.SourceCardId) == PrisonShipId;
                                }))
                                continue;
                            SPTESL.Enchantment enchantment =
                                ship.Auras[0].EnchantmentsToApply[0].Enchantment;
                            EnchantmentInstance added = new EnchantmentInstance(
                                enchantment, 1, ship.CardId, ship.InstanceId, target);
                            target.AuraEnchantments.Add(added);
                            target.Enchantments.Add(added);
                            changed = true;
                        }

                    if (ancestorActive && !target.AuraEnchantments.Exists(
                        delegate(EnchantmentInstance aura)
                        {
                            return aura.SourceId == target.InstanceId &&
                                VariantCardRules.CanonicalId(aura.SourceCardId) == VigilantAncestorId;
                        }))
                    {
                        SPTESL.Enchantment enchantment =
                            target.Auras[0].EnchantmentsToApply[0].Enchantment;
                        EnchantmentInstance added = new EnchantmentInstance(
                            enchantment, 1, target.CardId, target.InstanceId, target);
                        target.AuraEnchantments.Add(added);
                        target.Enchantments.Add(added);
                        changed = true;
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
                refreshingShackles = false;
            }
        }

        private static bool HasPrisonShipLink(
            ServerEffectUtilities.TriggerGameState state, ServerCard ship, ServerCard target)
        {
            var links = ship.GetLinkedCards(LinkType.PrisonShip, LinkedCard.LinkRole.Source);
            for (int i = 0; i < links.Count; ++i)
            {
                ServerCard linked = state.CardCatalog.FindServerCard(links[i]);
                if (linked != null && linked.InstanceId == target.InstanceId)
                    return true;
            }
            return false;
        }

        internal static void Apply(GameConfiguration config)
        {
            var conscript=config.FindCardById(1366329424);
            if(conscript!=null && !conscript.Auras.Exists(a=>a.NameForEditor=="CravenConscriptUnarmed")) {
                var aura=new Aura { NameForEditor="CravenConscriptUnarmed" };
                var enchant=new AuraEnchantment();enchant.Selectors.Add(new UnarmedSelf());
                enchant.Enchantment.AttributeModifications.Add(new EnchantmentAttributeModification(
                    EnchantmentAttributeModificationType.Set,AttributeType.Freeze,1));
                enchant.Enchantment.AttributeModifications.Add(new EnchantmentAttributeModification(
                    EnchantmentAttributeModificationType.Set,AttributeType.PermFreeze,1));
                aura.EnchantmentsToApply.Add(enchant);conscript.Auras.Add(aura);
            }
            // PC descriptions include both Summon damage and damage-triggered
            // Shackle. Keep the installed Summon; restore the missing listener.
            foreach(int id in new[]{881505215,-1932999038}) {
                var card=config.FindCardById(id);
                if(card==null || card.TriggeredEffects.Exists(t=>t.TriggerType==EffectTriggerType.WhenCardDealsDamage))continue;
                var effect=new Effect();
                var action=new CardEffectAction { HashId=-1,Action=new CardActions.Freeze {
                    PermanentFreeze=false,Type=EnchantmentUtilities.FreezeType.Normal }};
                action.Selectors.Add(new CardConditionals.CardRoleType {
                    Role=CardRole.EventTarget,Check=CardConditionals.CardRoleType.Requirement.Is });
                action.Selectors.Add(new LivingCreature());
                effect.CardActions.Add(action);
                var group=new EffectGroup();group.Effects.Add(effect);
                var trigger=new TriggeredEffect { TriggerType=EffectTriggerType.WhenCardDealsDamage,
                    SortOrder=TriggerSortOrder.EndOfActiveQueue,ClientTargetingType=PlayTargetingType.NoTarget,
                    EffectGroup=group };
                trigger.TriggerCardRequirements.Add(new DamageRequirement { LhsCard=CardRole.Self });
                card.TriggeredEffects.Add(trigger);
            }
        }
        sealed class UnarmedSelf : ICardConditionalExpression
        {
            public bool Evaluate(ServerCard card,CardConditionalArguments cards,PlayerConditionalArguments players,TriggerConditionalArguments trigger)
            {
                return card!=null && card==cards.SelfCard && !card.Silenced && !card.IsDying() &&
                    card.CurrentGameZoneName==GameZoneName.CreatureBoard &&
                    cards.CardCatalog.GatherCardsInZone(c=>c.ItemTarget==card.InstanceId,card.OwnerNetId,GameZoneName.ItemBoard).Count==0;
            }
        }
        sealed class DamageRequirement : GenericCardRequirement<DamageFromSelf> {}
        sealed class DamageFromSelf : ICardConditionalExpression
        {
            public bool Evaluate(ServerCard card,CardConditionalArguments cards,PlayerConditionalArguments players,TriggerConditionalArguments trigger)
            {
                return card!=null && !card.Silenced && cards.InciterCard==card && trigger.TriggerValue>0;
            }
        }
        sealed class LivingCreature : ICardConditionalExpression
        {
            public bool Evaluate(ServerCard card,CardConditionalArguments cards,PlayerConditionalArguments players,TriggerConditionalArguments trigger)
            {
                return card!=null && card.HasSubtype(CardSubtype.Creature) &&
                    card.CurrentGameZoneName==GameZoneName.CreatureBoard && !card.IsDying();
            }
        }
    }
}
