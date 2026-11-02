using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.IO.Compression;
using System.Security.Cryptography;
using SPTESL;
using UnityEngine;

namespace NyxTESLOffline
{
    /*
     * The retail runtime GameConfiguration is loaded from the compact binary
     * asset.  That graph is sufficient for the client presentation layer, but
     * a number of server/rules fields (TriggeredEffects,
     * ActionPlayInformation, target selectors, requirements, auras, etc.) are
     * absent/empty in the binary build.  The original client still contains
     * the canonical FullSerializer JSON loader and AssetCatalog folder
     * enumeration path used by the source configuration.
     *
     * Recover those rule-bearing fields from the shipped derived JSON assets
     * when they are available.  We merge only gameplay/rules data into the
     * already-loaded retail objects so card art, localization and client
     * references keep using the normal runtime configuration.
     */
    internal static class RetailRuleDataOverlay
    {
        private delegate void TextAssetReceiver(TextAsset value);

        private static readonly MethodInfo LoadJsonStringMethod =
            typeof(GameConfiguration).GetMethod(
                "LoadJSONString",
                BindingFlags.Instance | BindingFlags.NonPublic);

        internal static bool Started;
        internal static bool IsSettled;
        internal static bool RulesAvailable;
        internal const string LocalGraphSha256="eec6c9a9d1af22032d4f65470f8a1c29e511bacb4be673fed8094758839df8e9";
        internal const string LocalFxSha256="15af534ddf6e011d686ff856f5fe9a58741d905049e23d50843536e52938fe54";

        private static void RequireSha256(string path,string expected)
        {
            using(var sha=SHA256.Create())using(var stream=File.OpenRead(path)) {
                string actual=BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant();
                if(actual!=expected)throw new IOException("retail rule bundle checksum mismatch: "+Path.GetFileName(path));
            }
        }

        private static int RestoreSourceFxLookups(GameConfiguration config,string path)
        {
            RequireSha256(path,LocalFxSha256);
            int added=0;
            foreach(string line in File.ReadAllLines(path)) {
                if(line.StartsWith("hashId\t"))continue;
                string[] fields=line.Split('\t');
                if(fields.Length!=2)throw new IOException("invalid source FX mapping row");
                int hash=int.Parse(fields[0]);
                string name=fields[1];
                if(!config.CardFxDefinitions.Exists(d=>d.Name==name))
                    throw new IOException("source FX definition missing from retail catalog: "+name);
                string existing;
                if(config.CardFxDefinitionLookups.TryGetValue(hash,out existing)) {
                    if(existing!=name)throw new IOException("source FX hash conflict: "+hash);
                    continue;
                }
                config.CardFxDefinitionLookups.Add(hash,name);added++;
            }
            return added;
        }

        internal static int LoadLocalGraphBundle(GameConfiguration target,string path)
        {
            if(!File.Exists(path))return 0;
            RequireSha256(path,LocalGraphSha256);
            string fxPath=Path.Combine(Path.GetDirectoryName(path),"retail-rule-fx.tsv");
            if(!File.Exists(fxPath))throw new FileNotFoundException("retail source FX mappings missing",fxPath);
            var cards=new Dictionary<int,SPTESL.Card>();
            // The compact retail configuration is deserialized with BinaryFormatter;
            // its nonserialized FullSerializer instance is null in headless hosts.
            var decoder=new GameConfiguration();
            using(var stream=File.OpenRead(path))
            using(var gzip=new GZipStream(stream,CompressionMode.Decompress))
            using(var reader=new StreamReader(gzip)) {
                string line;
                while((line=reader.ReadLine())!=null) {
                    if(line.Length==0)continue;
                    CardSet set=Deserialize<CardSet>(decoder,line);
                    if(set==null || set.Cards==null)throw new IOException("invalid source card set");
                    foreach(var card in set.Cards) {
                        if(card==null || card.ResourceId.Id==0 || cards.ContainsKey(card.ResourceId.Id))
                            throw new IOException("duplicate/empty source card identity");
                        cards.Add(card.ResourceId.Id,card);
                    }
                }
            }
            int fx=RestoreSourceFxLookups(target,fxPath);
            int merged=MergeCards(target,cards);
            Plugin.Log("retail rule overlay: source=local-json-derived bundle cards="+cards.Count+" merged="+merged+" fxLookupsAdded="+fx);
            return merged;
        }

        internal static int ApplyPackagedRulesOrThrow(GameConfiguration target)
        {
            if (System.Object.ReferenceEquals(target, null))
                throw new ArgumentNullException("target");

            string assemblyPath = typeof(GameManager).Assembly.Location;
            if (String.IsNullOrEmpty(assemblyPath))
                throw new InvalidOperationException("retail assembly location is unavailable");
            string bundle = Path.Combine(
                Path.Combine(Path.GetDirectoryName(assemblyPath), "NyxTESLOffline-data"),
                "retail-rule-graphs.jsonl.gz");

            int expected = 0;
            foreach (CardSet set in target.CardCollection)
                if (!System.Object.ReferenceEquals(set, null) &&
                    !System.Object.ReferenceEquals(set.Cards, null))
                    expected += set.Cards.Count;

            int merged = LoadLocalGraphBundle(target, bundle);
            if (merged != expected)
                throw new IOException(
                    "retail rule bundle incomplete: merged=" + merged + " expected=" + expected);

            RulesAvailable = true;
            IsSettled = true;
            return merged;
        }

        internal static IEnumerator Load(GameConfiguration target)
        {
            Started = true;
            IsSettled = false;
            RulesAvailable = false;

            if (System.Object.ReferenceEquals(target, null))
            {
                Plugin.Warn("retail rule overlay: target GameConfiguration is null");
                IsSettled = true;
                yield break;
            }

            int packagedMerged=0;
            try {
                packagedMerged=ApplyPackagedRulesOrThrow(target);
            } catch(Exception ex) {
                Plugin.Warn("retail rule overlay local bundle failed: "+ex.Message);
            }
            if(packagedMerged>0){
                IEnumerator packagedLaneWaiter=LoadRetailLaneTriggers(target);
                while(!System.Object.ReferenceEquals(packagedLaneWaiter,null) &&
                      packagedLaneWaiter.MoveNext())
                    yield return packagedLaneWaiter.Current;
                RulesAvailable=true;IsSettled=true;yield break;
            }

            /* Developer/source installs can still contain the loose JSON tree.
             * Prefer it because GameConfiguration.LoadSourceGameConfiguration()
             * is the exact retail decoder path. */
            string looseCardDir = Path.Combine(
                GameConfiguration.RootPath, "CardCollection");
            string looseDefinitions = Path.Combine(
                GameConfiguration.RootPath, "card_definitions.json");
            if (Directory.Exists(looseCardDir) && File.Exists(looseDefinitions))
            {
                try
                {
                    GameConfiguration source = new GameConfiguration();
                    source.LoadSourceGameConfiguration();
                    int merged = MergeConfiguration(target, source, "loose-json");
                    RulesAvailable = merged > 0;
                    IsSettled = true;
                    yield break;
                }
                catch (Exception ex)
                {
                    Plugin.Warn("retail rule overlay loose-json failed; trying bundled JSON: "
                        + ex.Message);
                }
            }

            if (!AssetCatalog.IsReady() ||
                System.Object.ReferenceEquals(AssetCatalog.Instance, null))
            {
                Plugin.Warn("retail rule overlay: AssetCatalog is not ready");
                IsSettled = true;
                yield break;
            }

            if (System.Object.ReferenceEquals(LoadJsonStringMethod, null))
            {
                Plugin.Warn("retail rule overlay: GameConfiguration.LoadJSONString<T> not found");
                IsSettled = true;
                yield break;
            }

            List<string> cardPaths = new List<string>();
            IEnumerator scan = AssetCatalog.Instance.GetAssetListFromFolder(
                GameConfiguration.DerivedAssetPath + "CardCollection/",
                cardPaths,
                "json");
            if (!System.Object.ReferenceEquals(scan, null))
            {
                while (scan.MoveNext())
                    yield return scan.Current;
            }

            /* File manifests can expose the same asset through more than one
             * loaded bundle.  Deduplicate while keeping deterministic order. */
            Dictionary<string, bool> seen = new Dictionary<string, bool>();
            List<string> uniquePaths = new List<string>();
            int p;
            for (p = 0; p < cardPaths.Count; ++p)
            {
                string path = cardPaths[p];
                if (!String.IsNullOrEmpty(path) && !seen.ContainsKey(path))
                {
                    seen[path] = true;
                    uniquePaths.Add(path);
                }
            }
            uniquePaths.Sort();

            Dictionary<int, SPTESL.Card> sourceCards =
                new Dictionary<int, SPTESL.Card>();
            int loadedSets = 0;
            for (p = 0; p < uniquePaths.Count; ++p)
            {
                TextAsset asset = null;
                IEnumerator waiter = LoadTextAsset(uniquePaths[p], delegate(TextAsset x)
                {
                    asset = x;
                });
                while (!System.Object.ReferenceEquals(waiter, null) && waiter.MoveNext())
                    yield return waiter.Current;

                if (System.Object.ReferenceEquals(asset, null))
                    continue;

                CardSet set = null;
                try
                {
                    set = Deserialize<CardSet>(target, asset.text);
                }
                catch (Exception ex)
                {
                    Plugin.Warn("retail rule overlay: failed card set "
                        + uniquePaths[p] + ": " + ex.Message);
                }
                if (System.Object.ReferenceEquals(set, null) ||
                    System.Object.ReferenceEquals(set.Cards, null))
                    continue;

                loadedSets += 1;
                int c;
                for (c = 0; c < set.Cards.Count; ++c)
                {
                    SPTESL.Card card = set.Cards[c];
                    if (System.Object.ReferenceEquals(card, null) ||
                        card.ResourceId.Id == 0)
                        continue;
                    sourceCards[card.ResourceId.Id] = card;
                }
            }

            List<CardDefinition> sourceDefinitions = null;
            TextAsset definitionsAsset = null;
            IEnumerator definitionsWaiter = LoadTextAsset(
                GameConfiguration.DerivedAssetPath + "card_definitions",
                delegate(TextAsset x) { definitionsAsset = x; });
            while (!System.Object.ReferenceEquals(definitionsWaiter, null) &&
                   definitionsWaiter.MoveNext())
                yield return definitionsWaiter.Current;
            if (!System.Object.ReferenceEquals(definitionsAsset, null))
            {
                try
                {
                    sourceDefinitions = Deserialize<List<CardDefinition>>(
                        target, definitionsAsset.text);
                }
                catch (Exception ex)
                {
                    Plugin.Warn("retail rule overlay: card_definitions decode failed: "
                        + ex.Message);
                }
            }

            int mergedCards = MergeCards(target, sourceCards);
            int mergedDefinitions = MergeDefinitions(target, sourceDefinitions);
            int mergedLaneTriggers = 0;

            /* Lane triggers are represented by a private helper type in the
             * retail assembly.  Decode and convert it through reflection so we
             * do not duplicate its schema. */
            TextAsset laneAsset = null;
            IEnumerator laneWaiter = LoadTextAsset(
                GameConfiguration.DerivedAssetPath + "lane_triggers",
                delegate(TextAsset x) { laneAsset = x; });
            while (!System.Object.ReferenceEquals(laneWaiter, null) &&
                   laneWaiter.MoveNext())
                yield return laneWaiter.Current;
            if (!System.Object.ReferenceEquals(laneAsset, null))
            {
                try
                {
                    Type laneType = typeof(GameConfiguration).GetNestedType(
                        "SerializableLaneTriggers",
                        BindingFlags.NonPublic);
                    if (!System.Object.ReferenceEquals(laneType, null))
                    {
                        object decoded = Deserialize(target, laneAsset.text, laneType);
                        if (!System.Object.ReferenceEquals(decoded, null))
                        {
                            MethodInfo convert = laneType.GetMethod(
                                "ConvertToArray",
                                BindingFlags.Instance | BindingFlags.Public |
                                BindingFlags.NonPublic);
                            if (!System.Object.ReferenceEquals(convert, null))
                            {
                                LaneTrigger[] lanes = convert.Invoke(decoded, null)
                                    as LaneTrigger[];
                                if (!System.Object.ReferenceEquals(lanes, null))
                                {
                                    target.LaneTriggers = lanes;
                                    mergedLaneTriggers = lanes.Length;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Warn("retail rule overlay: lane_triggers decode failed: "
                        + ex.Message);
                }
            }

            RulesAvailable = mergedCards > 0 || mergedDefinitions > 0;
            Plugin.Log("retail rule overlay: source=bundled-json"
                + " paths=" + uniquePaths.Count
                + " sets=" + loadedSets
                + " sourceCards=" + sourceCards.Count
                + " mergedCards=" + mergedCards
                + " mergedDefinitions=" + mergedDefinitions
                + " laneTriggers=" + mergedLaneTriggers
                + " available=" + RulesAvailable);
            LogProbe(target, 157797006, "Afflicted Alit");
            LogProbe(target, 41830377, "Lightning Bolt");

            IsSettled = true;
        }

        private static IEnumerator LoadRetailLaneTriggers(
            GameConfiguration target)
        {
            TextAsset laneAsset = null;
            IEnumerator laneWaiter = LoadTextAsset(
                GameConfiguration.DerivedAssetPath + "lane_triggers",
                delegate(TextAsset x) { laneAsset = x; });
            while (!System.Object.ReferenceEquals(laneWaiter, null) &&
                   laneWaiter.MoveNext())
                yield return laneWaiter.Current;

            if (System.Object.ReferenceEquals(laneAsset, null))
            {
                /* Some shipped client bundles do not expose lane_triggers.
                 * The card-rule overlay is still valid; do not surface a
                 * coroutine exception that aborts the rest of startup. */
                Plugin.Warn("retail lane_triggers asset unavailable; keeping native lane table");
                yield break;
            }

            Type laneType = typeof(GameConfiguration).GetNestedType(
                "SerializableLaneTriggers",
                BindingFlags.NonPublic);
            if (System.Object.ReferenceEquals(laneType, null))
                throw new IOException("retail lane trigger decoder is unavailable");

            object decoded = Deserialize(target, laneAsset.text, laneType);
            MethodInfo convert = laneType.GetMethod(
                "ConvertToArray",
                BindingFlags.Instance | BindingFlags.Public |
                BindingFlags.NonPublic);
            if (System.Object.ReferenceEquals(decoded, null) ||
                System.Object.ReferenceEquals(convert, null))
                throw new IOException("retail lane trigger data could not be decoded");

            LaneTrigger[] lanes = convert.Invoke(decoded, null) as LaneTrigger[];
            if (System.Object.ReferenceEquals(lanes, null) ||
                lanes.Length != (int)CardLaneType.Count)
                throw new IOException("retail lane trigger table is incomplete");

            target.LaneTriggers = lanes;
            int ruleCount = 0;
            int i;
            for (i = 0; i < lanes.Length; ++i)
                if (!System.Object.ReferenceEquals(lanes[i], null) &&
                    !System.Object.ReferenceEquals(lanes[i].Triggers, null))
                    ruleCount += lanes[i].Triggers.Count;
            Plugin.Log("retail lane triggers loaded: lanes=" + lanes.Length
                + " rules=" + ruleCount);
        }

        private static IEnumerator LoadTextAsset(
            string path,
            TextAssetReceiver done)
        {
            AssetBundles.AssetBundleLoadAssetOperation operation =
                AssetCatalog.Instance.LoadAssetFromPathAsync(
                    path, typeof(TextAsset), "json");
            if (System.Object.ReferenceEquals(operation, null))
            {
                done(null);
                yield break;
            }

            while (!operation.IsDone())
                yield return null;

            done(operation.GetAsset<TextAsset>());
        }

        private static T Deserialize<T>(GameConfiguration config, string json)
            where T : class
        {
            return Deserialize(config, json, typeof(T)) as T;
        }

        private static object Deserialize(
            GameConfiguration config,
            string json,
            Type type)
        {
            MethodInfo closed = LoadJsonStringMethod.MakeGenericMethod(type);
            return closed.Invoke(config, new object[] { json });
        }

        private static int MergeConfiguration(
            GameConfiguration target,
            GameConfiguration source,
            string sourceName)
        {
            Dictionary<int, SPTESL.Card> cards =
                new Dictionary<int, SPTESL.Card>();
            if (!System.Object.ReferenceEquals(source.CardCollection, null))
            {
                int s;
                for (s = 0; s < source.CardCollection.Count; ++s)
                {
                    CardSet set = source.CardCollection[s];
                    if (System.Object.ReferenceEquals(set, null) ||
                        System.Object.ReferenceEquals(set.Cards, null))
                        continue;
                    int c;
                    for (c = 0; c < set.Cards.Count; ++c)
                    {
                        SPTESL.Card card = set.Cards[c];
                        if (!System.Object.ReferenceEquals(card, null) &&
                            card.ResourceId.Id != 0)
                            cards[card.ResourceId.Id] = card;
                    }
                }
            }

            int mergedCards = MergeCards(target, cards);
            int mergedDefinitions = MergeDefinitions(
                target, source.CardDefinitions);
            if (!System.Object.ReferenceEquals(source.LaneTriggers, null) &&
                source.LaneTriggers.Length > 0)
                target.LaneTriggers = source.LaneTriggers;
            if (!System.Object.ReferenceEquals(source.CommonAiWeights, null) &&
                source.CommonAiWeights.Count > 0)
                target.CommonAiWeights = source.CommonAiWeights;

            Plugin.Log("retail rule overlay: source=" + sourceName
                + " sourceCards=" + cards.Count
                + " mergedCards=" + mergedCards
                + " mergedDefinitions=" + mergedDefinitions
                + " laneTriggers="
                + (System.Object.ReferenceEquals(source.LaneTriggers, null)
                    ? 0 : source.LaneTriggers.Length));
            LogProbe(target, 157797006, "Afflicted Alit");
            LogProbe(target, 41830377, "Lightning Bolt");
            return mergedCards + mergedDefinitions;
        }

        private static int MergeCards(
            GameConfiguration target,
            IDictionary<int, SPTESL.Card> sourceCards)
        {
            if (System.Object.ReferenceEquals(target.CardCollection, null) ||
                System.Object.ReferenceEquals(sourceCards, null) ||
                sourceCards.Count == 0)
                return 0;

            int merged = 0;
            int s;
            for (s = 0; s < target.CardCollection.Count; ++s)
            {
                CardSet set = target.CardCollection[s];
                if (System.Object.ReferenceEquals(set, null) ||
                    System.Object.ReferenceEquals(set.Cards, null))
                    continue;
                int c;
                for (c = 0; c < set.Cards.Count; ++c)
                {
                    SPTESL.Card dst = set.Cards[c];
                    SPTESL.Card src;
                    if (System.Object.ReferenceEquals(dst, null) ||
                        !sourceCards.TryGetValue(dst.ResourceId.Id, out src) ||
                        System.Object.ReferenceEquals(src, null))
                        continue;

                    dst.DefaultRareAttributes = src.DefaultRareAttributes;
                    dst.PlayRequirements = src.PlayRequirements;
                    dst.BetrayRequirements = src.BetrayRequirements;
                    dst.PlayWeight = src.PlayWeight;
                    dst.ActionPlayInformation = src.ActionPlayInformation;
                    dst.AttackRequirements = src.AttackRequirements;
                    dst.StrongHintRequirements = src.StrongHintRequirements;
                    dst.ActivatedAbility = src.ActivatedAbility;
                    dst.TriggeredEffects = src.TriggeredEffects;
                    dst.Auras = src.Auras;
                    dst.MobilizeRecruitId = src.MobilizeRecruitId;
                    dst.HuntTypes = src.HuntTypes;
                    dst.UpgradesInto = src.UpgradesInto;
                    dst.DoubleCardReferences = src.DoubleCardReferences;
                    dst.DynamicTextAssignments = src.DynamicTextAssignments;
                    merged += 1;
                }
            }
            return merged;
        }

        private static int MergeDefinitions(
            GameConfiguration target,
            IList<CardDefinition> sourceDefinitions)
        {
            if (System.Object.ReferenceEquals(target.CardDefinitions, null) ||
                System.Object.ReferenceEquals(sourceDefinitions, null))
                return 0;

            Dictionary<string, CardDefinition> lookup =
                new Dictionary<string, CardDefinition>(StringComparer.Ordinal);
            int i;
            for (i = 0; i < sourceDefinitions.Count; ++i)
            {
                CardDefinition definition = sourceDefinitions[i];
                if (!System.Object.ReferenceEquals(definition, null) &&
                    !String.IsNullOrEmpty(definition.Name))
                    lookup[definition.Name] = definition;
            }

            int merged = 0;
            for (i = 0; i < target.CardDefinitions.Count; ++i)
            {
                CardDefinition dst = target.CardDefinitions[i];
                CardDefinition src;
                if (System.Object.ReferenceEquals(dst, null) ||
                    String.IsNullOrEmpty(dst.Name) ||
                    !lookup.TryGetValue(dst.Name, out src))
                    continue;
                dst.EntityAttributes = src.EntityAttributes;
                dst.Subtypes = src.Subtypes;
                dst.TriggeredEffects = src.TriggeredEffects;
                dst.Auras = src.Auras;
                merged += 1;
            }
            return merged;
        }

        private static void LogProbe(
            GameConfiguration config,
            int cardId,
            string name)
        {
            SPTESL.Card card = config.FindCardById(cardId);
            if (System.Object.ReferenceEquals(card, null))
            {
                Plugin.Warn("retail rule probe missing: " + name
                    + " card=" + cardId);
                return;
            }
            Plugin.Log("retail rule probe: " + name
                + " card=" + cardId
                + " actions="
                + (System.Object.ReferenceEquals(card.ActionPlayInformation, null)
                    ? -1 : card.ActionPlayInformation.Count)
                + " triggers="
                + (System.Object.ReferenceEquals(card.TriggeredEffects, null)
                    ? -1 : card.TriggeredEffects.Count)
                + " auras="
                + (System.Object.ReferenceEquals(card.Auras, null)
                    ? -1 : card.Auras.Count));
        }
    }
}
