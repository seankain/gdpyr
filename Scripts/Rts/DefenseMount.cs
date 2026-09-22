using Gdpyr.Core;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Rts;

/// <summary>
/// One of a barracks' own guns or mortars (docs/NETCODE.md §10.4).
///
/// A map node, authored as a child of the <see cref="Barracks"/> it defends, and
/// named on the wire by its index in the <c>barracks_defense</c> group sorted by
/// scene path — the order every peer derives from the same scene, and the index
/// its rounds carry as their owner (<see cref="OwnerId.ForDefense"/>).
///
/// It is the building's, not the strategist's: it costs nothing, is never on a
/// queue, cannot be selected or ordered, runs out of nothing and cannot be killed.
/// Every number that decides how dangerous it is lives here as an export, because
/// how far out the ground around a door is closed is a question for whoever lays
/// the map out, and one map's answer is not another's.
///
/// A body and a pose and nothing else. What it shoots at and when is
/// <see cref="DefenseBattery"/>, and the rules it does that by are
/// <see cref="DefenseSim"/> and <see cref="MortarSolver"/>, which are engine-free
/// and tested. Deliberately not a collider, for the reason a heavy gun is not
/// one: something to hide behind would have to be taught to the navigation bake
/// and to the projectile queries (docs/NETCODE.md §10.2). The barracks it stands
/// against is the cover.
/// </summary>
public partial class DefenseMount : Node3D
{
	/// <summary>Nodes in this group are the defences. The map owns them, not the manager.</summary>
	public const string Group = "barracks_defense";

	[Export]
	public DefenseKind Kind = DefenseKind.Gun;

	/// <summary>Which catalog weapon it fires. Both defaults have no magazine: a defence never runs dry.</summary>
	[Export]
	public int WeaponId = WeaponCatalog.Turret;

	/// <summary>Furthest it engages from, muzzle to target.</summary>
	[Export]
	public float RangeMeters = 100f;

	/// <summary>Nearest it engages at. A mortar's floor; leave a gun's at zero.</summary>
	[Export]
	public float MinRangeMeters;

	/// <summary>
	/// Seconds between picking somebody out and firing at them: the gunner's
	/// reaction, the mortar's laying time. The window a player who has just been seen
	/// has to get back behind something.
	/// </summary>
	[Export]
	public float ReactionSeconds = 0.5f;

	/// <summary>How fast a gun swings round. Ignored by a mortar, whose tube is laid per shell.</summary>
	[Export]
	public float TraverseDegreesPerSecond = 90f;

	/// <summary>How far off its aim a gun may be and still fire.</summary>
	[Export]
	public float OnTargetDegrees = 2f;

	/// <summary>
	/// Half-angle of a gun's accuracy cone. The number that decides whether the ring
	/// is dangerous or certain death, and a pure playtest knob, as a unit's is
	/// (<see cref="UnitDefinition.AccuracyConeDegrees"/>).
	/// </summary>
	[Export]
	public float AccuracyConeDegrees = 1.2f;

	/// <summary>
	/// Radius of the circle a mortar's shells land inside, about where the target was
	/// standing when the shell was fired. It does not lead: a shell is six seconds in
	/// the air, and what it punishes is standing still.
	/// </summary>
	[Export]
	public float ScatterMeters = 3f;

	/// <summary>Where a round leaves from, above the mount's origin.</summary>
	[Export]
	public float MuzzleHeightMeters = 1.6f;

	/// <summary>Index in the defence group, assigned by <see cref="DefenseBattery.Collect"/>; -1 until then.</summary>
	public int Index { get; set; } = -1;

	/// <summary>What every round it fires names as its owner.</summary>
	public int ShooterId => OwnerId.ForDefense(Index);

	/// <summary>Whose side it is on: its barracks', and the strategist's when it stands on its own.</summary>
	public Team Team => _barracks?.Team ?? Team.Strategist;

	public DefenseTraits Traits { get; private set; }

	public byte WeaponDefinitionId => (byte)Mathf.Clamp(WeaponId, 0, SimConfig.MaxWeaponDefinitions - 1);

	public WeaponStats Stats => WeaponCatalog.StatsFor(WeaponDefinitionId);

	public float AccuracyConeRadians => Spread.ConeFromDegrees(AccuracyConeDegrees);

	public Vector3 MuzzlePosition => GlobalPosition + (Vector3.Up * MuzzleHeightMeters);

	// ---- server state, driven by DefenseBattery ------------------------------

	public WeaponState Weapon;

	/// <summary>The peer it is shooting at, or 0.</summary>
	public int TargetPeerId { get; set; }

	/// <summary>When it picked its current target, which is what its reaction is counted from.</summary>
	public uint TargetSinceTick { get; set; }

	/// <summary>The next tick it re-scans on, staggered by index as units are by id.</summary>
	public uint NextScanTick { get; set; }

	/// <summary>Where it is pointed. World angles, in <see cref="Aim"/>'s convention.</summary>
	public float Yaw { get; set; }

	public float Pitch { get; set; }

	private Barracks _barracks;
	private Node3D _yawPivot;
	private Node3D _pitchPivot;
	private float _homeYaw;

	public override void _Ready()
	{
		AddToGroup(Group);
		_barracks = FindBarracks();

		Traits = new DefenseTraits(
			Kind,
			RangeMeters,
			MinRangeMeters,
			WeaponStats.SecondsToTicks(ReactionSeconds),
			Mathf.DegToRad(TraverseDegreesPerSecond) * SimConfig.TickDelta,
			Mathf.DegToRad(OnTargetDegrees));

		Aim.Angles(GlobalBasis * Vector3.Forward, out _homeYaw, out _);

		BuildBody();
		ResetState();
	}

	/// <summary>Stood down and pointed where the map put it, for a new round.</summary>
	public void ResetState()
	{
		Weapon = WeaponState.Ready(WeaponDefinitionId, Stats);
		TargetPeerId = 0;
		TargetSinceTick = 0;
		NextScanTick = 0;
		Yaw = _homeYaw;

		// A tube at rest is laid up; a gun at rest is level.
		Pitch = Kind == DefenseKind.Mortar ? Mathf.DegToRad(60f) : 0f;
		ApplyPose();
	}

	/// <summary>
	/// Points it along a round it has just fired. On a client this is the whole of
	/// its replication: a defence has nothing else a peer needs to know, and the
	/// spawn record every client already receives says which way it was facing
	/// (docs/NETCODE.md §4.2).
	/// </summary>
	public void PointAlong(Vector3 direction)
	{
		if (direction == Vector3.Zero)
		{
			return;
		}

		Aim.Angles(direction, out float yaw, out float pitch);
		Yaw = yaw;
		Pitch = pitch;
		ApplyPose();
	}

	/// <summary>Turns the body to <see cref="Yaw"/> and <see cref="Pitch"/>. Presentation only.</summary>
	public void ApplyPose()
	{
		if (_yawPivot == null)
		{
			return;
		}

		// World yaw, whichever way the barracks the mount stands on is turned.
		_yawPivot.GlobalBasis = Basis.FromEuler(new Vector3(0f, Yaw, 0f));
		_pitchPivot.Rotation = new Vector3(Pitch, 0f, 0f);
	}

	private Barracks FindBarracks()
	{
		for (Node node = GetParent(); node != null; node = node.GetParent())
		{
			if (node is Barracks barracks)
			{
				return barracks;
			}
		}

		return null;
	}

	/// <summary>
	/// A sandbagged pedestal and a barrel, or a baseplate and a tube. Built in code
	/// for the reason a heavy gun's is: a .tscn would be a second place for the
	/// muzzle height to disagree with where the rounds come from.
	/// </summary>
	private void BuildBody()
	{
		var metal = new StandardMaterial3D { AlbedoColor = new Color(0.2f, 0.21f, 0.24f) };
		var sandbags = new StandardMaterial3D { AlbedoColor = new Color(0.55f, 0.48f, 0.34f) };

		bool mortar = Kind == DefenseKind.Mortar;

		AddChild(new MeshInstance3D
		{
			Name = "Base",
			Position = new Vector3(0f, mortar ? 0.08f : (MuzzleHeightMeters - 0.3f) * 0.5f, 0f),
			Mesh = mortar
				? new CylinderMesh { TopRadius = 0.7f, BottomRadius = 0.7f, Height = 0.16f, RadialSegments = 12 }
				: new CylinderMesh
				{
					TopRadius = 1.1f,
					BottomRadius = 1.3f,
					Height = Mathf.Max(MuzzleHeightMeters - 0.3f, 0.2f),
					RadialSegments = 12,
				},
			MaterialOverride = mortar ? metal : sandbags,
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
		});

		_yawPivot = new Node3D { Name = "Traverse", Position = new Vector3(0f, MuzzleHeightMeters, 0f) };
		AddChild(_yawPivot);

		_pitchPivot = new Node3D { Name = "Elevation" };
		_yawPivot.AddChild(_pitchPivot);

		// -Z is forward, so the barrel reaches out along it from the pivot. A
		// cylinder stands along +Y; a quarter turn about X lays it along -Z.
		_pitchPivot.AddChild(new MeshInstance3D
		{
			Name = "Barrel",
			Position = new Vector3(0f, 0f, mortar ? -0.5f : -0.8f),
			Rotation = new Vector3(-Mathf.Pi * 0.5f, 0f, 0f),
			Mesh = new CylinderMesh
			{
				TopRadius = mortar ? 0.09f : 0.06f,
				BottomRadius = mortar ? 0.09f : 0.06f,
				Height = mortar ? 1.2f : 1.8f,
				RadialSegments = 8,
			},
			MaterialOverride = metal,
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
		});
	}
}
