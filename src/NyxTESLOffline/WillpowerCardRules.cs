using SPTESL;

namespace NyxTESLOffline
{
    internal static partial class RecoveredCardRules
    {
        private static bool IsWillpowerRule(int id)
        {
            return id == -452943738 || id == 2052829044 || id == 652289987 ||
                id == -1395960197 || id == -30507580 || id == 1462928361 ||
                id == -1543808196 || id == -587531429 || id == 775516546 ||
                id == -1143757804;
        }

        private static CardActions.Buff StatBuff(AttributeType attribute, int amount, bool temporary)
        {
            CardActions.Buff buff = new CardActions.Buff();
            buff.Enchantment.ExpiresAtEndOfTurn = temporary;
            buff.Enchantment.AttributeModifications.Add(new EnchantmentAttributeModification(
                EnchantmentAttributeModificationType.Add, attribute, amount));
            return buff;
        }

        private static bool InstallWillpowerRule(Card card)
        {
            if (!CanInstall(card)) return false;
            int id = card.ResourceId.Id;
            // Sunhold Medic / Cleric of Kyne: any creature, permanent +0/+2.
            if (id == -452943738 || id == 2052829044)
            {
                InstallTargetedSummon(card, StatBuff(AttributeType.MaxHealth, 2, false), TargetSelector(false, false));
                if (id == -452943738)
                    EnsureFxRelation(card, CardFxAction.Buff, 1823094219, "Card_SunholdMedic");
            }
            // Cloudrest Illusionist / Calm: any creature, -4 attack this turn.
            else if (id == 652289987)
                InstallTargetedSummon(card, StatBuff(AttributeType.Attack, -4, true), TargetSelector(false, false));
            else if (id == -1143757804)
                InstallTargetedAction(card, StatBuff(AttributeType.Attack, -4, true), TargetSelector(false, false));
            else if (id == 1462928361) // Spiteful Dremora
            {
                DecisionConditionalExpression selector = TargetSelector(false, false);
                selector.CardConditionalExpressions.Add(new CardConditionals.AttributeValue {
                    AttributeName = AttributeType.Attack,
                    Is = CardConditionals.AttributeValue.ComparisonType.LessThanOrEqualTo,
                    Value = new ConditionalValueData(2)
                });
                InstallTargetedSummon(card, new CardActions.Destroy(), selector);
            }
            else if (id == -1543808196) // Pillaging Tribune: Drain expires, including on self.
                InstallTargetedSummon(card, StatBuff(AttributeType.Drain, 1, true), TargetSelector(true, false));
            else
            {
                EffectGroup group = new EffectGroup();
                Effect effect = new Effect();
                group.Effects.Add(effect);
                if (id == -1395960197 || id == -30507580)
                {
                    // Priest of the Moons / Healing Potion target their owner's avatar.
                    CardEffectAction action = new CardEffectAction();
                    action.HashId = -1;
                    action.Action = new CardActions.Heal { HealthGain = new ActionValueData(id == -1395960197 ? 2 : 5) };
                    action.Selectors.Add(new FriendlyAvatarTarget());
                    // Empty DrainFxSelector means all cards to the retail selector.
                    // Set the source explicitly, so healing never binds to an unrelated card.
                    ((CardActions.Heal)action.Action).DrainFxSelector.Add(new CardConditionals.CardRoleType {
                        Role = CardRole.Self, Check = CardConditionals.CardRoleType.Requirement.Is
                    });
                    effect.CardActions.Add(action);
                }
                else if (id == 775516546) // Pack Wolf: only the opposite lane.
                    AddToken(effect, 858815917, new InsertPosition {
                        Type = InsertPosition.LaneChoiceType.Relative, Lhs = CardRole.Self,
                        Comparison = InsertPosition.PositionComparison.OtherLane,
                        InsertionType = CardInsertionType.Bottom
                    });
                else if (id == -587531429) // Scouting Patrol: one Grunt per lane.
                {
                    AddToken(effect, 1150098037, new InsertPosition {
                        Type = InsertPosition.LaneChoiceType.Constant, Lane = LaneIndexEnum.Left,
                        InsertionType = CardInsertionType.Bottom
                    });
                    AddToken(effect, 1150098037, new InsertPosition {
                        Type = InsertPosition.LaneChoiceType.Constant, Lane = LaneIndexEnum.Right,
                        InsertionType = CardInsertionType.Bottom
                    });
                }
                if (id == -30507580 || id == -587531429)
                    card.ActionPlayInformation.Add(new PlayCardAbility {
                        PlayTargetingType = PlayTargetingType.NoTarget, EffectGroup = group
                    });
                else
                    card.TriggeredEffects.Add(new TriggeredEffect {
                        TriggerType = EffectTriggerType.Summon,
                        SortOrder = TriggerSortOrder.EndOfActiveQueue,
                        ClientTargetingType = PlayTargetingType.NoTarget,
                        EffectGroup = group
                    });
            }
            return true;
        }

        private static void AddToken(Effect effect, int tokenId, InsertPosition position)
        {
            PlayerEffectAction action = new PlayerEffectAction();
            action.HashId = -1;
            action.Selectors.Add(new PlayerConditionals.PlayerRoleType {
                Role = PlayerRole.Self, Check = PlayerConditionals.PlayerRoleType.Requirement.Is
            });
            action.Action = new PlayerActions.CreateToken {
                CardReference = new EditorCardReference(tokenId), NumTokens = new ActionValueData(1),
                CreateInZoneType = GameZoneType.Board, InsertPosition = position
            };
            effect.PlayerActions.Add(action);
        }

        private sealed class FriendlyAvatarTarget : ICardConditionalExpression
        {
            public bool Evaluate(ServerCard card, CardConditionalArguments cards,
                PlayerConditionalArguments players, TriggerConditionalArguments trigger)
            {
                return card != null && cards.SelfCard != null &&
                    card.CurrentGameZoneName == GameZoneName.AvatarBoard &&
                    card.OwnerNetId == cards.SelfCard.OwnerNetId;
            }
        }
    }
}
