using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.ValueProps;

namespace Test.code;

internal static class RescueCorpseManager
{
	private sealed class CorpseState
	{
		public int DownCount;

		public int CurrentHp;

		public int MaxHp;

		public int SavedOrbSlotCapacity;

		public bool Active;
	}

	private static readonly Dictionary<ulong, CorpseState> _corpseStates = new Dictionary<ulong, CorpseState>();

	private static readonly int[] _corpseHpByDownCount = new int[3] { 16, 40, 70 };

	private static readonly Color _corpseBarColor = new Color("8F5A46");

	private static readonly FieldInfo _targetManagerValidTargetsTypeField = AccessTools.Field(typeof(NTargetManager), "_validTargetsType");

	private static readonly FieldInfo _attackCommandSingleTargetField = AccessTools.Field(typeof(AttackCommand), "_singleTarget");

	private static readonly FieldInfo _attackCommandHitCountField = AccessTools.Field(typeof(AttackCommand), "_hitCount");

	private static readonly FieldInfo _attackCommandBaseDamageField = AccessTools.Field(typeof(AttackCommand), "_damagePerHit");

	private static readonly FieldInfo _attackCommandCalculatedDamageField = AccessTools.Field(typeof(AttackCommand), "_calculatedDamageVar");

	private static readonly FieldInfo _healthBarCreatureField = AccessTools.Field(typeof(NHealthBar), "_creature");

	private static readonly FieldInfo _healthBarForegroundContainerField = AccessTools.Field(typeof(NHealthBar), "_hpForegroundContainer");

	private static readonly FieldInfo _healthBarForegroundField = AccessTools.Field(typeof(NHealthBar), "_hpForeground");

	private static readonly FieldInfo _healthBarPoisonForegroundField = AccessTools.Field(typeof(NHealthBar), "_poisonForeground");

	private static readonly FieldInfo _healthBarDoomForegroundField = AccessTools.Field(typeof(NHealthBar), "_doomForeground");

	private static readonly FieldInfo _healthBarMiddlegroundField = AccessTools.Field(typeof(NHealthBar), "_hpMiddleground");

	private static readonly FieldInfo _healthBarLabelField = AccessTools.Field(typeof(NHealthBar), "_hpLabel");

	private static readonly FieldInfo _healthBarExpectedWidthField = AccessTools.Field(typeof(NHealthBar), "_expectedMaxFgWidth");

	private static readonly FieldInfo _creatureNodeStateDisplayField = AccessTools.Field(typeof(NCreature), "_stateDisplay");

	private static readonly FieldInfo _stateDisplayHealthBarField = AccessTools.Field(typeof(NCreatureStateDisplay), "_healthBar");

	private static readonly FieldInfo _multiplayerStateHealthBarField = AccessTools.Field(typeof(NMultiplayerPlayerState), "_healthBar");

	private static readonly FieldInfo _multiplayerStateCharacterIconField = AccessTools.Field(typeof(NMultiplayerPlayerState), "_characterIcon");

	private static readonly FieldInfo _playerHandCombatStateField = AccessTools.Field(typeof(NPlayerHand), "_combatState");

	private static readonly AsyncLocal<int> _singleTargetDamageCallDepth = new AsyncLocal<int>();

	private static readonly AsyncLocal<int> _bypassCreatureDamagePatchDepth = new AsyncLocal<int>();

	private static readonly MethodInfo? _hookBeforePlayPhaseStartTwoArgMethod = AccessTools.Method(typeof(Hook), "BeforePlayPhaseStart", new Type[2]
	{
		typeof(CombatState),
		typeof(Player)
	});

	private static readonly MethodInfo? _hookBeforePlayPhaseStartFourArgMethod = AccessTools.GetDeclaredMethods(typeof(Hook)).FirstOrDefault((MethodInfo method) => method.Name == "BeforePlayPhaseStart" && method.GetParameters().Length == 4);

	private static readonly HashSet<ulong> _recentlyRevivedCorpsePlayers = new HashSet<ulong>();

	private static bool _clearRecentRevivesQueued;

	private static IRunState? _trackedRunState;

	private static CombatState? _roundTrackedCombat;

	private static int _lastCorpseHealRound = -1;

	private const string DownedRingNodeName = "Rescue_DownedRing";

	public static bool IsCorpse(Creature? creature)
	{
		if (creature?.Player == null || !creature.IsDead)
		{
			return false;
		}
		return TryGetState(creature.Player, out var state) && state.Active;
	}

	public static bool CanTargetCorpse(CardModel card, Creature? target)
	{
		if (target?.Player == null || !target.IsDead || card.Type != CardType.Attack || card.TargetType != TargetType.AnyEnemy)
		{
			return false;
		}
		if (!TryGetState(target.Player, out var state) || !state.Active)
		{
			return false;
		}
		return target.Player != card.Owner;
	}

	public static bool ShouldBlockHandActions(NPlayerHand playerHand)
	{
		if (_playerHandCombatStateField.GetValue(playerHand) is not CombatState combatState)
		{
			return false;
		}
		Player? me = LocalContext.GetMe(combatState);
		if (me?.Creature == null)
		{
			return false;
		}
		return me.Creature.IsDead;
	}

	public static async Task HandlePlayerDeathAsync(Player player)
	{
		EnsureRunStateInitialized(player.RunState);
		if (CombatManager.Instance.IsInProgress)
		{
			await PlayerCmd.SetEnergy(0m, player);
			await PlayerCmd.SetStars(0m, player);
		}
		ActivateCorpse(player);
		EnsureCorpseNodeTargetable(player.Creature);
	}

	public static void CacheOrbSlotsBeforeDeath(Creature creature)
	{
		Player? player = creature.Player;
		if (player?.PlayerCombatState == null)
		{
			return;
		}
		CorpseState state = GetOrCreateState(player);
		state.SavedOrbSlotCapacity = Math.Max(state.SavedOrbSlotCapacity, player.PlayerCombatState.OrbQueue.Capacity);
	}

	public static void HandleCombatSetup(CombatState state)
	{
		EnsureRunStateInitialized(state.RunState);
		foreach (Player player in state.Players)
		{
			if (TryGetState(player, out var corpseState))
			{
				corpseState.Active = false;
				corpseState.CurrentHp = 0;
				corpseState.MaxHp = 0;
			}
		}
	}

	public static void ResetCorpseCounters(IRunState runState)
	{
		EnsureRunStateInitialized(runState);
		foreach (Player player in runState.Players)
		{
			CorpseState state = GetOrCreateState(player);
			state.DownCount = 0;
			if (state.Active)
			{
				state.Active = false;
				state.CurrentHp = 0;
				state.MaxHp = 0;
				RefreshUi(player);
			}
		}
	}

	public static void ClearCorpse(Player player)
	{
		if (!TryGetState(player, out var state) || !state.Active)
		{
			return;
		}
		state.Active = false;
		state.CurrentHp = 0;
		state.MaxHp = 0;
		RefreshUi(player);
	}

	public static void HealCorpsesAtPlayerTurnStart(CombatState? combatState, int roundNumber)
	{
		if (combatState == null)
		{
			return;
		}
		if (!ReferenceEquals(_roundTrackedCombat, combatState))
		{
			_roundTrackedCombat = combatState;
			_lastCorpseHealRound = -1;
		}
		if (_lastCorpseHealRound == roundNumber)
		{
			return;
		}
		_lastCorpseHealRound = roundNumber;
		foreach (Player player in combatState.Players)
		{
			if (!TryGetState(player, out var state) || !state.Active)
			{
				continue;
			}
			int healAmount = Math.Max(1, (int)Math.Ceiling(state.MaxHp * 0.2m));
			state.CurrentHp = Math.Min(state.MaxHp, state.CurrentHp + healAmount);
			RefreshUi(player);
		}
	}

	public static async Task<IEnumerable<DamageResult>> DamageCorpseEnumerableAsync(PlayerChoiceContext choiceContext, Creature corpse, decimal amount, ValueProp props, Creature? dealer, CardModel? cardSource)
	{
		return new DamageResult[1] { await DamageCorpseAsync(choiceContext, corpse, amount, props, dealer, cardSource) };
	}

	public static bool ShouldBypassCreatureDamagePatch()
	{
		return _bypassCreatureDamagePatchDepth.Value > 0;
	}

	public static bool ShouldBlockPowerGain(Creature creature)
	{
		if (IsCorpse(creature))
		{
			return true;
		}
		Player? player = creature.Player;
		if (player == null)
		{
			return false;
		}
		return _recentlyRevivedCorpsePlayers.Contains(player.NetId);
	}

	public static void BeginSingleTargetDamageCall()
	{
		_singleTargetDamageCallDepth.Value++;
	}

	public static void EndSingleTargetDamageCall()
	{
		_singleTargetDamageCallDepth.Value = Math.Max(0, _singleTargetDamageCallDepth.Value - 1);
	}

	public static bool ShouldInterceptCreatureDamage(List<Creature> targetList, Creature? dealer, CardModel? cardSource)
	{
		if (dealer == null)
		{
			return false;
		}
		if (targetList.Any((Creature target) => IsCorpse(target) && target.Side == dealer.Side))
		{
			return true;
		}
		return GetGroupDamageCorpseTargets(targetList, dealer, cardSource).Count > 0;
	}

	public static async Task<IEnumerable<DamageResult>> DamageTargetsIncludingCorpsesAsync(PlayerChoiceContext choiceContext, List<Creature> originalTargets, decimal amount, ValueProp props, Creature? dealer, CardModel? cardSource)
	{
		List<Creature> allTargets = originalTargets.ToList();
		foreach (Creature corpseTarget in GetGroupDamageCorpseTargets(originalTargets, dealer, cardSource))
		{
			if (!allTargets.Contains(corpseTarget))
			{
				allTargets.Add(corpseTarget);
			}
		}
		if (dealer != null && dealer.IsDead)
		{
			return allTargets.Select((Creature target) => new DamageResult(target, props)).ToList();
		}
		List<Creature> normalTargets = new List<Creature>();
		List<Creature> corpseTargets = new List<Creature>();
		HashSet<Creature> seenTargets = new HashSet<Creature>();
		foreach (Creature target in allTargets)
		{
			if (!seenTargets.Add(target))
			{
				continue;
			}
			if (dealer != null && IsCorpse(target) && target.Side == dealer.Side)
			{
				corpseTargets.Add(target);
			}
			else
			{
				normalTargets.Add(target);
			}
		}
		List<DamageResult> results = new List<DamageResult>();
		if (normalTargets.Count > 0)
		{
			results.AddRange(await RunOriginalCreatureDamageAsync(choiceContext, normalTargets, amount, props, dealer, cardSource));
		}
		foreach (Creature corpseTarget in corpseTargets)
		{
			results.Add(await DamageCorpseAsync(choiceContext, corpseTarget, amount, props, dealer, cardSource));
		}
		return results;
	}

	public static async Task<DamageResult> DamageCorpseAsync(PlayerChoiceContext choiceContext, Creature corpse, decimal amount, ValueProp props, Creature? dealer, CardModel? cardSource)
	{
		DamageResult emptyResult = new DamageResult(corpse, props);
		Player? player = corpse.Player;
		if (player == null || dealer == null || dealer.IsDead || !TryGetState(player, out var state) || !state.Active)
		{
			return emptyResult;
		}
		IRunState runState = player.RunState;
		CombatState? combatState = corpse.CombatState;
		IEnumerable<AbstractModel> modifiers;
		decimal modifiedAmount = Hook.ModifyDamage(runState, combatState, corpse, dealer, amount, props, cardSource, ModifyDamageHookType.All, CardPreviewMode.None, out modifiers);
		await Hook.AfterModifyingDamageAmount(runState, combatState, cardSource, modifiers);
		await Hook.BeforeDamageReceived(choiceContext, runState, combatState, corpse, modifiedAmount, props, dealer, cardSource);
		int startingHp = state.CurrentHp;
		int totalDamage = Math.Max(0, (int)modifiedAmount);
		state.CurrentHp = Math.Max(0, startingHp - totalDamage);
		DamageResult result = new DamageResult(corpse, props)
		{
			UnblockedDamage = Math.Min(startingHp, totalDamage),
			OverkillDamage = Math.Max(0, totalDamage - startingHp),
			WasTargetKilled = totalDamage > 0 && state.CurrentHp <= 0
		};
		if (CombatManager.Instance.IsInProgress && !CombatManager.Instance.IsEnding)
		{
			CombatManager.Instance.History.DamageReceived(combatState, corpse, dealer, result, cardSource);
		}
		PlayCorpseDamageVfx(corpse, result);
		if (combatState != null)
		{
			await Hook.AfterDamageGiven(choiceContext, combatState, dealer, result, props, corpse, cardSource);
		}
		if (!result.WasTargetKilled)
		{
			await Hook.AfterDamageReceived(choiceContext, runState, combatState, corpse, result, props, dealer, cardSource);
		}
		RefreshUi(player);
		if (result.WasTargetKilled)
		{
			await ReviveCorpseAsync(player);
		}
		return result;
	}

	public static bool TryGetCorpseHealth(NHealthBar healthBar, out int currentHp, out int maxHp)
	{
		currentHp = 0;
		maxHp = 0;
		if (_healthBarCreatureField.GetValue(healthBar) is not Creature creature || creature.Player == null || !creature.IsDead)
		{
			return false;
		}
		if (!TryGetState(creature.Player, out var state) || !state.Active)
		{
			return false;
		}
		currentHp = state.CurrentHp;
		maxHp = state.MaxHp;
		return true;
	}

	public static bool TryGetDownedRingData(Player player, out int downCount, out float hpRatio, out bool isActive)
	{
		downCount = 0;
		hpRatio = 0f;
		isActive = false;
		EnsureRunStateInitialized(player.RunState);
		if (!TryGetState(player, out var state))
		{
			return false;
		}
		downCount = Math.Max(0, state.DownCount);
		isActive = state.Active && player.Creature.IsDead;
		if (state.MaxHp > 0)
		{
			hpRatio = Mathf.Clamp((float)state.CurrentHp / (float)state.MaxHp, 0f, 1f);
		}
		return true;
	}

	public static void EnsureCreatureDownedRing(NCreature creatureNode)
	{
		Player? player = creatureNode.Entity.Player;
		if (player == null)
		{
			return;
		}
		Control host = creatureNode.Hitbox;
		if (host.GetNodeOrNull<RescueDownedRingControl>(DownedRingNodeName) is RescueDownedRingControl ring)
		{
			ring.Bind(player, DownedRingDisplayMode.Corpse);
			return;
		}
		ring = new RescueDownedRingControl
		{
			Name = DownedRingNodeName,
			MouseFilter = Control.MouseFilterEnum.Ignore
		};
		ring.Bind(player, DownedRingDisplayMode.Corpse);
		ring.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		ring.OffsetLeft = 0f;
		ring.OffsetTop = 0f;
		ring.OffsetRight = 0f;
		ring.OffsetBottom = 0f;
		host.AddChild(ring);
	}

	public static void EnsureMultiplayerDownedRing(NMultiplayerPlayerState multiplayerState)
	{
		Player? player = multiplayerState.Player;
		if (player == null)
		{
			return;
		}
		if (_multiplayerStateCharacterIconField.GetValue(multiplayerState) is not Control icon)
		{
			return;
		}
		if (icon.GetNodeOrNull<RescueDownedRingControl>(DownedRingNodeName) is RescueDownedRingControl ring)
		{
			ring.Bind(player, DownedRingDisplayMode.Portrait);
			return;
		}
		ring = new RescueDownedRingControl
		{
			Name = DownedRingNodeName,
			MouseFilter = Control.MouseFilterEnum.Ignore
		};
		ring.Bind(player, DownedRingDisplayMode.Portrait);
		ring.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		ring.OffsetLeft = 0f;
		ring.OffsetTop = 0f;
		ring.OffsetRight = 0f;
		ring.OffsetBottom = 0f;
		icon.AddChild(ring);
	}

	public static TargetType GetTargetManagerTargetType(NTargetManager targetManager)
	{
		return (TargetType)_targetManagerValidTargetsTypeField.GetValue(targetManager);
	}

	public static Creature? GetSingleTarget(AttackCommand attackCommand)
	{
		return _attackCommandSingleTargetField.GetValue(attackCommand) as Creature;
	}

	public static async Task<AttackCommand> ExecuteCorpseAttackAsync(AttackCommand attackCommand, PlayerChoiceContext? choiceContext, Creature corpse)
	{
		Creature? attacker = attackCommand.Attacker;
		if (attacker == null)
		{
			return attackCommand;
		}
		CombatState combatState = attacker.CombatState;
		await Hook.BeforeAttack(combatState, attackCommand);
		decimal attackCount = Hook.ModifyAttackHitCount(combatState, attackCommand, (int)_attackCommandHitCountField.GetValue(attackCommand));
		CalculatedDamageVar? calculatedDamageVar = _attackCommandCalculatedDamageField.GetValue(attackCommand) as CalculatedDamageVar;
		decimal baseDamage = (decimal)_attackCommandBaseDamageField.GetValue(attackCommand);
		bool playedAttackAnim = false;
		for (int hitIndex = 0; (decimal)hitIndex < attackCount; hitIndex++)
		{
			if (attacker.IsDead || !IsCorpse(corpse))
			{
				break;
			}
			if (!playedAttackAnim)
			{
				playedAttackAnim = true;
				await CreatureCmd.TriggerAnim(attacker, "Attack", 0.1f);
			}
			decimal damageAmount = (calculatedDamageVar == null) ? baseDamage : calculatedDamageVar.Calculate(corpse);
			DamageResult result = await DamageCorpseAsync(choiceContext ?? new BlockingPlayerChoiceContext(), corpse, damageAmount, attackCommand.DamageProps, attacker, attackCommand.ModelSource as CardModel);
			attackCommand.AddResultsInternal(new DamageResult[1] { result });
		}
		CombatManager.Instance.History.CreatureAttacked(combatState, attacker, attackCommand.Results.ToList());
		await Hook.AfterAttack(combatState, attackCommand);
		return attackCommand;
	}

	private static void ActivateCorpse(Player player)
	{
		CorpseState state = GetOrCreateState(player);
		state.DownCount++;
		state.MaxHp = _corpseHpByDownCount[Math.Min(state.DownCount - 1, _corpseHpByDownCount.Length - 1)];
		state.CurrentHp = state.MaxHp;
		state.Active = true;
		EnsureCorpseNodeTargetable(player.Creature);
		RefreshUi(player);
		Log.Info($"[RescueCorpse] Player {player.NetId} became a corpse with {state.CurrentHp}/{state.MaxHp} HP.");
	}

	private static async Task ReviveCorpseAsync(Player player)
	{
		ClearCorpse(player);
		await CreatureCmd.Heal(player.Creature, 1m);
		await RestoreOrbSlotsAfterReviveAsync(player);
		MarkJustRevivedFromCorpse(player);
		await PowerCmd.Remove<DoomPower>(player.Creature);
		await GrantReviveTurnStartResourcesAsync(player);
		RestorePlayerTurnControlAfterRevive(player);
		RefreshUi(player);
	}

	private static async Task RestoreOrbSlotsAfterReviveAsync(Player player)
	{
		if (!TryGetState(player, out var state) || player.PlayerCombatState == null)
		{
			return;
		}
		int targetCapacity = Math.Max(state.SavedOrbSlotCapacity, player.BaseOrbSlotCount);
		state.SavedOrbSlotCapacity = 0;
		int currentCapacity = player.PlayerCombatState.OrbQueue.Capacity;
		if (currentCapacity > targetCapacity)
		{
			OrbCmd.RemoveSlots(player, currentCapacity - targetCapacity);
			return;
		}
		if (currentCapacity < targetCapacity)
		{
			await OrbCmd.AddSlots(player, targetCapacity - currentCapacity);
		}
	}

	private static async Task GrantReviveTurnStartResourcesAsync(Player player)
	{
		if (!CombatManager.Instance.IsInProgress || player.Creature.IsDead)
		{
			return;
		}
		CombatState? combatState = player.Creature.CombatState;
		if (combatState == null)
		{
			return;
		}
		PlayerChoiceContext choiceContext = new BlockingPlayerChoiceContext();
		await RunPseudoTurnEndForReviveAsync(choiceContext, combatState, player);
		await FlushHandBeforeReviveDrawAsync(combatState, player);
		if (Hook.ShouldPlayerResetEnergy(combatState, player))
		{
			SfxCmd.Play("event:/sfx/ui/gain_energy");
			player.PlayerCombatState.ResetEnergy();
		}
		else
		{
			player.PlayerCombatState.AddMaxEnergyToCurrent();
		}
		await Hook.AfterEnergyReset(combatState, player);
		await Hook.BeforeHandDraw(combatState, player, choiceContext);
		decimal handDraw = Hook.ModifyHandDraw(combatState, player, 5m, out IEnumerable<AbstractModel> modifiers);
		await Hook.AfterModifyingHandDraw(combatState, modifiers);
		await CardPileCmd.Draw(choiceContext, handDraw, player, fromHandDraw: true);
		await Hook.AfterPlayerTurnStart(combatState, choiceContext, player);
		await player.PlayerCombatState.OrbQueue.AfterTurnStart(choiceContext);
		if (combatState.CurrentSide == CombatSide.Player)
		{
			await BeforePlayPhaseStartCompat(combatState, player);
		}
	}

	private static async Task BeforePlayPhaseStartCompat(CombatState combatState, Player player)
	{
		Task? hookTask = _hookBeforePlayPhaseStartTwoArgMethod?.Invoke(null, new object[2] { combatState, player }) as Task;
		if (hookTask != null)
		{
			await hookTask;
			return;
		}
		if (_hookBeforePlayPhaseStartFourArgMethod == null)
		{
			return;
		}
		ulong? netId = LocalContext.NetId;
		if (!netId.HasValue)
		{
			return;
		}
		foreach (AbstractModel model in combatState.IterateHookListeners())
		{
			if (!TryCreateHookPlayerChoiceContext(model, netId.Value, combatState, out HookPlayerChoiceContext? hookPlayerChoiceContext))
			{
				continue;
			}
			Task task = model.BeforePlayPhaseStart(hookPlayerChoiceContext, player);
			Task? fourArgTask = _hookBeforePlayPhaseStartFourArgMethod.Invoke(null, new object[4] { hookPlayerChoiceContext, task, combatState, player }) as Task;
			if (fourArgTask != null)
			{
				await fourArgTask;
			}
			else
			{
				await hookPlayerChoiceContext.AssignTaskAndWaitForPauseOrCompletion(task);
			}
			model.InvokeExecutionFinished();
		}
	}

	private static bool TryCreateHookPlayerChoiceContext(AbstractModel model, ulong netId, CombatState combatState, out HookPlayerChoiceContext? context)
	{
		context = null;
		foreach (ConstructorInfo constructor in typeof(HookPlayerChoiceContext).GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
		{
			ParameterInfo[] parameters = constructor.GetParameters();
			object[] ctorArgs = new object[parameters.Length];
			bool canInvoke = true;
			for (int i = 0; i < parameters.Length; i++)
			{
				Type parameterType = parameters[i].ParameterType;
				if (parameterType.IsAssignableFrom(typeof(AbstractModel)))
				{
					ctorArgs[i] = model;
					continue;
				}
				if (parameterType == typeof(ulong) || parameterType == typeof(ulong?))
				{
					ctorArgs[i] = netId;
					continue;
				}
				if (parameterType.IsAssignableFrom(typeof(CombatState)))
				{
					ctorArgs[i] = combatState;
					continue;
				}
				if (parameterType.IsAssignableFrom(typeof(Player)))
				{
					ctorArgs[i] = (model as CardModel)?.Owner ?? (model as RelicModel)?.Owner ?? combatState.Players[0];
					continue;
				}
				if (parameterType.IsEnum)
				{
					Array enumValues = Enum.GetValues(parameterType);
					if (enumValues.Length == 0)
					{
						canInvoke = false;
						break;
					}
					object enumValue = enumValues.Cast<object>().FirstOrDefault((object value) => value.ToString()?.Equals("Combat", StringComparison.OrdinalIgnoreCase) == true) ?? enumValues.GetValue(0)!;
					ctorArgs[i] = enumValue;
					continue;
				}
				if (parameters[i].HasDefaultValue)
				{
					ctorArgs[i] = parameters[i].DefaultValue!;
					continue;
				}
				canInvoke = false;
				break;
			}
			if (!canInvoke)
			{
				continue;
			}
			try
			{
				context = constructor.Invoke(ctorArgs) as HookPlayerChoiceContext;
				if (context != null)
				{
					return true;
				}
			}
			catch
			{
			}
		}
		return false;
	}

	private static async Task RunPseudoTurnEndForReviveAsync(PlayerChoiceContext choiceContext, CombatState combatState, Player player)
	{
		await player.PlayerCombatState.OrbQueue.BeforeTurnEnd(choiceContext);
		CardPile handPile = PileType.Hand.GetPile(player);
		List<CardModel> turnEndCards = new List<CardModel>();
		List<CardModel> etherealCards = new List<CardModel>();
		foreach (CardModel card in handPile.Cards)
		{
			if (card.HasTurnEndInHandEffect)
			{
				turnEndCards.Add(card);
			}
			else if (card.Keywords.Contains(CardKeyword.Ethereal) && Hook.ShouldEtherealTrigger(combatState, card))
			{
				etherealCards.Add(card);
			}
		}
		foreach (CardModel card in etherealCards)
		{
			await CardCmd.Exhaust(choiceContext, card, causedByEthereal: true);
		}
		CardPile discardPile = PileType.Discard.GetPile(player);
		foreach (CardModel card in turnEndCards)
		{
			await CardPileCmd.Add(card, PileType.Play);
			if (LocalContext.IsMe(player))
			{
				await Cmd.CustomScaledWait(0.3f, 0.6f);
			}
			await card.OnTurnEndInHand(choiceContext);
			if (card.Keywords.Contains(CardKeyword.Ethereal))
			{
				await CardCmd.Exhaust(choiceContext, card, causedByEthereal: true);
			}
			else
			{
				await CardPileCmd.Add(card, discardPile);
			}
		}
	}

	private static async Task FlushHandBeforeReviveDrawAsync(CombatState combatState, Player player)
	{
		await Hook.BeforeFlush(combatState, player);
		CardPile handPile = PileType.Hand.GetPile(player);
		List<CardModel> toDiscard = new List<CardModel>();
		List<CardModel> retainedCards = new List<CardModel>();
		foreach (CardModel card in handPile.Cards)
		{
			if (card.ShouldRetainThisTurn)
			{
				retainedCards.Add(card);
			}
			else
			{
				toDiscard.Add(card);
			}
		}
		if (Hook.ShouldFlush(combatState, player))
		{
			await CardPileCmd.Add(toDiscard, PileType.Discard.GetPile(player));
		}
		foreach (CardModel card in retainedCards)
		{
			await Hook.AfterCardRetained(combatState, card);
		}
		player.PlayerCombatState.EndOfTurnCleanup();
	}

	private static void RestorePlayerTurnControlAfterRevive(Player player)
	{
		if (!CombatManager.Instance.IsInProgress)
		{
			return;
		}
		CombatState? combatState = player.Creature.CombatState;
		if (combatState == null || combatState.CurrentSide != CombatSide.Player)
		{
			return;
		}
		if (CombatManager.Instance.IsPlayerReadyToEndTurn(player))
		{
			CombatManager.Instance.UndoReadyToEndTurn(player);
		}
	}

	private static void PlayCorpseDamageVfx(Creature corpse, DamageResult result)
	{
		Node? vfxContainer = NCombatRoom.Instance?.CombatVfxContainer;
		if (result.TotalDamage > 0)
		{
			NDamageNumVfx? damageNum = NDamageNumVfx.Create(corpse, result);
			if (damageNum != null)
			{
				if (vfxContainer != null)
				{
					vfxContainer.AddChild(damageNum);
				}
				else
				{
					NRun.Instance?.GlobalUi.AddChild(damageNum);
				}
			}
			vfxContainer?.AddChild(NHitSparkVfx.Create(corpse));
		}
	}

	private static CorpseState GetOrCreateState(Player player)
	{
		if (!_corpseStates.TryGetValue(player.NetId, out var state))
		{
			state = new CorpseState();
			_corpseStates[player.NetId] = state;
		}
		return state;
	}

	private static IReadOnlyList<Creature> GetGroupDamageCorpseTargets(List<Creature> originalTargets, Creature? dealer, CardModel? cardSource)
	{
		if (dealer?.Player == null || _singleTargetDamageCallDepth.Value > 0)
		{
			return Array.Empty<Creature>();
		}
		if (cardSource == null)
		{
			return Array.Empty<Creature>();
		}
		if (cardSource.Type != CardType.Attack || cardSource.TargetType != TargetType.AllEnemies)
		{
			return Array.Empty<Creature>();
		}
		CombatState? combatState = dealer.CombatState;
		if (combatState == null)
		{
			return Array.Empty<Creature>();
		}
		return combatState.Players.Where((Player player) => player != dealer.Player && player.Creature.IsDead && TryGetState(player, out var state) && state.Active && !originalTargets.Contains(player.Creature)).Select((Player player) => player.Creature).ToList();
	}

	private static async Task<IEnumerable<DamageResult>> RunOriginalCreatureDamageAsync(PlayerChoiceContext choiceContext, IEnumerable<Creature> targets, decimal amount, ValueProp props, Creature? dealer, CardModel? cardSource)
	{
		_bypassCreatureDamagePatchDepth.Value++;
		try
		{
			return await CreatureCmd.Damage(choiceContext, targets, amount, props, dealer, cardSource);
		}
		finally
		{
			_bypassCreatureDamagePatchDepth.Value = Math.Max(0, _bypassCreatureDamagePatchDepth.Value - 1);
		}
	}

	private static void MarkJustRevivedFromCorpse(Player player)
	{
		_recentlyRevivedCorpsePlayers.Add(player.NetId);
		if (_clearRecentRevivesQueued)
		{
			return;
		}
		_clearRecentRevivesQueued = true;
		Callable.From(ClearRecentRevivedCorpsePlayers).CallDeferred();
	}

	private static void ClearRecentRevivedCorpsePlayers()
	{
		_recentlyRevivedCorpsePlayers.Clear();
		_clearRecentRevivesQueued = false;
	}

	private static void EnsureRunStateInitialized(IRunState runState)
	{
		if (ReferenceEquals(_trackedRunState, runState))
		{
			return;
		}
		_trackedRunState = runState;
		_corpseStates.Clear();
		_roundTrackedCombat = null;
		_lastCorpseHealRound = -1;
	}

	public static void EnsureCorpseNodeTargetable(Creature creature)
	{
		if (!IsCorpse(creature))
		{
			return;
		}
		if (NCombatRoom.Instance?.GetCreatureNode(creature) is not NCreature creatureNode)
		{
			return;
		}
		EnsureCreatureDownedRing(creatureNode);
		creatureNode.ToggleIsInteractable(on: true);
		creatureNode.Hitbox.MouseFilter = Control.MouseFilterEnum.Stop;
		creatureNode.Hitbox.FocusMode = Control.FocusModeEnum.All;
		if (_creatureNodeStateDisplayField.GetValue(creatureNode) is Control stateDisplay)
		{
			stateDisplay.Visible = !NCombatUi.IsDebugHidingHpBar;
			Color modulate = stateDisplay.Modulate;
			modulate.A = 1f;
			stateDisplay.Modulate = modulate;
		}
	}

	private static bool TryGetState(Player player, out CorpseState state)
	{
		return _corpseStates.TryGetValue(player.NetId, out state);
	}

	private static void RefreshUi(Player player)
	{
		RefreshCombatHealthBar(player.Creature);
		if (NCombatRoom.Instance?.GetCreatureNode(player.Creature) is NCreature creatureNode)
		{
			EnsureCreatureDownedRing(creatureNode);
		}
		SceneTree? sceneTree = Engine.GetMainLoop() as SceneTree;
		if (sceneTree?.Root != null)
		{
			RefreshMultiplayerHealthBars(sceneTree.Root, player);
		}
	}

	private static void RefreshCombatHealthBar(Creature creature)
	{
		if (NCombatRoom.Instance?.GetCreatureNode(creature) is not NCreature creatureNode)
		{
			return;
		}
		if (_creatureNodeStateDisplayField.GetValue(creatureNode) is not NCreatureStateDisplay stateDisplay)
		{
			return;
		}
		if (_stateDisplayHealthBarField.GetValue(stateDisplay) is NHealthBar healthBar)
		{
			healthBar.RefreshValues();
		}
	}

	private static void RefreshMultiplayerHealthBars(Node node, Player player)
	{
		if (node is NMultiplayerPlayerState multiplayerState && multiplayerState.Player == player)
		{
			EnsureMultiplayerDownedRing(multiplayerState);
			if (_multiplayerStateHealthBarField.GetValue(multiplayerState) is NHealthBar healthBar)
			{
				healthBar.RefreshValues();
			}
		}
		foreach (Node child in node.GetChildren())
		{
			RefreshMultiplayerHealthBars(child, player);
		}
	}

	public static void ApplyCorpseHealthBarVisuals(NHealthBar healthBar, int currentHp, int maxHp)
	{
		if (_healthBarForegroundContainerField.GetValue(healthBar) is not Control foregroundContainer || _healthBarForegroundField.GetValue(healthBar) is not Control foreground || _healthBarPoisonForegroundField.GetValue(healthBar) is not Control poisonForeground || _healthBarDoomForegroundField.GetValue(healthBar) is not Control doomForeground)
		{
			return;
		}
		float expectedWidth = (float)_healthBarExpectedWidthField.GetValue(healthBar);
		float maxWidth = (expectedWidth > 0f) ? expectedWidth : foregroundContainer.Size.X;
		float filledWidth = Math.Max(12f, maxWidth * ((float)currentHp / Math.Max(1, maxHp)));
		foreground.Visible = true;
		foreground.SelfModulate = _corpseBarColor;
		foreground.OffsetRight = filledWidth - maxWidth;
		poisonForeground.Visible = false;
		doomForeground.Visible = false;
		if (_healthBarMiddlegroundField.GetValue(healthBar) is Control middleground)
		{
			middleground.Visible = false;
		}
	}

	public static void ApplyCorpseHealthBarText(NHealthBar healthBar, int currentHp, int maxHp)
	{
		if (_healthBarLabelField.GetValue(healthBar) is MegaCrit.Sts2.addons.mega_text.MegaLabel hpLabel)
		{
			hpLabel.SetTextAutoSize($"{currentHp}/{maxHp}");
		}
	}
}

[HarmonyPatch(typeof(Creature), nameof(Creature.CanReceivePowers), MethodType.Getter)]
internal static class CreatureCanReceivePowersPatch
{
	private static bool Prefix(Creature __instance, ref bool __result)
	{
		if (!RescueCorpseManager.ShouldBlockPowerGain(__instance))
		{
			return true;
		}
		__result = false;
		return false;
	}
}

[HarmonyPatch(typeof(CreatureCmd), nameof(CreatureCmd.Kill), new Type[2]
{
	typeof(Creature),
	typeof(bool)
})]
internal static class CreatureCmdKillPatch
{
	private static void Prefix(Creature creature)
	{
		RescueCorpseManager.CacheOrbSlotsBeforeDeath(creature);
	}
}

[HarmonyPatch(typeof(CreatureCmd), nameof(CreatureCmd.Damage), new Type[5]
{
	typeof(PlayerChoiceContext),
	typeof(Creature),
	typeof(decimal),
	typeof(ValueProp),
	typeof(Creature)
})]
internal static class CreatureCmdSingleTargetDamagePatch
{
	private static void Prefix()
	{
		RescueCorpseManager.BeginSingleTargetDamageCall();
	}

	private static void Postfix()
	{
		RescueCorpseManager.EndSingleTargetDamageCall();
	}
}

[HarmonyPatch(typeof(CreatureCmd), nameof(CreatureCmd.Damage), new Type[6]
{
	typeof(PlayerChoiceContext),
	typeof(Creature),
	typeof(decimal),
	typeof(ValueProp),
	typeof(Creature),
	typeof(CardModel)
})]
internal static class CreatureCmdSingleTargetDamageWithSourcePatch
{
	private static void Prefix()
	{
		RescueCorpseManager.BeginSingleTargetDamageCall();
	}

	private static void Postfix()
	{
		RescueCorpseManager.EndSingleTargetDamageCall();
	}
}

[HarmonyPatch(typeof(CombatManager), "HandlePlayerDeath")]
internal static class CombatManagerHandlePlayerDeathPatch
{
	private static bool Prefix(Player player, ref Task __result)
	{
		__result = RescueCorpseManager.HandlePlayerDeathAsync(player);
		return false;
	}
}

[HarmonyPatch(typeof(Player), nameof(Player.ReviveBeforeCombatEnd))]
internal static class PlayerReviveBeforeCombatEndPatch
{
	private static void Prefix(Player __instance)
	{
		RescueCorpseManager.ClearCorpse(__instance);
	}
}

[HarmonyPatch(typeof(CombatManager), nameof(CombatManager.SetUpCombat))]
internal static class CombatManagerSetUpCombatPatch
{
	private static void Postfix(CombatState state)
	{
		RescueCorpseManager.HandleCombatSetup(state);
	}
}

[HarmonyPatch(typeof(RestSiteOption), nameof(RestSiteOption.Generate))]
internal static class RestSiteOptionGeneratePatch
{
	private static void Postfix(Player player)
	{
		RescueCorpseManager.ResetCorpseCounters(player.RunState);
	}
}

[HarmonyPatch(typeof(Creature), nameof(Creature.BeforeTurnStart))]
internal static class CreatureBeforeTurnStartPatch
{
	private static void Prefix(Creature __instance, int roundNumber, CombatSide side)
	{
		if (__instance.IsPlayer && side == CombatSide.Player)
		{
			RescueCorpseManager.HealCorpsesAtPlayerTurnStart(__instance.CombatState, roundNumber);
		}
	}
}

[HarmonyPatch(typeof(CardModel), nameof(CardModel.IsValidTarget))]
internal static class CardModelIsValidTargetPatch
{
	private static bool Prefix(CardModel __instance, Creature? target, ref bool __result)
	{
		if (!RescueCorpseManager.CanTargetCorpse(__instance, target))
		{
			return true;
		}
		__result = true;
		return false;
	}
}

[HarmonyPatch(typeof(NTargetManager), "AllowedToTargetCreature")]
internal static class NTargetManagerAllowedToTargetCreaturePatch
{
	private static bool Prefix(NTargetManager __instance, Creature creature, ref bool __result)
	{
		if (!RescueCorpseManager.IsCorpse(creature))
		{
			return true;
		}
		if (RescueCorpseManager.GetTargetManagerTargetType(__instance) != TargetType.AnyEnemy)
		{
			return true;
		}
		__result = creature.IsPlayer && creature.Player != null && !LocalContext.IsMe(creature.Player);
		return false;
	}
}

[HarmonyPatch(typeof(NPlayerHand), "AreCardActionsAllowed")]
internal static class NPlayerHandAreCardActionsAllowedPatch
{
	private static bool Prefix(NPlayerHand __instance, ref bool __result)
	{
		if (!RescueCorpseManager.ShouldBlockHandActions(__instance))
		{
			return true;
		}
		__result = false;
		return false;
	}
}

[HarmonyPatch(typeof(NCreature), nameof(NCreature.OnTargetingStarted))]
internal static class NCreatureOnTargetingStartedPatch
{
	private static void Postfix(NCreature __instance)
	{
		RescueCorpseManager.EnsureCorpseNodeTargetable(__instance.Entity);
	}
}

[HarmonyPatch(typeof(NCreature), nameof(NCreature._Ready))]
internal static class NCreatureReadyPatch
{
	private static void Postfix(NCreature __instance)
	{
		RescueCorpseManager.EnsureCreatureDownedRing(__instance);
	}
}

[HarmonyPatch(typeof(NMultiplayerPlayerState), nameof(NMultiplayerPlayerState._Ready))]
internal static class NMultiplayerPlayerStateReadyPatch
{
	private static void Postfix(NMultiplayerPlayerState __instance)
	{
		RescueCorpseManager.EnsureMultiplayerDownedRing(__instance);
	}
}

[HarmonyPatch(typeof(AttackCommand), nameof(AttackCommand.Execute))]
internal static class AttackCommandExecutePatch
{
	private static bool Prefix(AttackCommand __instance, PlayerChoiceContext? choiceContext, ref Task<AttackCommand> __result)
	{
		Creature? singleTarget = RescueCorpseManager.GetSingleTarget(__instance);
		if (!RescueCorpseManager.IsCorpse(singleTarget))
		{
			return true;
		}
		if (__instance.Attacker == null || __instance.Attacker.Side != singleTarget!.Side)
		{
			return true;
		}
		__result = RescueCorpseManager.ExecuteCorpseAttackAsync(__instance, choiceContext, singleTarget);
		return false;
	}
}

[HarmonyPatch(typeof(CreatureCmd), nameof(CreatureCmd.Damage), new Type[6]
{
	typeof(PlayerChoiceContext),
	typeof(IEnumerable<Creature>),
	typeof(decimal),
	typeof(ValueProp),
	typeof(Creature),
	typeof(CardModel)
})]
internal static class CreatureCmdDamagePatch
{
	private static bool Prefix(PlayerChoiceContext choiceContext, IEnumerable<Creature> targets, decimal amount, ValueProp props, Creature? dealer, CardModel? cardSource, ref Task<IEnumerable<DamageResult>> __result)
	{
		if (RescueCorpseManager.ShouldBypassCreatureDamagePatch())
		{
			return true;
		}
		List<Creature> targetList = targets.ToList();
		if (!RescueCorpseManager.ShouldInterceptCreatureDamage(targetList, dealer, cardSource))
		{
			return true;
		}
		__result = RescueCorpseManager.DamageTargetsIncludingCorpsesAsync(choiceContext, targetList, amount, props, dealer, cardSource);
		return false;
	}
}

[HarmonyPatch(typeof(NHealthBar), "RefreshMiddleground")]
internal static class NHealthBarRefreshMiddlegroundPatch
{
	private static bool Prefix(NHealthBar __instance)
	{
		if (!RescueCorpseManager.TryGetCorpseHealth(__instance, out _, out _))
		{
			return true;
		}
		return false;
	}
}

[HarmonyPatch(typeof(NHealthBar), "RefreshForeground")]
internal static class NHealthBarRefreshForegroundPatch
{
	private static void Postfix(NHealthBar __instance)
	{
		if (RescueCorpseManager.TryGetCorpseHealth(__instance, out int currentHp, out int maxHp))
		{
			RescueCorpseManager.ApplyCorpseHealthBarVisuals(__instance, currentHp, maxHp);
		}
	}
}

[HarmonyPatch(typeof(NHealthBar), "RefreshText")]
internal static class NHealthBarRefreshTextPatch
{
	private static void Postfix(NHealthBar __instance)
	{
		if (RescueCorpseManager.TryGetCorpseHealth(__instance, out int currentHp, out int maxHp))
		{
			RescueCorpseManager.ApplyCorpseHealthBarText(__instance, currentHp, maxHp);
		}
	}
}

