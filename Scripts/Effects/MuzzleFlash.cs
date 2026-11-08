using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Effects;

/// <summary>
/// One muzzle flash as drawn (docs/MUZZLE_EFFECTS.md §3): two quads crossed along the
/// barrel for the flame seen from the side, a star facing the camera for the flame seen
/// end on, and a light thrown on what is round the muzzle, for the two or three frames of
/// <see cref="MuzzleProfile.FlashSeconds"/>. Pooled by <see cref="MuzzleEffects"/>, and
/// built once: playing one allocates nothing.
///
/// A flash follows the node it was fired from for its few frames — the viewmodel's
/// barrel for the local player, the body for anyone else — so a fast turn does not leave
/// it hanging in the air behind the gun.
/// </summary>
public partial class MuzzleFlash : Node3D
{
	private MeshInstance3D _star;
	private MeshInstance3D _side;
	private MeshInstance3D _cross;
	private StandardMaterial3D _starMaterial;
	private StandardMaterial3D _sideMaterial;
	private OmniLight3D _light;

	private Node3D _anchor;
	private Transform3D _local;
	private MuzzleProfile _profile;
	private float _size;
	private float _age;
	private bool _built;

	/// <summary>The node it follows, if any; for the engine check.</summary>
	public Node3D Anchor => _anchor;

	/// <summary>Builds the meshes and the light; <paramref name="flame"/> and <paramref name="star"/> are shared by every flash.</summary>
	public void Build(Texture2D flame, Texture2D star)
	{
		if (_built)
		{
			return;
		}

		_built = true;
		var quad = new QuadMesh { Size = Vector2.One };

		_sideMaterial = Glow(flame, billboard: false);
		_starMaterial = Glow(star, billboard: true);

		_side = Part("Side", quad, _sideMaterial);
		_cross = Part("Cross", quad, _sideMaterial);
		_star = Part("Star", quad, _starMaterial);

		_light = new OmniLight3D
		{
			Name = "Light",
			ShadowEnabled = false,
			LightColor = new Color(1f, 0.7f, 0.35f),
			Visible = false,
		};
		AddChild(_light);
		Visible = false;
	}

	/// <summary>
	/// Starts a flash of <paramref name="profile"/> at <paramref name="muzzle"/> along
	/// <paramref name="forward"/>, following <paramref name="anchor"/> when there is one;
	/// <paramref name="size"/> varies it shot to shot.
	/// </summary>
	public void Play(in MuzzleProfile profile, Vector3 muzzle, Vector3 forward, Node3D anchor, float size)
	{
		_profile = profile;
		_size = size > 0f ? size : 1f;
		_age = 0f;

		Muzzles.Frame(forward, out Vector3 f, out Vector3 right, out Vector3 up);
		var world = new Transform3D(new Basis(right, up, -f), muzzle);
		_anchor = anchor != null && IsInstanceValid(anchor) && anchor.IsInsideTree() ? anchor : null;
		_local = _anchor != null ? _anchor.GlobalTransform.AffineInverse() * world : world;

		_sideMaterial.AlbedoColor = profile.FlashColor;
		_starMaterial.AlbedoColor = profile.FlashColor;
		_light.LightColor = profile.FlashColor;
		_light.OmniRange = Mathf.Max(profile.LightRange, 0.1f);

		Visible = true;
		Advance(0f);
	}

	/// <summary>Moves it on by <paramref name="delta"/> seconds; false once it has gone out and hidden itself.</summary>
	public bool Advance(float delta)
	{
		_age += delta;
		if (_age >= _profile.FlashSeconds)
		{
			Stop();
			return false;
		}

		if (_anchor != null && !(IsInstanceValid(_anchor) && _anchor.IsInsideTree()))
		{
			// What fired it is gone — a body freed mid-flash. Where it was is good enough for what is left.
			_local = GlobalTransform;
			_anchor = null;
		}

		GlobalTransform = _anchor != null ? _anchor.GlobalTransform * _local : _local;

		float scale = Muzzles.FlashScale(_age, _profile.FlashSeconds) * _size;
		float length = _profile.FlashLength * scale;
		float width = _profile.FlashWidth * scale;

		// The flame from the muzzle forward, in the flash's frame: −Z is down the barrel.
		var middle = new Vector3(0f, 0f, -length * 0.5f);
		_side.Transform = new Transform3D(new Basis(Vector3.Forward * length, Vector3.Up * width, Vector3.Right), middle);
		_cross.Transform = new Transform3D(new Basis(Vector3.Forward * length, Vector3.Right * width, Vector3.Down), middle);

		// The star a little out of the muzzle, as wide as the flame is long seen end on.
		float star = Mathf.Max(width * 1.6f, length * 0.6f);
		_star.Transform = new Transform3D(Basis.FromScale(Vector3.One * star), new Vector3(0f, 0f, -length * 0.15f));

		float energy = _profile.LightEnergy * Muzzles.FlashLight(_age, _profile.FlashSeconds);
		_light.Visible = energy > 0.01f;
		_light.LightEnergy = energy;
		_light.Position = new Vector3(0f, 0f, -length * 0.3f);
		return true;
	}

	/// <summary>Hides it at once.</summary>
	public void Stop()
	{
		Visible = false;
		_light.Visible = false;
		_anchor = null;
	}

	private MeshInstance3D Part(string name, Mesh mesh, Material material)
	{
		var part = new MeshInstance3D
		{
			Name = name,
			Mesh = mesh,
			MaterialOverride = material,
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
		};

		AddChild(part);
		return part;
	}

	/// <summary>
	/// Unlit, seen from both sides, the star always facing the camera. Blended over what is
	/// behind it rather than added to it: without bloom an added flash all but vanishes
	/// against a sunlit floor or sky, and the texture's own white-hot core is what reads.
	/// </summary>
	private static StandardMaterial3D Glow(Texture2D texture, bool billboard) => new()
	{
		ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
		Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
		CullMode = BaseMaterial3D.CullModeEnum.Disabled,
		AlbedoTexture = texture,
		BillboardMode = billboard ? BaseMaterial3D.BillboardModeEnum.Enabled : BaseMaterial3D.BillboardModeEnum.Disabled,
		BillboardKeepScale = billboard,
		NoDepthTest = false,
	};
}
