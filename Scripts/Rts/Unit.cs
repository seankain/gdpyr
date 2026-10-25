using Gdpyr.Core;
using Gdpyr.Sim;
using Gdpyr.Sim.Htn;
using Godot;

namespace Gdpyr.Rts;

/// <summary>
/// One RTS unit (docs/IMPLEMENTATION_PLAN.md §M3).
///
/// Server-only simulation: the authority walks it, points it and fires its weapon;
/// every client holds the same node as a puppet, placed from interpolated
/// snapshots and never stepped (docs/NETCODE.md §1, §6.1). Nothing here is
/// predicted, because 100–250 ms of command latency is what an RTS feels like
/// anyway (docs/IMPLEMENTATION_PLAN.md §2).
///
/// It is a body and a pair of eyes and nothing else. What it decides to do is
/// <see cref="UnitBrain"/>, which is engine-free; who it decides to do it to is
/// <see cref="UnitManager"/>, which owns the registry the decision is made
/// against — the plan rejects the pyrrhic original's per-unit
/// <c>FindObjectsOfType</c> scene scan outright (§1).
///
/// Built in code rather than from a scene: everything about its shape comes from
/// <see cref="UnitDefinition"/>, and a .tscn would only be a second place for the
/// capsule's height to disagree with the hitbox's.
/// </summary>
public partial class Unit : CharacterBody3D
{
	/// <summary>Metres the destination must move before the path is asked for again.</summary>
	private const float RepathThresholdMeters = 0.75f;

	/// <summary>How fast a unit turns to face where it is going or what it is shooting [rad/s].</summary>
	private const float TurnRateRadians = 6f;

	/// <summary>Server-allocated, never 0.</summary>
	public ushort UnitId { get; set; }

	public byte DefinitionId { get; set; }

	/// <summary>Whose it is. Every unit is the strategist's in M3; M5's design has no others either.</summary>
	public Team Team { get; set; } = Team.Strategist;

	/// <summary>True only on the authority. A client's copy is placed, never stepped.</summary>
	public bool IsSimulated { get; set; }

	/// <summary>Set by <see cref="UnitManager"/> before the node enters the tree.</summary>
	public UnitDefinition Definition { get; set; }

	public UnitTraits Traits { get; private set; }

	public float Health { get; private set; }

	public UnitStateId State { get; set; } = UnitStateId.Idle;

	public UnitOrder Order;

	public WeaponState Weapon;

	/// <summary>
	/// Server-side: the hand grenades it has left (docs/NETCODE.md §10.9) —
	/// <see cref="UnitDefinition.Grenades"/> at birth, none for a vehicle. A unit is not
	/// resupplied with them. Never replicated.
	/// </summary>
	public GrenadeState Grenades;

	/// <summary>
	/// Server-side: the peer whose order it is carrying out — a person, a computer
	/// strategist or an agent's seat — or 0 for one nobody gave: a fresh unit walking
	/// to its rally point, one a scenario placed. What tells a human's unit from a
	/// bot's (docs/HTN_BOTS.md §4.3, D1). Never replicated.
	/// </summary>
	public int OrderIssuer { get; set; }

	/// <summary>Server-side: the tick that order was given on.</summary>
	public uint OrderTick { get; set; }

	/// <summary>
	/// Server-side, under <c>--bot-ai htn</c>: what this unit plans with — its facts,
	/// its squad's census and the intent its running task leaves
	/// (docs/HTN_BOTS.md §5.2). Null under legacy and on clients. Never replicated.
	/// </summary>
	public UnitContext Plan { get; set; }

	/// <summary>Server-side: the last tick something hurt it; 0 for never. What tells a builder it is under fire (§5.2).</summary>
	public uint LastDamagedTick { get; set; }

	/// <summary>
	/// Server-side, under <c>--bot-ai htn</c>: the spot it has taken behind cover
	/// (docs/COVER.md §5), which other units' searches leave alone. Never replicated.
	/// </summary>
	public bool HasCoverSpot { get; set; }

	/// <inheritdoc cref="HasCoverSpot"/>
	public Vector3 CoverSpot { get; set; }

	/// <summary>
	/// Server-side: its target's chest is behind something and its head is not, so it
	/// aims at the head (docs/COVER.md §3). Decided on its scan; never replicated.
	/// </summary>
	public bool AimsHigh { get; set; }

	/// <summary>What this unit is shooting at, as an <see cref="OwnerId"/>. 0 for nothing.</summary>
	public int TargetOwnerId { get; set; }

	/// <summary>
	/// Server-side: where it last saw its target, for a unit that keeps firing there
	/// after losing sight of it — the heavy gunner (<see cref="Suppression"/>,
	/// docs/NETCODE.md §10.8). Empty for every other unit. Never replicated.
	/// </summary>
	public SuppressionMemory LastSeen;

	/// <summary>
	/// Server-side, under <c>--bot-ai htn</c>: what it knows of being shot at — the
	/// worst round this window, where it reckons the latest came from, and whose it was
	/// (docs/TACTICS.md §4.1). Written by <c>UnitManager.NoteRound</c>. Never replicated.
	/// </summary>
	public FireMemory Fire;

	/// <summary>
	/// Server-side, under micro-autonomy: what the grenade its plan means to throw claims
	/// (docs/TACTICS.md §5.4), from its last scan. Never replicated.
	/// </summary>
	public long ThrowKey;

	/// <summary>
	/// Server-side: the tick before which it does not look for a grenade's arc again,
	/// after a scan that found a hidden enemy and no arc to them. Never replicated.
	/// </summary>
	public uint NoArcUntilTick;

	/// <summary>
	/// Server-side, for a marksman under micro-autonomy: the peek pair it has taken
	/// (docs/TACTICS.md §5.3), whose hide spot other units' searches leave alone. Never
	/// replicated.
	/// </summary>
	public bool HasPeek;

	/// <inheritdoc cref="HasPeek"/>
	public PeekPair Peek;

	/// <summary>
	/// Server-side, for a unit in a squad that drills (docs/TACTICS.md §5.5): the spot its
	/// task there takes it to — a fire position, cover from the threat point, a bound spot —
	/// which other units' searches leave alone. Never replicated.
	/// </summary>
	public bool HasDrillSpot;

	/// <inheritdoc cref="HasDrillSpot"/>
	public Vector3 DrillSpot;

	/// <summary>
	/// Server-side, under micro-autonomy: the exposed builder it escorts (docs/TACTICS.md
	/// §5.10), as a unit id, 0 for none, and its place among that builder's escorts. Written
	/// by the escort pass every tick, before any unit moves. Never replicated.
	/// </summary>
	public ushort Escorting;

	/// <inheritdoc cref="Escorting"/>
	public int EscortSlot;

	/// <summary>Server-side: the tick its weapon last fired a round; 0 for never. What a drill's base of fire is judged firing by.</summary>
	public uint LastShotTick;

	/// <summary>
	/// Whether it uses the tactics of docs/TACTICS.md §5 on its own (§5.9): the side's
	/// default for new units when it is made, and the strategist's to change. Its
	/// planner reads it every tick. On a client, what the strategist's state message
	/// said, for the selection card; never in the unit snapshot.
	/// </summary>
	public MicroAutonomy Autonomy { get; set; }

	/// <summary>The next tick it re-scans for a target on, so scans can be staggered.</summary>
	public uint NextScanTick { get; set; }

	/// <summary>Server-side: the tick its corpse is taken off the field on.</summary>
	public uint DespawnTick { get; set; }

	public bool IsAlive => Health > 0f;

	/// <summary>
	/// The vehicle it is riding in, by <see cref="UnitId"/>; 0 on foot
	/// (docs/VEHICLES.md §5.1). A rider stays a unit — alive, hidden, solid to nothing,
	/// not simulated, and pinned to its vehicle's position every tick. Never set on a
	/// dead unit.
	/// </summary>
	public ushort RidingIn { get; set; }

	public bool IsRiding => RidingIn != 0;

	/// <summary>
	/// Alive and on foot: the question every system outside the unit loop is asking
	/// when it asks whether a unit is there to be seen, hit, counted on a node or
	/// selected. A rider is alive — it is still the strategist's army — and is none
	/// of those.
	/// </summary>
	public bool IsOnField => IsAlive && !IsRiding;

	/// <summary>
	/// Who is aboard, for a unit with seats (<see cref="UnitDefinition.Seats"/>); null
	/// for one without. Made once, in <c>_Ready</c>.
	/// </summary>
	public CargoHold Cargo { get; private set; }

	/// <summary>True for a unit other units can get into.</summary>
	public bool HasSeats => Cargo is { Seats: > 0 };

	/// <summary>
	/// Server-side, on a unit with seats: the peer whose embark request last reserved
	/// or filled one of them (docs/VEHICLES.md §5.3). A vehicle with somebody aboard or
	/// on the way whose loader is no longer seated as a strategist lets them out.
	/// </summary>
	public int CargoIssuer { get; set; }

	/// <summary>
	/// Server-side, on a unit with seats: an order given to it while units were still
	/// walking to it, carried out once none is (docs/VEHICLES.md §5.2). Meaningful only
	/// while <see cref="HasDeparture"/>.
	/// </summary>
	public UnitOrder Departure;

	/// <inheritdoc cref="Departure"/>
	public bool HasDeparture { get; set; }

	/// <summary>Server-side, on a unit with seats: units walking to it, as the last cargo pass counted them.</summary>
	public int BoardersWalking { get; set; }

	/// <summary>Server-side, on an unloading vehicle: the tick a rider with no clear place to get out is tried again on.</summary>
	public uint UnloadRetryTick { get; set; }

	/// <summary>
	/// An aircraft's flight (docs/VEHICLES.md §4.2): its phase, whether it has found
	/// nowhere to land, and whether somebody is under it as it comes down. Stepped on the
	/// authority by <see cref="Sim.Flight"/>; a client's copy has the phase and
	/// <see cref="CannotLand"/> from the snapshot.
	/// </summary>
	public FlightState Flight;

	/// <summary>
	/// An aircraft's flight phase, replicated in the flag byte;
	/// <see cref="FlightPhase.Grounded"/> for everything that does not fly. Off the
	/// ground it is solid to nobody.
	/// </summary>
	public FlightPhase Phase
	{
		get => Flight.Phase;
		set => Flight.Phase = value;
	}

	/// <summary>An aircraft holding at cruise because it found nowhere to land. Replicated, for the card.</summary>
	public bool CannotLand
	{
		get => Flight.CannotLand;
		set => Flight.CannotLand = value;
	}

	/// <summary>How it flies (<see cref="UnitDefinition.CruiseAltitudeMeters"/>); a cruise altitude of zero for a unit that does not.</summary>
	public FlightTraits FlightTraits { get; private set; }

	/// <summary>True for a unit that flies: the dropship (docs/VEHICLES.md §8).</summary>
	public bool IsAircraft => FlightTraits.IsAircraft;

	/// <summary>
	/// Server-side, for an aircraft: how far its feet are above the ground under it, as
	/// the last tick's ray found it; 0 on the ground, and for everything that does not
	/// fly. What a wreck's riders are judged by (<see cref="Cargo.WreckKillsAll"/>).
	/// Never replicated.
	/// </summary>
	public float HeightAboveGround { get; set; }

	/// <summary>
	/// Server-side, for an aircraft coming down: the ground it is landing on, found clear
	/// before it started down (docs/VEHICLES.md §8.2). Meaningful only while
	/// <see cref="HasLandingSpot"/>.
	/// </summary>
	public Vector3 LandingSpot { get; set; }

	/// <inheritdoc cref="LandingSpot"/>
	public bool HasLandingSpot { get; set; }

	/// <summary>
	/// Server-side: where its order meant it to land when it found <see cref="LandingSpot"/>
	/// there or near there. While the order still means that place, the aircraft has
	/// arrived, wherever round it the spot turned out to be.
	/// </summary>
	public Vector3 LandingFor { get; set; }

	/// <summary>Server-side, for an aircraft that found nowhere to land: the tick it looks again on.</summary>
	public uint LandingRetryTick { get; set; }

	/// <summary>
	/// How many are aboard, for a vehicle with seats: its hold on the authority, and on
	/// a client the records in the newest unit snapshot that name it
	/// (docs/VEHICLES.md §6.1) — units are never fogged or truncated, so the count is
	/// exact.
	/// </summary>
	public int AboardCount => IsSimulated ? Cargo?.Count ?? 0 : _remoteAboard;

	public float Yaw { get; private set; }

	/// <summary>
	/// Standing or crouched (docs/TACTICS.md §5.8): on the server what its plan asked for
	/// this tick, if its body can; on a client what the newest record said.
	/// </summary>
	public UnitPosture Posture { get; private set; }

	/// <summary>Its body, standing and crouched, from its definition.</summary>
	public UnitBody Body => Definition?.ToBody() ?? new UnitBody(1.8f, 0.4f, 1.5f);

	/// <summary>Where a shot leaves from, and where a line-of-sight ray starts: lower crouched.</summary>
	public Vector3 EyePosition => GlobalPosition + (Vector3.Up * Body.EyeHeight(Posture));

	/// <summary>
	/// This unit's damageable volume, from the same capsule it collides with, crouched or
	/// standing. Units keep no hitbox history: a projectile is already compensated by the
	/// shooter's latency when it spawns (docs/NETCODE.md §4.3), and a unit moves at
	/// 4.5 m/s, so the rewind the ring exists for is worth about seven centimetres here.
	/// </summary>
	public HitCapsule Hitbox => Body.Hitbox(GlobalPosition, Posture);

	private NavigationAgent3D _agent;
	private MeshInstance3D _mesh;

	/// <summary>An aircraft's rotors, on a peer that draws anything, and whether they are turning: off the ground, and shown.</summary>
	private Node3D[] _rotors;
	private bool _rotorsSpinning;
	private CollisionShape3D _collision;
	private Vector3 _destination;
	private bool _hasDestination;
	private bool _repath;

	/// <summary>A client's copy, until the first snapshot record about it: drawn as nothing, solid to nothing.</summary>
	private bool _awaitingFirstRecord;

	/// <summary>
	/// A client's copy: the vehicle the newest record says it is in, held until the
	/// render clock reaches the tick that record is about. State is decoded about a
	/// hundred milliseconds before the drawn transform gets there, and a rider shown at
	/// decode would stand in the truck bed and then slide out through the hull.
	/// </summary>
	private ushort _pendingRidingIn;
	private uint _ridingChangeTick;
	private bool _ridingChangePending;

	private int _remoteAboard;
	private float _gravity;

	public override void _Ready()
	{
		_gravity = ProjectSettings.GetSetting("physics/3d/default_gravity").AsSingle();

		Definition ??= UnitCatalog.Definition(DefinitionId);
		Traits = UnitCatalog.TraitsFor(DefinitionId);
		FlightTraits = UnitCatalog.FlightTraitsFor(DefinitionId);
		Health = Definition?.MaxHealth ?? 100f;

		byte weaponId = Definition?.WeaponDefinitionId ?? WeaponCatalog.Rifle;
		Weapon = WeaponState.Ready(weaponId, WeaponCatalog.StatsFor(weaponId));
		Grenades = GrenadeState.Full(Definition?.Grenades ?? 0);

		CollisionLayer = CollisionLayers.Units;
		CollisionMask = CollisionLayers.UnitMask;

		int seats = UnitCatalog.SeatsOf(DefinitionId);
		Cargo = seats > 0 ? new CargoHold(seats) : null;

		BuildBody();
		SetProcess(_rotors != null);

		// An aircraft flies a straight line over everything; it has no path to ask for.
		if (IsSimulated && !IsAircraft)
		{
			BuildAgent();
		}

		_destination = GlobalPosition;
		Order = UnitOrder.Hold(GlobalPosition);
	}

	// ---- server ------------------------------------------------------------

	/// <summary>
	/// Walks one tick towards <paramref name="destination"/>, or stands still.
	/// Called by <see cref="UnitManager"/> after the brain has decided where that
	/// is; this function has no opinion about it.
	/// </summary>
	public void Steer(Vector3 destination, bool hasDestination, float dt)
	{
		if (!IsSimulated)
		{
			return;
		}

		SetDestination(destination, hasDestination);

		Vector3 velocity = Velocity;
		Vector3 desired = Vector3.Zero;

		if (_hasDestination)
		{
			Vector3 next = NextPathPosition();
			Vector3 toNext = next - GlobalPosition;
			toNext.Y = 0f;

			if (toNext.LengthSquared() > 0.0001f)
			{
				desired = toNext.Normalized() * (Definition?.MoveSpeed ?? 4.5f) * Body.SpeedFactor(Posture);
			}
		}

		// No acceleration curve: a rifleman is not a vehicle, and the one thing a
		// strategist needs to be able to predict is where twenty of them will be in
		// five seconds.
		velocity.X = desired.X;
		velocity.Z = desired.Z;

		velocity.Y = IsOnFloor() ? 0f : velocity.Y - (_gravity * dt);

		Velocity = velocity;
		MoveAndSlide();
	}

	/// <summary>
	/// An aircraft's step (docs/VEHICLES.md §8.2), in place of <see cref="Steer"/>: a
	/// straight line across the ground towards <paramref name="destination"/> at its
	/// speed when <paramref name="horizontal"/> allows it, and
	/// <paramref name="verticalVelocity"/> up or down, both from <see cref="Sim.Flight"/>.
	/// Floating while it is off the ground, so the floor is not a floor to it; on the
	/// ground it stands still and settles, as a unit that is not walking does. Velocity
	/// is always written whole, Y included: a ground bot leads its shots with it.
	/// </summary>
	public void SteerFlying(Vector3 destination, bool horizontal, float verticalVelocity, float dt)
	{
		if (!IsSimulated)
		{
			return;
		}

		Vector3 velocity = Vector3.Zero;
		if (Phase == FlightPhase.Grounded && verticalVelocity <= 0f)
		{
			MotionMode = MotionModeEnum.Grounded;
			velocity.Y = IsOnFloor() ? 0f : Velocity.Y - (_gravity * dt);
		}
		else
		{
			MotionMode = MotionModeEnum.Floating;
			velocity.Y = verticalVelocity;

			Vector3 across = destination - GlobalPosition;
			across.Y = 0f;
			float distance = across.Length();
			if (horizontal && distance > 0.01f)
			{
				// No further than the destination in one tick, so it stops over it.
				float speed = Mathf.Min(Definition?.MoveSpeed ?? 0f, distance / Mathf.Max(dt, 1e-4f));
				velocity += across / distance * speed;
			}
		}

		Velocity = velocity;
		MoveAndSlide();
	}

	/// <summary>
	/// Turns to face a point over this tick. Facing is cosmetic — a unit's shots
	/// come from <see cref="EyePosition"/> along a direction the caller computes —
	/// but a firing line that is looking the wrong way reads as a bug.
	/// </summary>
	public void FaceTowards(Vector3 point, float dt)
	{
		Vector3 to = point - GlobalPosition;
		to.Y = 0f;
		if (to.LengthSquared() < 0.0004f)
		{
			return;
		}

		Aim.Angles(to, out float wanted, out _);
		float step = TurnRateRadians * dt;
		float difference = Mathf.AngleDifference(Yaw, wanted);
		Yaw += Mathf.Clamp(difference, -step, step);
		Basis = Basis.FromEuler(new Vector3(0f, Yaw, 0f));
	}

	/// <summary>Returns true when this damage killed the unit.</summary>
	public bool ApplyDamage(float amount)
	{
		if (!IsAlive || amount <= 0f)
		{
			return false;
		}

		Health = Mathf.Max(0f, Health - amount);
		if (IsAlive)
		{
			return false;
		}

		State = UnitStateId.Dead;
		ApplyPresentation();
		return true;
	}

	/// <summary>
	/// Asks for a fresh path on the next destination, however near the last one it is.
	/// For a unit put somewhere other than where it walked to — out of a vehicle — whose
	/// old path starts where it got in (docs/VEHICLES.md §5.4).
	/// </summary>
	public void ForceRepath()
	{
		_repath = true;
	}

	/// <summary>
	/// One tick of resupply (<see cref="Resupply"/>): health back at the unit's own
	/// rate, up to its maximum. The caller has decided it is resupplied; this only
	/// counts. Server-side, and replicated as health always is, in the unit snapshot.
	/// </summary>
	public void Heal(float dt)
	{
		Health = Resupply.Heal(Health, Definition?.MaxHealth ?? 100f, Definition?.RegenPerSecond ?? 0f, dt);
	}

	/// <summary>True while it has health to get back and a rate to get it back at.</summary>
	public bool CanBeResupplied => IsAlive && (Definition?.RegenPerSecond ?? 0f) > 0f
		&& Health < (Definition?.MaxHealth ?? 100f);

	public byte HealthPercent => UnitFlags.PackHealth(Health, Definition?.MaxHealth ?? 100f);

	// ---- client ------------------------------------------------------------

	/// <summary>
	/// Places a unit from an interpolated snapshot. The client's copy has no
	/// velocity, no navigation agent and no opinions (docs/NETCODE.md §6.1).
	/// </summary>
	public void ApplyRemoteTransform(Vector3 position, float yaw)
	{
		GlobalPosition = position;
		Yaw = yaw;
		Basis = Basis.FromEuler(new Vector3(0f, yaw, 0f));
	}

	/// <summary>
	/// Takes the server's word for what a unit is doing, how hurt it is, what it is
	/// riding in and how it is flying, from a record about <paramref name="tick"/>.
	/// The first record about a unit is applied whole, presentation and all, whatever
	/// it says — so a passenger never flashes visible to a peer that joined while it was
	/// aboard. After that, getting in or out waits for the render clock
	/// (<see cref="TryTakeRidingChange"/>); dying and taking off or landing do not.
	/// </summary>
	public void ApplyRemoteState(in UnitSnapshot record, uint tick)
	{
		bool wasAlive = IsAlive;
		FlightPhase wasPhase = Phase;
		TakePosture(record.Posture);
		State = record.State;
		Health = record.HealthPercent / (float)byte.MaxValue * (Definition?.MaxHealth ?? 100f);
		Phase = record.Phase;
		CannotLand = record.CannotLand;

		// The invariant, on this side too: a dead unit is never aboard.
		ushort ridingIn = IsAlive ? record.RidingIn : (ushort)0;

		if (_awaitingFirstRecord)
		{
			_awaitingFirstRecord = false;
			_ridingChangePending = false;
			RidingIn = ridingIn;
			ApplyPresentation();
			return;
		}

		if (ridingIn != (_ridingChangePending ? _pendingRidingIn : RidingIn))
		{
			_pendingRidingIn = ridingIn;
			_ridingChangeTick = tick;
			_ridingChangePending = ridingIn != RidingIn;
		}

		if (wasAlive != IsAlive || wasPhase != Phase)
		{
			if (!IsAlive)
			{
				// Dead is dead now, not at the render clock: there is no body to show.
				RidingIn = 0;
				_ridingChangePending = false;
			}

			ApplyPresentation();
		}
	}

	/// <summary>
	/// A client's copy, once per frame: gets in or out once <paramref name="renderTick"/>
	/// has reached the tick the record that said so was about. True when it did;
	/// <paramref name="gotOut"/> says it was getting out, when the caller drops the
	/// interpolator's samples from before <paramref name="changeTick"/> — the ones at
	/// the vehicle — so it appears where it got out instead of sliding there.
	/// </summary>
	public bool TryTakeRidingChange(float renderTick, out uint changeTick, out bool gotOut)
	{
		changeTick = _ridingChangeTick;
		gotOut = false;
		if (!_ridingChangePending || renderTick < _ridingChangeTick)
		{
			return false;
		}

		gotOut = RidingIn != 0 && _pendingRidingIn == 0;
		RidingIn = _pendingRidingIn;
		_ridingChangePending = false;
		ApplyPresentation();
		return true;
	}

	/// <summary>
	/// A client's copy, made by a spawn message before any snapshot has said where it is
	/// or whether it is riding in something: nothing to see or bump into until the first
	/// record does (docs/VEHICLES.md §6.2). Called after it enters the tree, whose
	/// <c>_Ready</c> puts it on its layer.
	/// </summary>
	public void HideUntilFirstRecord()
	{
		_awaitingFirstRecord = true;
		Visible = false;
		CollisionLayer = 0u;
	}

	/// <summary>A client's copy of a vehicle: how many records in the newest snapshot name it.</summary>
	public void SetRemoteAboard(int count)
	{
		_remoteAboard = count;
	}

	/// <summary>
	/// Whether it is drawn and solid, from whether it is alive and on foot — the one
	/// place both are decided, so that dying and getting in or out cannot undo each
	/// other (docs/VEHICLES.md §5.1). A dead unit is hidden and off its collision layer
	/// for the frame or two before it is freed, so nothing walks into an invisible
	/// corpse; a rider is hidden and off it for as long as it rides, and on the server
	/// collides with nothing either, so the hidden body never pushes its vehicle. An
	/// aircraft off the ground is drawn, on nobody's layer, and on the server meets only
	/// the world (docs/VEHICLES.md §8.3): nothing rides its roof into the air, and it
	/// flies over units and players rather than into them.
	/// </summary>
	public void ApplyPresentation()
	{
		bool shown = IsAlive && !IsRiding;
		bool grounded = Phase == FlightPhase.Grounded;
		Visible = shown;
		CollisionLayer = shown && grounded ? CollisionLayers.Units : 0u;

		if (IsSimulated)
		{
			CollisionMask = IsRiding ? 0u : grounded ? CollisionLayers.UnitMask : CollisionLayers.World;
		}

		_rotorsSpinning = shown && !grounded;
	}

	/// <summary>How fast an aircraft's rotors turn in the air [rad/s]. Cosmetic.</summary>
	private const float RotorRadiansPerSecond = 30f;

	/// <summary>Only an aircraft on a peer that draws it is processed at all: its rotors turn while it is off the ground.</summary>
	public override void _Process(double delta)
	{
		if (!_rotorsSpinning || _rotors == null)
		{
			return;
		}

		float turn = RotorRadiansPerSecond * (float)delta;
		for (int i = 0; i < _rotors.Length; i++)
		{
			// Alternate ways round, as a tandem pair does.
			_rotors[i].RotateY(i % 2 == 0 ? turn : -turn);
		}
	}

	// ---- posture (docs/TACTICS.md §5.8) ---------------------------------------

	/// <summary>
	/// Stands or crouches: the server's from its plan, every tick; a client's from a
	/// record. A body that cannot crouch, one riding in a vehicle and an aircraft stand.
	/// The collider and the drawing change height with it, the feet where they were.
	/// </summary>
	public void TakePosture(UnitPosture wanted)
	{
		UnitPosture posture = IsAircraft || RidingIn != 0 ? UnitPosture.Standing : Body.Take(wanted);
		if (posture == Posture)
		{
			return;
		}

		Posture = posture;
		float height = Body.Height(posture);
		if (_collision?.Shape is CapsuleShape3D shape)
		{
			shape.Height = height;
			_collision.Position = new Vector3(0f, height * 0.5f, 0f);
		}

		if (_mesh?.Mesh is CapsuleMesh mesh)
		{
			mesh.Height = height;
			_mesh.Position = new Vector3(0f, height * 0.5f, 0f);
		}
	}

	// ---- construction ------------------------------------------------------

	private void BuildBody()
	{
		float height = Definition?.HeightMeters ?? 1.8f;
		float radius = Definition?.RadiusMeters ?? 0.4f;

		_collision = new CollisionShape3D
		{
			Name = "CollisionShape3D",
			// A CharacterBody3D reports its feet, so the capsule sits half its height up.
			Position = new Vector3(0f, height * 0.5f, 0f),
			Shape = new CapsuleShape3D { Height = height, Radius = radius },
		};
		AddChild(_collision);

		if (IsAircraft)
		{
			// The collider stays the capsule the hitbox is; only what is drawn differs.
			if (!Bootstrap.IsDedicatedServer)
			{
				BuildAirframe(height, radius);
			}

			return;
		}

		_mesh = new MeshInstance3D
		{
			Name = "Mesh",
			Position = new Vector3(0f, height * 0.5f, 0f),
			Mesh = new CapsuleMesh { Height = height, Radius = radius, RadialSegments = 8, Rings = 3 },
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
			MaterialOverride = new StandardMaterial3D
			{
				AlbedoColor = Definition?.Color ?? new Color(0.85f, 0.25f, 0.2f),
			},
		};
		AddChild(_mesh);

		if (Definition?.IsSupply == true && !Bootstrap.IsDedicatedServer)
		{
			BuildSupplyRing(Definition.SupplyRadiusMeters);
		}
	}

	/// <summary>
	/// An aircraft as drawn (docs/VEHICLES.md §8.4): a hull on four legs and a tandem pair
	/// of rotors, inside the capsule it collides with and is hit as. All of it hangs off
	/// one child, so anything that ever tilts it tilts the drawing and never the body's
	/// basis, which is its facing on the wire.
	/// </summary>
	private void BuildAirframe(float height, float radius)
	{
		Color color = Definition?.Color ?? new Color(0.35f, 0.5f, 0.75f);
		var hullMaterial = new StandardMaterial3D { AlbedoColor = color };
		var darkMaterial = new StandardMaterial3D { AlbedoColor = color.Darkened(0.6f) };

		var airframe = new Node3D { Name = "Airframe" };
		AddChild(airframe);

		float legHeight = height * 0.22f;
		float hullHeight = height * 0.45f;
		float hullWidth = radius * 1.2f;
		float hullLength = radius * 2.3f;

		airframe.AddChild(Part("Hull", new BoxMesh { Size = new Vector3(hullWidth, hullHeight, hullLength) },
			new Vector3(0f, legHeight + (hullHeight * 0.5f), 0f), hullMaterial));

		for (int i = 0; i < 4; i++)
		{
			float x = (i % 2 == 0 ? -1f : 1f) * hullWidth * 0.4f;
			float z = (i < 2 ? -1f : 1f) * hullLength * 0.35f;
			airframe.AddChild(Part($"Leg{i}", new BoxMesh { Size = new Vector3(0.15f, legHeight, 0.15f) },
				new Vector3(x, legHeight * 0.5f, z), darkMaterial));
		}

		// Fore and aft, the aft one higher so the discs pass over each other.
		float blade = radius * 1.7f;
		float top = legHeight + hullHeight;
		_rotors = new Node3D[2];
		for (int i = 0; i < _rotors.Length; i++)
		{
			float z = (i == 0 ? -1f : 1f) * hullLength * 0.38f;
			float y = top + ((height - top) * (i == 0 ? 0.55f : 0.85f));
			var rotor = new Node3D { Name = $"Rotor{i}", Position = new Vector3(0f, y, z) };
			rotor.AddChild(Part("BladeA", new BoxMesh { Size = new Vector3(blade, 0.05f, 0.22f) }, Vector3.Zero,
				darkMaterial));
			rotor.AddChild(Part("BladeB", new BoxMesh { Size = new Vector3(0.22f, 0.05f, blade) }, Vector3.Zero,
				darkMaterial));
			airframe.AddChild(rotor);
			_rotors[i] = rotor;
		}
	}

	private static MeshInstance3D Part(string name, Mesh mesh, Vector3 position, Material material) => new()
	{
		Name = name,
		Mesh = mesh,
		Position = position,
		CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
		MaterialOverride = material,
	};

	/// <summary>
	/// A line on the ground at a supply truck's reach, as a barracks draws its ring:
	/// a strategist sending units to it, and a player deciding whether it is worth
	/// shooting, both need to see how far it reaches.
	/// </summary>
	private void BuildSupplyRing(float radius)
	{
		const float HalfWidth = 0.15f;

		AddChild(new MeshInstance3D
		{
			Name = "SupplyRing",
			Position = new Vector3(0f, 0.05f, 0f),
			Mesh = new TorusMesh
			{
				InnerRadius = Mathf.Max(radius - HalfWidth, 0f),
				OuterRadius = radius + HalfWidth,
				Rings = 64,
				RingSegments = 4,
			},
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
			MaterialOverride = new StandardMaterial3D
			{
				AlbedoColor = Definition?.Color ?? new Color(0.3f, 0.75f, 0.45f),
				ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
			},
		});
	}

	private void BuildAgent()
	{
		_agent = new NavigationAgent3D
		{
			Name = "NavigationAgent3D",
			Radius = Definition?.RadiusMeters ?? 0.4f,
			Height = Definition?.HeightMeters ?? 1.8f,
			MaxSpeed = Definition?.MoveSpeed ?? 4.5f,
			PathDesiredDistance = 1f,
			TargetDesiredDistance = Definition?.ArrivalRadiusMeters ?? 1.5f,

			// RVO across fifty agents is the kind of per-tick cost this prototype has
			// no budget for (docs/IMPLEMENTATION_PLAN.md §6), and units that push each
			// other out of the way with their collision shapes look close enough.
			AvoidanceEnabled = false,
		};
		AddChild(_agent);
	}

	private void SetDestination(Vector3 destination, bool hasDestination)
	{
		_hasDestination = hasDestination;
		if (!hasDestination)
		{
			return;
		}

		// Re-pathing every tick would recompute the same path sixty times a second
		// for a unit walking a straight line.
		if (_repath || _destination.DistanceSquaredTo(destination) > RepathThresholdMeters * RepathThresholdMeters)
		{
			_repath = false;
			_destination = destination;
			if (_agent != null && UnitManager.Instance is { NavigationReady: true })
			{
				_agent.TargetPosition = destination;
			}
		}
	}

	/// <summary>
	/// The next corner of the path, or the destination itself when there is no
	/// navigation mesh to path across.
	///
	/// The fallback is not a nicety. A greybox map is mostly open ground, the bake
	/// happens at runtime on the server, and a unit that refuses to move because a
	/// navmesh failed to bake is a milestone that cannot be playtested. Walking into
	/// a wall and sliding along it is worse pathing and a working game.
	/// </summary>
	private Vector3 NextPathPosition()
	{
		if (_agent == null || UnitManager.Instance is not { NavigationReady: true })
		{
			return _destination;
		}

		Vector3 next = _agent.GetNextPathPosition();
		return next.IsEqualApprox(Vector3.Zero) ? _destination : next;
	}
}
