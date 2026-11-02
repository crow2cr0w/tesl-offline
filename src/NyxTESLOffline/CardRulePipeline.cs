using System;
using System.Collections.Generic;
using System.Linq;
using SPTESL;

namespace NyxTESLOffline
{
    // One ordered rule initialization for client matches and diagnostics.
    internal static class CardRulePipeline
    {
        internal static void Apply(GameConfiguration config)
        {
            if (config == null) throw new ArgumentNullException("config");
            RetailServerDataHydrator.Hydrate(config);
            var originalGraphs = config.CardCollection.SelectMany(s=>s.Cards)
                .ToDictionary(c=>c.ResourceId.Id,c=>Composition(c));
            var sourceRecovered = new HashSet<int>(originalGraphs.Where(pair=>pair.Value.Length>0)
                .Select(pair=>pair.Key));
            RecoveredCardRules.Apply(config);
            AgilityCardRules.Apply(config);
            ExperimentalCardRules100.Apply(config);
            SharedPrimitiveCardRules.Apply(config);
            ReferenceSummonRules.Apply(config);
            ElsweyrKhajiitRules.Apply(config);
            ObservedAiRecovery.Apply(config);
            ConsumeAndLastGaspRules.Apply(config);
            ExternalCardRecovery.Apply(config);
            ObservedCardBatch02.Apply(config);
            ObservedCardBatch03.Apply(config);
            var manuallyRecovered = new HashSet<int>(config.CardCollection.SelectMany(s=>s.Cards)
                .Where(c=>!Composition(c).SequenceEqual(originalGraphs[c.ResourceId.Id]))
                .Select(c=>c.ResourceId.Id));
            manuallyRecovered.UnionWith(sourceRecovered);
            RetailTextSummonCompiler.Apply(config, manuallyRecovered);
            RecoveredOngoingRules.Apply(config);
            DoubleCardFaceRules.Apply(config);
            RetailNamedFxRecovery.Apply(config);
            VariantCardRules.Apply(config);
        }
        static object[] Composition(Card card)
        {
            return card.TriggeredEffects.Cast<object>()
                .Concat(card.ActionPlayInformation.Cast<object>())
                .Concat(card.Auras.Cast<object>())
                .Concat(card.ActivatedAbility.AbilityGroups.Cast<object>()).ToArray();
        }
    }
}
