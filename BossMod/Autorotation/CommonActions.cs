﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace BossMod
{
    abstract class CommonActions : IDisposable
    {
        public const int AutoActionNone = 0;
        public const int AutoActionAIIdle = 1;
        public const int AutoActionAIIdleMove = 2;
        public const int AutoActionFirstFight = 3;
        public const int AutoActionAIFight = 3;
        public const int AutoActionAIFightMove = 4;
        public const int AutoActionFirstCustom = 5;

        public enum Positional { Any, Flank, Rear }
        public enum ActionSource { Automatic, Planned, Manual, Emergency }

        public struct NextAction
        {
            public ActionID Action;
            public Actor? Target;
            public Vector3 TargetPos;
            public ActionDefinition Definition;
            public ActionSource Source;

            public NextAction(ActionID action, Actor? target, Vector3 targetPos, ActionDefinition definition, ActionSource source)
            {
                Action = action;
                Target = target;
                TargetPos = targetPos;
                Definition = definition;
                Source = source;
            }
        }

        public class SupportedAction
        {
            public ActionDefinition Definition;
            public bool IsGT;
            public Func<Actor?, bool>? Condition;
            public int PlaceholderForAuto; // if set, attempting to execute this action would instead initiate auto-strategy
            public Func<ActionID>? TransformAction;
            public Func<Actor?, Actor?>? TransformTarget;

            public SupportedAction(ActionDefinition definition, bool isGT)
            {
                Definition = definition;
                IsGT = isGT;
            }

            public bool Allowed(Actor player, Actor target)
            {
                if (Definition.Range > 0 && player != target)
                {
                    var distSq = (target.Position - player.Position).LengthSq();
                    var effRange = Definition.Range + player.HitboxRadius + target.HitboxRadius;
                    if (distSq > effRange * effRange)
                        return false;
                }
                return Condition == null || Condition(target);
            }
        }

        public Actor Player { get; init; }
        public Dictionary<ActionID, SupportedAction> SupportedActions { get; init; } = new();
        public Positional PreferredPosition { get; protected set; } // implementation can update this as needed
        public float PreferredRange { get; protected set; } = 3; // implementation can update this as needed
        protected Autorotation Autorot;
        protected int AutoAction { get; private set; }
        private DateTime _autoActionExpire;
        private QuestLockCheck _lock;
        private ManualActionOverride _mq;

        public SupportedAction SupportedSpell<AID>(AID aid) where AID : Enum => SupportedActions[ActionID.MakeSpell(aid)];

        protected unsafe CommonActions(Autorotation autorot, Actor player, QuestLockEntry[] unlockData, Dictionary<ActionID, ActionDefinition> supportedActions)
        {
            Player = player;
            Autorot = autorot;
            foreach (var (aid, def) in supportedActions)
                SupportedActions[aid] = new(def, aid.IsGroundTargeted());
            _lock = new(unlockData);
            _mq = new(autorot.Cooldowns, autorot.WorldState);
        }

        // this is called after worldstate update
        public void UpdateMainTick()
        {
            _mq.RemoveExpired();
            if (AutoAction != AutoActionNone && _autoActionExpire < Autorot.WorldState.CurrentTime)
            {
                Log($"Auto action {AutoAction} expired");
                AutoAction = AutoActionNone;
            }
        }

        // this is called from actionmanager's post-update callback
        public void UpdateAMTick()
        {
            if (AutoAction != AutoActionNone)
                UpdateInternalState(AutoAction);
        }

        public unsafe bool HaveItemInInventory(uint id)
        {
            return FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance()->GetInventoryItemCount(id % 1000000, id >= 1000000, false, false) > 0;
        }

        public float StatusDuration(DateTime expireAt)
        {
            return Math.Max((float)(expireAt - Autorot.WorldState.CurrentTime).TotalSeconds, 0.0f);
        }

        // check whether specified status is a damage buff
        public bool IsDamageBuff(uint statusID)
        {
            // see https://i.redd.it/xrtgpras94881.png
            // TODO: AST card buffs?, enemy debuffs?, single-target buffs (DRG dragon sight, DNC devilment)
            return statusID switch
            {
                49 => true, // medicated
                141 => true, // BRD battle voice
                //638 => true, // NIN trick attack - note that this is a debuff on enemy
                786 => true, // DRG battle litany
                1185 => true, // MNK brotherhood
                //1221 => true, // SCH chain stratagem - note that this is a debuff on enemy
                1297 => true, // RDM embolden
                1822 => true, // DNC technical finish
                1878 => true, // AST divination
                2599 => true, // RPR arcane circle
                2703 => true, // SMN searing light
                2964 => true, // BRD radiant finale
                _ => false
            };
        }

        public void UpdateAutoAction(int autoAction)
        {
            if (AutoAction != autoAction)
                Log($"Auto action set to {autoAction}");
            AutoAction = autoAction;
            _autoActionExpire = Autorot.WorldState.CurrentTime.AddSeconds(1.0f);
        }

        public bool HandleUserActionRequest(ActionID action, Actor? target)
        {
            var supportedAction = SupportedActions.GetValueOrDefault(action);
            if (supportedAction == null)
                return false;

            if (supportedAction.TransformAction != null)
            {
                var adjAction = supportedAction.TransformAction();
                if (adjAction != action)
                {
                    action = adjAction;
                    supportedAction = SupportedActions[adjAction];
                }
            }

            if (supportedAction.PlaceholderForAuto != AutoActionNone)
            {
                UpdateAutoAction(supportedAction.PlaceholderForAuto);
                return true;
            }

            // this is a manual action
            if (supportedAction.IsGT)
            {
                if (Autorot.Config.GTMode == AutorotationConfig.GroundTargetingMode.Manual)
                    return false;

                if (Autorot.Config.GTMode == AutorotationConfig.GroundTargetingMode.AtCursor)
                {
                    var pos = ActionManagerEx.Instance!.GetWorldPosUnderCursor();
                    if (pos == null)
                        return false; // same as manual...
                    _mq.Push(action, null, pos.Value, supportedAction.Definition, supportedAction.Condition);
                    return true;
                }
            }

            if (supportedAction.TransformTarget != null)
            {
                target = supportedAction.TransformTarget(target);
            }
            _mq.Push(action, target, new(), supportedAction.Definition, supportedAction.Condition);
            return true;
        }

        // effective animation lock is 0 if we're getting an action to use, otherwise it can be larger (e.g. if we're showing next-action hint during animation lock)
        public NextAction CalculateNextAction()
        {
            // check emergency mode
            var mqEmergency = _mq.PeekEmergency();
            if (mqEmergency != null)
                return new(mqEmergency.Action, mqEmergency.Target, mqEmergency.TargetPos, mqEmergency.Definition, ActionSource.Emergency);

            var effAnimLock = Autorot.EffAnimLock;
            var animLockDelay = Autorot.AnimLockDelay;

            // see if we have any GCD (queued or automatic)
            var mqGCD = _mq.PeekGCD();
            var nextGCD = mqGCD != null ? new NextAction(mqGCD.Action, mqGCD.Target, mqGCD.TargetPos, mqGCD.Definition, ActionSource.Manual) : AutoAction != AutoActionNone ? CalculateAutomaticGCD() : new();
            float ogcdDeadline = nextGCD.Action ? Autorot.Cooldowns[CommonDefinitions.GCDGroup] : float.MaxValue;

            // search for any oGCDs that we can execute without delaying GCD
            var mqOGCD = _mq.PeekOGCD(effAnimLock, animLockDelay, ogcdDeadline);
            if (mqOGCD != null)
                return new(mqOGCD.Action, mqOGCD.Target, mqOGCD.TargetPos, mqOGCD.Definition, ActionSource.Manual);

            // see if there is anything from cooldown plan to be executed
            var cooldownPlan = Autorot.Bossmods.ActiveModule?.PlanExecution;
            if (cooldownPlan != null)
            {
                // TODO: support non-self targeting
                // TODO: support custom conditions in planner
                var planTarget = Player;
                var cpAction = cooldownPlan.ActiveActions(Autorot.Bossmods.ActiveModule!.StateMachine).Where(x => CanExecutePlannedAction(x.Action, planTarget, x.Definition, effAnimLock, animLockDelay, ogcdDeadline)).MinBy(x => x.TimeLeft);
                if (cpAction.Action)
                    return new(cpAction.Action, planTarget, new(), cpAction.Definition, ActionSource.Planned);
            }

            // note: we intentionally don't check that automatic oGCD really does not clip GCD - we provide utilities that allow module checking that, but also allow overriding if needed
            var nextOGCD = AutoAction != AutoActionNone ? CalculateAutomaticOGCD(ogcdDeadline) : new();
            return nextOGCD.Action ? nextOGCD : nextGCD;
        }

        public void NotifyActionExecuted(ActionID action, Actor? target)
        {
            _mq.Pop(action);
            OnActionExecuted(action, target);
        }

        public void NotifyActionSucceeded(ActorCastEvent ev)
        {
            OnActionSucceeded(ev);
        }

        public abstract void Dispose();
        protected abstract void UpdateInternalState(int autoAction);
        protected abstract NextAction CalculateAutomaticGCD();
        protected abstract NextAction CalculateAutomaticOGCD(float deadline);
        protected abstract void OnActionExecuted(ActionID action, Actor? target);
        protected abstract void OnActionSucceeded(ActorCastEvent ev);

        protected NextAction MakeResult(ActionID action, Actor target)
        {
            var data = action ? SupportedActions[action] : null;
            return (data?.Allowed(Player, target) ?? false) ? new(action, target, new(), data.Definition, ActionSource.Automatic) : new();
        }
        protected NextAction MakeResult<AID>(AID aid, Actor target) where AID : Enum => MakeResult(ActionID.MakeSpell(aid), target);

        // fill common state properties
        protected unsafe void FillCommonPlayerState(CommonRotation.PlayerState s)
        {
            var am = ActionManagerEx.Instance!;
            var pc = Service.ClientState.LocalPlayer;
            s.Level = _lock.AdjustLevel(pc?.Level ?? 0);
            s.CurMP = pc?.CurrentMp ?? 0;
            s.AnimationLock = am.EffectiveAnimationLock;
            s.AnimationLockDelay = am.EffectiveAnimationLockDelay;
            s.ComboTimeLeft = am.ComboTimeLeft;
            s.ComboLastAction = am.ComboLastMove;

            foreach (var status in Player.Statuses.Where(s => IsDamageBuff(s.ID)))
            {
                s.RaidBuffsLeft = MathF.Max(s.RaidBuffsLeft, StatusDuration(status.ExpireAt));
            }
            // TODO: also check damage-taken debuffs on target
        }

        // fill common strategy properties
        protected void FillCommonStrategy(CommonRotation.Strategy strategy, ActionID potion)
        {
            strategy.Prepull = !Player.InCombat;
            strategy.FightEndIn = Autorot.Bossmods.ActiveModule?.PlanExecution?.EstimateTimeToNextDowntime(Autorot.Bossmods.ActiveModule?.StateMachine) ?? 0;
            strategy.RaidBuffsIn = Autorot.Bossmods.ActiveModule?.PlanExecution?.EstimateTimeToNextVulnerable(Autorot.Bossmods.ActiveModule?.StateMachine) ?? 10000;
            if (Autorot.Bossmods.ActiveModule?.PlanConfig != null) // assumption: if there is no planning support for encounter (meaning it's something trivial, like outdoor boss), don't expect any cooldowns
                strategy.RaidBuffsIn = Math.Min(strategy.RaidBuffsIn, Autorot.Bossmods.RaidCooldowns.NextDamageBuffIn(Autorot.WorldState.CurrentTime));
            strategy.PositionLockIn = Autorot.Config.EnableMovement ? (Autorot.Bossmods.ActiveModule?.PlanExecution?.EstimateTimeToNextPositioning(Autorot.Bossmods.ActiveModule?.StateMachine) ?? 10000) : 0;
            strategy.Potion = Autorot.Config.PotionUse;
            if (strategy.Potion != CommonRotation.Strategy.PotionUse.Manual && !HaveItemInInventory(potion.ID)) // don't try to use potions if player doesn't have any
                strategy.Potion = CommonRotation.Strategy.PotionUse.Manual;
        }

        // smart targeting utility: return target (if friendly) or mouseover (if friendly) or null (otherwise)
        protected Actor? SmartTargetFriendly(Actor? primaryTarget)
        {
            if (primaryTarget?.Type is ActorType.Player or ActorType.Chocobo)
                return primaryTarget;

            if (Autorot.SecondaryTarget?.Type is ActorType.Player or ActorType.Chocobo)
                return Autorot.SecondaryTarget;

            return null;
        }

        // smart targeting utility: return mouseover (if hostile and allowed) or target (otherwise)
        protected Actor? SmartTargetHostile(Actor? primaryTarget)
        {
            if (Autorot.SecondaryTarget?.Type == ActorType.Enemy && !Autorot.SecondaryTarget.IsAlly)
                return Autorot.SecondaryTarget;

            return primaryTarget;
        }

        // smart targeting utility: return target (if friendly) or mouseover (if friendly) or other tank (if available) or null (otherwise)
        protected Actor? SmartTargetCoTank(Actor? primaryTarget)
        {
            return SmartTargetFriendly(primaryTarget) ?? Autorot.WorldState.Party.WithoutSlot().Exclude(Player).FirstOrDefault(a => a.Role == Role.Tank);
        }

        protected void Log(string message)
        {
            if (Autorot.Config.Logging)
                Service.Log($"[AR] [{GetType()}] {message}");
        }

        private bool CanExecutePlannedAction(ActionID action, Actor target, ActionDefinition definition, float effAnimLock, float animLockDelay, float deadline)
        {
            // TODO: planned GCDs?..
            return definition.CooldownGroup != CommonDefinitions.GCDGroup
                && Autorot.Cooldowns[definition.CooldownGroup] - effAnimLock <= definition.CooldownAtFirstCharge
                && effAnimLock + definition.AnimationLock + animLockDelay <= deadline
                && SupportedActions[action].Allowed(Player, target);
        }
    }
}
