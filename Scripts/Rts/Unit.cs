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

	public float Yaw { get; private set; }

	/// <summary>Where a shot leaves from, and where a line-of-sight ray starts.</summary>
	public Vector3 EyePosition => GlobalPosition + (Vector3.Up * (Definition?.EyeHeightMeters ?? 1.5f));

	/// <summary>
	/// This unit's damageable volume, from the same capsule it collides with. Units
	/// keep no hitbox history: a projectile is already compensated by the shooter's
	/// latency when it spawns (docs/NETCODE.md §4.3), and a unit moves at 4.5 m/s,
	/// so the rewind the ring exists for is worth about seven centimetres here.
	/// </summary>
	public HitCapsule Hitbox => HitCapsule.FromFeet(GlobalPosition,
		Definition?.HeightMeters ?? 1.8f, Definition?.RadiusMeters ?? 0.4f);

	private NavigationAgent3D _agent;
	private MeshInstance3D _mesh;
	private CollisionShape3D _collision;
	private Vector3 _destination;
	private bool _hasDestination;
	private bool _repath;
	private float _gravity;

	public override void _Ready()
	{
		_gravity = ProjectSettings.GetSetting("physics/3d/default_gravity").AsSingle();

		Definition ??= UnitCatalog.Definition(DefinitionId);
		Traits = UnitCatalog.TraitsFor(DefinitionId);
		Health = Definition?.MaxHealth ?? 100f;

		byte weaponId = Definition?.WeaponDefinitionId ?? WeaponCatalog.Rifle;
		Weapon = WeaponState.Ready(weaponId, WeaponCatalog.StatsFor(weaponId));
		Grenades = GrenadeState.Full(Definition?.Grenades ?? 0);

		CollisionLayer = CollisionLayers.Units;
		CollisionMask = CollisionLayers.UnitMask;

		int seats = UnitCatalog.SeatsOf(DefinitionId);
		Cargo = seats > 0 ? new CargoHold(seats) : null;

		BuildBody();

		if (IsSimulated)
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
				desired = toNext.Normalized() * (Definition?.MoveSpeed ?? 4.5f);
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

	/// <summary>Takes the server's word for what a unit is doing and how hurt it is.</summary>
	public void ApplyRemoteState(UnitStateId state, byte healthPercent)
	{
		bool wasAlive = IsAlive;
		State = state;
		Health = healthPercent / (float)byte.MaxValue * (Definition?.MaxHealth ?? 100f);

		if (wasAlive != IsAlive)
		{
			ApplyPresentation();
		}
	}

	/// <summary>
	/// Whether it is drawn and solid, from whether it is alive and on foot — the one
	/// place both are decided, so that dying and getting in or out cannot undo each
	/// other (docs/VEHICLES.md §5.1). A dead unit is hidden and off its collision layer
	/// for the frame or two before it is freed, so nothing walks into an invisible
	/// corpse; a rider is hidden and off it for as long as it rides, and on the server
	/// collides with nothing either, so the hidden body never pushes its vehicle.
	/// </summary>
	public void ApplyPresentation()
	{
		bool shown = IsAlive && !IsRiding;
		Visible = shown;
		CollisionLayer = shown ? CollisionLayers.Units : 0u;

		if (IsSimulated)
		{
			CollisionMask = IsRiding ? 0u : CollisionLayers.UnitMask;
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
