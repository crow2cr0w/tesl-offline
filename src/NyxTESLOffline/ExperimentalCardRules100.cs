using SPTESL;
using System.Linq;
namespace NyxTESLOffline
{
    // Original descriptions -> native Damage emits Pilfer/Slay -> native permanent Buff.
    // Experimental only: presentation remains the original resolver, never fabricated.
    internal static class ExperimentalCardRules100
    {
        internal const int SylId = -86742806;
        private const int AvatarDamageFx = 1646985500; // retail card_damage
        internal static readonly int[] CardIds = {
            -1572966262, 988424426, 250717238, -6629593, -1310707462, -819919458, -1414651238
        };
        internal static void Apply(GameConfiguration config)
        {
            foreach (int id in CardIds) {
                Card card = config.FindCardById(id);
                if (card != null && Install(card)) Plugin.Log("experimental trigger recovery: " + id);
            }
            Card syl = config.FindCardById(SylId);
            if (syl != null && Install(syl)) Plugin.Log("experimental Syl lane recovery: " + SylId);
        }
        internal static void ResolveDementiaTurnStart(ServerEffectUtilities.TriggerGameState state, PlayerState active)
        {
            if (active == null) return;
            var catalog = state.CardCatalog;
            var opponent = state.Players.Find(p => p.NetId != active.NetId);
            if (opponent == null) return;

            int laneCount = catalog.FindLaneCount(GameZoneName.CreatureBoard, active.NetId);
            for (int lane = 0; lane < laneCount; lane++)
            {
                if (catalog.QueryCreatureBoardLaneType(active.NetId, lane) != CardLaneType.Dementia)
                    continue;

                var activeCards = catalog.GatherCardsInZone(c =>
                    !c.IsDying() && c.HasSubtype(CardSubtype.Creature) && c.CurrentLaneIndex == lane,
                    active.NetId, GameZoneName.CreatureBoard);
                if (activeCards.Count == 0) continue;
                var enemyCards = catalog.GatherCardsInZone(c =>
                    !c.IsDying() && c.HasSubtype(CardSubtype.Creature) && c.CurrentLaneIndex == lane,
                    opponent.NetId, GameZoneName.CreatureBoard);
                int activePower = activeCards.Max(c => c.QueryAttribute(AttributeType.Attack).Value);
                int enemyPower = enemyCards.Count == 0 ? int.MinValue : enemyCards.Max(c => c.QueryAttribute(AttributeType.Attack).Value);
                if (activePower < enemyPower) continue;
                ServerCard avatar = catalog.GatherCardsInZone(c => true,
                    opponent.NetId, GameZoneName.AvatarBoard).FirstOrDefault();
                if (avatar != null)
                {
                    int healthBefore = avatar.CurrentHealth;
                    PlayerActions.DamageAvatarOfPlayer.StaticExecute(
                        state, null, opponent, 3, AvatarDamageFx);
                    Plugin.Log("Dementia lane damage: activePlayer=" + active.NetId.Id
                        + " opponent=" + opponent.NetId.Id + " lane=" + lane
                        + " activeHighest=" + activePower + " opponentHighest=" + enemyPower
                        + " opponentHealth=" + healthBefore + "->" + avatar.CurrentHealth);
                }
            }
        }
        internal static bool Install(Card card)
        {
            int id = card.ResourceId.Id;
            if (id == SylId) return InstallSyl(card);
            if (System.Array.IndexOf(CardIds, id) < 0 || card.TriggeredEffects.Count != 0 ||
                card.ActionPlayInformation.Count != 0 || card.ActivatedAbility.IsValidAbility() || card.Auras.Count != 0)
                return false;
            int attack = 1, health = 1;
            if (id == 988424426) { attack = 3; health = 0; }
            if (id == 250717238) { attack = 2; health = 2; }
            if (id == -6629593) { attack = 4; health = 4; }
            if (id == -1310707462) { attack = 1; health = 0; }
            AddSelfBuff(card, id == -819919458 || id == -1414651238 ? EffectTriggerType.Slay : EffectTriggerType.Pilfer, attack, health);
            if (id == -1310707462) AddSelfBuff(card, EffectTriggerType.Slay, attack, health);
            return true;
        }
        private static bool InstallSyl(Card card)
        {
            if (card.TriggeredEffects.Count != 0 || card.ActionPlayInformation.Count != 0 ||
                card.ActivatedAbility.IsValidAbility() || card.Auras.Count != 0) return false;
            var action = new CardEffectAction {
                HashId = -1, Action = new CardActions.ChangeCardLaneType { NewLaneType = CardLaneType.Dementia }
            };
            action.Selectors.Add(new CardConditionals.CardRoleType {
                Role = CardRole.Self, Check = CardConditionals.CardRoleType.Requirement.Is
            });
            var effect = new Effect(); effect.CardActions.Add(action);
            var group = new EffectGroup(); group.Effects.Add(effect);
            card.TriggeredEffects.Add(new TriggeredEffect {
                TriggerType = EffectTriggerType.Summon, SortOrder = TriggerSortOrder.EndOfActiveQueue,
                ClientTargetingType = PlayTargetingType.NoTarget, EffectGroup = group
            });
            return true;
        }
        private static void AddSelfBuff(Card card, EffectTriggerType triggerType, int attack, int health)
        {
            var buff = new CardActions.Buff();
            buff.Enchantment.AttributeModifications.Add(new EnchantmentAttributeModification(
                EnchantmentAttributeModificationType.Add, AttributeType.Attack, attack));
            if (health != 0) buff.Enchantment.AttributeModifications.Add(new EnchantmentAttributeModification(
                EnchantmentAttributeModificationType.Add, AttributeType.MaxHealth, health));
            var action = new CardEffectAction { HashId = -1, Action = buff };
            action.Selectors.Add(new CardConditionals.CardRoleType {
                Role = CardRole.Self, Check = CardConditionals.CardRoleType.Requirement.Is
            });
            // A queued Slay buff cannot rescue its source after simultaneous lethal damage.
            action.Selectors.Add(new CardConditionals.IsDying {
                Dying = CardConditionals.IsDying.Requirement.IsNot
            });
            var effect = new Effect();
            effect.CardActions.Add(action);
            var group = new EffectGroup();
            group.Effects.Add(effect);
            card.TriggeredEffects.Add(new TriggeredEffect {
                TriggerType = triggerType, SortOrder = TriggerSortOrder.EndOfActiveQueue,
                ClientTargetingType = PlayTargetingType.NoTarget, EffectGroup = group
            });
        }
    }
}
