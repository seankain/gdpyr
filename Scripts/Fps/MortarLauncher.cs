using Gdpyr.Core;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Fps;

/// <summary>
/// A mortar launcher a ground-force player picks up, carries, puts down and fires, as
/// they do the heavy gun (<see cref="Emplacement"/>, docs/IMPLEMENTATION_PLAN.md §M5).
///
/// Everything a heavy gun is but three things:
///
/// <list type="bullet">
/// <item>It is laid, not pointed: the round comes down where its gunner's look meets the
/// ground it stands on, on the high arc, inside the same traverse arc and between
/// <see cref="MinRangeMeters"/> and <see cref="MaxRangeMeters"/> (<see cref="MortarLay"/>).
/// Its gunner's client draws a marker where that is.</item>
/// <item>It holds five rounds and never reloads; what fills it is an ammunition can on the
/// ground within <see cref="SimConfig.MortarFeedReachMeters"/> of it, five rounds a can,
/// taken once the tube is empty (<see cref="EmplacementSim.FeedsFrom"/>,
/// <see cref="EmplacementSim.TakesCan"/>). A can carried to it and put down with the use
/// key is set down beside it, inside that metre (<see cref="EmplacementSim.StackPoint"/>).</item>
/// <item>Its shell is the barracks mortar's 81 mm with a heavier direct hit: three
/// destroy a tank, two do not (Weapons/projectiles/mortar_81_launcher.tres).</item>
/// </list>
///
/// It is in the <c>emplacement</c> group, so it is named on the wire by the same index a
/// heavy gun is and goes through every transition <see cref="EmplacementManager"/> already
/// replicates; nothing about it adds a message.
/// </summary>
public partial class MortarLauncher : Emplacement
{
	/// <summary>The tube's elevation while nobody is laying it.</summary>
	private static readonly float RestPitch = Mathf.DegToRad(60f);

	private float _minRange;
	private float _maxRange;
	private bool _limitsKnown;

	private Node3D _traverse;
	private Node3D _elevation;
	private MeshInstance3D _marker;

	// The last lay drawn, so a gunner holding still does not cost a solve a frame.
	private Vector3 _drawnEye;
	private float _drawnYaw = float.NaN;
	private float _drawnPitch = float.NaN;
	private Vector3 _drawnTarget;
	private Vector3 _drawnDirection;

	public MortarLauncher()
	{
		WeaponId = WeaponCatalog.MortarLauncher;
	}

	public override float MuzzleHeightMeters => SimConfig.MortarMuzzleHeightMeters;

	public override bool FedFromGround => true;

	public override int RoundsPerCan(AmmoCan can) => SimConfig.MortarRoundsPerCan;

	public override string Noun => "mortar launcher";

	/// <summary>The round its tube fires.</summary>
	public ProjectileStats Round => WeaponCatalog.RoundFor(WeaponDefinitionId);

	/// <summary>The nearest it lays a round along level ground [m].</summary>
	public float MinRangeMeters
	{
		get
		{
			EnsureLimits();
			return _minRange;
		}
	}

	/// <summary>The furthest it lays a round along level ground [m].</summary>
	public float MaxRangeMeters
	{
		get
		{
			EnsureLimits();
			return _maxRange;
		}
	}

	public override void _Ready()
	{
		base._Ready();

		// The traverse is drawn from the deploy yaw it is given, not from the node's own
		// turn, which was read into HomeYaw above.
		Rotation = Vector3.Zero;
	}

	/// <summary>Where a gunner whose eye is at <paramref name="eye"/>, looking at <paramref name="yaw"/>, <paramref name="pitch"/>, lays a round.</summary>
	public Vector3 TargetFor(Vector3 eye, float yaw, float pitch) =>
		MortarLay.Target(eye, yaw, pitch, DeployPosition, DeployYaw, MinRangeMeters, MaxRangeMeters);

	/// <summary>The high arc that brings a round down on <see cref="TargetFor"/>.</summary>
	public override Vector3 AimFor(Vector3 eye, float yaw, float pitch) =>
		MortarLay.Direction(MuzzlePosition, TargetFor(eye, yaw, pitch), Round);

	public override void Place(Node3D carrier, fps_controller gunner, bool localGunner)
	{
		if (State == EmplacementStateId.Carried)
		{
			_marker.Visible = false;
			if (carrier != null)
			{
				GlobalPosition = carrier.GlobalPosition + CarryOffset;
				_traverse.Rotation = new Vector3(0f, carrier.GlobalBasis.GetEuler().Y, 0f);
				_elevation.Rotation = new Vector3(RestPitch, 0f, 0f);
			}
			return;
		}

		GlobalPosition = DeployPosition;

		if (gunner == null)
		{
			_marker.Visible = false;
			_traverse.Rotation = new Vector3(0f, DeployYaw, 0f);
			_elevation.Rotation = new Vector3(RestPitch, 0f, 0f);
			return;
		}

		Lay(gunner.EyePosition, gunner.Yaw, gunner.Pitch);

		Aim.Angles(_drawnDirection, out float yaw, out float pitch);
		_traverse.Rotation = new Vector3(0f, yaw, 0f);
		_elevation.Rotation = new Vector3(pitch, 0f, 0f);

		// Where it will come down, for the one person laying it.
		_marker.Visible = localGunner;
		_marker.GlobalPosition = _drawnTarget + (Vector3.Up * 0.05f);
	}

	private void Lay(Vector3 eye, float yaw, float pitch)
	{
		if (eye.IsEqualApprox(_drawnEye) && yaw == _drawnYaw && pitch == _drawnPitch)
		{
			return;
		}

		_drawnEye = eye;
		_drawnYaw = yaw;
		_drawnPitch = pitch;
		_drawnTarget = TargetFor(eye, yaw, pitch);
		_drawnDirection = MortarLay.Direction(MuzzlePosition, _drawnTarget, Round);
	}

	private void EnsureLimits()
	{
		if (_limitsKnown)
		{
			return;
		}

		_limitsKnown = true;
		if (!MortarLay.TryRangeLimits(Round, MuzzleHeightMeters, out _minRange, out _maxRange))
		{
			GD.PushError($"[emplacements] mortar launcher {Name}: weapon {WeaponDefinitionId}'s round cannot be laid");
		}
	}

	/// <summary>
	/// A baseplate, a bipod and a tube, built in code as the heavy gun's tripod is; and the
	/// marker its gunner sees, which stands on its own in the world rather than turning
	/// with the tube.
	/// </summary>
	protected override void BuildBody()
	{
		var material = new StandardMaterial3D { AlbedoColor = new Color(0.26f, 0.29f, 0.22f) };

		AddChild(new MeshInstance3D
		{
			Name = "Baseplate",
			Position = new Vector3(0f, 0.04f, 0f),
			Mesh = new BoxMesh { Size = new Vector3(0.7f, 0.08f, 0.7f) },
			MaterialOverride = material,
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
		});

		_traverse = new Node3D { Name = "Traverse", Position = new Vector3(0f, 0.12f, 0f) };
		AddChild(_traverse);

		_traverse.AddChild(new MeshInstance3D
		{
			Name = "Bipod",
			Position = new Vector3(0f, 0.25f, -0.45f),
			Mesh = new BoxMesh { Size = new Vector3(0.6f, 0.5f, 0.05f) },
			MaterialOverride = material,
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
		});

		_elevation = new Node3D { Name = "Elevation", Rotation = new Vector3(RestPitch, 0f, 0f) };
		_traverse.AddChild(_elevation);

		// -Z is forward, and a positive turn about X lifts it: the tube runs out along the lay.
		_elevation.AddChild(new MeshInstance3D
		{
			Name = "Tube",
			Position = new Vector3(0f, 0f, -0.4f),
			Mesh = new BoxMesh { Size = new Vector3(0.14f, 0.14f, 0.9f) },
			MaterialOverride = material,
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
		});

		_marker = new MeshInstance3D
		{
			Name = "LayMarker",
			TopLevel = true,
			Visible = false,
			Mesh = new TorusMesh { InnerRadius = 1.1f, OuterRadius = 1.3f, Rings = 24, RingSegments = 6 },
			MaterialOverride = new StandardMaterial3D
			{
				AlbedoColor = new Color(1f, 0.35f, 0.2f, 0.85f),
				ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
				Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
				NoDepthTest = true,
			},
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
		};
		AddChild(_marker);
	}
}
