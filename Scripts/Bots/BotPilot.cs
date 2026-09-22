using System;
using Gdpyr.Core;
using Gdpyr.Fps;
using Gdpyr.Match;
using Gdpyr.Rts;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Bots;

/// <summary>
/// One ground-force bot: the half of <see cref="BotBrain"/> that is allowed to
/// touch the engine (docs/IMPLEMENTATION_PLAN.md §M3.5).
///
/// It finds something to shoot at, decides where the bot should be standing, and
/// hands both to the brain, which turns them into the <see cref="InputFrame"/>
/// <see cref="Net.PlayerManager"/> feeds the character. Nothing else about the
/// character changes: it is simulated, damaged, killed and respawned by exactly
/// the code that does it for a human, because from the tick loop's point of view
/// the only difference is which side of the socket the frame came from.
///
/// Server-only, like every other decision in this codebase. A client never builds
/// one; a bot arrives on a client as an ordinary <c>SpawnPlayer</c> and is
/// interpolated from snapshots like any other remote player (docs/NETCODE.md §9).
/// </summary>
public sealed class BotPilot
{
	/// <summary>
	/// Ticks between target re-scans, staggered across bots by peer id. The same
	/// sixth of a second units use, and for the same reason: acquisition is the
	/// expensive part of the tick and staleness is invisible next to how long it
	/// takes anything to walk anywhere (docs/IMPLEMENTATION_PLAN.md §6).
	/// </summary>
	private const int ScanIntervalTicks = SimConfig.UnitTargetRefreshTicks;

	/// <summary>How often progress is checked, and how far it has to have got in that time.</summary>
	private const int StuckWindowTicks = 30;
	private const float StuckDistanceMeters = 0.6f;

	/// <summary>How long a bot sidesteps and jumps after deciding it is wedged.</summary>
	private const int StuckRecoveryTicks = 45;

	/// <summary>Ticks between picking a fresh loiter point around the objective.</summary>
	private const int ObjectiveIntervalTicks = SimConfig.TickRate * 8;

	/// <summary>How far around the objective a bot will post itself, so six do not stack on one door.</summary>
	private const float LoiterRadiusMeters = 14f;

	/// <summary>How far outside a defended barracks' ring a bot waits: far enough that a stray burst is a warning.</summary>
	private const float StandoffMarginMeters = 10f;

	/// <summary>Half the arc of the ring a bot's waiting point wanders over, so six do not wait on one spot.</summary>
	private const float StandoffArcRadians = Mathf.Pi / 6f;

	/// <summary>
	/// How far inside its waiting ring a bot may stand before it is walked back out.
	/// A bot standing on its waiting point is never exactly on the ring, and one that
	/// was sent back out every time it arrived would never stop walking.
	/// </summary>
	private const float StandoffToleranceMeters = 2f;

	/// <summary>A friendly this close to the line of fire is close enough to be shot by mistake.</summary>
	private const float FriendlySweepRadiusMeters = 0.3f;

	/// <summary>Metres the destination must move before the navigation agent is asked to re-path.</summary>
	private const float RepathThresholdMeters = 1.5f;

	/// <summary>Distinguishes this use of <see cref="Spread.Seed"/> from a weapon's and from the brain's.</summary>
	private const byte LoiterSeedSalt = SimConfig.MaxWeaponDefinitions + 3;

	private readonly int _peerId;
	private readonly BotTraits _traits;
	private readonly fps_controller _character;

	/// <summary>Reused by every scan so acquisition allocates nothing on the tick.</summary>
	private readonly GroundContact[] _contacts = new GroundContact[GroundSensor.MaxContacts];

	private int _contactCount;

	private NavigationAgent3D _agent;

	/// <summary>What it is shooting at, as an owner id: a unit, a structure, or none.</summary>
	private int _targetOwnerId;
	private uint _targetSinceTick;
	private uint _nextScanTick;

	private Vector3 _objective;
	private bool _hasObjective;
	private uint _objectiveBucket = uint.MaxValue;

	private Vector3 _lastProgressPosition;
	private uint _nextProgressTick;
	private uint _stuckUntilTick;
	private bool _wantedToMove;

	public BotPilot(int peerId, fps_controller character, in BotTraits traits)
	{
		_peerId = peerId;
		_character = character;
		_traits = traits;
	}

	public int PeerId => _peerId;

	/// <summary>What it is shooting at, as a unit id. 0 for nothing, or for a structure. For the debug HUD.</summary>
	public ushort TargetUnitId => OwnerId.UnitOf(_targetOwnerId);

	/// <summary>
	/// One tick of intent. Called by <see cref="Net.PlayerManager"/> in place of
	/// popping the jitter buffer this bot does not have.
	/// </summary>
	public InputFrame Sample(uint tick)
	{
		fps_controller character = _character;
		if (character == null || !GodotObject.IsInstanceValid(character))
		{
			return InputFrame.Neutral(tick);
		}

		PlayerCombat combat = CombatManager.Instance?.Find(_peerId);
		if (combat == null || !combat.IsAlive)
		{
			// A dead bot holds its angles and does nothing else, which is precisely
			// what PlayerManager does to a dead human's recorded frame.
			_targetOwnerId = OwnerId.None;
			return InputFrame.Neutral(tick, character.Yaw, character.Pitch);
		}

		if (tick >= _nextScanTick)
		{
			AcquireTarget(character, combat.Team, tick);

			// The first scan is offset by the bot's own id so that six of them do not
			// re-scan on the same tick for the rest of the round.
			uint stagger = _nextScanTick == 0 ? (uint)(_peerId % ScanIntervalTicks) : 0u;
			_nextScanTick = tick + (uint)ScanIntervalTicks + stagger;
		}

		Vector3 eye = character.EyePosition;
		bool hasTarget = TryResolveTarget(eye, combat, out Vector3 aimDirection, out Vector3 targetPosition,
			out float targetDistance);

		if (!hasTarget)
		{
			_targetOwnerId = OwnerId.None;
		}

		bool visible = hasTarget && TargetInSight(eye, targetPosition)
			&& !FriendlyInLineOfFire(eye, targetPosition, combat.Team);

		// A bot with something to shoot at walks towards it; the brain stops it and
		// strafes once it is inside its preferred range.
		Vector3 goal;
		bool hasGoal;
		if (hasTarget)
		{
			goal = targetPosition;
			hasGoal = true;
		}
		else
		{
			hasGoal = Objective(tick, character, out goal);
		}

		if (hasGoal)
		{
			goal = OutsideDefences(character, combat.Team, goal);
		}

		Vector3 moveDirection = SteerDirection(character, tick, hasGoal, goal);
		float goalDistance = hasGoal ? character.SimPosition.DistanceTo(goal) : 0f;

		TrackProgress(character, tick);

		WeaponStats stats = combat.EquippedStats;
		bool needsReload = !stats.HasUnlimitedAmmo && !combat.Equipped.IsReloading
			&& combat.Equipped.Ammo <= stats.MagazineSize / 2;

		var situation = new BotSituation(
			alive: true,
			yaw: character.Yaw,
			pitch: character.Pitch,
			speed: new Vector2(character.Velocity.X, character.Velocity.Z).Length(),
			hasTarget: hasTarget,
			aimDirection: aimDirection,
			targetDistance: targetDistance,
			targetVisible: visible,
			ticksOnTarget: (int)Math.Min(tick - _targetSinceTick, (uint)int.MaxValue),
			hasDestination: hasGoal,
			moveDirection: moveDirection,
			destinationDistance: goalDistance,
			stuck: tick < _stuckUntilTick,
			needsReload: needsReload);

		_wantedToMove = hasGoal || (hasTarget && visible);

		return BotBrain.Frame(tick, _peerId, situation, _traits);
	}

	/// <summary>Drops the navigation agent when the bot leaves. The character node is freed with it.</summary>
	public void Dispose()
	{
		if (_agent != null && GodotObject.IsInstanceValid(_agent))
		{
			_agent.QueueFree();
		}
		_agent = null;
	}

	// ---- targets -----------------------------------------------------------

	/// <summary>
	/// The nearest enemy unit this bot can see, out of <see cref="GroundSensor"/>'s
	/// scan — the same filter an attached policy's observation is built from, so a
	/// policy and a bot are looking at one world through one pair of eyes
	/// (docs/AGENT_API.md §6.1).
	///
	/// Enemy *players* are never candidates. There are only two sides, a strategist
	/// has no body on the field, and friendly fire between players is on — a bot
	/// that could acquire a player would eventually acquire a teammate.
	/// </summary>
	private void AcquireTarget(fps_controller character, Team team, uint tick)
	{
		_contactCount = GroundSensor.Scan(character, _peerId, team, _traits.SensorRadiusMeters, _contacts);
		int best = GroundSensor.NearestHostileTarget(_contacts.AsSpan(0, _contactCount), team);

		if (best != _targetOwnerId)
		{
			// The reaction delay is measured from the switch, not from the scan: a bot
			// that swaps targets has to re-acquire the new one before it may fire.
			_targetOwnerId = best;
			_targetSinceTick = tick;
		}
	}

	/// <summary>
	/// Where the current target is now, and where to aim to hit it. Runs every tick,
	/// unlike acquisition, because a target's position is the thing being aimed at.
	/// </summary>
	private bool TryResolveTarget(Vector3 eye, PlayerCombat combat, out Vector3 aimDirection,
		out Vector3 targetPosition, out float distance)
	{
		aimDirection = Vector3.Zero;
		targetPosition = Vector3.Zero;
		distance = float.MaxValue;

		if (_targetOwnerId == OwnerId.None || !TryTargetState(out targetPosition, out Vector3 targetVelocity))
		{
			return false;
		}

		distance = eye.DistanceTo(targetPosition);

		// Hysteresis, as units have: a target walking the edge of the sensor must not
		// make the bot flicker between fighting and wandering off every scan.
		if (distance > _traits.SensorRadiusMeters * 1.25f)
		{
			return false;
		}

		byte weaponId = combat.EquippedDefinitionId;
		float muzzleVelocity = weaponId < WeaponCatalog.Projectiles.Length
			? WeaponCatalog.Projectiles[weaponId].MuzzleVelocity
			: 0f;

		// The same first-order lead units use, for the same reason: without it a bot
		// firing at a unit crossing in front of it misses every time, and the
		// firefight M3.5 exists to produce never happens (docs/NETCODE.md §4.4).
		Vector3 lead = UnitBrain.Lead(eye, targetPosition, targetVelocity, muzzleVelocity);
		Vector3 to = lead - eye;
		if (to.LengthSquared() < 0.0001f)
		{
			return false;
		}

		aimDirection = to.Normalized();
		return true;
	}

	/// <summary>
	/// Where the current target is and how fast it is going: a unit's capsule, or
	/// the middle of what stands of a structure, which is not going anywhere.
	/// </summary>
	private bool TryTargetState(out Vector3 position, out Vector3 velocity)
	{
		position = Vector3.Zero;
		velocity = Vector3.Zero;

		UnitManager units = UnitManager.Instance;
		if (units == null)
		{
			return false;
		}

		if (OwnerId.IsStructure(_targetOwnerId))
		{
			Structure structure = units.StructureOf(_targetOwnerId);
			if (structure == null || structure.IsDestroyed)
			{
				return false;
			}

			position = structure.AimPoint;
			return true;
		}

		Unit unit = units.Find(OwnerId.UnitOf(_targetOwnerId));
		if (unit == null || !unit.IsAlive)
		{
			return false;
		}

		position = unit.Hitbox.Center;
		velocity = unit.Velocity;
		return true;
	}

	/// <summary>
	/// Whether the target can be seen from the eye. A structure is seen when the
	/// first thing the line meets is the structure, since a finished one is itself
	/// the world geometry an ordinary sight line would call "blocked".
	/// </summary>
	private bool TargetInSight(Vector3 eye, Vector3 targetPosition)
	{
		if (OwnerId.IsStructure(_targetOwnerId) && UnitManager.Instance?.StructureOf(_targetOwnerId) is { } structure)
		{
			return GroundSensor.CanSee(_character.GetWorld3D()?.DirectSpaceState, eye, structure);
		}

		return HasLineOfSight(eye, targetPosition);
	}

	// ---- where to stand ----------------------------------------------------

	/// <summary>
	/// Where a bot with nothing to shoot at goes: the enemy's barracks, offset by a
	/// per-bot loiter point that moves every few seconds.
	///
	/// The barracks is the only fixed thing on the map that matters — it is where
	/// the strategist's pressure comes from — so a ground force that walks towards
	/// it is a ground force that meets units. The offset is what stops six bots
	/// queueing through one doorway.
	///
	/// A barracks with defences of its own is waited for from outside them, on the
	/// side the bot came from (docs/NETCODE.md §10.4). The door is not somewhere a
	/// body can stand any more, and a bot that walked to it anyway would spend the
	/// ground force's tickets on nothing.
	/// </summary>
	private bool Objective(uint tick, fps_controller character, out Vector3 goal)
	{
		uint bucket = tick / (uint)ObjectiveIntervalTicks;
		if (bucket != _objectiveBucket)
		{
			_objectiveBucket = bucket;
			_hasObjective = TryFindObjective(character, bucket, out _objective);
		}

		goal = _objective;
		return _hasObjective;
	}

	private bool TryFindObjective(fps_controller character, uint bucket, out Vector3 goal)
	{
		goal = Vector3.Zero;

		UnitManager units = UnitManager.Instance;
		Team team = CombatManager.Instance?.TeamOf(_peerId) ?? Team.GroundForce;

		Vector3 anchor = Vector3.Zero;
		float defended = 0f;
		float best = float.MaxValue;
		bool found = false;

		for (int i = 0; units != null && i < units.BarracksCount; i++)
		{
			Barracks barracks = units.BarracksAt(i);
			if (barracks == null || barracks.Team == team)
			{
				continue;
			}

			float distance = character.SimPosition.DistanceTo(barracks.GlobalPosition);
			if (distance < best)
			{
				best = distance;
				anchor = barracks.GlobalPosition;
				defended = barracks.DefendedRadiusMeters;
				found = true;
			}
		}

		if (!found)
		{
			return false;
		}

		uint seed = Spread.Seed(_peerId, LoiterSeedSalt, bucket);

		if (defended > 0f)
		{
			// A point on the ring's edge, somewhere in an arc facing the bot, held until
			// the next bucket.
			float ring = defended + StandoffMarginMeters;
			float swing = (((seed >> 8) / 16777216f) * 2f) - 1f;
			goal = Reachable(DefenseSim.StandoffPoint(anchor, character.SimPosition, ring,
				swing * StandoffArcRadians));

			// A ring that runs past the map's edge puts part of that arc beyond a wall,
			// and the nearest place a bot can actually stand to a point beyond a wall is
			// along the wall — back towards the guns. Straight out from the barracks is
			// always the way away from it.
			if (DefenseSim.IsInside(anchor, goal, ring - StandoffToleranceMeters))
			{
				goal = Reachable(DefenseSim.StandoffPoint(anchor, character.SimPosition, ring, 0f));
			}

			return true;
		}

		// A point on a circle around the objective, held until the next bucket.
		float angle = (seed >> 8) * (Quantize.TwoPi / 16777216f);
		goal = anchor + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * LoiterRadiusMeters;
		return true;
	}

	/// <summary>
	/// <paramref name="goal"/>, unless it or the bot is inside an enemy barracks'
	/// defences, in which case the edge of them straight out from the bot. A bot that
	/// has seen a unit by the door fights it from outside the ring — and the unit,
	/// sooner or later, has to come out. A bot that has strayed inside, cutting a
	/// corner on its way round or strafing in a firefight, leaves by the shortest way.
	/// </summary>
	private Vector3 OutsideDefences(fps_controller character, Team team, Vector3 goal)
	{
		UnitManager units = UnitManager.Instance;

		for (int i = 0; units != null && i < units.BarracksCount; i++)
		{
			Barracks barracks = units.BarracksAt(i);
			if (barracks == null || barracks.Team == team || barracks.DefendedRadiusMeters <= 0f)
			{
				continue;
			}

			float ring = barracks.DefendedRadiusMeters + StandoffMarginMeters;
			if (DefenseSim.IsInside(barracks.GlobalPosition, goal, ring - StandoffToleranceMeters)
				|| DefenseSim.IsInside(barracks.GlobalPosition, character.SimPosition, ring - StandoffToleranceMeters))
			{
				return Reachable(DefenseSim.StandoffPoint(barracks.GlobalPosition, character.SimPosition, ring, 0f));
			}
		}

		return goal;
	}

	/// <summary>
	/// The nearest point to <paramref name="point"/> a bot can stand on, or the point
	/// itself when there is no navigation mesh to ask. A waiting point on the far side
	/// of a wall is one the bot slides along the wall towards for ever.
	/// </summary>
	private Vector3 Reachable(Vector3 point)
	{
		if (UnitManager.Instance is not { NavigationReady: true } || _character == null
			|| !GodotObject.IsInstanceValid(_character))
		{
			return point;
		}

		// A map that has not synced its regions yet answers with the origin, which is
		// somewhere, but not somewhere near the point that was asked about.
		Vector3 snapped = NavigationServer3D.MapGetClosestPoint(_character.GetWorld3D().NavigationMap, point);
		return snapped == Vector3.Zero ? point : snapped;
	}

	/// <summary>
	/// The world direction to walk in this tick: towards the next corner of the path
	/// when there is a navigation mesh, straight at the goal when there is not, and
	/// across the obstacle when the bot has stopped making progress.
	/// </summary>
	private Vector3 SteerDirection(fps_controller character, uint tick, bool hasGoal, Vector3 goal)
	{
		if (!hasGoal)
		{
			return Vector3.Zero;
		}

		Vector3 next = NextPathPosition(goal);
		Vector3 direction = next - character.SimPosition;
		direction.Y = 0f;

		if (direction.LengthSquared() < 0.0001f)
		{
			return Vector3.Zero;
		}

		direction = direction.Normalized();

		if (tick < _stuckUntilTick)
		{
			// A quarter turn off the blocked direction, one way or the other. Combined
			// with the brain's jump this clears kerbs, crates and inside corners
			// without a bot needing to understand what it is stuck on.
			float sign = BotBrain.StrafeSign(tick, _peerId, StuckRecoveryTicks);
			direction = direction.Rotated(Vector3.Up, sign * Mathf.Pi * 0.5f);
		}

		return direction;
	}

	/// <summary>
	/// The next corner of the path, or the goal itself when the map has no navigation
	/// mesh — the same fallback units take, and for the same reason: worse pathing is
	/// a playable milestone and a refusal to move is not (see <c>Unit.NextPathPosition</c>).
	/// </summary>
	private Vector3 NextPathPosition(Vector3 goal)
	{
		if (UnitManager.Instance is not { NavigationReady: true })
		{
			return goal;
		}

		_agent ??= CreateAgent();
		if (_agent == null)
		{
			return goal;
		}

		if (_agent.TargetPosition.DistanceSquaredTo(goal) > RepathThresholdMeters * RepathThresholdMeters)
		{
			_agent.TargetPosition = goal;
		}

		Vector3 next = _agent.GetNextPathPosition();
		return next.IsEqualApprox(Vector3.Zero) ? goal : next;
	}

	private NavigationAgent3D CreateAgent()
	{
		if (_character == null || !GodotObject.IsInstanceValid(_character))
		{
			return null;
		}

		var agent = new NavigationAgent3D
		{
			Name = "BotNavigationAgent",
			Radius = 0.5f,
			Height = 2f,
			PathDesiredDistance = 1f,
			TargetDesiredDistance = 1.5f,

			// Off for the same reason units have it off: RVO across everything on the
			// field is a per-tick cost this prototype has no budget for, and character
			// bodies already push each other out of the way.
			AvoidanceEnabled = false,
		};

		_character.AddChild(agent);
		agent.TargetPosition = _character.SimPosition;
		return agent;
	}

	/// <summary>
	/// Decides whether the bot is wedged: it wanted to move, and a half-second later
	/// it is still in the same place.
	/// </summary>
	private void TrackProgress(fps_controller character, uint tick)
	{
		if (tick < _nextProgressTick)
		{
			return;
		}

		bool first = _nextProgressTick == 0;
		if (!first && _wantedToMove
			&& character.SimPosition.DistanceTo(_lastProgressPosition) < StuckDistanceMeters)
		{
			_stuckUntilTick = tick + (uint)StuckRecoveryTicks;
		}

		_lastProgressPosition = character.SimPosition;
		_nextProgressTick = tick + (uint)StuckWindowTicks;
	}

	// ---- world queries -----------------------------------------------------

	private bool HasLineOfSight(Vector3 from, Vector3 to) =>
		GroundSensor.HasLineOfSight(_character, from, to);

	/// <summary>
	/// Whether somebody on the bot's own side is standing in the shot.
	///
	/// This exists because friendly fire between players is on: a round is stopped by
	/// the first capsule it reaches whoever owns it (see <c>CombatManager.ResolveSegment</c>),
	/// so without this check a bot will eventually shoot the person it is supposed to
	/// be helping — the fastest way to make a playtester stop wanting bots.
	/// </summary>
	private bool FriendlyInLineOfFire(Vector3 from, Vector3 to, Team team)
	{
		CombatManager combat = CombatManager.Instance;
		if (combat == null)
		{
			return false;
		}

		for (int i = 0; i < combat.PlayerCount; i++)
		{
			PlayerCombat other = combat.PlayerAt(i);
			if (other == null || other.PeerId == _peerId || other.Team != team || !other.IsAlive
				|| other.Character == null)
			{
				continue;
			}

			if (Hitbox.SegmentIntersects(other.Character.Hitbox, from, to, FriendlySweepRadiusMeters, out _))
			{
				return true;
			}
		}

		return false;
	}
}
