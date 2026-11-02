using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using SPTESL;

namespace NyxTESLOffline
{
    /*
     * Conservative fallback for retail descriptions whose server rule graph is
     * absent from the shipped client configuration. It compiles only exact,
     * unambiguous summon templates and leaves every other card pending for a
     * card-specific reconstruction. Premium uses the same definition; alternate
     * IDs are compiled independently before VariantCardRules inherits missing
     * compositions.
     */
    internal static class RetailTextSummonCompiler
    {
        static readonly Regex DamagePattern = new Regex(
            @"^deal (\d+) damage(?: to (your opponent|a random enemy(?: creature)?|a creature|all enemy creatures|all enemy creatures in this lane|all other creatures(?: in this lane)?|each player))?[.!]?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static readonly int[] SteelDaggerSummonIds = { -501985501, -474818882, -846195381, -258100774 };
        static readonly int[] EastmarchCrusaderIds = { -212193127, 2132131160 };
        const int BloodCrazedDaedrothId = 169936922;
        static readonly Regex BuffPattern = new Regex(
            @"^give (?:(?:a|your) (?:friendly )?)?creature \+(\d+)\/\+(\d+)(?: and guard)?[.!]?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static readonly Regex HealPattern = new Regex(
            @"^(?:you gain|gain) (\d+) health[.!]?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static readonly Regex SummonHealPattern = new Regex(
            @"^gain (\d+) health[.!]?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static readonly Regex SummonGainMaxManaPattern = new Regex(
            @"^gain \+(\d+) max magicka[.!]?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static readonly Regex DestroyWoundedPattern = new Regex(
            @"^destroy a wounded creature[.!]?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static readonly Regex DestroyLowPowerEnemyPattern = new Regex(
            @"^destroy an enemy creature with (\d+) power or less[.!]?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static readonly Regex EnemyLanePowerDebuffPattern = new Regex(
            @"^give all enemy creatures in this lane -(\d+)\/[-+]?0[.!]?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static readonly Regex SummonBuffPerEnemyInLanePattern = new Regex(
            @"^\+(\d+)\/\+(\d+) for each enemy creature in this lane[.!]?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static readonly Regex ConditionalSelfBuffPattern = new Regex(
            @"^\+(\d+)\/\+(\d+) if you have another (?:friendly )?(.+?)[.!]?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static readonly Regex DrawWithTwoOtherCreaturesPattern = new Regex(
            @"^draw a card if you have two other creatures[.!]?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static readonly Regex KeywordPattern = new Regex(
            @"^give (?:a|your) (?:friendly )?creature (?:a )?(guard|cover|ward)[.!]?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static readonly Regex ActionDamagePattern = new Regex(
            @"^deal (\d+) damage(?: to (a creature|your opponent))?[.!]?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static readonly Regex ActionBuffPattern = new Regex(
            @"^give a creature \+(\d+)\/\+(\d+)[.!]?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static readonly Regex ActionDebuffPattern = new Regex(
            @"^give a creature -(\d+)\/-(\d+)[.!]?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static readonly Regex ActionHealPattern = new Regex(
            @"^gain (\d+) health[.!]?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static readonly Regex ActionUnsummonFriendlyPattern = new Regex(
            @"^unsummon a friendly creature[.!]?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static readonly Regex ActionDrawPattern = new Regex(
            @"^draw (a|one|two|three|four|five) cards?[.!]?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static readonly Regex ActionRandomDrawPattern = new Regex(
            @"^draw a random (creature|item|action|support)[.!]?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static readonly Regex ActionAreaDamagePattern = new Regex(
            @"^deal (\d+) damage to (all enemies|each enemy creature|all enemy creatures|all creatures|each player)[.!]?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static readonly Regex FriendlyAuraPattern = new Regex(
            @"^friendly creatures have \+(\d+)\/\+(\d+)[.!]?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static readonly HashSet<int> FriendlyAuraCardIds = new HashSet<int>();

        internal static void Apply(GameConfiguration config, HashSet<int> manuallyRecovered = null)
        {
            int installed = 0;
            var counts = new Dictionary<string, int>();
            foreach (var set in config.CardCollection)
            foreach (var card in set.Cards)
            {
                // A text fallback must never replace a graph installed by the
                // recovery modules earlier in the shared client/runner pipeline.
                if(manuallyRecovered!=null && manuallyRecovered.Contains(card.ResourceId.Id))continue;
                string pattern;
                if (Install(card, out pattern))
                {
                    installed++;
                    int count; counts.TryGetValue(pattern, out count); counts[pattern] = count + 1;
                }
            }
            Plugin.Log("retail summon text compiler: installed=" + installed
                + "; patterns=" + String.Join(",", FormatCounts(counts).ToArray())
                + "; unsupported descriptions remain pending");
        }

        static List<string> FormatCounts(Dictionary<string, int> counts)
        {
            var result = new List<string>();
            foreach (var pair in counts) result.Add(pair.Key + ":" + pair.Value);
            return result;
        }

        static bool Install(Card card, out string pattern)
        {
            pattern = "";
            if (card == null) return false;
            if(card.ResourceId.Id==496581115 && card.Auras.Count==0 &&
                !card.ActivatedAbility.IsValidAbility() &&
                PlainText(card.Description).IndexOf("Summon: Give a creature Cover",
                    StringComparison.OrdinalIgnoreCase)>=0)
            {
                // Gloomlurker has no usable retail composition in this client
                // config. Restore its exact printed, targeted Cover trigger.
                card.TriggeredEffects.Clear();
                card.ActionPlayInformation.Clear();
                AddTargeted(card,new CardActions.Cover { MultipleTurns=false },
                    new BoardTarget { CreatureOnly=true, AllOwners=true });
                pattern="summon-grant-cover-to-creature";
                return true;
            }
            if(Array.IndexOf(SteelDaggerSummonIds,card.ResourceId.Id)>=0 &&
                PlainText(card.Description).IndexOf("Summon: Put a Steel Dagger into your hand",
                    StringComparison.OrdinalIgnoreCase)>=0)
            {
                var existing=card.TriggeredEffects.Find(t=>t.TriggerType==EffectTriggerType.Summon);
                bool nativeCreateToken=existing!=null && existing.EffectGroup!=null && existing.EffectGroup.Effects
                    .SelectMany(e=>e.PlayerActions).Any(a=>a.Action is PlayerActions.CreateToken);
                if(nativeCreateToken)return false;
                if(existing!=null)card.TriggeredEffects.Remove(existing);
                var summon=NewSummon(card,PlayTargetingType.NoTarget);
                var token=new PlayerEffectAction {HashId=-1,Action=new PlayerActions.CreateToken {
                    CardReference=new EditorCardReference(939372786),CreateInZoneType=GameZoneType.Hand,
                    NumTokens=new ActionValueData(1),InsertPosition=new InsertPosition {
                        Type=InsertPosition.LaneChoiceType.Constant,Lane=LaneIndexEnum.Left,
                        InsertionType=CardInsertionType.Bottom}}};
                token.Selectors.Add(new PlayerConditionals.PlayerRoleType {
                    Role=PlayerRole.Self,Check=PlayerConditionals.PlayerRoleType.Requirement.Is});
                summon.EffectGroup.Effects[0].PlayerActions.Add(token);
                pattern="summon-create-steel-dagger-in-hand";
                return true;
            }
            if(Array.IndexOf(EastmarchCrusaderIds,card.ResourceId.Id)>=0 &&
                PlainText(card.Description).IndexOf("Summon: Draw a card if your opponent has a destroyed rune",
                    StringComparison.OrdinalIgnoreCase)>=0)
            {
                var existing=card.TriggeredEffects.Find(t=>t.TriggerType==EffectTriggerType.Summon);
                if(existing!=null)card.TriggeredEffects.Remove(existing);
                AddSummonAction(card,new DrawIfOpponentRuneLost());
                pattern="summon-draw-if-opponent-rune-destroyed";
                return true;
            }
            if(card.ResourceId.Id==BloodCrazedDaedrothId &&
                PlainText(card.Description).IndexOf("Summon: Draw a card if there is a Wounded creature in this lane",
                    StringComparison.OrdinalIgnoreCase)>=0)
            {
                var existing=card.TriggeredEffects.Find(t=>t.TriggerType==EffectTriggerType.Summon);
                if(existing!=null)card.TriggeredEffects.Remove(existing);
                AddSummonAction(card,new DrawIfWoundedCreatureInSourceLane());
                pattern="summon-draw-if-wounded-creature-in-lane";
                return true;
            }
            if ((card.ResourceId.Id == 132354610 || card.ResourceId.Id == -648740334) &&
                card.Auras.Count == 0 && !card.ActivatedAbility.IsValidAbility() &&
                PlainText(card.Description).IndexOf("for each enemy creature in this lane",
                    StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // The retail text is explicit, but the shipped offline graph
                // does not resolve its lane-count buff. Replace only these two
                // verified creature definitions; do not touch neighboring items.
                card.TriggeredEffects.Clear();
                card.ActionPlayInformation.Clear();
            }
            if(card.ResourceId.Id==-1652308136 && card.Auras.Count==0 &&
                !card.ActivatedAbility.IsValidAbility() &&
                PlainText(card.Description).IndexOf("if you have another",StringComparison.OrdinalIgnoreCase)>=0 &&
                PlainText(card.Description).IndexOf("Orc",StringComparison.OrdinalIgnoreCase)>=0)
            {
                card.TriggeredEffects.Clear();
                card.ActionPlayInformation.Clear();
                AddSummonAction(card,new ConditionalSelfBuff(2,2,card.ResourceId.Id));
                pattern="summon-self-buff-if-another-friendly-orc";
                return true;
            }
            if(card.ResourceId.Id==-1071100254 && card.Auras.Count==0 &&
                !card.ActivatedAbility.IsValidAbility() &&
                PlainText(card.Description).IndexOf("if your opponent has more cards in hand",
                    StringComparison.OrdinalIgnoreCase)>=0)
            {
                card.TriggeredEffects.Clear();
                card.ActionPlayInformation.Clear();
                AddSummonAction(card,new ConditionalHandCountSelfBuff(2,2));
                pattern="summon-self-buff-if-opponent-has-more-cards-in-hand";
                return true;
            }
            if((card.ResourceId.Id==1984109634 || card.ResourceId.Id==400984530) &&
                card.Auras.Count==0 && !card.ActivatedAbility.IsValidAbility() &&
                PlainText(card.Description).IndexOf("if you have more health than your opponent",
                    StringComparison.OrdinalIgnoreCase)>=0 &&
                PlainText(card.Description).IndexOf("draw two cards",StringComparison.OrdinalIgnoreCase)>=0)
            {
                card.TriggeredEffects.Clear();
                card.ActionPlayInformation.Clear();
                AddSummonAction(card,new DrawIfOwnerHasHealthAdvantage(2));
                pattern="summon-draw-two-if-owner-has-health-advantage";
                return true;
            }
            if(card.ResourceId.Id==2092651621 && card.Auras.Count==0 &&
                !card.ActivatedAbility.IsValidAbility() &&
                PlainText(card.Description).IndexOf("Gains Charge if you have another Orc",
                    StringComparison.OrdinalIgnoreCase)>=0)
            {
                // Retail's two count branches are present as no-op FX shells.
                // Replace only the exact printed Orc gate with a state effect.
                card.TriggeredEffects.Clear();
                card.ActionPlayInformation.Clear();
                AddSummonAction(card,new ConditionalChargeIfAnotherOrc());
                pattern="summon-gain-charge-if-another-friendly-orc";
                return true;
            }
            if((card.ResourceId.Id==-1903529353 || card.ResourceId.Id==-461655438) &&
                card.Auras.Count==0 && !card.ActivatedAbility.IsValidAbility() &&
                PlainText(card.Description).IndexOf("enemy creature in this lane",
                    StringComparison.OrdinalIgnoreCase)>=0)
            {
                // Silvenar Tracker's retail graph is a no-op count shell. The
                // exact IDs and lane text constrain these conditional keywords.
                card.TriggeredEffects.Clear();
                card.ActionPlayInformation.Clear();
                AddSummonAction(card,new ConditionalLaneKeyword(card.ResourceId.Id));
                pattern=card.ResourceId.Id==-1903529353
                    ? "summon-drain-if-enemy-in-source-lane"
                    : "summon-charge-if-wounded-enemy-in-source-lane";
                return true;
            }
            if(card.ResourceId.Id==-2082355203 && card.Auras.Count==0 &&
                !card.ActivatedAbility.IsValidAbility() && card.TriggeredEffects.Count==0 &&
                card.ActionPlayInformation.Count==0 &&
                PlainText(card.Description).IndexOf("Friendly creatures gain Rally this turn",
                    StringComparison.OrdinalIgnoreCase)>=0)
            {
                AddSummonAction(card,new TemporaryFriendlyRally());
                pattern="summon-friendly-creatures-gain-rally-this-turn";
                return true;
            }
            if ((card.ResourceId.Id == -1652308136 || card.ResourceId.Id == 1477162806 ||
                 card.ResourceId.Id == 1544383450 || card.ResourceId.Id == 780490122) &&
                card.Auras.Count == 0 && !card.ActivatedAbility.IsValidAbility() &&
                PlainText(card.Description).IndexOf("if you have another",
                    StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // These four retail gates have incomplete offline trigger graphs.
                // The exact card IDs and parsed printed clause constrain the override.
                card.TriggeredEffects.Clear();
                card.ActionPlayInformation.Clear();
            }
            if ((card.ResourceId.Id == 1931741259 || card.ResourceId.Id == 964579944 || card.ResourceId.Id == -1206609355) &&
                card.ActionPlayInformation.Count == 0 && card.Auras.Count == 0 &&
                !card.ActivatedAbility.IsValidAbility())
            {
                // These exact retail Last Gasp descriptions have a shipped
                // trigger shell that emits the trigger but misses its state
                // effect in the offline host. Replace only that incomplete
                // shell with the narrowly compiled text effect.
                card.TriggeredEffects.Clear();
                if (InstallSimpleLastGasp(card, out pattern)) return true;
            }
            if (card.ResourceId.Id == -1299948782 &&
                card.ActionPlayInformation.Count == 0 && card.Auras.Count == 0 &&
                !card.ActivatedAbility.IsValidAbility())
            {
                // Retail/reference text calls for two other Strength creatures.
                // The shipped graph counts any creatures and also admits the source.
                card.TriggeredEffects.Clear();
                AddSummonAction(card, new DrawIfTwoOtherStrengthCreatures());
                pattern = "summon-draw-if-two-other-friendly-strength-creatures";
                return true;
            }
            if (card.TriggeredEffects.Count != 0 || card.ActionPlayInformation.Count != 0 ||
                card.Auras.Count != 0 || card.ActivatedAbility.IsValidAbility()) return false;

            if (InstallFriendlyAura(card, out pattern)) return true;

            if (InstallCombinedSummonDrawShuffle(card, out pattern)) return true;

            if (InstallSimpleLastGasp(card, out pattern)) return true;

            if (InstallSimpleAction(card, out pattern)) return true;

            string description = Regex.Replace(card.Description ?? "", "<[^>]*>", " ");
            description = description.Replace("&amp;", "&").Replace("&lt;", "<")
                .Replace("&gt;", ">").Replace("&nbsp;", " ").Replace("&quot;", "\"")
                .Replace("&#39;", "'");
            description = Regex.Replace(description, @"\s+", " ").Trim();
            // Some retail descriptions include formatting whitespace before
            // the label colon (for example, "Last Gasp :"). Normalize only
            // that punctuation boundary so the label parser accepts both.
            description = Regex.Replace(description, @"\s+:", ":");
            int marker = description.IndexOf("Summon:", StringComparison.OrdinalIgnoreCase);
            if (marker < 0) return false;
            int start = marker + "Summon:".Length;
            int end = description.IndexOfAny(new[] { '\n', '\r' }, start);
            string effectText = (end < 0 ? description.Substring(start) : description.Substring(start, end - start)).Trim();
            // Inline retail markup can leave a space before terminal punctuation.
            effectText = Regex.Replace(effectText, @"\s+([.!])$", "$1");
            var nextMechanic = Regex.Match(effectText,
                @"\s+(?:Last\s+Gasp|Slay|Pilfer|Activate|Expertise|When\s+[^:]+):",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if(nextMechanic.Success)effectText=effectText.Substring(0,nextMechanic.Index).Trim();

            var summonHeal = SummonHealPattern.Match(PlainText(effectText));
            if (summonHeal.Success)
            {
                int amount = Int32.Parse(summonHeal.Groups[1].Value);
                AddUnselected(card, new CardActions.Heal {
                    HealthGain = new ActionValueData(amount)
                }, new BoardTarget { Avatar = true, AvatarOnly = true, FriendlyOnly = true });
                pattern = "summon-heal-owner-" + amount;
                return true;
            }

            var gainMaxMana = SummonGainMaxManaPattern.Match(PlainText(effectText));
            if (gainMaxMana.Success)
            {
                AddSummonAction(card, new GainMaxMagicka {
                    Amount = Int32.Parse(gainMaxMana.Groups[1].Value)
                });
                pattern = "summon-gain-max-magicka-and-current-magicka";
                return true;
            }

            string normalizedSummonText = PlainText(effectText).Trim().TrimEnd('.').Trim();
            if(card.ResourceId.Id==1849423892 &&
                normalizedSummonText.Equals("+1/+1 if you have an action in your hand",StringComparison.OrdinalIgnoreCase) &&
                !card.TriggeredEffects.Any(t=>t.TriggerType==EffectTriggerType.Summon))
            {
                AddSummonAction(card,new ConditionalActionInHandBuff());
                pattern="summon-plus-one-plus-one-if-action-in-hand";
                return true;
            }
            var conditionalBuff=ConditionalSelfBuffPattern.Match(normalizedSummonText);
            if(conditionalBuff.Success && InstallConditionalSelfBuff(card,conditionalBuff,out pattern))
                return true;
            var perEnemyInLane = SummonBuffPerEnemyInLanePattern.Match(normalizedSummonText);
            if (perEnemyInLane.Success && card.HasSubtype(CardSubtype.Creature) &&
                (card.ResourceId.Id == 132354610 || card.ResourceId.Id == -648740334))
            {
                AddSummonAction(card, new BuffPerEnemyCreatureInLane(
                    Int32.Parse(perEnemyInLane.Groups[1].Value),
                    Int32.Parse(perEnemyInLane.Groups[2].Value)));
                pattern = "summon-buff-per-enemy-creature-in-source-lane";
                return true;
            }
            if (normalizedSummonText.Equals("draw a card. if it costs 7 or more, reduce its cost by 1",
                StringComparison.OrdinalIgnoreCase))
            {
                AddSummonAction(card, new DrawAndDiscountExpensive());
                pattern = "summon-draw-and-discount-cost-seven-plus";
                return true;
            }
            if (normalizedSummonText.Equals("invade", StringComparison.OrdinalIgnoreCase) &&
                (card.ResourceId.Id == 1759297880 || card.ResourceId.Id == 1285002775 || card.ResourceId.Id == -499614382))
            {
                var action = new PlayerEffectAction { HashId = -1, Action = new PlayerActions.Invade {
                    TimesToInvade = new ActionValueData(1), IsSummonInvade = true } };
                action.Selectors.Add(new PlayerConditionals.PlayerRoleType {
                    Role = PlayerRole.Self, Check = PlayerConditionals.PlayerRoleType.Requirement.Is });
                NewSummon(card, PlayTargetingType.NoTarget).EffectGroup.Effects[0].PlayerActions.Add(action);
                pattern = "summon-invade-once";
                return true;
            }
            if (normalizedSummonText.Equals("battle an enemy creature", StringComparison.OrdinalIgnoreCase) &&
                (card.ResourceId.Id == -117580191 || card.ResourceId.Id == 824194903))
            {
                var fight = new CardActions.Fight();
                fight.InciterSelectors.Add(Role(CardRole.Self));
                AddTargeted(card, fight, new BoardTarget { Enemy = true, CreatureOnly = true });
                pattern = "summon-battle-enemy-creature";
                return true;
            }
            if (normalizedSummonText.Equals("+1/+1 for each other friendly creature", StringComparison.OrdinalIgnoreCase) &&
                (card.ResourceId.Id == 1916219696 || card.ResourceId.Id == -2018410927 || card.ResourceId.Id == -1050726668))
            {
                AddSummonAction(card, new BuffPerOtherFriendlyCreature(card.ResourceId.Id == 1916219696));
                pattern = card.ResourceId.Id == 1916219696
                    ? "summon-plus-one-plus-one-per-other-friendly-endurance-creature"
                    : "summon-plus-one-plus-one-per-other-friendly-creature";
                return true;
            }
            if (DestroyWoundedPattern.IsMatch(PlainText(effectText)))
            {
                AddTargeted(card, new CardActions.Destroy(),
                    new BoardTarget { CreatureOnly = true, Wounded = true, AllOwners = true });
                pattern = "summon-destroy-wounded-creature";
                return true;
            }
            var lowPowerDestroy = DestroyLowPowerEnemyPattern.Match(PlainText(effectText));
            if (lowPowerDestroy.Success)
            {
                AddTargeted(card, new CardActions.Destroy(), new BoardTarget {
                    Enemy = true, CreatureOnly = true,
                    MaximumAttack = Int32.Parse(lowPowerDestroy.Groups[1].Value)
                });
                pattern = "summon-destroy-enemy-at-or-below-power";
                return true;
            }
            if (String.Equals(normalizedSummonText, "destroy all wounded enemy creatures in this lane", StringComparison.OrdinalIgnoreCase))
            {
                AddUnselected(card, new CardActions.Destroy(),
                    new BoardTarget { Enemy = true, CreatureOnly = true, Wounded = true, SameLane = true });
                pattern = "summon-destroy-all-wounded-enemies-in-lane";
                return true;
            }
            var laneDebuff = EnemyLanePowerDebuffPattern.Match(PlainText(effectText));
            if (laneDebuff.Success)
            {
                var action = new CardActions.Debuff();
                action.Enchantment.PermanentExpiresOnLeavingBoard = true;
                action.Enchantment.AttributeModifications.Add(new EnchantmentAttributeModification(
                    EnchantmentAttributeModificationType.Add, AttributeType.Attack,
                    -Int32.Parse(laneDebuff.Groups[1].Value)));
                AddUnselected(card, action,
                    new BoardTarget { Enemy = true, CreatureOnly = true, SameLane = true });
                pattern = "summon-debuff-enemies-in-lane-power";
                return true;
            }
            if (String.Equals(normalizedSummonText, "all enemy creatures lose guard", StringComparison.OrdinalIgnoreCase))
            {
                var action = new CardActions.Debuff();
                action.Enchantment.PermanentExpiresOnLeavingBoard = true;
                action.Enchantment.AttributeModifications.Add(new EnchantmentAttributeModification(
                    EnchantmentAttributeModificationType.Set, AttributeType.Taunt, 0));
                AddUnselected(card, action, new BoardTarget { Enemy = true, CreatureOnly = true });
                pattern = "summon-remove-guard-from-enemies";
                return true;
            }

            if (DrawWithTwoOtherCreaturesPattern.IsMatch(PlainText(effectText)))
            {
                AddSummonAction(card, new DrawIfTwoOtherCreatures());
                pattern = "summon-draw-if-two-other-friendly-creatures";
                return true;
            }

            var damage = DamagePattern.Match(effectText);
            if (damage.Success)
            {
                int amount = Int32.Parse(damage.Groups[1].Value);
                string target = damage.Groups[2].Value.ToLowerInvariant();
                if (target == "your opponent")
                {
                    AddTargeted(card, new CardActions.Damage {
                        DamageSource = CardRole.Self, HealthLoss = new ActionValueData(amount)
                    }, new BoardTarget { Enemy = true, Avatar = true, AvatarOnly = true });
                    pattern = "damage-opponent";
                }
                else if (target == "a creature")
                {
                    AddTargeted(card, new CardActions.Damage {
                        DamageSource = CardRole.Self, HealthLoss = new ActionValueData(amount)
                    }, new BoardTarget { CreatureOnly = true, AllOwners = true });
                    pattern = "damage-creature";
                }
                else if (target == "a random enemy" || target == "a random enemy creature")
                {
                    AddRandom(card, new CardActions.Damage {
                        DamageSource = CardRole.Self, HealthLoss = new ActionValueData(amount)
                    }, new BoardTarget { Enemy = true, Avatar = true,
                        CreatureOnly = target == "a random enemy creature" });
                    pattern = "damage-random-enemy";
                }
                else if (target == "all enemy creatures" || target == "all enemy creatures in this lane" || target == "all other creatures in this lane")
                {
                    AddUnselected(card, new CardActions.Damage {
                        DamageSource = CardRole.Self, HealthLoss = new ActionValueData(amount)
                    }, new BoardTarget {
                        Enemy = target.StartsWith("all enemy", StringComparison.OrdinalIgnoreCase),
                        CreatureOnly = true, SameLane = target != "all enemy creatures", OtherThanSelf = target == "all other creatures in this lane",
                        AllOwners = target.StartsWith("all other", StringComparison.OrdinalIgnoreCase)
                    });
                    pattern = target == "all enemy creatures" ? "damage-all-enemy-creatures"
                        : target.StartsWith("all enemy", StringComparison.OrdinalIgnoreCase)
                            ? "damage-all-enemies-in-lane" : "damage-other-creatures-in-lane";
                }
                else if(target=="all other creatures")
                {
                    AddUnselected(card,new CardActions.Damage {
                        DamageSource=CardRole.Self,HealthLoss=new ActionValueData(amount)
                    },new BoardTarget { CreatureOnly=true,AllOwners=true,OtherThanSelf=true });
                    pattern="damage-all-other-creatures";
                }
                else if (target == "each player")
                {
                    AddUnselected(card, new CardActions.Damage {
                        DamageSource = CardRole.Self, HealthLoss = new ActionValueData(amount)
                    }, new BoardTarget { Avatar = true, AllOwners = true });
                    pattern = "damage-each-player";
                }
                else
                {
                    AddTargeted(card, new CardActions.Damage {
                        DamageSource = CardRole.Self, HealthLoss = new ActionValueData(amount)
                    }, new BoardTarget { Avatar = true, AllOwners = true });
                    pattern = "damage-selected-target";
                }
                return true;
            }

            if (String.Equals(effectText, "draw a card", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(effectText, "draw a card.", StringComparison.OrdinalIgnoreCase))
            {
                AddDraw(card, 1);
                pattern = "draw-one";
                return true;
            }

            var heal = HealPattern.Match(effectText);
            if (heal.Success)
            {
                AddUnselected(card, new CardActions.Heal {
                    HealthGain = new ActionValueData(Int32.Parse(heal.Groups[1].Value))
                }, new BoardTarget { Avatar = true, FriendlyOnly = true });
                pattern = "heal-owner-avatar";
                return true;
            }

            var buff = BuffPattern.Match(effectText);
            if (buff.Success)
            {
                int power = Int32.Parse(buff.Groups[1].Value);
                int health = Int32.Parse(buff.Groups[2].Value);
                var action = new CardActions.Buff();
                if (power != 0) action.Enchantment.AttributeModifications.Add(
                    new EnchantmentAttributeModification(EnchantmentAttributeModificationType.Add, AttributeType.Attack, power));
                if (health != 0) action.Enchantment.AttributeModifications.Add(
                    new EnchantmentAttributeModification(EnchantmentAttributeModificationType.Add, AttributeType.MaxHealth, health));
                if (effectText.IndexOf("and guard", StringComparison.OrdinalIgnoreCase) >= 0)
                    action.Enchantment.AttributeModifications.Add(
                        new EnchantmentAttributeModification(EnchantmentAttributeModificationType.Set, AttributeType.Taunt, 1));
                bool friendlyOnly = effectText.IndexOf("friendly", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    effectText.StartsWith("give your ", StringComparison.OrdinalIgnoreCase);
                AddTargeted(card, action, new BoardTarget { CreatureOnly = true, FriendlyOnly = friendlyOnly, AllOwners = !friendlyOnly });
                pattern = "buff-selected-creature";
                return true;
            }

            var keyword = KeywordPattern.Match(effectText);
            if (keyword.Success)
            {
                string granted=keyword.Groups[1].Value;
                if((card.ResourceId.Id==-1209831649 || card.ResourceId.Id==-987994366 || card.ResourceId.Id==496581115) &&
                    (granted.Equals("guard",StringComparison.OrdinalIgnoreCase) ||
                     granted.Equals("ward",StringComparison.OrdinalIgnoreCase) ||
                     granted.Equals("cover",StringComparison.OrdinalIgnoreCase)))
                    card.TriggeredEffects.RemoveAll(t=>t!=null && t.TriggerType==EffectTriggerType.Summon);
                ICardAction action;
                if(granted.Equals("ward",StringComparison.OrdinalIgnoreCase)) action=new CardActions.Ward();
                else if(granted.Equals("cover",StringComparison.OrdinalIgnoreCase)) action=new CardActions.Cover { MultipleTurns=false };
                else {
                    var keywordBuff=new CardActions.Buff();
                    AttributeType attribute=granted.Equals("guard",StringComparison.OrdinalIgnoreCase)
                        ?AttributeType.Taunt:AttributeType.Cover;
                    keywordBuff.Enchantment.AttributeModifications.Add(
                        new EnchantmentAttributeModification(EnchantmentAttributeModificationType.Set,attribute,1));
                    action=keywordBuff;
                }
                bool friendlyOnly=normalizedSummonText.IndexOf("friendly",StringComparison.OrdinalIgnoreCase)>=0 ||
                    normalizedSummonText.StartsWith("give your ",StringComparison.OrdinalIgnoreCase);
                AddTargeted(card, action, new BoardTarget { CreatureOnly = true,
                    FriendlyOnly = friendlyOnly, AllOwners = !friendlyOnly });
                pattern = "summon-grant-"+granted.ToLowerInvariant()+"-to-creature";
                return true;
            }

            string normalizedEffectText = PlainText(effectText).Trim().TrimEnd('.').Trim();
            if (String.Equals(normalizedEffectText, "shackle an enemy creature", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(normalizedEffectText, "shackle an enemy creature in this lane", StringComparison.OrdinalIgnoreCase))
            {
                AddTargeted(card, new CardActions.Freeze {
                    PermanentFreeze = false, Type = EnchantmentUtilities.FreezeType.Normal
                }, new BoardTarget { Enemy = true, CreatureOnly = true,
                    SameLane = normalizedEffectText.IndexOf("in this lane", StringComparison.OrdinalIgnoreCase) >= 0 });
                pattern = "shackle-enemy-creature";
                return true;
            }
            return false;
        }

        static bool InstallFriendlyAura(Card card, out string pattern)
        {
            pattern = "";
            if (!card.HasSubtype(CardSubtype.Support)) return false;
            string text = PlainText(card.Description);
            var match = FriendlyAuraPattern.Match(text);
            if (!match.Success) return false;

            var enchantment = new Enchantment();
            int attack = Int32.Parse(match.Groups[1].Value);
            int health = Int32.Parse(match.Groups[2].Value);
            if (attack != 0) enchantment.AttributeModifications.Add(
                new EnchantmentAttributeModification(EnchantmentAttributeModificationType.Add, AttributeType.Attack, attack));
            if (health != 0) enchantment.AttributeModifications.Add(
                new EnchantmentAttributeModification(EnchantmentAttributeModificationType.Add, AttributeType.MaxHealth, health));
            var aura = new Aura();
            var applied = new AuraEnchantment { Enchantment = enchantment };
            applied.Selectors.Add(new FriendlyCreatureAuraTarget());
            aura.EnchantmentsToApply.Add(applied);
            card.Auras.Add(aura);
            FriendlyAuraCardIds.Add(card.ResourceId.Id);
            pattern = "aura-friendly-creatures-" + attack + "-" + health;
            return true;
        }

        internal static void RefreshFriendlyAuras(ServerEffectUtilities.TriggerGameState state)
        {
            if (FriendlyAuraCardIds.Count == 0) return;
            var cards = state.CardCatalog.GatherDynamicCardList();
            var sources = cards.FindAll(c => FriendlyAuraCardIds.Contains(c.CardId) &&
                c.CurrentGameZoneName == GameZoneName.SupportBoard && !c.IsDying() && !c.Silenced &&
                GameManager.Instance.Config.FindCardById(c.CardId).Auras.Count > 0);
            foreach (var target in cards)
            {
                bool onBoard = target.CurrentGameZoneName == GameZoneName.CreatureBoard &&
                    target.HasSubtype(CardSubtype.Creature) && !target.IsDying();
                bool changed = false;
                for (int i = target.AuraEnchantments.Count - 1; i >= 0; --i)
                {
                    var aura = target.AuraEnchantments[i];
                    if (!FriendlyAuraCardIds.Contains(aura.SourceCardId)) continue;
                    bool valid = onBoard && sources.Exists(s => s.InstanceId == aura.SourceId &&
                        s.CardId == aura.SourceCardId && s.OwnerNetId == target.OwnerNetId);
                    if (valid && target.Enchantments.Contains(aura)) continue;
                    target.AuraEnchantments.RemoveAt(i);
                    target.Enchantments.Remove(aura);
                    changed = true;
                }
                if (onBoard)
                foreach (var source in sources)
                {
                    if (source.OwnerNetId != target.OwnerNetId ||
                        target.AuraEnchantments.Exists(a => a.SourceId == source.InstanceId && a.SourceCardId == source.CardId)) continue;
                    var definition = GameManager.Instance.Config.FindCardById(source.CardId);
                    if (definition == null || definition.Auras.Count == 0 || definition.Auras[0].EnchantmentsToApply.Count == 0) continue;
                    var added = new EnchantmentInstance(definition.Auras[0].EnchantmentsToApply[0].Enchantment,
                        1, source.CardId, source.InstanceId, target);
                    target.AuraEnchantments.Add(added);
                    target.Enchantments.Add(added);
                    changed = true;
                }
                if (changed)
                {
                    if (state.Log != null) state.Log.StartEvent(CcgRpc.HistoryType.EventTrigger,
                        target.OwnerNetId, target.NetUid, target.NetUid, -1);
                    try
                    {
                        target.RecalculateEnchantmentsWithTriggers(state, null);
                        HistoryLoggingUtilities.LogFullCardRefresh(state.Log, state.CardCatalog, target, false);
                    }
                    finally { if (state.Log != null) state.Log.EndEvent(); }
                }
            }
        }

        static string PlainText(string value)
        {
            string text = Regex.Replace(value ?? "", "<[^>]*>", " ");
            text = text.Replace("&amp;", "&").Replace("&lt;", "<")
                .Replace("&gt;", "> ").Replace("&nbsp;", " ").Replace("&quot;", "\"")
                .Replace("&#39;", "'").Replace('\u00a0',' ');
            return Regex.Replace(text, @"\s+", " ").Trim();
        }

        // Exact one-clause action templates. Text with conditions or multiple
        // effects stays pending until the full sequence can be represented.
        static bool InstallSimpleAction(Card card, out string pattern)
        {
            pattern = "";
            string text = Regex.Replace(card.Description ?? "", "<[^>]*>", " ");
            text = text.Replace("&amp;", "&").Replace("&lt;", "<")
                .Replace("&gt;", ">").Replace("&nbsp;", " ").Replace("&quot;", "\"")
                .Replace("&#39;", "'");
            text = Regex.Replace(text, @"\s+", " ").Trim();
            if (Regex.IsMatch(text, @"\b(?:Summon|Last\s+Gasp|Activate|Slay|Pilfer):", RegexOptions.IgnoreCase))
                return false;

            var draw = ActionDrawPattern.Match(text);
            if (draw.Success)
            {
                string countText = draw.Groups[1].Value.ToLowerInvariant();
                int count = countText == "a" || countText == "one" ? 1 :
                    countText == "two" ? 2 : countText == "three" ? 3 :
                    countText == "four" ? 4 : 5;
                AddPlayActionWithoutTarget(card, new DrawOne { Count = count }, Role(CardRole.Self));
                pattern = "action-draw-" + count;
                return true;
            }

            var randomDraw = ActionRandomDrawPattern.Match(text);
            if (randomDraw.Success)
            {
                string type=randomDraw.Groups[1].Value.ToLowerInvariant();
                AddPlayActionWithoutTarget(card,new DrawRandomCardType(type),Role(CardRole.Self));
                pattern="action-draw-random-"+type;
                return true;
            }

            var areaDamage = ActionAreaDamagePattern.Match(text);
            if (areaDamage.Success)
            {
                string target = areaDamage.Groups[2].Value.ToLowerInvariant();
                bool enemyOnly = target == "all enemies" || target == "each enemy creature" ||
                    target == "all enemy creatures" || target == "all enemy creatures in a lane";
                bool creaturesOnly = target != "all enemies" && target != "each player";
                var selector = new BoardTarget {
                    Enemy = enemyOnly,
                    AllOwners = !enemyOnly,
                    Avatar = !creaturesOnly,
                    AvatarOnly = target == "each player",
                    CreatureOnly = creaturesOnly,
                };
                AddPlayActionWithoutTarget(card, new CardActions.Damage {
                    DamageSource = CardRole.Self,
                    HealthLoss = new ActionValueData(Int32.Parse(areaDamage.Groups[1].Value))
                }, selector);
                pattern = "action-area-damage-" + target.Replace(' ', '-');
                return true;
            }

            var damage = ActionDamagePattern.Match(text);
            if (damage.Success)
            {
                string target = damage.Groups[2].Value.ToLowerInvariant();
                var action = new CardActions.Damage {
                    DamageSource = CardRole.Self,
                    HealthLoss = new ActionValueData(Int32.Parse(damage.Groups[1].Value))
                };
                if (target == "your opponent")
                    AddPlayAction(card, action, new BoardTarget { Enemy = true, Avatar = true, AvatarOnly = true });
                else if (target == "a creature")
                    AddPlayAction(card, action, new BoardTarget { CreatureOnly = true, AllOwners = true });
                else
                    AddPlayAction(card, action, new BoardTarget { CreatureOnly = true, Avatar = true, AllOwners = true });
                pattern = target == "a creature" ? "action-damage-creature" :
                    target == "your opponent" ? "action-damage-opponent" : "action-damage-selected-target";
                return true;
            }

            var heal = ActionHealPattern.Match(text);
            if (heal.Success)
            {
                AddPlayActionWithoutTarget(card, new CardActions.Heal {
                    HealthGain = new ActionValueData(Int32.Parse(heal.Groups[1].Value))
                }, new BoardTarget { Avatar = true, AvatarOnly = true, FriendlyOnly = true });
                pattern = "action-heal-owner-avatar";
                return true;
            }

            if (ActionUnsummonFriendlyPattern.IsMatch(text) && card.ResourceId.Id==133401086)
            {
                AddPlayAction(card,new CardActions.ReturnToHand { DrawCopies=false },
                    new BoardTarget { CreatureOnly=true, FriendlyOnly=true });
                pattern="action-unsummon-friendly-creature";
                return true;
            }

            var buff = ActionBuffPattern.Match(text);
            if (buff.Success)
            {
                AddPlayAction(card, CreateBuff(Int32.Parse(buff.Groups[1].Value),
                    Int32.Parse(buff.Groups[2].Value), false),
                    new BoardTarget { CreatureOnly = true, AllOwners = true });
                pattern = "action-buff-creature";
                return true;
            }

            var debuff = ActionDebuffPattern.Match(text);
            if (debuff.Success)
            {
                AddPlayAction(card, CreateBuff(-Int32.Parse(debuff.Groups[1].Value),
                    -Int32.Parse(debuff.Groups[2].Value), true),
                    new BoardTarget { CreatureOnly = true, AllOwners = true });
                pattern = "action-debuff-creature";
                return true;
            }

            if (text.Equals("shackle an enemy creature.", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("shackle an enemy creature", StringComparison.OrdinalIgnoreCase))
            {
                AddPlayAction(card, new CardActions.Freeze {
                    PermanentFreeze = false, Type = EnchantmentUtilities.FreezeType.Normal
                }, new BoardTarget { Enemy = true, CreatureOnly = true });
                pattern = "action-shackle-enemy-creature";
                return true;
            }

            if (text.Equals("give a creature a ward.", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("give a creature a ward", StringComparison.OrdinalIgnoreCase))
            {
                AddPlayAction(card, new CardActions.Ward(),
                    new BoardTarget { CreatureOnly = true, AllOwners = true });
                pattern = "action-grant-ward";
                return true;
            }

            if (text.Equals("destroy a wounded enemy creature.", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("destroy a wounded enemy creature", StringComparison.OrdinalIgnoreCase))
            {
                AddPlayAction(card, new CardActions.Destroy(),
                    new BoardTarget { Enemy = true, CreatureOnly = true, Wounded = true });
                pattern = "action-destroy-wounded-enemy";
                return true;
            }

            if (text.Equals("silence a creature.", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("silence a creature", StringComparison.OrdinalIgnoreCase))
            {
                AddPlayAction(card, new CardActions.Silence(),
                    new BoardTarget { CreatureOnly = true, AllOwners = true });
                pattern = "action-silence-creature";
                return true;
            }

            if (text.Equals("destroy a creature.", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("destroy a creature", StringComparison.OrdinalIgnoreCase))
            {
                AddPlayAction(card, new CardActions.Destroy(),
                    new BoardTarget { CreatureOnly = true, AllOwners = true });
                pattern = "action-destroy-creature";
                return true;
            }
            return false;
        }

        static void AddPlayAction(Card card, ICardAction action, ICardConditionalExpression selector)
        {
            var decision = new DecisionConditionalExpression { DecisionType = DecisionType.Card };
            decision.CardConditionalExpressions.Add(selector);
            var group = new EffectGroup();
            var effect = new Effect();
            var entry = new CardEffectAction { HashId = -1, Action = action };
            entry.Selectors.Add(Role(CardRole.DecisionTarget));
            entry.Selectors.Add(selector);
            effect.CardActions.Add(entry);
            group.Effects.Add(effect);
            var ability = new PlayCardAbility {
                PlayTargetingType = PlayTargetingType.RequiredTarget,
                EffectGroup = group
            };
            ability.TargetSelectors.Add(decision);
            card.ActionPlayInformation.Add(ability);
        }

        static void AddPlayActionWithoutTarget(Card card, ICardAction action, ICardConditionalExpression selector)
        {
            var group = new EffectGroup();
            var effect = new Effect();
            var entry = new CardEffectAction { HashId = -1, Action = action };
            entry.Selectors.Add(selector);
            effect.CardActions.Add(entry);
            group.Effects.Add(effect);
            card.ActionPlayInformation.Add(new PlayCardAbility {
                PlayTargetingType = PlayTargetingType.NoTarget,
                EffectGroup = group
            });
        }

        static ICardAction CreateBuff(int attack, int health, bool debuff)
        {
            if (debuff)
            {
                var action = new CardActions.Debuff();
                AddStats(action.Enchantment.AttributeModifications, attack, health);
                return action;
            }
            else
            {
                var action = new CardActions.Buff();
                AddStats(action.Enchantment.AttributeModifications, attack, health);
                return action;
            }
        }

        static void AddStats(ICollection<EnchantmentAttributeModification> modifications, int attack, int health)
        {
            if (attack != 0) modifications.Add(
                new EnchantmentAttributeModification(EnchantmentAttributeModificationType.Add, AttributeType.Attack, attack));
            if (health != 0) modifications.Add(
                new EnchantmentAttributeModification(EnchantmentAttributeModificationType.Add, AttributeType.MaxHealth, health));
        }

        // Last Gasp needs the host's explicit dying-card dispatch. Support only
        // exact single-block draw and random-damage templates here.
        static bool InstallSimpleLastGasp(Card card, out string pattern)
        {
            pattern = "";
            string description = Regex.Replace(card.Description ?? "", "<[^>]*>", " ");
            description = description.Replace("&amp;", "&").Replace("&lt;", "<")
                .Replace("&gt;", ">").Replace("&nbsp;", " ").Replace("&quot;", "\"")
                .Replace("&#39;", "'");
            description = Regex.Replace(description, @"\s+", " ").Trim();
            description = Regex.Replace(description, @"\s+:", ":");
            var blocks = Regex.Matches(description,
                @"(?<label>Summon|Last\s+Gasp|Slay|Pilfer|Activate):\s*(?<text>.*?)(?=(?:\s+(?:Summon|Last\s+Gasp|Slay|Pilfer|Activate):)|$)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            Match lastGaspBlock=null;
            for(int i=0;i<blocks.Count;i++)if(blocks[i].Groups["label"].Value.Equals("Last Gasp",StringComparison.OrdinalIgnoreCase)) {
                if(lastGaspBlock!=null)return false;
                lastGaspBlock=blocks[i];
            }
            if(lastGaspBlock==null || blocks.Count!=1)return false;
            string effectText = lastGaspBlock.Groups["text"].Value.Trim().TrimEnd('.').Trim();
            var effect = new Effect();
            var group = new EffectGroup(); group.Effects.Add(effect);
            var trigger = new TriggeredEffect {
                TriggerType = EffectTriggerType.LastGaspTrigger,
                SortOrder = TriggerSortOrder.EndOfActiveQueue,
                ClientTargetingType = PlayTargetingType.NoTarget,
                EffectGroup = group
            };
            if (effectText.Equals("draw a card", StringComparison.OrdinalIgnoreCase))
            {
                var entry = new CardEffectAction { HashId = -1, Action = new DrawOne { Count = 1 } };
                entry.Selectors.Add(Role(CardRole.Self));
                effect.CardActions.Add(entry);
                pattern = "last-gasp-draw-one";
            }
            else
            {
                string normalized = PlainText(effectText);
                var opponentDamageAndHeal = Regex.Match(normalized,
                    @"^deal (?<damage>\d+) damage to your opponent and gain (?<heal>\d+) health$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                var opponentDamage = Regex.Match(normalized,
                    @"^deals? (?<amount>\d+) damage to your opponent$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                var heal = Regex.Match(normalized,
                    @"^gain (?<amount>\d+) health$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (opponentDamageAndHeal.Success)
                {
                    var damageEntry = new CardEffectAction { HashId = -1, Action = new CardActions.Damage {
                        DamageSource = CardRole.Self,
                        HealthLoss = new ActionValueData(Int32.Parse(opponentDamageAndHeal.Groups["damage"].Value))
                    } };
                    damageEntry.Selectors.Add(new BoardTarget { Enemy = true, Avatar = true, AvatarOnly = true });
                    effect.CardActions.Add(damageEntry);
                    var healEntry = new CardEffectAction { HashId = -1, Action = new CardActions.Heal {
                        HealthGain = new ActionValueData(Int32.Parse(opponentDamageAndHeal.Groups["heal"].Value))
                    } };
                    healEntry.Selectors.Add(new BoardTarget { Avatar = true, FriendlyOnly = true });
                    effect.CardActions.Add(healEntry);
                    pattern = "last-gasp-opponent-damage-and-owner-heal";
                }
                else if (opponentDamage.Success)
                {
                    var entry = new CardEffectAction { HashId = -1, Action = new CardActions.Damage {
                        DamageSource = CardRole.Self,
                        HealthLoss = new ActionValueData(Int32.Parse(opponentDamage.Groups["amount"].Value))
                    } };
                    entry.Selectors.Add(new BoardTarget { Enemy = true, Avatar = true, AvatarOnly = true });
                    effect.CardActions.Add(entry);
                    pattern = "last-gasp-opponent-avatar-damage";
                }
                else if (heal.Success)
                {
                    var entry = new CardEffectAction { HashId = -1, Action = new CardActions.Heal {
                        HealthGain = new ActionValueData(Int32.Parse(heal.Groups["amount"].Value))
                    } };
                    entry.Selectors.Add(new BoardTarget { Avatar = true, FriendlyOnly = true });
                    effect.CardActions.Add(entry);
                    pattern = "last-gasp-owner-avatar-heal";
                }
                else
                {
                var damage = Regex.Match(normalized, @"^deals? (?<amount>\d+) damage to (?<target>a random enemy(?: creature)?|all creatures in this lane|all enemy creatures in this lane)$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (!damage.Success) return false;
                int amount = Int32.Parse(damage.Groups["amount"].Value);
                var action = new CardActions.Damage { DamageSource = CardRole.Self, HealthLoss = new ActionValueData(amount) };
                var entry = new CardEffectAction { HashId = -1, Action = action };
                string target = damage.Groups["target"].Value;
                bool allInLane = target.IndexOf("creatures in this lane", StringComparison.OrdinalIgnoreCase) >= 0;
                bool enemyLaneOnly = target.Equals("all enemy creatures in this lane", StringComparison.OrdinalIgnoreCase);
                entry.Selectors.Add(new BoardTarget { Enemy = !allInLane || enemyLaneOnly, Avatar = !allInLane,
                    CreatureOnly = allInLane, AllOwners = allInLane && !enemyLaneOnly, SameLane = allInLane });
                if (!allInLane) { entry.SelectorSort.Method = CardListSort.SortMethod.Random; entry.SelectorSort.NumToReturn = new ActionValueData(1); }
                effect.CardActions.Add(entry);
                pattern = allInLane ? (enemyLaneOnly ? "last-gasp-damage-all-enemy-creatures-in-lane-" : "last-gasp-damage-all-creatures-in-lane-") + amount
                    : "last-gasp-random-enemy-damage-" + amount;
                }
            }
            card.TriggeredEffects.Add(trigger);
            return true;
        }

        static bool InstallCombinedSummonDrawShuffle(Card card, out string pattern)
        {
            pattern = "";
            string description = PlainText(card.Description);
            var combined=Regex.Match(description,
                @"^Summon:\s*(?<summon>.*?)\s+(?<trigger>Last\s+Gasp|Pilfer):\s*(?<followUp>.*)$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if(!combined.Success)return false;
            string triggerLabel=combined.Groups["trigger"].Value;
            bool lastGasp=triggerLabel.Equals("Last Gasp",StringComparison.OrdinalIgnoreCase);
            bool pilfer=triggerLabel.Equals("Pilfer",StringComparison.OrdinalIgnoreCase);
            if(!lastGasp && !pilfer)return false;
            string summon = combined.Groups["summon"].Value.Trim().TrimEnd('.').Trim();
            string followUp = combined.Groups["followUp"].Value.Trim().TrimEnd('.').Trim();
            var shuffle = Regex.Match(followUp, lastGasp
                    ? @"^shuffle a 0-cost (?<name>.+) into your deck$"
                    : @"^shuffle a (?<name>.+?) with\s*[“""]\s*Summon:\s*Draw a card\s*[”""]\s*into your deck$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!summon.Equals("draw a card", StringComparison.OrdinalIgnoreCase) || !shuffle.Success ||
                !shuffle.Groups["name"].Value.Equals(PlainText(card.Name), StringComparison.OrdinalIgnoreCase)) return false;

            AddDraw(card, 1);
            var effect = new Effect();
            var group = new EffectGroup(); group.Effects.Add(effect);
            var trigger = new TriggeredEffect {
                TriggerType = lastGasp ? EffectTriggerType.LastGaspTrigger : EffectTriggerType.Pilfer,
                SortOrder = TriggerSortOrder.EndOfActiveQueue,
                ClientTargetingType = PlayTargetingType.NoTarget,
                EffectGroup = group
            };
            var entry = new CardEffectAction { HashId = -1, Action = new ShuffleSelfCopy(lastGasp) };
            entry.Selectors.Add(Role(CardRole.Self));
            effect.CardActions.Add(entry);
            card.TriggeredEffects.Add(trigger);
            pattern = lastGasp ? "summon-draw-one-last-gasp-shuffle-zero-cost-self" :
                "summon-draw-one-pilfer-shuffle-self";
            return true;
        }

        static TriggeredEffect NewSummon(Card card, PlayTargetingType targeting)
        {
            var effect = new Effect();
            var group = new EffectGroup(); group.Effects.Add(effect);
            var trigger = new TriggeredEffect {
                TriggerType = EffectTriggerType.Summon,
                SortOrder = TriggerSortOrder.EndOfActiveQueue,
                ClientTargetingType = targeting,
                EffectGroup = group
            };
            card.TriggeredEffects.Add(trigger);
            return trigger;
        }

        static ICardConditionalExpression Role(CardRole role)
        {
            return new CardConditionals.CardRoleType {
                Role = role, Check = CardConditionals.CardRoleType.Requirement.Is
            };
        }

        static void AddTargeted(Card card, ICardAction action, ICardConditionalExpression selector)
        {
            var trigger = NewSummon(card, PlayTargetingType.RequiredTarget);
            var decision = new DecisionConditionalExpression { DecisionType = DecisionType.Card };
            decision.CardConditionalExpressions.Add(selector);
            trigger.TargetSelectors.Add(decision);
            var entry = new CardEffectAction { HashId = -1, Action = action };
            entry.Selectors.Add(Role(CardRole.DecisionTarget));
            entry.Selectors.Add(selector);
            trigger.EffectGroup.Effects[0].CardActions.Add(entry);
        }

        static void AddRandom(Card card, ICardAction action, ICardConditionalExpression selector)
        {
            var trigger = NewSummon(card, PlayTargetingType.NoTarget);
            var entry = new CardEffectAction { HashId = -1, Action = action };
            entry.Selectors.Add(selector);
            entry.SelectorSort.Method = CardListSort.SortMethod.Random;
            entry.SelectorSort.NumToReturn = new ActionValueData(1);
            trigger.EffectGroup.Effects[0].CardActions.Add(entry);
        }

        static void AddUnselected(Card card, ICardAction action, ICardConditionalExpression selector)
        {
            var trigger = NewSummon(card, PlayTargetingType.NoTarget);
            var entry = new CardEffectAction { HashId = -1, Action = action };
            entry.Selectors.Add(selector);
            trigger.EffectGroup.Effects[0].CardActions.Add(entry);
        }

        static void AddDraw(Card card, int count)
        {
            var trigger = NewSummon(card, PlayTargetingType.NoTarget);
            var entry = new CardEffectAction { HashId = -1, Action = new DrawOne { Count = count } };
            entry.Selectors.Add(Role(CardRole.Self));
            trigger.EffectGroup.Effects[0].CardActions.Add(entry);
        }

        static void AddSummonAction(Card card, ICardAction action)
        {
            var trigger = NewSummon(card, PlayTargetingType.NoTarget);
            var entry = new CardEffectAction { HashId = -1, Action = action };
            entry.Selectors.Add(Role(CardRole.Self));
            trigger.EffectGroup.Effects[0].CardActions.Add(entry);
        }

        static bool InstallConditionalSelfBuff(Card card,Match match,out string pattern)
        {
            pattern="";
            int id=card.ResourceId.Id;
            string expected=id==-1652308136?"friendly orc":id==1477162806?"creature with ward":
                id==1544383450?"creature with lethal":id==780490122?"creature with guard":"";
            if(String.IsNullOrEmpty(expected) || !PlainText(match.Groups[3].Value).Equals(expected,StringComparison.OrdinalIgnoreCase))
                return false;
            AddSummonAction(card,new ConditionalSelfBuff(
                Int32.Parse(match.Groups[1].Value),Int32.Parse(match.Groups[2].Value),id));
            pattern="summon-self-buff-if-another-friendly-"+expected.Replace(' ','-');
            return true;
        }

        sealed class DrawOne : ICardAction
        {
            internal int Count;
            public CardFxAction GetFxActionType() { return CardFxAction.CountOf; }
            public void Execute(CardActionArguments args)
            {
                ServerCard source = args.CardArguments.SelfCard;
                if (source == null) return;
                PlayerState owner = args.TriggerGameState.Players.Find(p => p.NetId == source.OwnerNetId);
                if (owner != null)
                    OfflineDrawUtilities.Draw(args.TriggerGameState, owner, Count,
                        CcgRpc.HistoryType.ActionMoveCards,
                        "retail-summon-text-compiler-" + source.CardId);
            }
        }

        sealed class DrawRandomCardType : ICardAction
        {
            readonly CardSubtype subtype;
            internal DrawRandomCardType(string type)
            {
                subtype=type=="creature"?CardSubtype.Creature:
                    type=="item"?CardSubtype.Item:
                    type=="support"?CardSubtype.Support:CardSubtype.Spell;
            }
            public CardFxAction GetFxActionType() { return CardFxAction.ActionMoveCard; }
            public void Execute(CardActionArguments args)
            {
                ServerCard source=args.CardArguments.SelfCard;
                if(source==null) return;
                var state=args.TriggerGameState;
                PlayerState owner=state.Players.Find(p=>p.NetId==source.OwnerNetId);
                if(owner==null) return;
                List<ServerCard> eligible=state.CardCatalog.GatherCardsInZone(
                    c=>c.HasSubtype(subtype),owner.NetId,GameZoneName.Deck);
                if(eligible.Count==0) return;
                int index=state.RandomGenerator.GenerateInRange(0,eligible.Count-1);
                ServerCard selected=eligible[index];
                var moved=ServerCardCatalog.MoveCard(state,
                    new GameZoneId(owner.NetId,GameZoneType.Deck),
                    new GameZoneId(owner.NetId,GameZoneType.Hand),selected.InstanceId,0,
                    CardInsertionType.Bottom,ServerCardCatalog.MoveOperationType.DefaultDraw);
                if(moved!=GameZoneOperationResult.Succeeded) return;
                PlayerActions.DrawCards.LogDrawnCards(CcgRpc.HistoryType.ActionMoveCards,
                    state.Log,state.Players,state.CardCatalog,new List<ServerCard>{selected},
                    source.FindOverrideOrDefaultAssetHash(CardFxAction.ActionMoveCard));
            }
        }

        sealed class DrawAndDiscountExpensive : ICardAction
        {
            public CardFxAction GetFxActionType() { return CardFxAction.CountOf; }
            public void Execute(CardActionArguments args)
            {
                var source = args.CardArguments.SelfCard;
                if (source == null) return;
                var state = args.TriggerGameState;
                var owner = state.Players.Find(p => p.NetId == source.OwnerNetId);
                if (owner == null) return;
                var drawn = OfflineDrawUtilities.Draw(state, owner, 1,
                    CcgRpc.HistoryType.ActionMoveCards,
                    "retail-summon-discount-draw-" + source.CardId);
                if (drawn == null || drawn.Count == 0) return;
                var card = drawn[drawn.Count - 1];
                if (card.QueryAttribute(AttributeType.ManaCost).Value < 7) return;
                var enchantment = new Enchantment();
                enchantment.AttributeModifications.Add(new EnchantmentAttributeModification(
                    EnchantmentAttributeModificationType.Add, AttributeType.ManaCost, -1));
                card.Enchantments.Add(new EnchantmentInstance(enchantment, 1,
                    source.CardId, source.InstanceId, card));
                card.RecalculateEnchantmentsWithTriggers(state, source);
                HistoryLoggingUtilities.LogFullCardRefresh(state.Log, state.CardCatalog, card, false);
            }
        }

        sealed class ShuffleSelfCopy : ICardAction
        {
            readonly bool zeroCost;
            internal ShuffleSelfCopy(bool zeroCost) { this.zeroCost=zeroCost; }
            public CardFxAction GetFxActionType() { return CardFxAction.CreateToken; }
            public void Execute(CardActionArguments args)
            {
                var source = args.CardArguments.SelfCard;
                if (source == null) return;
                var state = args.TriggerGameState;
                var definition = GameManager.Instance.Config.FindCardById(source.CardId);
                if (definition == null) return;
                Enchantment copyBuff=null;
                if(zeroCost) {
                    copyBuff = new Enchantment();
                    copyBuff.AttributeModifications.Add(new EnchantmentAttributeModification(
                        EnchantmentAttributeModificationType.Set, AttributeType.ManaCost, 0));
                }
                var position = state.CardCatalog.FindInsertionPosition(source.OwnerNetId,
                    GameZoneName.Deck, 0, CardInsertionType.Random);
                PlayerActions.CreateToken.StaticExecuteWithBuffs(state, source, definition,
                    UniqueResourceId.GetInvalid(), source.Premium, GameZoneType.Deck, position,
                    source.OwnerNetId, copyBuff, null, 1, source.FindOverrideOrDefaultAssetHash(CardFxAction.CreateToken),
                    false, source);
            }
        }

        sealed class GainMaxMagicka : ICardAction
        {
            internal int Amount;
            public CardFxAction GetFxActionType() { return CardFxAction.Buff; }
            public void Execute(CardActionArguments args)
            {
                ServerCard source = args.CardArguments.SelfCard;
                if (source == null) return;
                var state = args.TriggerGameState;
                PlayerState owner = state.Players.Find(p => p.NetId == source.OwnerNetId);
                ServerCard avatar = state.CardCatalog.FindCardInDynamicZone(
                    c => c.OwnerNetId == source.OwnerNetId,
                    source.OwnerNetId, GameZoneName.AvatarBoard);
                if (owner == null || avatar == null || Amount <= 0) return;
                int avatarMaxBefore = avatar.QueryAttribute(AttributeType.MaxMana).Value;
                var enchantment = new Enchantment();
                enchantment.AttributeModifications.Add(new EnchantmentAttributeModification(
                    EnchantmentAttributeModificationType.Add, AttributeType.MaxMana, Amount));
                CardActions.Buff.StaticExecute(state, source, new List<ServerCard> { avatar },
                    enchantment, owner, args.AssetHash, args.UsingDefaultFx);
                int gained = avatar.QueryAttribute(AttributeType.MaxMana).Value - avatarMaxBefore;
                if (gained <= 0) return;
                PlayerActions.SetAttribute.StaticExecute(state.Log, owner, AttributeType.MaxMana,
                    owner.QueryAttribute(AttributeType.MaxMana).Value + gained);
                PlayerActions.ModifyMana.StaticExecute(state, owner, gained);
            }
        }

        sealed class BuffPerOtherFriendlyCreature : ICardAction
        {
            readonly bool enduranceOnly;
            internal BuffPerOtherFriendlyCreature(bool enduranceOnly) { this.enduranceOnly = enduranceOnly; }
            public CardFxAction GetFxActionType() { return CardFxAction.Buff; }
            public void Execute(CardActionArguments args)
            {
                ServerCard source = args.CardArguments.SelfCard;
                if (source == null || source.IsDying()) return;
                int count = args.TriggerGameState.CardCatalog.GatherCardsInZone(c =>
                    c.HasSubtype(CardSubtype.Creature) && !c.IsDying() && c.InstanceId != source.InstanceId &&
                    (!enduranceOnly || c.HasSubtype(CardSubtype.Endurance)),
                    source.OwnerNetId, GameZoneName.CreatureBoard).Count;
                if (count == 0) return;
                var enchantment = new Enchantment { PermanentExpiresOnLeavingBoard = true };
                enchantment.AttributeModifications.Add(new EnchantmentAttributeModification(
                    EnchantmentAttributeModificationType.Add, AttributeType.Attack, count));
                enchantment.AttributeModifications.Add(new EnchantmentAttributeModification(
                    EnchantmentAttributeModificationType.Add, AttributeType.MaxHealth, count));
                PlayerState owner = args.TriggerGameState.Players.Find(p => p.NetId == source.OwnerNetId);
                if (owner != null)
                    CardActions.Buff.StaticExecute(args.TriggerGameState, source,
                        new List<ServerCard> { source }, enchantment, owner, args.AssetHash, args.UsingDefaultFx);
            }
        }

        sealed class BuffPerEnemyCreatureInLane : ICardAction
        {
            readonly int powerPerCreature, healthPerCreature;
            internal BuffPerEnemyCreatureInLane(int power, int health)
            { powerPerCreature=power; healthPerCreature=health; }
            public CardFxAction GetFxActionType() { return CardFxAction.Buff; }
            public void Execute(CardActionArguments args)
            {
                ServerCard source=args.CardArguments.SelfCard;
                if(source==null || source.IsDying()) return;
                PlayerState opponent=args.TriggerGameState.Players.Find(p=>p.NetId!=source.OwnerNetId);
                if(opponent==null) return;
                int count=args.TriggerGameState.CardCatalog.GatherCardsInZone(c=>
                    c.HasSubtype(CardSubtype.Creature) && !c.IsDying() &&
                    c.CurrentLaneIndex==source.CurrentLaneIndex,
                    opponent.NetId,GameZoneName.CreatureBoard).Count;
                if(count<=0) return;
                var enchantment=new Enchantment { PermanentExpiresOnLeavingBoard=true };
                if(powerPerCreature!=0) enchantment.AttributeModifications.Add(
                    new EnchantmentAttributeModification(EnchantmentAttributeModificationType.Add,
                        AttributeType.Attack,powerPerCreature*count));
                if(healthPerCreature!=0) enchantment.AttributeModifications.Add(
                    new EnchantmentAttributeModification(EnchantmentAttributeModificationType.Add,
                        AttributeType.MaxHealth,healthPerCreature*count));
                PlayerState owner=args.TriggerGameState.Players.Find(p=>p.NetId==source.OwnerNetId);
                if(owner!=null) CardActions.Buff.StaticExecute(args.TriggerGameState,source,
                    new List<ServerCard>{source},enchantment,owner,args.AssetHash,args.UsingDefaultFx);
            }
        }

        sealed class ConditionalSelfBuff : ICardAction
        {
            readonly int power,health,id;
            internal ConditionalSelfBuff(int p,int h,int cardId){power=p;health=h;id=cardId;}
            public CardFxAction GetFxActionType(){return CardFxAction.Buff;}
            public void Execute(CardActionArguments args)
            {
                ServerCard source=args.CardArguments.SelfCard;
                if(source==null || source.IsDying())return;
                bool eligible=args.TriggerGameState.CardCatalog.GatherCardsInZone(c=>
                    c.HasSubtype(CardSubtype.Creature) && c.InstanceId!=source.InstanceId &&
                    (id==-1652308136?c.HasSubtype(CardSubtype.Orc):
                     id==1477162806?c.QueryAttribute(AttributeType.Ward).Value>0:
                     id==1544383450?c.QueryAttribute(AttributeType.Deathtouch).Value>0:
                     c.QueryAttribute(AttributeType.Taunt).Value>0),
                    source.OwnerNetId,GameZoneName.CreatureBoard).Count>0;
                if(!eligible)return;
                var enchantment=new Enchantment{PermanentExpiresOnLeavingBoard=true};
                if(power!=0)enchantment.AttributeModifications.Add(new EnchantmentAttributeModification(
                    EnchantmentAttributeModificationType.Add,AttributeType.Attack,power));
                if(health!=0)enchantment.AttributeModifications.Add(new EnchantmentAttributeModification(
                    EnchantmentAttributeModificationType.Add,AttributeType.MaxHealth,health));
                PlayerState owner=args.TriggerGameState.Players.Find(p=>p.NetId==source.OwnerNetId);
                if(owner!=null)CardActions.Buff.StaticExecute(args.TriggerGameState,source,
                    new List<ServerCard>{source},enchantment,owner,args.AssetHash,args.UsingDefaultFx);
            }
        }

        sealed class ConditionalHandCountSelfBuff : ICardAction
        {
            readonly int power,health;
            internal ConditionalHandCountSelfBuff(int p,int h){power=p;health=h;}
            public CardFxAction GetFxActionType(){return CardFxAction.Buff;}
            public void Execute(CardActionArguments args)
            {
                ServerCard source=args.CardArguments.SelfCard;
                if(source==null || source.IsDying())return;
                var catalog=args.TriggerGameState.CardCatalog;
                int ownHand=catalog.FindCardCount(GameZoneName.Hand,source.OwnerNetId);
                var opponent=args.TriggerGameState.Players.FirstOrDefault(p=>p.NetId!=source.OwnerNetId);
                if(opponent==null)return;
                int opponentHand=catalog.FindCardCount(GameZoneName.Hand,opponent.NetId);
                if(opponentHand<=ownHand)return;
                var enchantment=new Enchantment{PermanentExpiresOnLeavingBoard=true};
                if(power!=0)enchantment.AttributeModifications.Add(new EnchantmentAttributeModification(
                    EnchantmentAttributeModificationType.Add,AttributeType.Attack,power));
                if(health!=0)enchantment.AttributeModifications.Add(new EnchantmentAttributeModification(
                    EnchantmentAttributeModificationType.Add,AttributeType.MaxHealth,health));
                var owner=args.TriggerGameState.Players.FirstOrDefault(p=>p.NetId==source.OwnerNetId);
                if(owner!=null)CardActions.Buff.StaticExecute(args.TriggerGameState,source,
                    new List<ServerCard>{source},enchantment,owner,args.AssetHash,args.UsingDefaultFx);
            }
        }

        sealed class DrawIfOwnerHasHealthAdvantage : ICardAction
        {
            readonly int count;
            internal DrawIfOwnerHasHealthAdvantage(int drawCount){count=drawCount;}
            public CardFxAction GetFxActionType(){return CardFxAction.ActionMoveCard;}
            public void Execute(CardActionArguments args)
            {
                ServerCard source=args.CardArguments.SelfCard;
                if(source==null || source.IsDying())return;
                var players=args.TriggerGameState.Players;
                PlayerState owner=players.FirstOrDefault(p=>p.NetId==source.OwnerNetId);
                PlayerState opponent=players.FirstOrDefault(p=>p.NetId!=source.OwnerNetId);
                if(owner==null || opponent==null)return;
                var catalog=args.TriggerGameState.CardCatalog;
                ServerCard ownerAvatar=catalog.GatherCardsInZone(c=>true,owner.NetId,GameZoneName.AvatarBoard).FirstOrDefault();
                ServerCard opponentAvatar=catalog.GatherCardsInZone(c=>true,opponent.NetId,GameZoneName.AvatarBoard).FirstOrDefault();
                if(ownerAvatar==null || opponentAvatar==null || ownerAvatar.CurrentHealth<=opponentAvatar.CurrentHealth)return;
                OfflineDrawUtilities.Draw(args.TriggerGameState,owner,count,
                    CcgRpc.HistoryType.ActionMoveCards,"retail-summon-text-compiler-"+source.CardId);
            }
        }

        sealed class ConditionalActionInHandBuff : ICardAction
        {
            public CardFxAction GetFxActionType(){return CardFxAction.Buff;}
            public void Execute(CardActionArguments args)
            {
                ServerCard source=args.CardArguments.SelfCard;
                if(source==null || source.IsDying())return;
                bool hasAction=args.TriggerGameState.CardCatalog.GatherCardsInZone(c=>
                    c.HasSubtype(CardSubtype.Spell),source.OwnerNetId,GameZoneName.Hand).Count>0;
                if(!hasAction)return;
                var enchantment=new Enchantment{PermanentExpiresOnLeavingBoard=true};
                enchantment.AttributeModifications.Add(new EnchantmentAttributeModification(
                    EnchantmentAttributeModificationType.Add,AttributeType.Attack,1));
                enchantment.AttributeModifications.Add(new EnchantmentAttributeModification(
                    EnchantmentAttributeModificationType.Add,AttributeType.MaxHealth,1));
                var owner=args.TriggerGameState.Players.Find(p=>p.NetId==source.OwnerNetId);
                if(owner!=null)CardActions.Buff.StaticExecute(args.TriggerGameState,source,
                    new List<ServerCard>{source},enchantment,owner,args.AssetHash,args.UsingDefaultFx);
            }
        }

        sealed class DrawIfOpponentRuneLost : ICardAction
        {
            public CardFxAction GetFxActionType(){return CardFxAction.CountOf;}
            public void Execute(CardActionArguments args)
            {
                var source=args.CardArguments.SelfCard;
                if(source==null || source.IsDying())return;
                var opponent=args.TriggerGameState.Players.Find(p=>p.NetId!=source.OwnerNetId);
                if(opponent==null || opponent.QueryAttribute(AttributeType.NumRunesLost).Value<=0)return;
                var owner=args.TriggerGameState.Players.Find(p=>p.NetId==source.OwnerNetId);
                if(owner!=null)OfflineDrawUtilities.Draw(args.TriggerGameState,owner,1,
                    CcgRpc.HistoryType.ActionMoveCards,"eastmarch-crusader-destroyed-rune-"+source.CardId);
            }
        }

        sealed class DrawIfWoundedCreatureInSourceLane : ICardAction
        {
            public CardFxAction GetFxActionType(){return CardFxAction.CountOf;}
            public void Execute(CardActionArguments args)
            {
                var source=args.CardArguments.SelfCard;
                if(source==null || source.IsDying())return;
                bool wounded=args.TriggerGameState.CardCatalog.GatherDynamicCardList().Exists(c=>
                    c.HasSubtype(CardSubtype.Creature) && c.CurrentGameZoneName==GameZoneName.CreatureBoard &&
                    c.CurrentLaneIndex==source.CurrentLaneIndex && !c.IsDying() &&
                    c.CurrentHealth<c.QueryAttribute(AttributeType.MaxHealth).Value);
                if(!wounded)return;
                var owner=args.TriggerGameState.Players.Find(p=>p.NetId==source.OwnerNetId);
                if(owner!=null)OfflineDrawUtilities.Draw(args.TriggerGameState,owner,1,
                    CcgRpc.HistoryType.ActionMoveCards,"blood-crazed-daedroth-wounded-lane");
            }
        }

        sealed class ConditionalChargeIfAnotherOrc : ICardAction
        {
            public CardFxAction GetFxActionType(){return CardFxAction.GainKeyword;}
            public void Execute(CardActionArguments args)
            {
                ServerCard source=args.CardArguments.SelfCard;
                if(source==null || source.IsDying())return;
                bool hasOtherOrc=args.TriggerGameState.CardCatalog.GatherCardsInZone(c=>
                    c.HasSubtype(CardSubtype.Creature) && c.HasSubtype(CardSubtype.Orc) &&
                    c.InstanceId!=source.InstanceId && !c.IsDying(),
                    source.OwnerNetId,GameZoneName.CreatureBoard).Count>0;
                if(!hasOtherOrc)return;
                var enchantment=new Enchantment{PermanentExpiresOnLeavingBoard=true};
                enchantment.AttributeModifications.Add(new EnchantmentAttributeModification(
                    EnchantmentAttributeModificationType.Add,AttributeType.Charge,1));
                PlayerState owner=args.TriggerGameState.Players.Find(p=>p.NetId==source.OwnerNetId);
                if(owner!=null)CardActions.Buff.StaticExecute(args.TriggerGameState,source,
                    new List<ServerCard>{source},enchantment,owner,args.AssetHash,args.UsingDefaultFx);
            }
        }

        sealed class ConditionalLaneKeyword : ICardAction
        {
            readonly int id;
            internal ConditionalLaneKeyword(int cardId){id=cardId;}
            public CardFxAction GetFxActionType(){return CardFxAction.GainKeyword;}
            public void Execute(CardActionArguments args)
            {
                ServerCard source=args.CardArguments.SelfCard;
                if(source==null || source.IsDying())return;
                bool qualified=args.TriggerGameState.CardCatalog.GatherCardsInZone(c=>
                    c.HasSubtype(CardSubtype.Creature) && c.OwnerNetId!=source.OwnerNetId &&
                    c.CurrentLaneIndex==source.CurrentLaneIndex && !c.IsDying() &&
                    (id==-461655438?c.CurrentHealth<c.QueryAttribute(AttributeType.MaxHealth).Value:true),
                    source.OwnerNetId==args.TriggerGameState.Players[0].NetId
                        ? args.TriggerGameState.Players[1].NetId : args.TriggerGameState.Players[0].NetId,
                    GameZoneName.CreatureBoard).Count>0;
                if(!qualified)return;
                var keyword=id==-1903529353?AttributeType.Drain:AttributeType.Charge;
                var enchantment=new Enchantment{PermanentExpiresOnLeavingBoard=true};
                enchantment.AttributeModifications.Add(new EnchantmentAttributeModification(
                    EnchantmentAttributeModificationType.Add,keyword,1));
                PlayerState owner=args.TriggerGameState.Players.Find(p=>p.NetId==source.OwnerNetId);
                if(owner!=null)CardActions.Buff.StaticExecute(args.TriggerGameState,source,
                    new List<ServerCard>{source},enchantment,owner,args.AssetHash,args.UsingDefaultFx);
            }
        }

        sealed class TemporaryFriendlyRally : ICardAction
        {
            public CardFxAction GetFxActionType(){return CardFxAction.Rally;}
            public void Execute(CardActionArguments args)
            {
                ServerCard source=args.CardArguments.SelfCard;
                if(source==null || source.IsDying())return;
                var allies=args.TriggerGameState.CardCatalog.GatherCardsInZone(c=>
                    c.HasSubtype(CardSubtype.Creature) && !c.IsDying(),
                    source.OwnerNetId,GameZoneName.CreatureBoard);
                if(allies.Count==0)return;
                var enchantment=new Enchantment{ExpiresAtEndOfTurn=true};
                enchantment.AttributeModifications.Add(new EnchantmentAttributeModification(
                    EnchantmentAttributeModificationType.Add,AttributeType.Rally,1));
                PlayerState owner=args.TriggerGameState.Players.Find(p=>p.NetId==source.OwnerNetId);
                if(owner!=null)CardActions.Buff.StaticExecute(args.TriggerGameState,source,
                    allies,enchantment,owner,args.AssetHash,args.UsingDefaultFx);
            }
        }

        sealed class DrawIfTwoOtherStrengthCreatures : ICardAction
        {
            public CardFxAction GetFxActionType() { return CardFxAction.CountOfSubtypeInZonetypeSuccess; }
            public void Execute(CardActionArguments args)
            {
                ServerCard source = args.CardArguments.SelfCard;
                if (source == null || source.IsDying()) return;
                int count = args.TriggerGameState.CardCatalog.GatherCardsInZone(c =>
                    c.HasSubtype(CardSubtype.Creature) && c.HasSubtype(CardSubtype.Strength) &&
                    !c.IsDying() && c.InstanceId != source.InstanceId,
                    source.OwnerNetId, GameZoneName.CreatureBoard).Count;
                if (count < 2) return;
                PlayerState owner = args.TriggerGameState.Players.Find(p => p.NetId == source.OwnerNetId);
                if (owner != null)
                    OfflineDrawUtilities.Draw(args.TriggerGameState, owner, 1,
                        CcgRpc.HistoryType.ActionMoveCards,
                        "retail-stronghold-patrol-two-strength-creatures-" + source.CardId);
            }
        }

        sealed class DrawIfTwoOtherCreatures : ICardAction
        {
            public CardFxAction GetFxActionType() { return CardFxAction.CountOf; }
            public void Execute(CardActionArguments args)
            {
                ServerCard source = args.CardArguments.SelfCard;
                if (source == null || source.IsDying()) return;
                int count = args.TriggerGameState.CardCatalog.GatherCardsInZone(c =>
                    c.HasSubtype(CardSubtype.Creature) && !c.IsDying() && c.InstanceId != source.InstanceId,
                    source.OwnerNetId, GameZoneName.CreatureBoard).Count;
                if (count >= 2)
                {
                    PlayerState owner = args.TriggerGameState.Players.Find(p => p.NetId == source.OwnerNetId);
                    if (owner != null)
                        OfflineDrawUtilities.Draw(args.TriggerGameState, owner, 1,
                            CcgRpc.HistoryType.ActionMoveCards,
                            "retail-summon-text-compiler-two-creatures-" + source.CardId);
                }
            }
        }

        sealed class BoardTarget : ICardConditionalExpression
        {
            internal bool Enemy, Avatar, AvatarOnly, CreatureOnly, SameLane, OtherThanSelf, AllOwners, FriendlyOnly, Wounded;
            internal int MaximumAttack = -1;
            public bool Evaluate(ServerCard card, CardConditionalArguments cards,
                PlayerConditionalArguments players, TriggerConditionalArguments trigger)
            {
                ServerCard source = cards.SelfCard;
                if (card == null || source == null || card.IsDying()) return false;
                if (Enemy && card.OwnerNetId == source.OwnerNetId) return false;
                if (FriendlyOnly && card.OwnerNetId != source.OwnerNetId) return false;
                if (!AllOwners && !Enemy && !FriendlyOnly && card.OwnerNetId != source.OwnerNetId) return false;
                bool avatar = card.CurrentGameZoneName == GameZoneName.AvatarBoard;
                bool creature = card.CurrentGameZoneName == GameZoneName.CreatureBoard && card.HasSubtype(CardSubtype.Creature);
                if (AvatarOnly) return avatar;
                if (CreatureOnly) return creature && (!Wounded || card.CurrentHealth < card.QueryAttribute(AttributeType.MaxHealth).Value)
                    && (MaximumAttack < 0 || card.QueryAttribute(AttributeType.Attack).Value <= MaximumAttack)
                    && (!SameLane || card.CurrentLaneIndex == source.CurrentLaneIndex)
                    && (!OtherThanSelf || card.InstanceId != source.InstanceId);
                return creature || (Avatar && avatar);
            }
        }

        sealed class FriendlyCreatureAuraTarget : ICardConditionalExpression
        {
            public bool Evaluate(ServerCard card, CardConditionalArguments cards,
                PlayerConditionalArguments players, TriggerConditionalArguments trigger)
            {
                ServerCard source = cards.SelfCard;
                return card != null && source != null && !card.IsDying() &&
                    card.CurrentGameZoneName == GameZoneName.CreatureBoard &&
                    card.HasSubtype(CardSubtype.Creature) && card.OwnerNetId == source.OwnerNetId;
            }
        }

    }
}
