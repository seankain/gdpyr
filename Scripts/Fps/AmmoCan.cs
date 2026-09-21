using Godot;

namespace Gdpyr.Fps;

/// <summary>
/// A can of ammunition for a heavy gun (docs/IMPLEMENTATION_PLAN.md §M5).
///
/// The other half of the emplacement loop: a deployed gun holds one belt and never
/// reloads, so keeping it firing is somebody walking back to the spawn and
/// carrying one of these out. That walk is the cost the gun is priced at, and it
/// is why cans live at the ground force's spawn rather than next to the gun.
///
/// Named on the wire by its index in the <c>ammo_can</c> group sorted by name,
/// exactly as a gun, a barracks and a resource node are.
/// </summary>
public partial class AmmoCan : Node3D
{
	/// <summary>Nodes in this group are the ammunition cans.</summary>
	public const string Group = "ammo_can";

	/// <summary>Where it rides on a carrier, relative to their feet.</summary>
	private static readonly Vector3 CarryOffset = new(0f, 1.1f, 0f);

	/// <summary>Rounds it adds to a belt. Whatever does not fit is wasted.</summary>
	[Export]
	public int Rounds = 40;

	/// <summary>The peer carrying it, or 0.</summary>
	public int CarrierPeerId { get; set; }

	/// <summary>False while it is spent and waiting to come back.</summary>
	public bool Available { get; set; } = true;

	/// <summary>Server-side: the tick a spent can reappears on.</summary>
	public uint RespawnTick { get; set; }

	/// <summary>Where it is when nobody is carrying it.</summary>
	public Vector3 RestPosition { get; set; }

	/// <summary>Where the map put it, which is where a spent one comes back.</summary>
	public Vector3 HomePosition { get; private set; }

	public bool IsCarried => CarrierPeerId != 0;

	public override void _Ready()
	{
		AddToGroup(Group);

		HomePosition = GlobalPosition;
		RestPosition = HomePosition;

		BuildBody();
	}

	/// <summary>Back to where the map put it, for the next round.</summary>
	public void ResetToHome()
	{
		CarrierPeerId = 0;
		Available = true;
		RespawnTick = 0;
		RestPosition = HomePosition;
	}

	/// <summary>Puts the node where its state says it is. Presentation only.</summary>
	public void Place(Node3D carrier)
	{
		Visible = Available;

		GlobalPosition = CarrierPeerId != 0 && carrier != null
			? carrier.GlobalPosition + CarryOffset
			: RestPosition;
	}

	private void BuildBody()
	{
		var mesh = new MeshInstance3D
		{
			Name = "Mesh",
			Position = new Vector3(0f, 0.2f, 0f),
			Mesh = new BoxMesh { Size = new Vector3(0.5f, 0.4f, 0.32f) },
			MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.35f, 0.42f, 0.2f) },
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
		};
		AddChild(mesh);
	}
}
