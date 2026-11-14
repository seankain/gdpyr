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
///
/// In the map editor the ground changes under it (§8.3, LE4): a dab draws again the mesh
/// chunks its rectangle reaches, and one sample further for their normals
/// (<see cref="RefreshMeshes"/>); a stroke's end gives the collider's chunks it touched their
/// triangles again (<see cref="RefreshColliders"/>); the water and the band numbers follow
/// the sliders (<see cref="SetBands"/>). It keeps each chunk's vertices as it last drew
/// them, which is what an engine check holds the drawing to (<see cref="ChunkVertices"/>).
/// </summary>
public partial class TerrainNode : StaticBody3D
{
	/// <summary>Cells along a side of one shape and one mesh chunk.</summary>
	public const int ChunkCells = 16;

	public const string ShaderPath = "res://Levels/materials/terrain.gdshader";
	public const string PalettePath = "res://Levels/materials/terrain_palette.tres";

	private ConcavePolygonShape3D[] _shapes = Array.Empty<ConcavePolygonShape3D>();
	private ArrayMesh[] _meshes = Array.Empty<ArrayMesh>();
	private Vector3[][] _vertices = Array.Empty<Vector3[]>();
	private int[] _indices;
	private ShaderMaterial _material;
	private MeshInstance3D _water;
	private HeightField _field;
	private float _waterLevel;
	private bool _draws;
	private bool _buildable;

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

	/// <summary>Chunks along X, and along Z.</summary>
	public int ChunksAcross => (Width - 1) / ChunkCells;

	public int ChunksDown => (Depth - 1) / ChunkCells;

	/// <summary>How many times a mesh chunk has been drawn again since it was built: the editor's (LE4).</summary>
	public int MeshesRedrawn { get; private set; }

	/// <summary>How many times a collider chunk has been given its triangles again since it was built.</summary>
	public int CollidersReset { get; private set; }

	/// <summary>How long the last <see cref="RefreshColliders"/> took [ms].</summary>
	public double LastColliderMilliseconds { get; private set; }

	/// <summary>
	/// Whether each vertex carries LE-D21's answer for a structure there, in its colour's green,
	/// for the editor's <i>buildable</i> overlay (§8.3, LE5): set by the editor, and every chunk
	/// is drawn again. The rule reads the ground up to 2.6 m off, so a change draws again three
	/// samples further while it is on. Off in a round, which draws no overlay.
	/// </summary>
	public bool Buildable
	{
		get => _buildable;
		set
		{
			if (_buildable == value)
			{
				return;
			}

			_buildable = value;
			if (_field != null)
			{
				RefreshMeshes(_field.Bounds);
			}
		}
	}

	/// <summary>How many samples past a change the buildable answer can change: the rule's reach, 2.55 m, rounded up.</summary>
	public const int BuildableReach = 3;

	/// <summary>The water's quad is drawn: there is a quad, and ground under it.</summary>
	public bool WaterDrawn => _water != null && _water.Visible;

	/// <summary>A drawn chunk's vertices as it last drew them, 17 × 17 row by row; null where nothing is drawn.</summary>
	public Vector3[] ChunkVertices(int chunkX, int chunkZ) =>
		_draws && chunkX >= 0 && chunkX < ChunksAcross && chunkZ >= 0 && chunkZ < ChunksDown
			? _vertices[(chunkZ * ChunksAcross) + chunkX]
			: null;

	/// <summary>A collider chunk's triangles as the physics server has them.</summary>
	public Vector3[] ColliderFaces(int chunkX, int chunkZ) => _shapes[(chunkZ * ChunksAcross) + chunkX].GetFaces();

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
		_draws = draws;
		CollisionLayer = CollisionLayers.World;
		CollisionMask = 0;

		float water = document.Bands.WaterLevelMeters;
		_waterLevel = water;
		Measure();

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

	/// <summary>The lowest and highest sample, and how many are under the water.</summary>
	private void Measure()
	{
		MinHeight = float.PositiveInfinity;
		MaxHeight = float.NegativeInfinity;
		int submerged = 0;
		foreach (float h in _field.Heights)
		{
			MinHeight = MathF.Min(MinHeight, h);
			MaxHeight = MathF.Max(MaxHeight, h);
			submerged += h < _waterLevel ? 1 : 0;
		}

		SubmergedSamples = submerged;
	}

	/// <summary>The chunks whose cells hold a sample of <paramref name="samples"/>: inclusive ranges, empty when none.</summary>
	public void ChunksOf(in SampleRect samples, out int minX, out int minZ, out int maxX, out int maxZ)
	{
		SampleRect inside = samples.Intersect(_field.Bounds);
		if (inside.IsEmpty)
		{
			minX = minZ = 0;
			maxX = maxZ = -1;
			return;
		}

		// A sample is a corner of the cells either side of it: the chunk before it too when it is on a chunk's edge.
		minX = Math.Clamp((inside.MinX - 1) / ChunkCells, 0, ChunksAcross - 1);
		minZ = Math.Clamp((inside.MinZ - 1) / ChunkCells, 0, ChunksDown - 1);
		maxX = Math.Clamp(inside.MaxX / ChunkCells, 0, ChunksAcross - 1);
		maxZ = Math.Clamp(inside.MaxZ / ChunkCells, 0, ChunksDown - 1);
		if (inside.MinX == 0)
		{
			minX = 0;
		}

		if (inside.MinZ == 0)
		{
			minZ = 0;
		}
	}

	/// <summary>
	/// Draws again the chunks that a change to <paramref name="samples"/> reaches: those with a
	/// vertex in it or next to it, since a vertex's normal reads the samples either side
	/// (§8.3). Returns how many it drew. Nothing where nothing is drawn.
	/// </summary>
	public int RefreshMeshes(in SampleRect samples)
	{
		if (!_draws || samples.IsEmpty)
		{
			return 0;
		}

		ChunksOf(samples.Grown(1 + (_buildable ? BuildableReach : 0)), out int minX, out int minZ, out int maxX, out int maxZ);
		int drawn = 0;
		for (int cz = minZ; cz <= maxZ; cz++)
		{
			for (int cx = minX; cx <= maxX; cx++)
			{
				DrawChunk(cx, cz);
				drawn++;
			}
		}

		MeshesRedrawn += drawn;
		return drawn;
	}

	/// <summary>
	/// Gives the collider's chunks with a cell on a sample of <paramref name="samples"/> their
	/// triangles again: what ends a stroke (§8.3), 0.2–0.3 ms a chunk by LE0. Returns how many.
	/// </summary>
	public int RefreshColliders(in SampleRect samples)
	{
		if (samples.IsEmpty)
		{
			return 0;
		}

		long before = Stopwatch.GetTimestamp();
		ChunksOf(samples, out int minX, out int minZ, out int maxX, out int maxZ);
		int reset = 0;
		for (int cz = minZ; cz <= maxZ; cz++)
		{
			for (int cx = minX; cx <= maxX; cx++)
			{
				_shapes[(cz * ChunksAcross) + cx].SetFaces(ChunkFaces(_field, cx, cz));
				reset++;
			}
		}

		LastColliderMilliseconds = (Stopwatch.GetTimestamp() - before) * 1000.0 / Stopwatch.Frequency;
		CollidersReset += reset;
		return reset;
	}

	/// <summary>
	/// The map's water and band numbers, as the editor's sliders set them (§8.3): the shader's
	/// uniforms, and the water's quad at its level, drawn when any ground is under it.
	/// </summary>
	public void SetBands(in BandSettings bands)
	{
		_waterLevel = bands.WaterLevelMeters;
		if (_material != null)
		{
			ApplyBands(_material, bands);
		}

		RefreshWater();
	}

	/// <summary>Counts the ground under the water again, and shows the water's quad where it has any: after an edit.</summary>
	public void RefreshWater()
	{
		Measure();
		if (!_draws)
		{
			return;
		}

		if (HasWater && _water == null)
		{
			BuildWater(_waterLevel);
		}

		if (_water != null)
		{
			_water.Visible = HasWater;
			_water.Position = new Vector3(0f, _waterLevel, 0f);
		}
	}

	/// <summary>The editor's overlay (§8.3): the terrain shader's <c>overlay</c>.</summary>
	public void SetOverlay(int overlay) => _material?.SetShaderParameter("overlay", overlay);

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
		_material = TerrainMaterial(document.Bands.Settings);
		const int Side = ChunkCells + 1;
		_indices = new int[ChunkCells * ChunkCells * 6];
		int n = 0;
		for (int z = 0; z < ChunkCells; z++)
		{
			for (int x = 0; x < ChunkCells; x++)
			{
				int a = (z * Side) + x;
				_indices[n++] = a;
				_indices[n++] = a + 1;
				_indices[n++] = a + Side;
				_indices[n++] = a + 1;
				_indices[n++] = a + Side + 1;
				_indices[n++] = a + Side;
			}
		}

		_meshes = new ArrayMesh[across * down];
		_vertices = new Vector3[across * down][];
		for (int cz = 0; cz < down; cz++)
		{
			for (int cx = 0; cx < across; cx++)
			{
				_meshes[(cz * across) + cx] = new ArrayMesh();
				DrawChunk(cx, cz);
				AddChild(new MeshInstance3D { Name = $"Chunk_{cx:00}_{cz:00}", Mesh = _meshes[(cz * across) + cx] });
			}
		}
	}

	/// <summary>One chunk's mesh, from the samples as they are now.</summary>
	private void DrawChunk(int cx, int cz)
	{
		const int Side = ChunkCells + 1;
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

				// A road's coverage is LE6's; until then none. Green is buildable, for the editor's overlay.
				bool buildable = _buildable && BuildableGround.At(_field, _field.WorldX(sx), _field.WorldZ(sz));
				colors[(z * Side) + x] = new Color(0f, buildable ? 1f : 0f, 0f, 1f);
			}
		}

		var arrays = new Godot.Collections.Array();
		arrays.Resize((int)Mesh.ArrayType.Max);
		arrays[(int)Mesh.ArrayType.Vertex] = vertices;
		arrays[(int)Mesh.ArrayType.Normal] = normals;
		arrays[(int)Mesh.ArrayType.Color] = colors;
		arrays[(int)Mesh.ArrayType.Index] = _indices;

		int index = (cz * ChunksAcross) + cx;
		ArrayMesh mesh = _meshes[index];
		mesh.ClearSurfaces();
		mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
		mesh.SurfaceSetMaterial(0, _material);
		_vertices[index] = vertices;
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

		ApplyBands(material, bands);
		return material;
	}

	/// <summary>The map's seven numbers, as the shader's uniforms.</summary>
	private static void ApplyBands(ShaderMaterial material, in BandSettings bands)
	{
		material.SetShaderParameter("water_level", bands.WaterLevelMeters);
		material.SetShaderParameter("slope_free", bands.SlopeFreeDegrees);
		material.SetShaderParameter("slope_gain", bands.SlopeGainMetersPerDegree);
		material.SetShaderParameter("sand_top", bands.SandTopMeters);
		material.SetShaderParameter("mud_top", bands.MudTopMeters);
		material.SetShaderParameter("grass_top", bands.GrassTopMeters);
		material.SetShaderParameter("blend", bands.BlendMeters);
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
		_water = new MeshInstance3D
		{
			Name = "Water",
			Position = new Vector3(0f, level, 0f),
			Mesh = new PlaneMesh { Size = new Vector2(width, depth), Material = water },
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
		};
		AddChild(_water);
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
		_vertices = Array.Empty<Vector3[]>();
		_water = null;
	}
}
