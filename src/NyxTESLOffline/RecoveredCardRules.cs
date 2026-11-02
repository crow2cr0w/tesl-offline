using System;
using SPTESL;

namespace NyxTESLOffline
{
    // Small, explicit rule overlay. The retail rule executor, selectors,
    // enchantments, history and FX remain responsible for execution.
    // Never replace rules supplied by a complete source configuration.
    internal static partial class RecoveredCardRules
    {
        internal static readonly int[] CardIds = {
            -1209831649, 207833797, 1106955994, -987994366,
            171706845, -646559647, -1865755971, 41830377,
            -1086320745,
            -710854512, -1769639680, 1911637876, 767299130, 1009056840, 831893683,
            -751645406, 1916654634, 627783150, -632269135,
            1744549196, -1094245595, 1405826749, -2043856038, 1076369070,
            -37005376
            ,195169233, -1085593596, 631750256, -1871307781, 935076603,
            -33546008, 1995521600, -443759246, -1879063369, -2096820926,
            1038408842, -424209530, 806805335, 1970986215, -371816192,
            -1951249314, -1052020638, 94406568, -2090944892, 1322838203,
            -561910135, 1691833255, -295361290, 1037073850, -1139008311,
            1300162036, -800102561, -1173040636, -1249810974,
            1750084664, -456767346, 1366329424,
            -1767873507, 842097822,
            -452943738, 2052829044, 652289987, -1395960197, -30507580,
            1462928361, -1543808196, -587531429, 775516546, -1143757804,
            -520695966, -967450522, -463613369, 2095683028, 6794648,
            294653485, 1831263143, 1594345279, -1975806074,
            1789830533, -1800265625, -542954561, -462299413, 780292596, 43252412, -522300233,
            694231768, -1192168222
        };

        internal static void Apply(GameConfiguration config)
        {
            int count = 0;
            foreach (int id in CardIds)
            {
                Card card = config.FindCardById(id);
                if (card != null && Install(card))
                {
                    ++count;
                    Plugin.Log("recovered retail rule: card=" + id + " name=" + card.Name);
                }
            }
            Plugin.Log("recovered retail rules installed=" + count);
        }

        internal static bool Install(Card card)
        {
            int id = card.ResourceId.Id;
            if (IsReferenceExpansion(id)) return InstallReferenceExpansion(card);
            if (IsNextPrimitiveRule(id)) return InstallNextPrimitiveRule(card);
            if (IsWillpowerRule(id))
                return InstallWillpowerRule(card);
            bool baseKeywords = InstallRedDeckBaseKeywords(card);
            baseKeywords = InstallBlueDeckBaseRules(card) || baseKeywords;
            if (id == 631750256 || id == 1970986215)
                return InstallRally(card);
            if (id == 831893683)
                return InstallBushwhack(card);
            if (id == -1086320745)
                return InstallHiddenTrail(card);
            if (id == -710854512)
                return InstallTargetedSummon(card, new CardActions.Freeze {
                    PermanentFreeze = false,
                    Type = EnchantmentUtilities.FreezeType.Normal
                }, EnemyCreatureSelector());
            if (id == -456767346)
                return InstallTargetedSummon(card, new CardActions.Freeze {
                    PermanentFreeze = false,
                    Type = EnchantmentUtilities.FreezeType.Normal
                }, EnemyCreatureSelector());
            if (id == -1249810974 || id == 1750084664)
                return InstallTargetedSummon(card, new CardActions.Damage {
                    HealthLoss = new ActionValueData(id == -1249810974 ? 2 : 4),
                    DamageSource = CardRole.Self
                }, TargetSelector(false, false, true));
            // Retail descriptions: Sharpshooter Scout / Valenwood Huntsman,
            // "Summon: Deal 1 damage." Keep the native targeting and FX path.
            if (id == -1871307781 || id == -1767873507)
                return InstallTargetedSummon(card, new CardActions.Damage {
                    HealthLoss = new ActionValueData(1),
                    DamageSource = CardRole.Self
                }, AnyDamageTarget());
            if (id == 842097822)
            {
                // Retail Savage Ogre: +5/+0 to a creature for this turn only.
                CardActions.Buff buff = new CardActions.Buff();
                buff.Enchantment.ExpiresAtEndOfTurn = true;
                buff.Enchantment.AttributeModifications.Add(
                    new EnchantmentAttributeModification(
                        EnchantmentAttributeModificationType.Add,
                        AttributeType.Attack, 5));
                return InstallTargetedSummon(card, buff, TargetSelector(false, false));
            }
            if (id == 1911637876)
                return InstallEarthboneSpinner(card);
            if (id == 767299130)
                return InstallTargetedSummon(
                    card, new CardActions.Silence(), TargetSelector(false, false, true));
            if (id == 1009056840)
                return InstallTargetedAction(
                    card, new CardActions.Silence(), TargetSelector(false, false, false));
            if (id == -751645406)
            {
                bool installed = InstallTargetedAction(
                    card, new CardActions.Destroy(), TargetSelector(false, false, false));
                if (installed)
                {
                    // Destroy emits ActionPreDestroy before host death cleanup.
                    // Its main GetFxActionType is CountOf and DiscardCards is
                    // stripped, so that path cannot deliver the spell visual.
                    EnsureFxRelation(card, CardFxAction.PreDestroy, -351172765, "Card_PiercingJavelin");
                }
                return installed;
            }
            if (id == 1916654634)
                return InstallTargetedAction(card,
                    TargetSelector(false, false, false),
                    new CardActions.Freeze {
                        PermanentFreeze = false,
                        Type = EnchantmentUtilities.FreezeType.Normal
                    },
                    new CardActions.Damage {
                        HealthLoss = new ActionValueData(1),
                        DamageSource = CardRole.Self
                    });
            if (id == 627783150 || id == -632269135)
            {
                int amount = id == 627783150 ? -1 : -2;
                CardActions.Debuff debuff = new CardActions.Debuff();
                debuff.Enchantment.AttributeModifications.Add(
                    new EnchantmentAttributeModification(
                        EnchantmentAttributeModificationType.Add,
                        AttributeType.Attack, amount));
                debuff.Enchantment.AttributeModifications.Add(
                    new EnchantmentAttributeModification(
                        EnchantmentAttributeModificationType.Add,
                        AttributeType.MaxHealth, amount));
                return InstallTargetedAction(
                    card, debuff, TargetSelector(false, false, false));
            }
            if (id == 1744549196 || id == -1094245595)
            {
                bool installed = InstallTargetedAction(card, new CardActions.Damage {
                    HealthLoss = new ActionValueData(id == 1744549196 ? 3 : 2),
                    DamageSource = CardRole.Self
                }, TargetSelector(false, id == 1744549196, false));
                if (installed && id == 1744549196)
                    EnsureFxRelation(
                        card, CardFxAction.Damage, -451226214,
                        "Card_CrushingBlow_01");
                return installed;
            }
            if (id == 1405826749 || id == 1076369070)
                return InstallTargetedAction(card, new CardActions.Freeze {
                    PermanentFreeze = false,
                    Type = EnchantmentUtilities.FreezeType.Normal
                }, id == 1405826749
                    ? EnemyCreatureSelector()
                    : TargetSelector(false, false, false));
            if (id == -2043856038)
                return InstallTargetedAction(card,
                    TargetSelector(false, false, false),
                    new CardActions.GiveKeyword {
                        KeywordAttrib = AttributeType.Deathtouch
                    },
                    new CardActions.Ward());
            if (id == -37005376)
            {
                DecisionConditionalExpression executeSelector = TargetSelector(false, false, false);
                executeSelector.CardConditionalExpressions.Add(new CardConditionals.AttributeValue {
                    AttributeName = AttributeType.Attack,
                    Is = CardConditionals.AttributeValue.ComparisonType.LessThanOrEqualTo,
                    Value = new ConditionalValueData(2)
                });
                return InstallTargetedAction(card, new CardActions.Destroy(), executeSelector);
            }
            bool summon = id == -1209831649 || id == 207833797 || id == -1769639680 ||
                id == 1106955994 || id == -987994366;
            bool support = id == -646559647;
            bool spell = id == 171706845 || id == -1865755971 || id == 41830377;
            if (!summon && !support && !spell)
                return baseKeywords;
            if (card.TriggeredEffects.Count != 0 ||
                card.ActionPlayInformation.Count != 0 ||
                card.ActivatedAbility.IsValidAbility())
                return false;

            EffectGroup group = new EffectGroup();
            Effect effect = new Effect();
            group.Effects.Add(effect);
            if (id == 1106955994 || id == 207833797 || id == -1769639680)
            {
                CardActions.Buff buff = new CardActions.Buff();
                buff.Enchantment.AttributeModifications.Add(
                    new EnchantmentAttributeModification(
                        EnchantmentAttributeModificationType.Add, AttributeType.Attack, 2));
                if (id == 207833797 || id == -1769639680)
                    buff.Enchantment.AttributeModifications.Add(
                        new EnchantmentAttributeModification(
                            EnchantmentAttributeModificationType.Add, AttributeType.MaxHealth, 2));
                AddAction(effect, buff);
            }
            if (id == -1209831649 || id == 207833797 || id == -1769639680 || support)
                AddAction(effect, new CardActions.GiveKeyword { KeywordAttrib = AttributeType.Taunt });
            if (id == -987994366 || id == 171706845)
                AddAction(effect, new CardActions.Ward());
            if (id == -1865755971 || id == 41830377)
            {
                if (id == 41830377)
                    EnsureFxRelation(
                        card, CardFxAction.Damage, 1762015085,
                        "Card_LightningBolt");
                AddAction(effect, new CardActions.Damage {
                    HealthLoss = new ActionValueData(id == 41830377 ? 4 : 2),
                    DamageSource = CardRole.Self
                });
            }

            DecisionConditionalExpression selector = TargetSelector(support, id == 41830377);
            if (summon)
            {
                TriggeredEffect trigger = new TriggeredEffect();
                trigger.TriggerType = EffectTriggerType.Summon;
                // Instant bypasses the decision queue and executes with a null target.
                trigger.SortOrder = TriggerSortOrder.EndOfActiveQueue;
                trigger.ClientTargetingType = PlayTargetingType.RequiredTarget;
                trigger.TargetSelectors.Add(selector);
                trigger.EffectGroup = group;
                card.TriggeredEffects.Add(trigger);
            }
            else if (support)
            {
                card.ActivatedAbility.TargetingType = PlayTargetingType.RequiredTarget;
                card.ActivatedAbility.TargetSelectors.Add(selector);
                card.ActivatedAbility.AbilityGroups.Add(new AbilityGroup { EffectGroup = group });
                card.SetIntegerAttribute(AttributeType.AbilityUsesPerTurn, 1);
                card.SetIntegerAttribute(AttributeType.TotalUsesAllowed, 3);
            }
            else
            {
                PlayCardAbility ability = new PlayCardAbility();
                ability.PlayTargetingType = PlayTargetingType.RequiredTarget;
                ability.TargetSelectors.Add(selector);
                ability.EffectGroup = group;
                ability.IsDamageOnly = id == -1865755971 || id == 41830377;
                card.ActionPlayInformation.Add(ability);
            }
            return true;
        }

        private static bool InstallRedDeckBaseKeywords(Card card)
        {
            int id = card.ResourceId.Id;
            bool changed = false;
            if (id == 195169233 || id == 1038408842 || id == -424209530 ||
                id == 1970986215 || id == -371816192 || id == -1951249314 ||
                id == -2090944892 || id == 1322838203)
            {
                SetBaseKeyword(card, AttributeType.Charge);
                changed = true;
            }
            if (id == -1879063369 || id == -443759246 || id == 806805335 ||
                id == -1951249314)
            {
                SetBaseKeyword(card, AttributeType.Breakthrough);
                changed = true;
            }
            if (id == 1995521600 || id == -2096820926 || id == -371816192 ||
                id == 1106955994 || id == -1052020638 || id == 94406568 ||
                id == 1322838203)
            {
                SetBaseKeyword(card, AttributeType.Taunt);
                changed = true;
            }
            if (id == -1871307781 || id == 935076603 || id == -33546008 ||
                id == 1995521600 || id == -2096820926 || id == 1106955994)
            {
                SetBaseKeyword(card, AttributeType.Prophecy);
                changed = true;
            }
            if (id == 631750256 || id == 1970986215)
            {
                SetBaseKeyword(card, AttributeType.Rally);
                changed = true;
            }
            return changed;
        }

        private static bool InstallBlueDeckBaseRules(Card card)
        {
            int id = card.ResourceId.Id;
            bool changed = false;
            if (id == -561910135 || id == 1037073850 || id == -1139008311 ||
                id == -710854512 || id == 41830377 || id == -1094245595)
            { SetBaseKeyword(card, AttributeType.Prophecy); changed = true; }
            if (id == -561910135 || id == 1691833255 || id == -1139008311 ||
                id == -800102561 || id == -1173040636)
            { SetBaseKeyword(card, AttributeType.Taunt); changed = true; }
            if (id == -561910135 || id == 1691833255 || id == -295361290 ||
                id == 1300162036 || id == -800102561)
            { SetBaseKeyword(card, AttributeType.Ward); changed = true; }
            if (id == -1173040636)
            { SetBaseKeyword(card, AttributeType.PermFreeze); changed = true; }
            return changed;
        }

        private static void SetBaseKeyword(Card card, AttributeType keyword)
        {
            // Retail stores these as BoolAttribute. SetIntegerAttribute would
            // append a duplicate with the same name, changing the source schema.
            SPTESL.Attribute existing = card.EntityAttributes.Find(
                delegate(SPTESL.Attribute value) { return value.Name == keyword; });
            if (existing is BoolAttribute)
                ((BoolAttribute)existing).Value = true;
            else
                card.SetIntegerAttribute(keyword, 1);
        }

        internal static bool RemoveCravenConscriptShackle(ServerCard card)
        {
            if (Object.ReferenceEquals(card, null) || VariantCardRules.CanonicalId(card.CardId) != 1366329424 ||
                card.QueryAttribute(AttributeType.PermFreeze).Value <= 0)
                return false;

            card.SetAttribute(AttributeType.PermFreeze, 0);
            return true;
        }

        private static bool InstallRally(Card card)
        {
            SetBaseKeyword(card, AttributeType.Rally);
            return true;
        }

        private static void EnsureFxRelation(
            Card card,
            CardFxAction action,
            int assetHash,
            string definition)
        {
            CardFxRelationData existing = card.CardFxRelations.Find(
                delegate(CardFxRelationData relation) {
                    return relation != null && relation.FXAction == action;
                });
            if (existing != null)
                return;

            card.CardFxRelations.Add(new CardFxRelationData {
                FXAction = action,
                AssetHash = assetHash,
                CardFxDefinition = definition
            });
        }

        private static bool InstallBushwhack(Card card)
        {
            CardActions.Buff buff = new CardActions.Buff();
            buff.Enchantment.ExpiresAtEndOfTurn = true;
            buff.Enchantment.AttributeModifications.Add(
                new EnchantmentAttributeModification(
                    EnchantmentAttributeModificationType.Add, AttributeType.IgnoreTaunt, 1));
            buff.Enchantment.AttributeModifications.Add(
                new EnchantmentAttributeModification(
                    EnchantmentAttributeModificationType.Add, AttributeType.CanAttackOtherLane, 1));
            return InstallTargetedAction(card, buff, TargetSelector(true, false));
        }

        private static bool InstallHiddenTrail(Card card)
        {
            if (card.TriggeredEffects.Count != 0 ||
                card.ActionPlayInformation.Count != 0 ||
                card.ActivatedAbility.IsValidAbility())
                return false;

            /* Retail supplies lane changes and aura enchantment primitives.
             * AgilityCardRules restores evaluation of this stripped aura.
             * Recreate only Hidden Trail's two source-defined effects: turn
             * ordinary lanes into Stealth and grant friendly creatures +1 ATK. */
            Aura aura = new Aura();
            AuraEnchantment enchantment = new AuraEnchantment();
            enchantment.Selectors.Add(new CardConditionals.Subtype {
                Check = CardConditionals.Subtype.Requirement.Is,
                SubtypeName = CardSubtype.Creature
            });
            enchantment.Selectors.Add(new CardConditionals.Relationship {
                OtherCard = CardRole.Self,
                Are = CardConditionals.Relationship.RelationshipType.Friends
            });
            enchantment.Enchantment.AttributeModifications.Add(
                new EnchantmentAttributeModification(
                    EnchantmentAttributeModificationType.Add,
                    AttributeType.Attack,
                    1));
            aura.EnchantmentsToApply.Add(enchantment);
            card.Auras.Add(aura);

            Effect effect = new Effect();
            PlayerEffectAction laneChange = new PlayerEffectAction();
            laneChange.Selectors.Add(new PlayerConditionals.PlayerRoleType {
                Check = PlayerConditionals.PlayerRoleType.Requirement.Is,
                Role = PlayerRole.Self
            });
            laneChange.Action = new PlayerActions.ChangeLaneType {
                OldLaneType = CardLaneType.Default,
                NewLaneType = CardLaneType.Stealth
            };
            effect.PlayerActions.Add(laneChange);

            TriggeredEffect trigger = new TriggeredEffect();
            trigger.TriggerType = EffectTriggerType.Summon;
            trigger.EffectGroup.Effects.Add(effect);
            card.TriggeredEffects.Add(trigger);
            Plugin.Log("recovered retail lane rule: card=" + card.ResourceId.Id
                + " name=" + card.Name + " change=Default->Stealth aura=ally-creature+1-attack");
            return true;
        }

        private static bool InstallEarthboneSpinner(Card card)
        {
            if (!CanInstall(card))
                return false;

            EffectGroup group = new EffectGroup();
            Effect effect = new Effect();
            AddAction(effect, new CardActions.Silence());
            AddAction(effect, new CardActions.Damage {
                HealthLoss = new ActionValueData(1),
                DamageSource = CardRole.Self
            });
            group.Effects.Add(effect);

            TriggeredEffect trigger = new TriggeredEffect();
            trigger.TriggerType = EffectTriggerType.Summon;
            // Instant bypasses the decision queue and executes with a null target.
            trigger.SortOrder = TriggerSortOrder.EndOfActiveQueue;
            trigger.ClientTargetingType = PlayTargetingType.RequiredTarget;
            trigger.TargetSelectors.Add(TargetSelector(false, false, true));
            trigger.EffectGroup = group;
            card.TriggeredEffects.Add(trigger);
            return true;
        }

        private static bool InstallTargetedSummon(Card card, ICardAction action,
            DecisionConditionalExpression selector)
        {
            if (!CanInstall(card))
                return false;
            EffectGroup group = NewEffectGroup(action);
            TriggeredEffect trigger = new TriggeredEffect();
            trigger.TriggerType = EffectTriggerType.Summon;
            // Instant bypasses the decision queue and executes with a null target.
            trigger.SortOrder = TriggerSortOrder.EndOfActiveQueue;
            trigger.ClientTargetingType = PlayTargetingType.RequiredTarget;
            trigger.TargetSelectors.Add(selector);
            trigger.EffectGroup = group;
            card.TriggeredEffects.Add(trigger);
            return true;
        }

        private static bool InstallTargetedAction(Card card, ICardAction action,
            DecisionConditionalExpression selector)
        {
            return InstallTargetedAction(card, selector, action);
        }

        private static bool InstallTargetedAction(Card card,
            DecisionConditionalExpression selector, params ICardAction[] actions)
        {
            if (!CanInstall(card))
                return false;
            PlayCardAbility ability = new PlayCardAbility();
            ability.PlayTargetingType = PlayTargetingType.RequiredTarget;
            ability.TargetSelectors.Add(selector);
            EffectGroup group = new EffectGroup();
            Effect effect = new Effect();
            int i;
            for (i = 0; i < actions.Length; ++i)
                AddAction(effect, actions[i]);
            group.Effects.Add(effect);
            ability.EffectGroup = group;
            card.ActionPlayInformation.Add(ability);
            return true;
        }

        private static EffectGroup NewEffectGroup(ICardAction action)
        {
            EffectGroup group = new EffectGroup();
            Effect effect = new Effect();
            AddAction(effect, action);
            group.Effects.Add(effect);
            return group;
        }

        private static bool CanInstall(Card card)
        {
            return card.TriggeredEffects.Count == 0 &&
                card.ActionPlayInformation.Count == 0 &&
                !card.ActivatedAbility.IsValidAbility();
        }

        private static void AddAction(Effect effect, ICardAction action)
        {
            CardEffectAction entry = new CardEffectAction();
            // -1 asks the original executor for this card's FX override/default.
            entry.HashId = -1;
            entry.Action = action;
            entry.Selectors.Add(new CardConditionals.CardRoleType {
                Role = CardRole.DecisionTarget,
                Check = CardConditionals.CardRoleType.Requirement.Is
            });
            effect.CardActions.Add(entry);
        }

        private static DecisionConditionalExpression TargetSelector(bool friendly, bool allowAvatar)
        {
            return TargetSelector(friendly, allowAvatar, false);
        }

        private static DecisionConditionalExpression TargetSelector(
            bool friendly, bool allowAvatar, bool other)
        {
            DecisionConditionalExpression selector = new DecisionConditionalExpression();
            selector.DecisionType = SPTESL.DecisionType.Card;
            selector.CardConditionalExpressions.Add(new BoardTarget { AllowAvatar = allowAvatar });
            if (friendly)
                selector.CardConditionalExpressions.Add(new CardConditionals.Relationship {
                    OtherCard = CardRole.Self,
                    Are = CardConditionals.Relationship.RelationshipType.Friends
                });
            if (other)
                selector.CardConditionalExpressions.Add(new CardConditionals.CardRoleType {
                    Role = CardRole.Self,
                    Check = CardConditionals.CardRoleType.Requirement.IsNot
                });
            if (!friendly && !allowAvatar && !other)
            {
                // No relationship restriction: actions such as Suppress may
                // target either player's creature.
            }
            return selector;
        }

        private static DecisionConditionalExpression EnemyCreatureSelector()
        {
            DecisionConditionalExpression selector =
                TargetSelector(false, false, true);
            selector.CardConditionalExpressions.Add(
                new CardConditionals.Relationship {
                    OtherCard = CardRole.Self,
                    Are = CardConditionals.Relationship.RelationshipType.Enemies
                });
            return selector;
        }

        private static DecisionConditionalExpression AnyDamageTarget()
        {
            // "Deal 1 damage" has no "another"/"enemy" qualifier. The local
            // structured reference also records target=any for both scouts.
            DecisionConditionalExpression selector = TargetSelector(false, true);
            ((BoardTarget)selector.CardConditionalExpressions[0]).AllowFriendlyAvatar = true;
            return selector;
        }

        // Zone names avoid accidentally offering support/item cards or the dead
        // cards temporarily held in Deathrattle to targeted damage/buffs.
        internal sealed class BoardTarget : ICardConditionalExpression
        {
            internal bool AllowAvatar;
            internal bool AllowFriendlyAvatar;
            public bool Evaluate(ServerCard card, CardConditionalArguments cards,
                PlayerConditionalArguments players, TriggerConditionalArguments trigger)
            {
                if (card == null || card.IsDying())
                    return false;
                if (card.CurrentGameZoneName == GameZoneName.CreatureBoard)
                    return true;
                return AllowAvatar &&
                    card.CurrentGameZoneName == GameZoneName.AvatarBoard &&
                    cards.SelfCard != null &&
                    (AllowFriendlyAvatar || card.OwnerNetId != cards.SelfCard.OwnerNetId);
            }
        }
    }
}
