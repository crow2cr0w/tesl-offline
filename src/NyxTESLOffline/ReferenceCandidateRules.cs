using SPTESL;
namespace NyxTESLOffline
{
    internal static partial class ReferenceSummonRules
    {
        static bool InstallCandidate(Card card)
        {
            int id = card.ResourceId.Id;
            if (id == -2048305816) return InstallMarshal(card);
            if (id == -1299948782) return InstallPatrol(card);
            if (id != -1432506315 && id != 820258389 && id != -1955645154 && id != 848305548 && id != 1129542058) return false;
            var group = new EffectGroup(); var effect = new Effect(); group.Effects.Add(effect);
            var action = new CardEffectAction { HashId = -1 };
            if (id == -1432506315 || id == 820258389) action.Action = new CardActions.Damage {
                HealthLoss = new ActionValueData(id == -1432506315 ? 1 : 4), DamageSource = CardRole.Self };
            else if (id == -1955645154) action.Action = new CardActions.Freeze {
                Type = EnchantmentUtilities.FreezeType.Normal, PermanentFreeze = false };
            else if (id == 1129542058) action.Action = new CardActions.RemoveCover();
            else {
                var heal = new CardActions.Heal { HealthGain = new ActionValueData(4) };
                heal.DrainFxSelector.Add(new CardConditionals.CardRoleType {
                    Role = CardRole.Self, Check = CardConditionals.CardRoleType.Requirement.Is });
                action.Action = heal;
            }
            action.Selectors.Add(new CandidateTarget { Id = id });
            if (id == 820258389) {
                action.SelectorSort.Method = CardListSort.SortMethod.Random;
                action.SelectorSort.NumToReturn = new ActionValueData(1);
            }
            effect.CardActions.Add(action);
            var trigger = new TriggeredEffect { TriggerType = id == 820258389 ? EffectTriggerType.WhenPlayerTurnStarts :
                id == -1955645154 ? EffectTriggerType.WhenCardAttackComplete : EffectTriggerType.Summon,
                SortOrder = TriggerSortOrder.EndOfActiveQueue, ClientTargetingType = PlayTargetingType.NoTarget, EffectGroup = group };
            trigger.TriggerCardRequirements.Add(new CandidateRequirement {
                LhsCard = CardRole.Self, Conditional = new CandidateSource { Id = id } });
            card.TriggeredEffects.Add(trigger); return true;
        }
        sealed class CandidateRequirement : GenericCardRequirement<CandidateSource> { }
        static bool InstallPatrol(Card card)
        {
            var group = new EffectGroup(); var effect = new Effect(); group.Effects.Add(effect);
            var action = new PlayerEffectAction { HashId = -1, Action = new PatrolDraw() };
            action.Selectors.Add(new PlayerConditionals.PlayerRoleType { Role = PlayerRole.Self,
                Check = PlayerConditionals.PlayerRoleType.Requirement.Is }); effect.PlayerActions.Add(action);
            var trigger = new TriggeredEffect { TriggerType = EffectTriggerType.Summon, SortOrder = TriggerSortOrder.EndOfActiveQueue,
                ClientTargetingType = PlayTargetingType.NoTarget, EffectGroup = group };
            trigger.TriggerCardRequirements.Add(new CandidateRequirement { LhsCard = CardRole.Self,
                Conditional = new CandidateSource { Id = -1299948782 } });
            card.TriggeredEffects.Add(trigger); return true;
        }
        sealed class PatrolDraw : IPlayerAction
        {
            public CardFxAction GetFxActionType() { return CardFxAction.CountOf; }
            public void Execute(PlayerActionArguments args)
            {
                var source = args.CardArguments.SelfCard;
                foreach (var player in args.ValidTargetList) {
                    var others = args.TriggerGameState.CardCatalog.GatherCardsInZone(c => c != source &&
                        !c.IsDying() && c.HasSubtype(CardSubtype.Strength), player.NetId, GameZoneName.CreatureBoard);
                    if (others.Count < 2) continue;
                    OfflineDrawUtilities.Draw(args.TriggerGameState, player, 1, CcgRpc.HistoryType.ActionMoveCards, "Stronghold Patrol");
                }
            }
        }
        sealed class CandidateSource : ICardConditionalExpression
        {
            public int Id;
            public bool Evaluate(ServerCard card, CardConditionalArguments cards, PlayerConditionalArguments players, TriggerConditionalArguments trigger)
            {
                return card != null && card.CurrentGameZoneName == GameZoneName.CreatureBoard && !card.IsDying() &&
                    (Id != 820258389 || (players.SelfPlayerState != null && players.SelfPlayerState.IsMyTurn)) &&
                    (Id != -1955645154 || card == cards.InciterCard);
            }
        }
        sealed class CandidateTarget : ICardConditionalExpression
        {
            public int Id;
            public bool Evaluate(ServerCard card, CardConditionalArguments cards, PlayerConditionalArguments players, TriggerConditionalArguments trigger)
            {
                var self = cards.SelfCard;
                if (card == null || self == null || card.IsDying()) return false;
                if (Id == 848305548) return card.CurrentGameZoneName == GameZoneName.AvatarBoard && card.OwnerNetId == self.OwnerNetId;
                if (Id == -1955645154) return card == self;
                if (Id == 820258389) return card.OwnerNetId != self.OwnerNetId &&
                    (card.CurrentGameZoneName == GameZoneName.CreatureBoard || card.CurrentGameZoneName == GameZoneName.AvatarBoard);
                return card.CurrentGameZoneName == GameZoneName.CreatureBoard && card.CurrentLaneIndex == self.CurrentLaneIndex &&
                    (Id == -1432506315 ? card != self : card.OwnerNetId != self.OwnerNetId);
            }
        }
    }
}
