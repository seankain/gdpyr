using System;
using Gdpyr.Core;
using Gdpyr.Fps;
using Gdpyr.Match;
using Gdpyr.Net;
using Gdpyr.Rts;
using Gdpyr.Sim;
using Gdpyr.Sim.Htn;
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
	public const int ScanIntervalTicks = SimConfig.UnitTargetRefreshTicks;

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

	/// <summary>
	/// Added to a launcher's blast radius before a bot will fire it: at the target's
	/// distance from itself, and at a teammate's from the target. A grenade does not
	/// land exactly where it was aimed.
	/// </summary>
	private const float BlastMarginMeters = 2f;

	/// <summary>Inside this of its goal a bot walks straight at it rather than along the path.</summary>
	private const float FinalApproachMeters = 1.2f;

	/// <summary>Metres the destination must move before the navigation agent is asked to re-path.</summary>
	private const float RepathThresholdMeters = 1.5f;

	/// <summary>Distinguishes this use of <see cref="Spread.Seed"/> from a weapon's and from the brain's.</summary>
	private const byte LoiterSeedSalt = SimConfig.MaxWeaponDefinitions + 3;

	private readonly int _peerId;
	private readonly BotTraits _traits;
	private readonly fps_controller _character;

	/// <summary>The team's memory, which every scan reports into (<see cref="BotDirector.GroundContacts"/>).</summary>
	private readonly ContactMemory _teamContacts;

	/// <summary>Reused by every scan so acquisition allocates nothing on the tick.</summary>
	private readonly GroundContact[] _contacts = new GroundContact[GroundSensor.MaxContacts];

	/// <summary>
	/// The ground domain every bot shares, under <c>--bot-ai htn</c>; null for legacy
	/// (docs/HTN_BOTS.md §4.2 rule 6), which leaves every legacy line as it was.
	/// </summary>
	private readonly GroundPlanning _planning;

	/// <summary>This bot's planning context: its facts, what the coordinator gave it, and the intent.</summary>
	private readonly GroundContext _plan;

	/// <summary>Friends for the <see cref="Neighbourhood"/> survey, reused by every scan.</summary>
	private readonly Neighbour[] _friends;

	/// <summary>The spots behind cover the team's bots have taken (<see cref="BotDirector.GroundCover"/>); null under legacy.</summary>
	private readonly CoverClaims _coverClaims;

	/// <summary>The other bots' spots, gathered for each cover search. Reused.</summary>
	private readonly Vector3[] _claimed = new Vector3[BotRoster.MaxBots];

	/// <summary>Whether, at the last scan, the box beside its spot hid it from the fight (docs/COVER.md §5).</summary>
	private bool _coverHides;

	/// <summary>The node the coordinator gave it, by economy index; -1 for none.</summary>
	private int _zone = -1;
	private int _sweepZone = -1;

	private uint _nextUseTick;
	private bool _planWasAlive;
	private Vector3 _lastPlannedPosition;
	private bool _rescan;

	/// <summary>
	/// Further than this in one tick is not a walk: the character was put back at a
	/// spawn, by a death or by a round reset, and the plan belongs to the life before.
	/// </summary>
	private const float TeleportMeters = 5f;

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

	public BotPilot(int peerId, fps_controller character, in BotTraits traits, ContactMemory teamContacts,
		GroundPlanning planning = null, CoverClaims coverClaims = null)
	{
		_peerId = peerId;
		_character = character;
		_traits = traits;
		_teamContacts = teamContacts;
		_planning = planning;
		_coverClaims = planning != null ? coverClaims : null;

		if (planning != null)
		{
			_plan = planning.CreateContext();
			_friends = new Neighbour[SnapshotCodec.MaxPlayers];
		}
	}

	public int PeerId => _peerId;

	/// <summary>How far it notices things. The coordinator picks focus targets inside it.</summary>
	public float SensorRadiusMeters => _traits.SensorRadiusMeters;

	/// <summary>True when it plans with the ground HTN (docs/HTN_BOTS.md §5.1).</summary>
	public bool Plans => _plan != null;

	/// <summary>What its running task asks for; <see cref="GroundGoal.None"/> for legacy. For the debug HUD.</summary>
	public GroundGoal Goal => _plan?.Intent.Goal ?? GroundGoal.None;

	/// <summary>What the coordinator last gave it.</summary>
	public GroundRole Role => _plan?.Role ?? GroundRole.Assault;

	public bool Sweeping => _plan != null && _plan.Is(GroundFact.Sweep);

	/// <summary>Trips to the locker it gave up on, for the coordinator (§3.4, P4).</summary>
	public int FailedLockerTrips => _plan?.FailedLockerTrips ?? 0;

	/// <summary>What it is shooting at, as a unit id. 0 for nothing, or for a structure. For the debug HUD.</summary>
	public ushort TargetUnitId => OwnerId.UnitOf(_targetOwnerId);

	/// <summary>What it is shooting at, as an owner id; 0 for nothing. For the spectator's debugger.</summary>
	public int TargetOwnerId => _targetOwnerId;

	/// <summary>Its planning context, for the spectator's debugger (docs/AI_DEBUG.md §6); null under legacy. Read it, never write it.</summary>
	public GroundContext Plan => _plan;

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
			if (_plan != null && _planWasAlive)
			{
				// A new life plans from nothing, on a scan of its own: whatever the last
				// one was doing ended with it.
				_planning.Reset(_plan);
				_planWasAlive = false;
				_rescan = true;
				LoseCover();
			}

			return InputFrame.Neutral(tick, character.Yaw, character.Pitch);
		}

		// Whether what is in its hands can hurt armour decides what it may pick as a
		// target at all (GroundSensor.NearestHostileTarget), and whether it has to
		// mind the blast before it fires.
		ProjectileStats round = EquippedRound(combat);
		bool explosive = round.IsExplosive;

		if (_plan != null && _planWasAlive
			&& character.SimPosition.DistanceSquaredTo(_lastPlannedPosition) > TeleportMeters * TeleportMeters)
		{
			// A round reset respawns without a death, so the check above never saw
			// this life end, and the last life's task — a node, a buddy, a ghost from
			// a round that is over — would carry on from the spawn (docs/HTN_BOTS.md
			// §6, H2).
			_planning.Reset(_plan);
			_planWasAlive = false;
			_rescan = true;
			LoseCover();
		}

		bool scheduled = tick >= _nextScanTick;
		if (scheduled || _rescan)
		{
			_rescan = false;
			AcquireTarget(character, combat.Team, explosive, tick);
			if (_plan != null)
			{
				Encode(character, combat, explosive, tick);
			}

			// The first scan is offset by the bot's own id so that six of them do not
			// re-scan on the same tick for the rest of the round. A scan forced by a
			// respawn leaves the schedule where it was, or every bot put back by a round
			// reset would scan on one tick from then on.
			if (scheduled)
			{
				uint stagger = _nextScanTick == 0 ? (uint)(_peerId % ScanIntervalTicks) : 0u;
				_nextScanTick = tick + (uint)ScanIntervalTicks + stagger;
			}
		}

		Vector3 eye = character.EyePosition;
		bool hasTarget = TryResolveTarget(eye, combat, out Vector3 aimDirection, out Vector3 targetPosition,
			out float targetDistance, out bool sightChecked, out bool inSight);

		if (!hasTarget)
		{
			_targetOwnerId = OwnerId.None;
		}

		bool visible = hasTarget && (sightChecked ? inSight : TargetInSight(eye, targetPosition))
			&& !FriendlyInLineOfFire(eye, targetPosition, combat.Team);

		// A launcher's round goes off wherever it lands, and a blast spares nobody on
		// the ground force: not a teammate standing by the target, and not the bot
		// that fired it from too close.
		bool holdFire = visible && explosive
			&& BlastEndangersFriends(targetPosition, targetDistance, round.ExplosionRadiusMeters, combat.Team);

		// A bot with something to shoot at walks towards it; the brain stops it and
		// strafes once it is inside its preferred range.
		Vector3 goal;
		bool hasGoal;
		bool keepMoving = false;
		bool use = false;
		bool inCover = false;
		if (_plan != null)
		{
			hasGoal = Planned(character, explosive, tick, hasTarget, targetPosition, out goal, out keepMoving, out use,
				out inCover);
		}
		else if (hasTarget)
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
			needsReload: needsReload,
			semiAutomatic: stats.Mode != FireMode.Auto,
			holdFire: holdFire,
			keepMoving: keepMoving,
			use: use,
			inCover: inCover,
			reloading: combat.Equipped.IsReloading);

		_wantedToMove = hasGoal || (hasTarget && visible);

		return BotBrain.Frame(tick, _peerId, situation, _traits);
	}

	/// <summary>Drops the navigation agent when the bot leaves. The character node is freed with it.</summary>
	public void Dispose()
	{
		LoseCover();

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
	private void AcquireTarget(fps_controller character, Team team, bool explosive, uint tick)
	{
		_contactCount = GroundSensor.Scan(character, _peerId, team, _traits.SensorRadiusMeters, _contacts,
			exposedHeads: _plan != null);
		Report(team, tick);
		int best = GroundSensor.NearestHostileTarget(_contacts.AsSpan(0, _contactCount), team, explosive);

		// The plan's choice, when this scan can see it and the weapon can hurt it: the
		// coordinator's focus target (docs/HTN_BOTS.md §5.1). The nearest otherwise.
		int preferred = _plan?.Intent.TargetOwnerId ?? OwnerId.None;
		if (preferred != OwnerId.None && preferred != best && CanTarget(preferred, team, explosive))
		{
			best = preferred;
		}

		if (best != _targetOwnerId)
		{
			// The reaction delay is measured from the switch, not from the scan: a bot
			// that swaps targets has to re-acquire the new one before it may fire.
			_targetOwnerId = best;
			_targetSinceTick = tick;
		}
	}

	/// <summary>Whether this scan can see <paramref name="ownerId"/> as a hostile the weapon in hand hurts.</summary>
	private bool CanTarget(int ownerId, Team team, bool explosive)
	{
		for (int i = 0; i < _contactCount; i++)
		{
			GroundContact seen = _contacts[i];
			if (seen.IsPlayer || seen.Team == team || !GroundSensor.CanHurt(seen, explosive))
			{
				continue;
			}

			int id = seen.IsStructure ? seen.StructureOwnerId : OwnerId.ForUnit(seen.UnitId);
			if (id == ownerId)
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// Tells the team what this scan found: every hostile unit and armed structure,
	/// as this bot's own eyes saw it (docs/HTN_BOTS.md §4.3, D3). Friends are not
	/// contacts, and an enemy player never shows up in a ground scan — a strategist
	/// has no body on the field.
	/// </summary>
	private void Report(Team team, uint tick)
	{
		if (_teamContacts == null)
		{
			return;
		}

		int observer = BotRoster.SlotOf(_peerId);
		for (int i = 0; i < _contactCount; i++)
		{
			GroundContact seen = _contacts[i];
			if (seen.IsPlayer || seen.Team == team)
			{
				continue;
			}

			_teamContacts.Observe(tick, observer, new KnownContact
			{
				OwnerId = seen.IsStructure ? seen.StructureOwnerId : OwnerId.ForUnit(seen.UnitId),
				Kind = seen.IsStructure ? ContactKind.ArmedStructure
					: seen.BulletProof ? ContactKind.Armour
					: ContactKind.Infantry,
				BulletProof = seen.BulletProof,
				Position = seen.Position,
				Velocity = seen.Velocity,
				HealthFraction = seen.HealthFraction,
			});
		}
	}

	/// <summary>
	/// Where the current target is now, and where to aim to hit it. Runs every tick,
	/// unlike acquisition, because a target's position is the thing being aimed at.
	/// </summary>
	private bool TryResolveTarget(Vector3 eye, PlayerCombat combat, out Vector3 aimDirection,
		out Vector3 targetPosition, out float distance, out bool sightChecked, out bool inSight)
	{
		aimDirection = Vector3.Zero;
		targetPosition = Vector3.Zero;
		distance = float.MaxValue;
		sightChecked = false;
		inSight = false;

		if (_targetOwnerId == OwnerId.None
			|| !TryTargetState(out targetPosition, out Vector3 targetVelocity, out Vector3 head))
		{
			return false;
		}

		// A unit behind a low wall shows its head over it (docs/COVER.md §3): under
		// the plan, a bot whose line to the middle is blocked aims at the head when
		// that is clear. One ray more, and only while the middle is hidden.
		if (_plan != null && OwnerId.IsUnit(_targetOwnerId))
		{
			sightChecked = true;
			inSight = HasLineOfSight(eye, targetPosition);
			if (!inSight && HasLineOfSight(eye, head))
			{
				inSight = true;
				targetPosition = head;
			}
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
	private bool TryTargetState(out Vector3 position, out Vector3 velocity, out Vector3 head)
	{
		position = Vector3.Zero;
		velocity = Vector3.Zero;
		head = Vector3.Zero;

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
			head = position;
			return true;
		}

		Unit unit = units.Find(OwnerId.UnitOf(_targetOwnerId));
		if (unit == null || !unit.IsAlive)
		{
			return false;
		}

		position = unit.Hitbox.Center;
		head = unit.EyePosition;
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

	// ---- planning (--bot-ai htn) -------------------------------------------

	/// <summary>
	/// One tick of the ground domain (docs/HTN_BOTS.md §5.1): the bot's position and
	/// hands into the context, the planner ticked, and the intent it leaves turned
	/// into where to walk, whether to keep walking, and whether to tap use.
	/// </summary>
	private bool Planned(fps_controller character, bool explosive, uint tick, bool hasTarget, Vector3 targetPosition,
		out Vector3 goal, out bool keepMoving, out bool use, out bool inCover)
	{
		GroundContext plan = _plan;
		plan.Tick = tick;
		plan.Position = character.SimPosition;
		plan.Explosive = explosive;
		_lastPlannedPosition = plan.Position;

		// The hands are known every tick, not only on a scan: a locker swap lands
		// between scans, and the task after it is planned on what it produced.
		plan.Sense(GroundFact.Armed, (byte)(explosive ? Arms.Explosive : Arms.SmallArms));

		// And whether it has reached its spot: the spot and whether its wall hides it
		// are the scan's, the walk to it is every tick's (docs/COVER.md §5).
		plan.Sense(GroundFact.Cover, (byte)CoverBands.Band(plan.HasCoverPoint, Flat(plan.Position, plan.CoverPoint),
			_coverHides, (CoverState)plan.Get(GroundFact.Cover)));
		_planning.Tick(plan);
		_planWasAlive = true;

		GroundIntent intent = plan.Intent;
		keepMoving = intent.Move == GroundMove.Run;
		inCover = false;

		// A tap is a press and a release. Pressed on one tick and a quarter-second
		// apart, which is long enough for the swap to be in hand before the next
		// press is decided — so a bot never cycles past the launcher.
		use = intent.UseLocker && !explosive && tick >= _nextUseTick;
		if (use)
		{
			_nextUseTick = tick + (uint)plan.Traits.UseTapIntervalTicks;
		}

		goal = intent.Point;
		switch (intent.Move)
		{
			case GroundMove.Stay:
				// A reload in its spot is a reload in cover: down behind the wall.
				inCover = plan.Get(GroundFact.Cover) == (byte)CoverState.In;
				return false;

			case GroundMove.Point:
			case GroundMove.Run:
				return intent.HasPoint;

			case GroundMove.Cover:
				// Walking to the spot it shoots on the move; standing in it, it stands
				// still behind its wall rather than strafing out from behind it, and
				// steps back in when a body shoves it off.
				float off = Flat(plan.Position, intent.Point);
				inCover = off <= CoverBands.ArriveMeters;
				keepMoving = !inCover;
				return intent.HasPoint && off > CoverSettleMeters;

			case GroundMove.Target:
				if (hasTarget)
				{
					goal = targetPosition;
					return true;
				}

				return intent.HasPoint;

			default:
				// Standoff, and the tick before anything was planned: today's behaviour.
				if (hasTarget)
				{
					goal = targetPosition;
					return true;
				}

				return Objective(tick, character, out goal);
		}
	}

	/// <summary>
	/// Bands what this scan found, what the team remembers, who is around and the
	/// bot's own body into the ground facts. On the scan the bot already runs, over
	/// what it already paid for: no ray here that the scan did not cast.
	/// </summary>
	private void Encode(fps_controller character, PlayerCombat combat, bool explosive, uint tick)
	{
		GroundContext plan = _plan;
		Team team = combat.Team;
		Vector3 at = character.SimPosition;

		WeaponStats stats = combat.EquippedStats;
		var sense = new GroundSense
		{
			Explosive = explosive,
			HealthFraction = combat.Health / (float)SimConfig.MaxHealth,
			MagazineLow = !stats.HasUnlimitedAmmo && !combat.Equipped.IsReloading
				&& combat.Equipped.Ammo <= stats.MagazineSize / 2,
		};

		bool armour = false;
		float armourDistance = float.MaxValue;
		for (int i = 0; i < _contactCount; i++)
		{
			GroundContact seen = _contacts[i];
			if (seen.IsPlayer || seen.Team == team)
			{
				continue;
			}

			sense.VisibleHostiles++;
			if (!seen.BulletProof)
			{
				sense.VisibleSoft++;
			}
			else if (seen.DistanceMeters < armourDistance)
			{
				armour = true;
				armourDistance = seen.DistanceMeters;
				plan.ArmourPoint = seen.Position;
			}
		}

		// What the team remembers nearby that this bot's own scan did not find: the
		// nearest soft one is the ghost worth walking to.
		float radius = plan.Traits.InvestigateRadiusMeters;
		float soft = float.MaxValue;
		float any = float.MaxValue;
		Vector3 softPoint = Vector3.Zero;
		Vector3 anyPoint = Vector3.Zero;
		for (int i = 0; _teamContacts != null && i < _teamContacts.Count; i++)
		{
			if (!_teamContacts.IsRemembered(i, tick))
			{
				continue;
			}

			KnownContact known = _teamContacts.At(i);
			float distance = at.DistanceTo(known.Position);
			if (distance > radius || InScan(known.OwnerId))
			{
				continue;
			}

			sense.Remembered = true;
			if (known.BulletProof)
			{
				if (!armour || distance < armourDistance)
				{
					armour = true;
					armourDistance = distance;
					plan.ArmourPoint = known.Position;
				}
			}
			else
			{
				sense.RememberedSoft = true;
				if (distance < soft)
				{
					soft = distance;
					softPoint = known.Position;
				}
			}

			if (distance < any)
			{
				any = distance;
				anyPoint = known.Position;
			}
		}

		plan.HasArmourPoint = armour;
		plan.GhostPoint = sense.RememberedSoft ? softPoint : anyPoint;

		int friends = FillFriends(team);
		Neighbourhood around = Neighbourhood.Survey(_peerId, at, _friends.AsSpan(0, friends), _teamContacts, tick,
			(OddsBand)plan.Get(GroundFact.Odds));
		sense.Odds = around.Odds;
		sense.NearestFriendMeters = around.NearestFriendMeters;
		plan.FallbackPoint = around.Friends > 0 ? around.Centroid
			: plan.HasBuddy ? plan.BuddyPoint
			: PlayerManager.Instance?.SpawnCentroid ?? at;

		Vector3 exit = OutsideDefences(character, team, at);
		sense.InsideDefences = exit != at;
		plan.ExitPoint = exit;

		sense.AtZone = _zone >= 0 && CombatManager.Instance?.Economy.NodeAt(_zone) is { } node && node.Covers(at);

		if (EmplacementManager.Instance is { } emplacements && emplacements.TryNearestLocker(at, out Vector3 locker))
		{
			plan.HasLocker = true;
			plan.LockerPoint = locker;
		}
		else
		{
			plan.HasLocker = false;
		}

		sense.Cover = SenseCover(character, team, at, plan);

		plan.Encode(sense);
	}

	/// <summary>How close to its spot a bot in cover stops walking: closer than the arrival band, so it settles in it.</summary>
	private const float CoverSettleMeters = 0.35f;

	/// <summary>
	/// The cover search, on the scan (docs/COVER.md §5): against the soft hostile it
	/// is shooting at, else the nearest one it can see, keep the spot it has while the
	/// wall beside it still hides it from there and it is still in the fight, else
	/// take the best one no other bot has. A remembered fight keeps the spot it had;
	/// no fight lets it go. No ray: the board is boxes.
	/// </summary>
	private CoverState SenseCover(fps_controller character, Team team, Vector3 at, GroundContext plan)
	{
		CoverBoard board = UnitManager.Instance?.Cover;
		if (board == null || board.Count == 0)
		{
			LoseCover();
			return CoverState.None;
		}

		bool fight = TryCoverThreat(team, out Vector3 threat, out float threatEye, out float threatChest);
		if (!fight)
		{
			if (plan.HasCoverPoint && plan.Get(GroundFact.Contact) == (byte)ContactLevel.Ghost)
			{
				return CoverBands.Band(true, Flat(at, plan.CoverPoint), _coverHides,
					(CoverState)plan.Get(GroundFact.Cover));
			}

			LoseCover();
			return CoverState.None;
		}

		GroundPlanTraits traits = plan.Traits;
		var query = new CoverQuery
		{
			From = at,
			Threat = threat,
			ThreatEyeMeters = threatEye,
			ThreatAimMeters = threatChest,
			SearchMeters = traits.CoverSearchMeters,
			BodyRadiusMeters = CoverBodyRadiusMeters,
			// A standing body's numbers whatever this one is doing: crouched to reload,
			// its own eye is below the wall it means to fire over when it stands.
			ProtectMeters = UnitManager.CoverThreatChestMeters,
			EyeMeters = UnitManager.CoverThreatEyeMeters,
			MustFire = true,
			MinThreatMeters = traits.CoverMinThreatMeters,
			MaxThreatMeters = traits.CoverMaxThreatMeters,
		};

		bool kept = plan.HasCoverPoint && Keeps(board, plan.CoverPoint, query);
		if (!kept)
		{
			int claimed = _coverClaims?.Gather(BotRoster.SlotOf(_peerId), _claimed) ?? 0;
			// Snapped to where a bot can stand, and still behind its wall once it is: a
			// spot the navigation mesh moved out of cover would be walked to for ever.
			Vector3 snapped = Vector3.Zero;
			if (board.TryFindSpot(query, _claimed.AsSpan(0, claimed), out CoverSpot spot)
				&& !InsideEnemyDefences(team, spot.Position)
				&& board.Protects(snapped = Reachable(spot.Position), query))
			{
				plan.HasCoverPoint = true;
				plan.CoverPoint = snapped;
				_coverClaims?.Claim(BotRoster.SlotOf(_peerId), plan.CoverPoint);
			}
			else
			{
				LoseCover();
				return CoverState.None;
			}
		}

		_coverHides = board.Protects(plan.CoverPoint, query);
		return CoverBands.Band(true, Flat(at, plan.CoverPoint), _coverHides, (CoverState)plan.Get(GroundFact.Cover));
	}

	/// <summary>Whether a spot it already has still does: within reach, in the fight's range, and its wall still hides it.</summary>
	private static bool Keeps(CoverBoard board, Vector3 spot, in CoverQuery query)
	{
		float range = Flat(spot, query.Threat);
		return Flat(spot, query.From) <= query.SearchMeters + CoverBands.LeaveMeters
			&& range >= query.MinThreatMeters && range <= query.MaxThreatMeters
			&& board.Protects(spot, query);
	}

	/// <summary>
	/// What it takes cover from: the unit it is shooting at when bullets hurt it, else
	/// the nearest such unit its scan found — a unit's feet, eye and middle above them.
	/// Armour is not a fight a wall settles, and a launcher has "engage armour".
	/// </summary>
	private bool TryCoverThreat(Team team, out Vector3 threat, out float eye, out float chest)
	{
		threat = Vector3.Zero;
		eye = 0f;
		chest = 0f;

		UnitManager units = UnitManager.Instance;
		if (units == null)
		{
			return false;
		}

		Unit unit = OwnerId.IsUnit(_targetOwnerId) ? units.Find(OwnerId.UnitOf(_targetOwnerId)) : null;
		if (unit == null || !unit.IsAlive || unit.Team == team || (unit.Definition?.IsBulletProof ?? false))
		{
			unit = null;
			for (int i = 0; i < _contactCount && unit == null; i++)
			{
				GroundContact seen = _contacts[i];
				if (!seen.IsPlayer && !seen.IsStructure && seen.Team != team && !seen.BulletProof)
				{
					unit = units.Find(seen.UnitId);
				}
			}
		}

		if (unit == null || !unit.IsAlive)
		{
			return false;
		}

		threat = unit.GlobalPosition;
		eye = unit.EyePosition.Y - threat.Y;
		chest = unit.Hitbox.Center.Y - threat.Y;
		return true;
	}

	/// <summary>Whether a point is inside an enemy barracks' defended ring, where <see cref="OutsideDefences"/> would never let it walk.</summary>
	private static bool InsideEnemyDefences(Team team, Vector3 point)
	{
		UnitManager units = UnitManager.Instance;
		for (int i = 0; units != null && i < units.BarracksCount; i++)
		{
			Barracks barracks = units.BarracksAt(i);
			if (barracks != null && barracks.Team != team && barracks.DefendedRadiusMeters > 0f
				&& DefenseSim.IsInside(barracks.GlobalPosition, point,
					barracks.DefendedRadiusMeters + StandoffMarginMeters))
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>Lets its spot go: no fight, a new life, or gone.</summary>
	private void LoseCover()
	{
		_coverHides = false;
		if (_plan != null)
		{
			_plan.HasCoverPoint = false;
		}

		_coverClaims?.Release(BotRoster.SlotOf(_peerId));
	}

	/// <summary>A player's capsule radius, for how far off a wall a bot stands.</summary>
	private const float CoverBodyRadiusMeters = 0.5f;

	private static float Flat(Vector3 a, Vector3 b)
	{
		float x = a.X - b.X;
		float z = a.Z - b.Z;
		return Mathf.Sqrt((x * x) + (z * z));
	}

	/// <summary>
	/// What the ground coordinator gave this bot (docs/HTN_BOTS.md §5.1). Points are
	/// snapped to the navigation mesh once, when the assignment changes, not every tick.
	/// </summary>
	public void Apply(in GroundOrders orders)
	{
		GroundContext plan = _plan;
		if (plan == null)
		{
			return;
		}

		plan.SetRole(orders.Role);

		if (orders.Zone != _zone)
		{
			_zone = orders.Zone;
			plan.HasZone = orders.Zone >= 0;
			plan.ZonePoint = plan.HasZone ? Reachable(orders.ZonePoint) : Vector3.Zero;
		}

		if (orders.SweepZone != _sweepZone)
		{
			_sweepZone = orders.SweepZone;
			plan.SweepPoint = orders.SweepZone >= 0 ? Reachable(orders.SweepPoint) : Vector3.Zero;
		}

		plan.Sense(GroundFact.Sweep, orders.SweepZone >= 0);
		plan.FocusOwnerId = orders.FocusOwnerId;
		plan.HasBuddy = orders.BuddyId != 0;
		plan.BuddyPoint = orders.BuddyPosition;
	}

	/// <summary>Whether this scan saw <paramref name="ownerId"/>.</summary>
	private bool InScan(int ownerId)
	{
		for (int i = 0; i < _contactCount; i++)
		{
			GroundContact seen = _contacts[i];
			if (!seen.IsPlayer && (seen.IsStructure ? seen.StructureOwnerId : OwnerId.ForUnit(seen.UnitId)) == ownerId)
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>Every living teammate, people and bots, for the survey. The bot itself is skipped by id.</summary>
	private int FillFriends(Team team)
	{
		int count = 0;
		CombatManager combat = CombatManager.Instance;
		for (int i = 0; combat != null && i < combat.PlayerCount && count < _friends.Length; i++)
		{
			PlayerCombat other = combat.PlayerAt(i);
			if (other == null || other.Team != team || !other.IsAlive || other.Character == null)
			{
				continue;
			}

			_friends[count++] = new Neighbour(other.PeerId, other.Character.SimPosition,
				other.Health / (float)SimConfig.MaxHealth, engaging: false);
		}

		return count;
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

		// Under the plan the last metre is walked straight: the navigation agent calls a
		// goal reached at 1.5 m, which is further off a spot behind cover than its wall is.
		Vector3 next = _plan != null && Flat(character.SimPosition, goal) <= FinalApproachMeters
			? goal
			: NextPathPosition(goal);
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
	/// <summary>The round the bot's equipped weapon fires. Default for a swing, which is no round at all.</summary>
	private static ProjectileStats EquippedRound(PlayerCombat combat)
	{
		byte weaponId = combat.EquippedDefinitionId;
		return weaponId < WeaponCatalog.Projectiles.Length ? WeaponCatalog.Projectiles[weaponId] : default;
	}

	/// <summary>
	/// Whether a blast at the target would reach the bot or a teammate
	/// (<see cref="BlastMarginMeters"/> included). The launcher is why this exists:
	/// <c>CombatManager.Explode</c> spares friendly *units* and nobody else, and a bot
	/// that occasionally kills the person it spawned to help is worse than no bot.
	/// </summary>
	private bool BlastEndangersFriends(Vector3 target, float targetDistance, float blastRadius, Team team)
	{
		float reach = blastRadius + BlastMarginMeters;
		if (targetDistance <= reach)
		{
			return true;
		}

		CombatManager combat = CombatManager.Instance;
		for (int i = 0; combat != null && i < combat.PlayerCount; i++)
		{
			PlayerCombat other = combat.PlayerAt(i);
			if (other == null || other.PeerId == _peerId || other.Team != team || !other.IsAlive
				|| other.Character == null)
			{
				continue;
			}

			if (other.Character.Hitbox.Center.DistanceTo(target) <= reach)
			{
				return true;
			}
		}

		return false;
	}

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
