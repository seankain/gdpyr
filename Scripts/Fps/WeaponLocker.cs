using Godot;

namespace Gdpyr.Fps;

/// <summary>
/// A weapon locker at the ground force's spawn (docs/NETCODE.md §10.6): stand at it
/// and tap the use key to swap the large weapon in your hands for the next one it
/// holds — rifle, launcher, DMR, and round again.
///
/// It is how the ground force answers armour mid-life. A tank, a pillbox and a
/// sniper tower take nothing from a bullet, and before the locker the only way to
/// a launcher was to die and pick one; now it is a walk back to the spawn, which is
/// the same price an ammunition can is paid for at.
///
/// A marker and nothing else. It has no state — every locker holds every large
/// weapon, for everybody, always — so nothing about it is on the wire: which weapon
/// a player walked away with travels in their snapshot like the rest of their
/// weapon (<c>Sim.WeaponFlags</c>). <see cref="EmplacementManager"/> finds it by its
/// group and decides what a press at it means, because the use key is one key.
///
/// No collider, as a can has none: it is furniture to walk up to, not cover, and a
/// body on the world layer at the spawn would be one more thing for the navigation
/// mesh to be baked around.
/// </summary>
public partial class WeaponLocker : Node3D
{
	/// <summary>Nodes in this group are weapon lockers.</summary>
	public const string Group = "weapon_locker";

	private static readonly Vector3 CabinetSize = new(1.2f, 2f, 0.5f);

	public override void _Ready()
	{
		AddToGroup(Group);
		BuildBody();
	}

	private void BuildBody()
	{
		var cabinet = new MeshInstance3D
		{
			Name = "Cabinet",
			Position = new Vector3(0f, CabinetSize.Y * 0.5f, 0f),
			Mesh = new BoxMesh { Size = CabinetSize },
			MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.22f, 0.3f, 0.36f) },
		};
		AddChild(cabinet);

		// Three racks on the front, one per weapon it holds, so it reads as a locker
		// rather than as a box from across the spawn.
		for (int i = 0; i < 3; i++)
		{
			var rack = new MeshInstance3D
			{
				Name = $"Rack{i}",
				Position = new Vector3((i - 1) * 0.36f, CabinetSize.Y * 0.55f, CabinetSize.Z * 0.5f + 0.02f),
				Mesh = new BoxMesh { Size = new Vector3(0.08f, 1.2f, 0.04f) },
				MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.1f, 0.1f, 0.1f) },
				CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
			};
			AddChild(rack);
		}

		var label = new Label3D
		{
			Name = "Label",
			Text = "WEAPON LOCKER",
			Position = new Vector3(0f, CabinetSize.Y + 0.35f, 0f),
			Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
			PixelSize = 0.004f,
			OutlineSize = 8,
		};
		AddChild(label);
	}
}
