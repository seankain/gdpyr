using System;
using System.Diagnostics;
using Gdpyr.Core;
using Gdpyr.Sim.Level;
using Godot;

namespace Gdpyr.Level;

/// <summary>
/// A data map's ground (docs/LEVEL_EDITOR.md §7.1, LE-D5): one <see cref="StaticBody3D"/> on
/// layer 1 with a <see cref="ConcavePolygonShape3D"/> for each 16 × 16 cells, 225 of them,
/// made of the triangles a heightmap would make of each cell (§3); under <c>Geometry</c>,
/// so in group <c>navmesh</c>, and so the bake's source and every ray's ground.
///
/// Where this process draws, the same triangles as 225 meshes with the terrain shader
/// (§7.2), and the water's quad when any of the ground is under it. A dedicated server
/// and a headless process build neither.
///
/// It lets go of its shapes when it leaves the tree: LE0's stripped server ended in a
/// segmentation fault when that was left to .NET's finalizer as the physics server went
/// down (§7.1).
/// </summary>
public partial class TerrainNode : StaticBody3D
{
	/// <summary>Cells along a side of one shape and one mesh chunk.</summary>
	public const int ChunkCells = 16;

	public const string ShaderPath = "res://Levels/materials/terrain.gdshader";
	public const string PalettePath = "res://Levels/materials/terrain_palette.tres";

	private ConcavePolygonShape3D[] _shapes = Array.Empty<ConcavePolygonShape3D>();
	private ArrayMesh[] _meshes = Array.Empty<ArrayMesh>();
	private HeightField _field;

	public int Width => _field?.Width ?? 0;
	public int Depth => _field?.Depth ?? 0;
	public int ShapeCount => _shapes.Length;
	public int MeshChunks => _meshes.Length;

	/// <summary>How long handing the collider its triangles took [ms]: LE0's 37–48 ms.</summary>
	public double SetMilliseconds { get; private set; }

	public float MinHeight { get; private set; }
	public float MaxHeight { get; private set; }

	/// <summary>Samples under the map's water level.</summary>
	public int SubmergedSamples { get; private set; }

	public bool HasWater => SubmergedSamples > 0;

	/// <summary>The ground under (x, z) as the collider has it (§6.1's <c>Sample</c>).</summary>
	public float HeightAt(float x, float z) => _field.Sample(x, z);

	/// <summary>The height of the lowest and highest sample along a field's edge: what a perimeter wall spans.</summary>
	public void EdgeRange(int edge, out float lowest, out float highest)
	{
		lowest = float.PositiveInfinity;
		highest = float.NegativeInfinity;
		int count = edge < 2 ? Width : Depth;
		for (int i = 0; i < count; i++)
		{
			float h = edge switch
			{
				0 => _field[i, 0],
				1 => _field[i, Depth - 1],
				2 => _field[0, i],
				_ => _field[Width - 1, i],
			};
			lowest = MathF.Min(lowest, h);
			highest = MathF.Max(highest, h);
		}
	}

	/// <summary>Builds the collider, and the meshes and water when <paramref name="draws"/>. Before it enters the tree.</summary>
	public void Build(MapDocument document, bool draws)
	{
		_field = document.Terrain;
		CollisionLayer = CollisionLayers.World;
		CollisionMask = 0;

		float water = document.Bands.WaterLevelMeters;
		MinHeight = float.PositiveInfinity;
		MaxHeight = float.NegativeInfinity;
		SubmergedSamples = 0;
		foreach (float h in _field.Heights)
		{
			MinHeight = MathF.Min(MinHeight, h);
			MaxHeight = MathF.Max(MaxHeight, h);
			SubmergedSamples += h < water ? 1 : 0;
		}

		int across = (Width - 1) / ChunkCells;
		int down = (Depth - 1) / ChunkCells;
		_shapes = new ConcavePolygonShape3D[across * down];

		long before = Stopwatch.GetTimestamp();
		for (int cz = 0; cz < down; cz++)
		{
			for (int cx = 0; cx < across; cx++)
			{
				var shape = new ConcavePolygonShape3D();
				shape.SetFaces(ChunkFaces(_field, cx, cz));
				_shapes[(cz * across) + cx] = shape;
				AddChild(new CollisionShape3D { Name = $"Shape_{cx:00}_{cz:00}", Shape = shape });
			}
		}

		SetMilliseconds = (Stopwatch.GetTimestamp() - before) * 1000.0 / Stopwatch.Frequency;

		if (draws)
		{
			BuildMeshes(document, across, down);
			if (HasWater)
			{
				BuildWater(water);
			}
		}
	}

	/// <summary>
	/// A chunk's triangles, two a cell in the heightmap's order and winding (§3): (x,z)
	/// (x+1,z) (x,z+1), then (x+1,z) (x+1,z+1) (x,z+1); clockwise seen from above, so their
	/// fronts face up. LE0's collider, the one it measured.
	/// </summary>
	public static Vector3[] ChunkFaces(HeightField field, int chunkX, int chunkZ)
	{
		var faces = new Vector3[ChunkCells * ChunkCells * 6];
		int n = 0;
		for (int z = chunkZ * ChunkCells; z < (chunkZ + 1) * ChunkCells; z++)
		{
			for (int x = chunkX * ChunkCells; x < (chunkX + 1) * ChunkCells; x++)
			{
				Vector3 a = field.Position(x, z);
				Vector3 b = field.Position(x + 1, z);
				Vector3 c = field.Position(x, z + 1);
				Vector3 d = field.Position(x + 1, z + 1);
				faces[n++] = a;
				faces[n++] = b;
				faces[n++] = c;
				faces[n++] = b;
				faces[n++] = d;
				faces[n++] = c;
			}
		}

		return faces;
	}

	/// <summary>
	/// The drawn ground: one mesh a chunk on the collider's triangles, 17 × 17 vertices lit by
	/// §6.1's normals, every one with the terrain shader and the map's band numbers (§7.2).
	/// </summary>
	private void BuildMeshes(MapDocument document, int across, int down)
	{
		ShaderMaterial material = TerrainMaterial(document.Bands.Settings);
		const int Side = ChunkCells + 1;
		var indices = new int[ChunkCells * ChunkCells * 6];
		int n = 0;
		for (int z = 0; z < ChunkCells; z++)
		{
			for (int x = 0; x < ChunkCells; x++)
			{
				int a = (z * Side) + x;
				indices[n++] = a;
				indices[n++] = a + 1;
				indices[n++] = a + Side;
				indices[n++] = a + 1;
				indices[n++] = a + Side + 1;
				indices[n++] = a + Side;
			}
		}

		_meshes = new ArrayMesh[across * down];
		for (int cz = 0; cz < down; cz++)
		{
			for (int cx = 0; cx < across; cx++)
			{
				var vertices = new Vector3[Side * Side];
				var normals = new Vector3[Side * Side];
				var colors = new Color[Side * Side];
				for (int z = 0; z < Side; z++)
				{
					for (int x = 0; x < Side; x++)
					{
						int sx = (cx * ChunkCells) + x;
						int sz = (cz * ChunkCells) + z;
						vertices[(z * Side) + x] = _field.Position(sx, sz);
						normals[(z * Side) + x] = _field.Normal(sx, sz);

						// A road's coverage is LE6's; until then none.
						colors[(z * Side) + x] = new Color(0f, 0f, 0f, 1f);
					}
				}

				var arrays = new Godot.Collections.Array();
				arrays.Resize((int)Mesh.ArrayType.Max);
				arrays[(int)Mesh.ArrayType.Vertex] = vertices;
				arrays[(int)Mesh.ArrayType.Normal] = normals;
				arrays[(int)Mesh.ArrayType.Color] = colors;
				arrays[(int)Mesh.ArrayType.Index] = indices;

				var mesh = new ArrayMesh();
				mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
				mesh.SurfaceSetMaterial(0, material);
				_meshes[(cz * across) + cx] = mesh;
				AddChild(new MeshInstance3D { Name = $"Chunk_{cx:00}_{cz:00}", Mesh = mesh });
			}
		}
	}

	/// <summary>The terrain shader with the palette's textures and the map's band numbers.</summary>
	public static ShaderMaterial TerrainMaterial(in BandSettings bands)
	{
		var material = new ShaderMaterial { Shader = GD.Load<Shader>(ShaderPath) };
		if (ResourceLoader.Load(PalettePath) is TerrainPalette palette)
		{
			palette.Apply(material);
		}
		else
		{
			GD.PushWarning($"[level] no terrain palette at {PalettePath}: the ground is drawn untextured");
		}

		material.SetShaderParameter("water_level", bands.WaterLevelMeters);
		material.SetShaderParameter("slope_free", bands.SlopeFreeDegrees);
		material.SetShaderParameter("slope_gain", bands.SlopeGainMetersPerDegree);
		material.SetShaderParameter("sand_top", bands.SandTopMeters);
		material.SetShaderParameter("mud_top", bands.MudTopMeters);
		material.SetShaderParameter("grass_top", bands.GrassTopMeters);
		material.SetShaderParameter("blend", bands.BlendMeters);
		return material;
	}

	/// <summary>The water: one quad over the field at its level, drawn only (LE-D8).</summary>
	private void BuildWater(float level)
	{
		var water = new StandardMaterial3D
		{
			AlbedoColor = new Color(1f, 1f, 1f, 0.82f),
			Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
			Roughness = 0.15f,
			CullMode = BaseMaterial3D.CullModeEnum.Disabled,
		};
		if (ResourceLoader.Load(PalettePath) is TerrainPalette palette && palette.Water != null)
		{
			water.AlbedoTexture = palette.Water;
			water.Uv1Scale = new Vector3(palette.WaterScale, palette.WaterScale, 1f);
		}
		else
		{
			water.AlbedoColor = new Color(0.2f, 0.35f, 0.8f, 0.82f);
		}

		float width = Width - 1;
		float depth = Depth - 1;
		AddChild(new MeshInstance3D
		{
			Name = "Water",
			Position = new Vector3(0f, level, 0f),
			Mesh = new PlaneMesh { Size = new Vector2(width, depth), Material = water },
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
		});
	}

	public override void _ExitTree()
	{
		// Shapes and meshes let go of while the servers are still up (§7.1).
		foreach (Node child in GetChildren())
		{
			if (child is CollisionShape3D collision)
			{
				collision.Shape = null;
			}
			else if (child is MeshInstance3D drawn)
			{
				drawn.Mesh = null;
			}
		}

		foreach (ConcavePolygonShape3D shape in _shapes)
		{
			shape.Dispose();
		}

		foreach (ArrayMesh mesh in _meshes)
		{
			mesh.Dispose();
		}

		_shapes = Array.Empty<ConcavePolygonShape3D>();
		_meshes = Array.Empty<ArrayMesh>();
	}
}
