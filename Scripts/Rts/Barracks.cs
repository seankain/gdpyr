using Gdpyr.Core;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Rts;

/// <summary>
/// Where units come from: a build queue, a spawn point and a rally point
/// (docs/IMPLEMENTATION_PLAN.md §M3).
///
/// A node in the map rather than something the strategist places, because
/// construction is a second system and the plan cuts builder units from the first
/// pass (§5). It belongs to the map for the same reason spawn points do: the
/// layout decides where the strategist's pressure comes from, and that is one of
/// the three things worth iterating.
///
/// The queue itself is engine-free (<see cref="BuildQueue"/>) and owns the money;
/// this class is the part of it that knows where the ground is.
/// </summary>
public partial class Barracks : Node3D
{
	/// <summary>Nodes in this group are the barracks. The map owns them, not the manager.</summary>
	public const string Group = "barracks";

	/// <summary>Metres in front of the spawn point that fresh units are sent by default.</summary>
	private const float DefaultRallyDistance = 8f;

	/// <summary>Whose it is. Every unit in this design is the strategist's.</summary>
	public Team Team { get; set; } = Team.Strategist;

	/// <summary>Server-side. A client holds a barracks node and an empty queue it never ticks.</summary>
	public BuildQueue Queue { get; } = new();

	/// <summary>
	/// Where a finished unit appears. A child <c>Marker3D</c> named
	/// <c>SpawnPoint</c> if the map supplies one, so that the door is authored and
	/// not computed.
	/// </summary>
	public Vector3 SpawnPosition => _spawnPoint?.GlobalPosition ?? GlobalPosition;

	/// <summary>Where finished units are sent. Set by the strategist; defaults ahead of the door.</summary>
	public Vector3 RallyPoint { get; set; }

	/// <summary>
	/// How far out, measured flat from the middle of the building, its own defences
	/// can reach: the furthest any child <see cref="DefenseMount"/> engages, plus how
	/// far that mount stands off-centre. 0 for a barracks with none.
	///
	/// An outer bound rather than a footprint — a gun that cannot see round a wall
	/// does not cover what is behind it — which is the right way round for the two
	/// things that read it: a bot deciding where it is safe to wait, and a ring on
	/// the ground telling a player where it is not (docs/NETCODE.md §10.4).
	/// </summary>
	public float DefendedRadiusMeters { get; private set; }

	private Marker3D _spawnPoint;
	private Vector3 _forward = Vector3.Forward;
	private Vector3 _right = Vector3.Right;

	public override void _Ready()
	{
		AddToGroup(Group);
		_spawnPoint = GetNodeOrNull<Marker3D>("SpawnPoint");

		// -Z is forward in Godot. Everything below walks away from the building along
		// this, which is what keeps units from being formed up inside it.
		Basis basis = _spawnPoint?.GlobalBasis ?? GlobalBasis;
		Vector3 forward = basis * Vector3.Forward;
		forward.Y = 0f;
		if (forward.LengthSquared() > 0.001f)
		{
			_forward = forward.Normalized();
			_right = new Vector3(-_forward.Z, 0f, _forward.X);
		}

		RallyPoint = SpawnPosition + (_forward * DefaultRallyDistance);

		// Children are ready before their parent, so the mounts have their numbers.
		foreach (Node child in GetChildren())
		{
			if (child is DefenseMount mount)
			{
				Vector3 offset = mount.GlobalPosition - GlobalPosition;
				offset.Y = 0f;
				DefendedRadiusMeters = Mathf.Max(DefendedRadiusMeters, offset.Length() + mount.RangeMeters);
			}
		}

		if (DefendedRadiusMeters > 0f && !Bootstrap.IsDedicatedServer)
		{
			BuildDefenseRing(DefendedRadiusMeters);
		}
	}

	/// <summary>
	/// A line on the ground at <see cref="DefendedRadiusMeters"/>. The ring only works
	/// as a design if the people it keeps out can see where it is: dying to a gun
	/// you did not know was there teaches nothing but that the map is unfair.
	/// </summary>
	private void BuildDefenseRing(float radius)
	{
		const float HalfWidth = 0.3f;

		AddChild(new MeshInstance3D
		{
			Name = "DefenseRing",
			Position = new Vector3(0f, 0.05f, 0f),
			Mesh = new TorusMesh
			{
				InnerRadius = Mathf.Max(radius - HalfWidth, 0f),
				OuterRadius = radius + HalfWidth,
				Rings = 128,
				RingSegments = 4,
			},
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
			MaterialOverride = new StandardMaterial3D
			{
				AlbedoColor = new Color(0.85f, 0.2f, 0.15f),
				ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
			},
		});
	}

	/// <summary>
	/// Where the <paramref name="ordinal"/>-th unit forms up: rows in front of the
	/// door, filling left to right and stepping forwards.
	///
	/// Rows rather than a ring around the spawn point, because a ring puts half of
	/// every turn inside the building the door belongs to, and a unit that spawns
	/// inside a static body either sticks or is shoved out of it.
	/// </summary>
	public Vector3 SpawnSlot(int ordinal)
	{
		const int PerRow = 6;
		const float Spacing = 1.6f;

		if (ordinal < 0)
		{
			ordinal = 0;
		}

		int row = ordinal / PerRow;
		float lateral = ((ordinal % PerRow) - ((PerRow - 1) * 0.5f)) * Spacing;

		return SpawnPosition + (_right * lateral) + (_forward * (row * Spacing));
	}
}
