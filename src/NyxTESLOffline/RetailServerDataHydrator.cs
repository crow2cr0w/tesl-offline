using System;
using System.Collections.Generic;
using SPTESL;

namespace NyxTESLOffline
{
    /*
     * The retail client loads GameConfiguration through BinaryFormatter.
     * Several fields needed by the authoritative/server-side classes are
     * explicitly [NonSerialized] in Assembly-CSharp.dll.  BinaryFormatter does
     * not run the normal field initializers for those members, so the retail
     * graph can contain null where the simulation code expects the constructor
     * defaults (empty lists / empty requirement objects).
     *
     * This class restores only those structural defaults.  It deliberately does
     * NOT invent card effects, targeting rules, AI weights or historical preset
     * deck contents that are absent from the retail binary.
     */
    internal static class RetailServerDataHydrator
    {
        private static GameConfiguration _hydratedConfig;

        internal static void Hydrate(GameConfiguration config)
        {
            if (System.Object.ReferenceEquals(config, null))
                throw new ArgumentNullException("config");

            if (System.Object.ReferenceEquals(_hydratedConfig, config))
                return;

            int cards = 0;
            int cardFields = 0;
            int definitions = 0;
            int definitionFields = 0;
            int presets = 0;
            int presetFields = 0;
            int scenarios = 0;
            int scenarioFields = 0;

            if (System.Object.ReferenceEquals(config.CommonAiWeights, null))
                config.CommonAiWeights = new List<CommonAiWeight>();

            int laneTriggerFields = HydrateLaneTriggerDefaults(config);

            if (!System.Object.ReferenceEquals(config.CardCollection, null))
            {
                int setIndex;
                for (setIndex = 0; setIndex < config.CardCollection.Count; ++setIndex)
                {
                    CardSet set = config.CardCollection[setIndex];
                    if (System.Object.ReferenceEquals(set, null) ||
                        System.Object.ReferenceEquals(set.Cards, null))
                        continue;

                    int cardIndex;
                    for (cardIndex = 0; cardIndex < set.Cards.Count; ++cardIndex)
                    {
                        SPTESL.Card card = set.Cards[cardIndex];
                        if (System.Object.ReferenceEquals(card, null))
                            continue;

                        cards += 1;

                        if (System.Object.ReferenceEquals(card.EntityAttributes, null))
                        {
                            card.EntityAttributes = new List<SPTESL.Attribute>();
                            cardFields += 1;
                        }
                        if (System.Object.ReferenceEquals(card.PermanentHangers, null))
                        {
                            card.PermanentHangers = new List<LocalizedHangerInformation>();
                            cardFields += 1;
                        }
                        if (System.Object.ReferenceEquals(card.MechanicsEntries, null))
                        {
                            card.MechanicsEntries = new List<UIGameMechanicEntry>();
                            cardFields += 1;
                        }
                        if (System.Object.ReferenceEquals(card.CardReferences, null))
                        {
                            card.CardReferences = new List<UniqueResourceReference>();
                            cardFields += 1;
                        }
                        if (System.Object.ReferenceEquals(card.HuntTypes, null))
                        {
                            card.HuntTypes = new List<TreasureHuntType>();
                            cardFields += 1;
                        }
                        if (System.Object.ReferenceEquals(card.UpgradesInto, null))
                        {
                            card.UpgradesInto = new List<UniqueResourceReference>();
                            cardFields += 1;
                        }
                        if (System.Object.ReferenceEquals(card.MobilizeRecruitId, null))
                        {
                            card.MobilizeRecruitId = new SimpleCardReference();
                            cardFields += 1;
                        }
                        if (System.Object.ReferenceEquals(card.DefaultRareAttributes, null))
                        {
                            card.DefaultRareAttributes = new List<RareAttributeDefault>();
                            cardFields += 1;
                        }
                        if (System.Object.ReferenceEquals(card.PlayRequirements, null))
                        {
                            card.PlayRequirements = new PlayCardRequirements();
                            cardFields += 1;
                        }
                        if (System.Object.ReferenceEquals(card.BetrayRequirements, null))
                        {
                            card.BetrayRequirements = new PlayCardRequirements();
                            cardFields += 1;
                        }
                        if (System.Object.ReferenceEquals(card.PlayWeight, null))
                        {
                            card.PlayWeight = new FullAiWeight();
                            cardFields += 1;
                        }
                        if (System.Object.ReferenceEquals(card.ActionPlayInformation, null))
                        {
                            card.ActionPlayInformation = new List<PlayCardAbility>();
                            cardFields += 1;
                        }
                        if (System.Object.ReferenceEquals(card.AttackRequirements, null))
                        {
                            card.AttackRequirements = new AttackCardRequirements();
                            cardFields += 1;
                        }
                        if (System.Object.ReferenceEquals(card.StrongHintRequirements, null))
                        {
                            card.StrongHintRequirements = new StrongHintRequirements();
                            cardFields += 1;
                        }
                        if (System.Object.ReferenceEquals(card.CardFxRelations, null))
                        {
                            card.CardFxRelations = new List<CardFxRelationData>();
                            cardFields += 1;
                        }
                        if (System.Object.ReferenceEquals(card.ActivatedAbility, null))
                        {
                            card.ActivatedAbility = new ActivatedAbility();
                            cardFields += 1;
                        }
                        if (System.Object.ReferenceEquals(card.Subtypes, null))
                        {
                            card.Subtypes = new List<CardSubtype>();
                            cardFields += 1;
                        }
                        if (System.Object.ReferenceEquals(card.ArenaDraftSubtypes, null))
                        {
                            card.ArenaDraftSubtypes = new List<CardSubtype>();
                            cardFields += 1;
                        }
                        if (System.Object.ReferenceEquals(card.TriggeredEffects, null))
                        {
                            card.TriggeredEffects = new List<TriggeredEffect>();
                            cardFields += 1;
                        }
                        if (System.Object.ReferenceEquals(card.Auras, null))
                        {
                            card.Auras = new List<Aura>();
                            cardFields += 1;
                        }
                        if (System.Object.ReferenceEquals(card.DoubleCardReferences, null))
                        {
                            card.DoubleCardReferences = new List<UniqueResourceReference>();
                            cardFields += 1;
                        }
                        if (System.Object.ReferenceEquals(card.DynamicTextAssignments, null))
                        {
                            card.DynamicTextAssignments = new List<DynamicTextAssignment>();
                            cardFields += 1;
                        }
                    }
                }
            }

            if (!System.Object.ReferenceEquals(config.CardDefinitions, null))
            {
                int i;
                for (i = 0; i < config.CardDefinitions.Count; ++i)
                {
                    CardDefinition definition = config.CardDefinitions[i];
                    if (System.Object.ReferenceEquals(definition, null))
                        continue;
                    definitions += 1;

                    if (System.Object.ReferenceEquals(definition.EntityAttributes, null))
                    {
                        definition.EntityAttributes = new List<SPTESL.Attribute>();
                        definitionFields += 1;
                    }
                    if (System.Object.ReferenceEquals(definition.Subtypes, null))
                    {
                        definition.Subtypes = new List<CardSubtype>();
                        definitionFields += 1;
                    }
                    if (System.Object.ReferenceEquals(definition.TriggeredEffects, null))
                    {
                        definition.TriggeredEffects = new List<TriggeredEffect>();
                        definitionFields += 1;
                    }
                    if (System.Object.ReferenceEquals(definition.Auras, null))
                    {
                        definition.Auras = new List<Aura>();
                        definitionFields += 1;
                    }
                }
            }

            if (!System.Object.ReferenceEquals(config.CustomDecks, null))
            {
                int d;
                for (d = 0; d < config.CustomDecks.Count; ++d)
                {
                    CustomDeck custom = config.CustomDecks[d];
                    if (System.Object.ReferenceEquals(custom, null) ||
                        System.Object.ReferenceEquals(custom.Decks, null))
                        continue;

                    int p;
                    for (p = 0; p < custom.Decks.Count; ++p)
                    {
                        PresetDeck preset = custom.Decks[p];
                        if (System.Object.ReferenceEquals(preset, null))
                            continue;
                        presets += 1;
                        if (System.Object.ReferenceEquals(preset.Cards, null))
                        {
                            preset.Cards = new List<DeckCardInfo>();
                            presetFields += 1;
                        }
                        if (System.Object.ReferenceEquals(preset.HumanReadable, null))
                        {
                            preset.HumanReadable = new List<string>();
                            presetFields += 1;
                        }
                    }
                }
            }

            if (!System.Object.ReferenceEquals(config.Scenarios, null))
            {
                int s;
                for (s = 0; s < config.Scenarios.Count; ++s)
                {
                    Scenario scenario = config.Scenarios[s];
                    if (System.Object.ReferenceEquals(scenario, null))
                        continue;
                    scenarios += 1;
                    if (System.Object.ReferenceEquals(scenario.AvatarOverrides, null))
                    {
                        scenario.AvatarOverrides = new List<AvatarOverride>();
                        scenarioFields += 1;
                    }
                    if (System.Object.ReferenceEquals(scenario.PlayerOverrides, null))
                    {
                        scenario.PlayerOverrides = new List<PlayerOverride>();
                        scenarioFields += 1;
                    }
                    if (System.Object.ReferenceEquals(scenario.DeckOverrides, null))
                    {
                        scenario.DeckOverrides = new List<DeckOverride>();
                        scenarioFields += 1;
                    }
                    if (System.Object.ReferenceEquals(scenario.GameZoneOverrides, null))
                    {
                        scenario.GameZoneOverrides = new List<GameZoneOverrideList>();
                        scenarioFields += 1;
                    }
                    if (System.Object.ReferenceEquals(scenario.TriggeredEffects, null))
                    {
                        scenario.TriggeredEffects = new List<TriggeredEffect>();
                        scenarioFields += 1;
                    }
                }
            }

            _hydratedConfig = config;

            Plugin.Log("retail server-data hydration: cards=" + cards
                + " cardFields=" + cardFields
                + " definitions=" + definitions
                + " definitionFields=" + definitionFields
                + " presets=" + presets
                + " presetFields=" + presetFields
                + " scenarios=" + scenarios
                + " scenarioFields=" + scenarioFields
                + " laneTriggerFields=" + laneTriggerFields
                + " laneTriggers="
                + (System.Object.ReferenceEquals(config.LaneTriggers, null) ? "missing" : "present"));
        }

        private static int HydrateLaneTriggerDefaults(GameConfiguration config)
        {
            int changed = 0;
            int laneCount = (int)CardLaneType.Count;
            LaneTrigger[] previous = config.LaneTriggers;

            if (System.Object.ReferenceEquals(previous, null) ||
                previous.Length != laneCount)
            {
                LaneTrigger[] replacement = new LaneTrigger[laneCount];
                if (!System.Object.ReferenceEquals(previous, null))
                {
                    int copyCount = Math.Min(previous.Length, replacement.Length);
                    int copyIndex;
                    for (copyIndex = 0; copyIndex < copyCount; ++copyIndex)
                        replacement[copyIndex] = previous[copyIndex];
                }
                config.LaneTriggers = replacement;
                changed += 1;
            }

            int i;
            for (i = 0; i < config.LaneTriggers.Length; ++i)
            {
                LaneTrigger lane = config.LaneTriggers[i];
                if (System.Object.ReferenceEquals(lane, null))
                {
                    lane = new LaneTrigger();
                    config.LaneTriggers[i] = lane;
                    changed += 1;
                }

                if (System.Object.ReferenceEquals(lane.Triggers, null))
                {
                    lane.Triggers = new List<LaneTriggerItem>();
                    changed += 1;
                }
                if (System.Object.ReferenceEquals(lane.CreatureSummonRequirements, null))
                {
                    lane.CreatureSummonRequirements = new List<ICardRequirement>();
                    changed += 1;
                }
                if (System.Object.ReferenceEquals(lane.LaneSummonRequirements, null))
                {
                    lane.LaneSummonRequirements = new List<ILaneRequirement>();
                    changed += 1;
                }
                if (System.Object.ReferenceEquals(lane.AuraEnchantments, null))
                {
                    lane.AuraEnchantments = new List<AuraEnchantment>();
                    changed += 1;
                }
            }

            return changed;
        }
    }
}
