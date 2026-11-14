using Godot;

namespace Gdpyr.Level;

/// <summary>
/// The terrain's look (docs/LEVEL_EDITOR.md §7.2, LE-D22): a texture for each band and the
/// water, and how many metres one repeat of each covers. <c>Levels/materials/terrain_palette.tres</c>
/// is the one the game draws with; replacing art is replacing that resource. The
/// placeholders are <c>ThirdParty/GreyboxTextures</c>' grids: yellow sand, orange mud,
/// green grass, grey stone, and solid blue water.
/// </summary>
[GlobalClass]
public partial class TerrainPalette : Resource
{
	[Export] public Texture2D Sand { get; set; }
	[Export] public Texture2D Mud { get; set; }
	[Export] public Texture2D Grass { get; set; }
	[Export] public Texture2D Stone { get; set; }
	[Export] public Texture2D Water { get; set; }

	/// <summary>Metres one repeat of each texture covers.</summary>
	[Export] public float SandScale { get; set; } = 4f;
	[Export] public float MudScale { get; set; } = 4f;
	[Export] public float GrassScale { get; set; } = 4f;
	[Export] public float StoneScale { get; set; } = 4f;

	/// <summary>Repeats of the water's texture across the field.</summary>
	[Export] public float WaterScale { get; set; } = 60f;

	/// <summary>Hands the shader its textures and their scales.</summary>
	public void Apply(ShaderMaterial material)
	{
		material.SetShaderParameter("sand_texture", Sand);
		material.SetShaderParameter("mud_texture", Mud);
		material.SetShaderParameter("grass_texture", Grass);
		material.SetShaderParameter("stone_texture", Stone);
		material.SetShaderParameter("texture_meters", new Vector4(SandScale, MudScale, GrassScale, StoneScale));
	}
}
