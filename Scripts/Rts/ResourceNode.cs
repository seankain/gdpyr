using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Rts;

/// <summary>
/// A point on the map that pays the strategist for holding it
/// (docs/IMPLEMENTATION_PLAN.md §M5).
///
/// Captured by standing on it rather than by a builder unit: §5 of the plan cuts
/// builders from the first pass because a proximity test exercises the same
/// economy loop with far less code, and the thing being measured is whether the
/// loop is worth playing at all.
///
/// A map node like <see cref="Barracks"/>, and for the same reason — where the
/// money is decides where the fighting is, and map layout is one of the three
/// variables the prototype exists to iterate. The rules are engine-free
/// (<see cref="CaptureState"/>); this class is the part that knows where the
/// ground is and what colour to paint it.
/// </summary>
public partial class ResourceNode : Node3D
{
	/// <summary>Nodes in this group are the resource nodes. The map owns them, not the manager.</summary>
	public const string Group = "resource_node";

	/// <summary>How close a body has to be to count as standing on it.</summary>
	[Export]
	public float CaptureRadiusMeters = 10f;

	/// <summary>Seconds of uncontested presence that flip it.</summary>
	[Export]
	public float CaptureSeconds = 8f;

	/// <summary>Points paid to the strategist per payment.</summary>
	[Export]
	public int IncomePoints = 25;

	/// <summary>Seconds between payments. 25 points every 5 s is 300 a minute — six riflemen.</summary>
	[Export]
	public float IncomeIntervalSeconds = 5f;

	/// <summary>Who holds it, how far a capture has got and what it has paid. Server-authoritative.</summary>
	public CaptureState Capture { get; } = new();

	/// <summary>The engine-free view of the four numbers above, in ticks.</summary>
	public CaptureRules Rules { get; private set; }

	/// <summary>Height a body may be above or below the pad and still be standing on it.</summary>
	private const float VerticalToleranceMeters = 4f;

	private MeshInstance3D _pad;
	private StandardMaterial3D _material;
	private NodeHolder _painted = NodeHolder.Neutral;
	private bool _paintedContested;

	public override void _Ready()
	{
		AddToGroup(Group);

		Rules = new CaptureRules(
			WeaponStats.SecondsToTicks(CaptureSeconds),
			SimConfig.CaptureScanIntervalTicks,
			WeaponStats.SecondsToTicks(IncomeIntervalSeconds),
			IncomePoints);

		BuildPad();
		Repaint();
	}

	/// <summary>
	/// Whether a body at <paramref name="position"/> is standing on this node.
	///
	/// Horizontal distance with a height tolerance rather than a sphere: the pad is
	/// flat, a player crouched on it and a player standing on it are both on it, and
	/// a player on a roof four metres above it is not.
	/// </summary>
	public bool Covers(Vector3 position)
	{
		Vector3 to = position - GlobalPosition;
		if (Mathf.Abs(to.Y) > VerticalToleranceMeters)
		{
			return false;
		}

		to.Y = 0f;
		return to.LengthSquared() <= CaptureRadiusMeters * CaptureRadiusMeters;
	}

	/// <summary>Colours the pad for whoever holds it. Presentation only; called on every process.</summary>
	public void Repaint()
	{
		if (_material == null || (_painted == Capture.Owner && _paintedContested == Capture.Contested))
		{
			return;
		}

		_painted = Capture.Owner;
		_paintedContested = Capture.Contested;

		Color colour = Capture.Owner switch
		{
			NodeHolder.GroundForce => new Color(0.25f, 0.55f, 0.95f),
			NodeHolder.Strategist => new Color(0.9f, 0.3f, 0.25f),
			_ => new Color(0.75f, 0.75f, 0.75f),
		};

		// Contested is the one state worth reading across the map at a glance: it is
		// the only one that means somebody is standing there right now.
		_material.AlbedoColor = Capture.Contested ? new Color(0.95f, 0.85f, 0.2f) : colour;
	}

	/// <summary>
	/// A flat disc the size of the capture radius, built in code so that the radius
	/// and the thing drawn for it cannot disagree — the same argument
	/// <see cref="Unit"/> makes for not having a scene.
	/// </summary>
	private void BuildPad()
	{
		_material = new StandardMaterial3D
		{
			AlbedoColor = new Color(0.75f, 0.75f, 0.75f),
			// The pad is drawn flat on the ground and is lit from directly above; an
			// unshaded disc reads as a marker rather than as a piece of scenery.
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
		};

		_pad = new MeshInstance3D
		{
			Name = "Pad",
			Position = new Vector3(0f, 0.06f, 0f),
			Mesh = new CylinderMesh
			{
				TopRadius = CaptureRadiusMeters,
				BottomRadius = CaptureRadiusMeters,
				Height = 0.12f,
				RadialSegments = 24,
				Rings = 1,
			},
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
			MaterialOverride = _material,
		};

		AddChild(_pad);
	}
}
