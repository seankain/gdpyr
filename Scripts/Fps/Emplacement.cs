using Gdpyr.Core;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Fps;

/// <summary>
/// A heavy gun a ground-force player picks up, carries, puts down and shoots
/// (docs/IMPLEMENTATION_PLAN.md §M5).
///
/// A map node like <see cref="Rts.Barracks"/> and <see cref="Rts.ResourceNode"/>,
/// named on the wire by its index in the <c>emplacement</c> group sorted by name —
/// an order every peer derives from the same scene, so a state message needs four
/// bytes to say which gun it is about.
///
/// It is a body, a barrel and a belt. What a press of the use key does with it is
/// <see cref="EmplacementSim"/>, which is engine-free and tested; when that
/// happens and who it happens to is <see cref="EmplacementManager"/>.
///
/// Deliberately not a collider. A gun that could be sheltered behind would have to
/// be a static body that moves, which the navigation bake and the bullet queries
/// would both have to be taught about; here it is something to stand behind and
/// shoot with, and rounds pass through it.
/// </summary>
public partial class Emplacement : Node3D
{
	/// <summary>Nodes in this group are the heavy guns. The map owns them, not the manager.</summary>
	public const string Group = "emplacement";

	/// <summary>Where the gun sits on a carrier's back, relative to their feet.</summary>
	private static readonly Vector3 CarryOffset = new(0f, 1.5f, 0f);

	/// <summary>Which catalog weapon it fires. The one weapon in the catalog with no reload.</summary>
	[Export]
	public int WeaponId = WeaponCatalog.Heavy;

	/// <summary>Where it is, and who has it. Server-authoritative; clients are told on every change.</summary>
	public EmplacementStateId State { get; set; } = EmplacementStateId.Deployed;

	/// <summary>The peer carrying it, or 0.</summary>
	public int CarrierPeerId { get; set; }

	/// <summary>The peer behind it, or 0.</summary>
	public int GunnerPeerId { get; set; }

	/// <summary>Where it was last put down, and which way it was facing when it was.</summary>
	public Vector3 DeployPosition { get; set; }

	public float DeployYaw { get; set; }

	/// <summary>Its belt. The magazine is the whole of its ammunition: there is no reload.</summary>
	public WeaponState Weapon;

	/// <summary>Where it started, so a new round puts it back.</summary>
	public Vector3 HomePosition { get; private set; }

	public float HomeYaw { get; private set; }

	public byte WeaponDefinitionId => (byte)Mathf.Clamp(WeaponId, 0, SimConfig.MaxWeaponDefinitions - 1);

	public WeaponStats Stats => WeaponCatalog.StatsFor(WeaponDefinitionId);

	public bool IsDeployed => State != EmplacementStateId.Carried;

	/// <summary>Where a round leaves from: the barrel, not the gunner's eye.</summary>
	public Vector3 MuzzlePosition => DeployPosition + (Vector3.Up * SimConfig.EmplacementMuzzleHeightMeters);

	private Node3D _yawPivot;
	private MeshInstance3D _barrel;

	public override void _Ready()
	{
		AddToGroup(Group);

		HomePosition = GlobalPosition;
		HomeYaw = GlobalBasis.GetEuler().Y;
		DeployPosition = HomePosition;
		DeployYaw = HomeYaw;

		Weapon = WeaponState.Ready(WeaponDefinitionId, Stats);

		BuildBody();
	}

	/// <summary>Back to where the map put it, with a full belt, for the next round.</summary>
	public void ResetToHome()
	{
		State = EmplacementStateId.Deployed;
		CarrierPeerId = 0;
		GunnerPeerId = 0;
		DeployPosition = HomePosition;
		DeployYaw = HomeYaw;
		Weapon = WeaponState.Ready(WeaponDefinitionId, Stats);
	}

	/// <summary>
	/// Puts the node where its state says it is. Presentation only, on the render
	/// frame: nothing about a gun's position is replicated per tick, because it is
	/// always either standing where it was put down or riding on a character whose
	/// position is already on the wire.
	/// </summary>
	public void Place(Node3D carrier, float gunnerYaw, bool hasGunner)
	{
		if (State == EmplacementStateId.Carried)
		{
			if (carrier != null)
			{
				GlobalPosition = carrier.GlobalPosition + CarryOffset;
				_yawPivot.Rotation = new Vector3(0f, carrier.GlobalBasis.GetEuler().Y, 0f);
			}
			return;
		}

		GlobalPosition = DeployPosition;

		// The barrel tracks its gunner inside the traverse arc; an unmanned gun keeps
		// pointing where it was left.
		float yaw = hasGunner
			? DeployYaw + Mathf.Clamp(Mathf.AngleDifference(DeployYaw, gunnerYaw),
				-SimConfig.EmplacementTraverseRadians, SimConfig.EmplacementTraverseRadians)
			: DeployYaw;

		_yawPivot.Rotation = new Vector3(0f, yaw, 0f);
	}

	/// <summary>
	/// A tripod and a barrel, built in code for the same reason a unit's capsule is:
	/// a .tscn would only be a second place for the muzzle height to disagree with
	/// where the shots come from.
	/// </summary>
	private void BuildBody()
	{
		var material = new StandardMaterial3D { AlbedoColor = new Color(0.22f, 0.24f, 0.28f) };

		var legs = new MeshInstance3D
		{
			Name = "Legs",
			Position = new Vector3(0f, 0.35f, 0f),
			Mesh = new BoxMesh { Size = new Vector3(1.1f, 0.7f, 1.1f) },
			MaterialOverride = material,
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
		};
		AddChild(legs);

		_yawPivot = new Node3D { Name = "Traverse", Position = new Vector3(0f, SimConfig.EmplacementMuzzleHeightMeters, 0f) };
		AddChild(_yawPivot);

		_barrel = new MeshInstance3D
		{
			Name = "Barrel",
			// -Z is forward, so the barrel reaches out along it from the pivot.
			Position = new Vector3(0f, 0f, -0.7f),
			Mesh = new BoxMesh { Size = new Vector3(0.18f, 0.18f, 1.6f) },
			MaterialOverride = material,
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
		};
		_yawPivot.AddChild(_barrel);
	}
}
