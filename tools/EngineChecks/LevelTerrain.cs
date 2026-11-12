using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Godot;

namespace Gdpyr.EngineChecks;

/// <summary>
/// LE0's ground (docs/LEVEL_EDITOR.md §11): the greybox's slab replaced by terrain of
/// 241 × 241 samples 1 m apart (LE-D3), on layer 1, in group <c>navmesh</c>. A node in a copy
/// of <c>Scenes/Test.tscn</c> that <c>scripts/level-editor-checks.sh</c> edits; not part of
/// the game, and not what LE1–LE3 build — only what their numbers were measured on.
///
/// <c>LE0_COLLIDER</c> names what it is made of: <c>trimesh</c>, the plan's collider (LE-D5),
/// one <see cref="ConcavePolygonShape3D"/> for each 16 × 16 cells, 225 of them; anything
/// else, the one <see cref="HeightMapShape3D"/> the plan started from, kept to be measured
/// beside it. Both are the same triangles.
///
/// <c>LE0_TERRAIN</c> names the ground this process starts on:
/// <list type="bullet">
/// <item><c>flat</c>: every sample at the slab's top, 0.5 m.</item>
/// <item><c>hills</c>: §10's rolling hills, read from <c>res://EngineChecks/hills.i16</c>
/// (int16, 1/256 m, as <c>tools/level_editor_checks/le0_terrain.py</c> writes them), levelled
/// to 0.5 m under everything the greybox holds and along the walls by §6.3's operator with a
/// 30° skirt.</item>
/// <item><c>ramp:&lt;degrees&gt;:&lt;rise&gt;</c> and <c>ridge:&lt;degrees&gt;:&lt;rise&gt;</c>: the slab, and
/// one plane of that grade in the open ground at x 10…112, z 52…110.</item>
/// </list>
/// <see cref="Set"/> gives the collider other heights while the process runs.
///
/// <c>LE0_RIM=1</c> keeps what the greybox's slab has and a 240 m field has not: the 30 m of
/// floor outside the walls, as four boxes with their tops at 0.5 m. One scenario stands a
/// player there (<c>barracks_mortar</c>, at z = −125), so the suite is run with it; nothing
/// else is, since a data map ends at its walls.
/// </summary>
public partial class LevelTerrain : StaticBody3D
{
	public const int Size = 241;
	public const int Half = 120;

	/// <summary>The top of the slab this replaces (Scenes/Test.tscn): the height every piece of the greybox stands at.</summary>
	public const float Base = 0.5f;

	/// <summary>One step of a stored sample (LE-D3).</summary>
	public const float Step = 1f / 256f;

	/// <summary>The west edge of a ramp or a ridge, and the strip of z it fills.</summary>
	public const int RampWest = 10;
	public const int RampEast = 112;
	public const int RampSouth = 52;
	public const int RampNorth = 110;

	public static LevelTerrain Instance { get; private set; }

	/// <summary>
	/// Cells along a side of one of the triangle-mesh collider's shapes: 16, as the heightmap's
	/// own bounds chunks are, unless <c>LE0_CHUNK</c> names another that divides 240.
	/// </summary>
	public static readonly int ChunkCells = ChunkCellsAsked();

	public static readonly int ChunksAcross = (Size - 1) / ChunkCells;

	private static int ChunkCellsAsked() =>
		int.TryParse(System.Environment.GetEnvironmentVariable("LE0_CHUNK"), out int cells) && cells > 0 && (Size - 1) % cells == 0
			? cells
			: 16;

	public string Kind { get; private set; }
	public float[] Heights { get; private set; }

	/// <summary>The heightmap; null when this ground is the fallback's triangle meshes.</summary>
	public HeightMapShape3D Shape { get; private set; }

	/// <summary>LE-D5's fallback: one triangle mesh a chunk, row by row; null when this ground is the heightmap.</summary>
	public ConcavePolygonShape3D[] Chunks { get; private set; }

	public bool IsTrimesh => Chunks != null;

	public string ColliderName => IsTrimesh ? "trimesh" : "heightmap";

	/// <summary>How long the last <see cref="Set"/> spent handing the collider its data [µs].</summary>
	public long LastSetMicroseconds { get; private set; }

	/// <summary>How long reading and decoding the samples took at start [µs]; 0 for a ground that has no file.</summary>
	public long DecodeMicroseconds { get; private set; }

	/// <summary>The discs levelled under the greybox's pieces: (x, z, radius).</summary>
	public List<Vector3> Pads { get; } = new();

	public override void _EnterTree()
	{
		Instance = this;
		Kind = System.Environment.GetEnvironmentVariable("LE0_TERRAIN");
		if (string.IsNullOrEmpty(Kind))
		{
			Kind = "flat";
		}

		CollectPads();
		if (System.Environment.GetEnvironmentVariable("LE0_COLLIDER") == "trimesh")
		{
			Chunks = new ConcavePolygonShape3D[ChunksAcross * ChunksAcross];
			for (int i = 0; i < Chunks.Length; i++)
			{
				Chunks[i] = new ConcavePolygonShape3D();
				AddChild(new CollisionShape3D { Name = $"Chunk{i:000}", Shape = Chunks[i] });
			}
		}
		else
		{
			Shape = new HeightMapShape3D { MapWidth = Size, MapDepth = Size };
			AddChild(new CollisionShape3D { Name = "CollisionShape3D", Shape = Shape });
		}

		bool rim = System.Environment.GetEnvironmentVariable("LE0_RIM") == "1";
		if (rim)
		{
			// The slab is 300 m across and 1 m thick, its top at 0.5 m: its four strips outside ±120.
			(Vector3 Size, Vector3 At)[] strips =
			{
				(new Vector3(300f, 1f, 30f), new Vector3(0f, 0f, -135f)),
				(new Vector3(300f, 1f, 30f), new Vector3(0f, 0f, 135f)),
				(new Vector3(30f, 1f, 240f), new Vector3(-135f, 0f, 0f)),
				(new Vector3(30f, 1f, 240f), new Vector3(135f, 0f, 0f)),
			};
			for (int i = 0; i < strips.Length; i++)
			{
				AddChild(new CollisionShape3D
				{
					Name = $"Rim{i}",
					Shape = new BoxShape3D { Size = strips[i].Size },
					Position = strips[i].At,
				});
			}
		}

		Set(Build(Kind));
		GD.Print($"[checks] terrain: {Kind}, {ColliderName}{(IsTrimesh ? $" of {Chunks.Length} shapes" : "")}, {Size} x {Size} samples{(rim ? ", the slab's rim outside the walls" : "")}, {Pads.Count} pads,"
			+ $" set in {LastSetMicroseconds / 1000.0:0.00} ms, decoded in {DecodeMicroseconds / 1000.0:0.00} ms");
	}

	public override void _ExitTree()
	{
		if (Instance == this)
		{
			Instance = null;
		}
	}

	/// <summary>
	/// Lets go of the shapes while the engine is still up. A process that exits holding
	/// them leaves it to .NET's finalizer thread to free them as the physics server goes
	/// down, and the stripped export has been seen to end in a segmentation fault there.
	/// </summary>
	public void Release()
	{
		foreach (Node child in GetChildren())
		{
			if (child is CollisionShape3D shape)
			{
				shape.Shape = null;
				RemoveChild(shape);
				shape.Free();
			}
		}

		if (Chunks != null)
		{
			foreach (ConcavePolygonShape3D chunk in Chunks)
			{
				chunk.Dispose();
			}
		}

		Shape?.Dispose();
		Chunks = null;
		Shape = null;
		Heights = null;
		GC.Collect();
		GC.WaitForPendingFinalizers();
	}

	/// <summary>Hands the collider <paramref name="heights"/>, one float a sample, index z · 241 + x.</summary>
	public long Set(float[] heights)
	{
		Heights = heights;
		long before = Stopwatch.GetTimestamp();
		if (IsTrimesh)
		{
			for (int i = 0; i < Chunks.Length; i++)
			{
				Chunks[i].SetFaces(ChunkFaces(heights, i % ChunksAcross, i / ChunksAcross));
			}
		}
		else
		{
			Shape.MapData = heights;
		}

		LastSetMicroseconds = (Stopwatch.GetTimestamp() - before) * 1_000_000L / Stopwatch.Frequency;
		return LastSetMicroseconds;
	}

	/// <summary>The fallback's one chunk given its triangles again from <see cref="Heights"/>: what a stroke that touched it costs [µs].</summary>
	public long SetChunk(int chunkX, int chunkZ)
	{
		long before = Stopwatch.GetTimestamp();
		Chunks[(chunkZ * ChunksAcross) + chunkX].SetFaces(ChunkFaces(Heights, chunkX, chunkZ));
		return (Stopwatch.GetTimestamp() - before) * 1_000_000L / Stopwatch.Frequency;
	}

	/// <summary>A chunk's triangles, 512 of them at 16 cells a side, each cell's two in the heightmap's order and winding (§3): (x,z) (x+1,z) (x,z+1), then (x+1,z) (x+1,z+1) (x,z+1).</summary>
	public static Vector3[] ChunkFaces(float[] heights, int chunkX, int chunkZ)
	{
		var faces = new Vector3[ChunkCells * ChunkCells * 6];
		int n = 0;
		for (int z = chunkZ * ChunkCells; z < (chunkZ + 1) * ChunkCells; z++)
		{
			for (int x = chunkX * ChunkCells; x < (chunkX + 1) * ChunkCells; x++)
			{
				var a = new Vector3(x - Half, heights[(z * Size) + x], z - Half);
				var b = new Vector3(x + 1 - Half, heights[(z * Size) + x + 1], z - Half);
				var c = new Vector3(x - Half, heights[((z + 1) * Size) + x], z + 1 - Half);
				var d = new Vector3(x + 1 - Half, heights[((z + 1) * Size) + x + 1], z + 1 - Half);
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

	/// <summary>The ground a kind names; <see cref="Flat"/> for one it does not know.</summary>
	public float[] Build(string kind)
	{
		string[] parts = kind.Split(':');
		switch (parts[0])
		{
			case "hills":
				return Conformed(ReadHills());

			case "ramp":
			case "ridge":
				float degrees = float.Parse(parts[1], CultureInfo.InvariantCulture);
				float rise = parts.Length > 2 ? float.Parse(parts[2], CultureInfo.InvariantCulture) : 8f;
				return Ramp(degrees, rise, parts[0] == "ridge", out _, out _);

			default:
				return Flat();
		}
	}

	// ---- engine-free: §6.1's sampling and the grounds ------------------------------------

	public static float[] Flat()
	{
		var heights = new float[Size * Size];
		Array.Fill(heights, Base);
		return heights;
	}

	/// <summary>
	/// §6.1's <c>Sample</c>: the height of the collider's triangle under (x, z), as
	/// GodotPhysics3D splits a cell (§3) — (x,z) (x+1,z) (x,z+1) where the fractions sum
	/// to at most 1, else (x+1,z) (x+1,z+1) (x,z+1).
	/// </summary>
	public static float Sample(float[] heights, float x, float z)
	{
		float fx = Math.Clamp(x + Half, 0f, Size - 1);
		float fz = Math.Clamp(z + Half, 0f, Size - 1);
		int ix = Math.Min((int)fx, Size - 2);
		int iz = Math.Min((int)fz, Size - 2);
		float u = fx - ix;
		float v = fz - iz;
		float h00 = heights[(iz * Size) + ix];
		float h10 = heights[(iz * Size) + ix + 1];
		float h01 = heights[((iz + 1) * Size) + ix];
		float h11 = heights[((iz + 1) * Size) + ix + 1];
		return u + v <= 1f
			? h00 + (u * (h10 - h00)) + (v * (h01 - h00))
			: h11 + ((1f - u) * (h01 - h11)) + ((1f - v) * (h10 - h11));
	}

	/// <summary>The same cell split along its other diagonal: what <see cref="Sample"/> would be were §3 read wrong.</summary>
	public static float SampleOtherDiagonal(float[] heights, float x, float z)
	{
		float fx = Math.Clamp(x + Half, 0f, Size - 1);
		float fz = Math.Clamp(z + Half, 0f, Size - 1);
		int ix = Math.Min((int)fx, Size - 2);
		int iz = Math.Min((int)fz, Size - 2);
		float u = fx - ix;
		float v = fz - iz;
		float h00 = heights[(iz * Size) + ix];
		float h10 = heights[(iz * Size) + ix + 1];
		float h01 = heights[((iz + 1) * Size) + ix];
		float h11 = heights[((iz + 1) * Size) + ix + 1];
		return u >= v
			? h00 + (u * (h10 - h00)) + (v * (h11 - h10))
			: h00 + (v * (h01 - h00)) + (u * (h11 - h01));
	}

	/// <summary>The steepest face, as a grade: each cell's two triangles, the length of its gradient (§6).</summary>
	public static float SteepestFace(float[] heights, out float over45Share)
	{
		float steepest = 0f;
		int over = 0;
		for (int z = 0; z < Size - 1; z++)
		{
			for (int x = 0; x < Size - 1; x++)
			{
				float a = heights[(z * Size) + x];
				float b = heights[(z * Size) + x + 1];
				float c = heights[((z + 1) * Size) + x];
				float d = heights[((z + 1) * Size) + x + 1];
				float first = MathF.Sqrt(((b - a) * (b - a)) + ((c - a) * (c - a)));
				float second = MathF.Sqrt(((d - c) * (d - c)) + ((d - b) * (d - b)));
				steepest = MathF.Max(steepest, MathF.Max(first, second));
				over += (first > 1f ? 1 : 0) + (second > 1f ? 1 : 0);
			}
		}

		over45Share = over / (float)((Size - 1) * (Size - 1) * 2);
		return steepest;
	}

	/// <summary>
	/// The slab with one plane on it: rising east from x = 10 at a grade that is a whole
	/// number of steps a metre, so that every sample is on the plane exactly, over a whole
	/// number of metres to at least <paramref name="rise"/>; then level to x = 112, or
	/// (<paramref name="ridge"/>) straight back down at the same grade. z 52…110.
	/// </summary>
	public static float[] Ramp(float degrees, float rise, bool ridge, out float grade, out int run)
	{
		grade = MathF.Max(MathF.Round(MathF.Tan(Mathf.DegToRad(degrees)) / Step), 1f) * Step;
		run = Math.Max((int)MathF.Ceiling(rise / grade), 1);
		float[] heights = Flat();
		for (int z = RampSouth; z <= RampNorth; z++)
		{
			for (int x = RampWest; x <= RampEast; x++)
			{
				int along = ridge ? Math.Max(Math.Min(x - RampWest, (2 * run) - (x - RampWest)), 0) : Math.Min(x - RampWest, run);
				heights[((z + Half) * Size) + x + Half] = Base + (grade * along);
			}
		}

		return heights;
	}

	/// <summary>§10's rolling hills as the file holds them: int16 in 1/256 m, row by row.</summary>
	private float[] ReadHills()
	{
		long before = Stopwatch.GetTimestamp();
		byte[] bytes = FileAccess.GetFileAsBytes("res://EngineChecks/hills.i16");
		var heights = new float[Size * Size];
		if (bytes == null || bytes.Length != heights.Length * 2)
		{
			GD.PrintErr($"[checks] FAIL terrain: res://EngineChecks/hills.i16 is {bytes?.Length ?? 0} bytes, not {heights.Length * 2}");
			Array.Fill(heights, Base);
			return heights;
		}

		for (int i = 0; i < heights.Length; i++)
		{
			heights[i] = (short)(bytes[i * 2] | (bytes[(i * 2) + 1] << 8)) * Step;
		}

		DecodeMicroseconds = (Stopwatch.GetTimestamp() - before) * 1_000_000L / Stopwatch.Frequency;
		return heights;
	}

	/// <summary>
	/// §6.3 with one target: clamp(h, 0.5 − k·d, 0.5 + k·d), d the distance to the nearest
	/// pad or to the strip along the walls, k = tan 30°; then rounded to steps (LE-D3) and
	/// held to LE-D4's −16…+64 m.
	/// </summary>
	private float[] Conformed(float[] heights)
	{
		float skirt = MathF.Tan(Mathf.DegToRad(30f));
		for (int iz = 0; iz < Size; iz++)
		{
			for (int ix = 0; ix < Size; ix++)
			{
				float x = ix - Half;
				float z = iz - Half;
				float d = MathF.Max(116f - MathF.Max(MathF.Abs(x), MathF.Abs(z)), 0f);
				for (int p = 0; p < Pads.Count && d > 0f; p++)
				{
					float dx = x - Pads[p].X;
					float dz = z - Pads[p].Y;
					d = MathF.Min(d, MathF.Max(MathF.Sqrt((dx * dx) + (dz * dz)) - Pads[p].Z, 0f));
				}

				float h = Math.Clamp(heights[(iz * Size) + ix], Base - (skirt * d), Base + (skirt * d));
				heights[(iz * Size) + ix] = Math.Clamp(MathF.Round(h / Step) * Step, -16f, 64f);
			}
		}

		return heights;
	}

	// ---- the greybox's pieces, as discs to level under --------------------------------------

	private void CollectPads()
	{
		Pads.Clear();

		// Open floor the greybox leaves free: where the checks put a structure and a helicopter.
		Pad(new Vector3(40f, 0f, 30f), 10f);
		Node root = GetParent();
		if (root == null)
		{
			return;
		}

		foreach (Node child in root.GetChildren())
		{
			if (child == this || child is not Node3D piece)
			{
				continue;
			}

			Transform3D at = piece.Transform;
			if (piece is CsgCombiner3D)
			{
				foreach (Node box in piece.GetChildren())
				{
					if (box is CsgBox3D csg && !csg.Name.ToString().StartsWith("Wall"))
					{
						Pad(at * csg.Transform.Origin, (0.5f * new Vector2(csg.Size.X, csg.Size.Z).Length()) + 1f);
					}
				}
			}
			else if (piece.IsInGroup("barracks"))
			{
				Pad(at.Origin, 16f);
			}
			else if (piece.IsInGroup("resource_node"))
			{
				Pad(at.Origin, 13f);
			}
			else if (piece.IsInGroup("vehicle_kiosk"))
			{
				Pad(at.Origin, 3f);
				foreach (Node pad in piece.GetChildren())
				{
					if (pad is Marker3D marker)
					{
						Pad(at * marker.Transform.Origin, marker.Name.ToString().Contains("Helicopter") ? 9f : 6f);
					}
				}
			}
			else if (piece.IsInGroup("emplacement") || piece.IsInGroup("ammo_can") || piece.IsInGroup("weapon_locker"))
			{
				Pad(at.Origin, 2.5f);
			}
			else if (piece.Name == "PlayerSpawns")
			{
				foreach (Node spawn in piece.GetChildren())
				{
					if (spawn is Marker3D marker)
					{
						Pad(at * marker.Transform.Origin, 3f);
					}
				}
			}
			else if (piece.Name == "Pillbox")
			{
				Pad(at.Origin, 23f);
			}
			else if (piece.Name == "VehicleTestStrip")
			{
				for (float along = 0f; along <= 30f; along += 5f)
				{
					Pad(at * new Vector3(along, 0f, 0f), 7f);
				}
			}
			else if (piece is StaticBody3D)
			{
				Pad(at.Origin, piece.Name == "Cube2" ? 8.5f : 2.5f);
			}
		}
	}

	private void Pad(Vector3 at, float radius) => Pads.Add(new Vector3(at.X, at.Z, radius));
}
