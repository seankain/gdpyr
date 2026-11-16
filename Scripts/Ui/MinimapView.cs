using System;
using Gdpyr.Sim.Level;
using Godot;

namespace Gdpyr.Ui;

/// <summary>
/// The engine-free half of the strategist's map (<see cref="Minimap"/>): which square of the
/// field it shows, where a point of the world is on it and which point of the world a pixel
/// on it is, and how it zooms.
///
/// The map is the field seen from straight above with north up: world −Z is the top of the
/// map and +X its right, which is what the strategist camera shows looking straight down
/// at a yaw of zero. World points are taken as <c>(x, z)</c>.
///
/// Zoomed out it shows the whole field, ±<see cref="MapLimits.FieldHalfMeters"/> on X and Z
/// (docs/MAPS.md §7, docs/LEVEL_EDITOR.md §4). Zoomed in it shows a smaller square, never
/// any of the outside of the field: a zoom anchored at the cursor keeps the point under the
/// cursor where it was, unless that would show past the edge, and then the square is held
/// against the edge instead.
/// </summary>
public sealed class MinimapView
{
	/// <summary>The zooms the map steps through: the whole field, then a half, then a quarter of it across.</summary>
	public static readonly float[] Zooms = { 1f, 2f, 4f };

	/// <summary>
	/// The share of the shown square, at each side, that <see cref="Follow"/> leaves between a
	/// point it brings back into view and the edge.
	/// </summary>
	public const float FollowMargin = 0.15f;

	public MinimapView(float fieldHalfMeters = MapLimits.FieldHalfMeters)
	{
		FieldHalf = fieldHalfMeters;
	}

	/// <summary>Metres from the middle of the field to its edge, on X and on Z.</summary>
	public float FieldHalf { get; }

	/// <summary>Which of <see cref="Zooms"/> the map is at.</summary>
	public int ZoomIndex { get; private set; }

	public float Zoom => Zooms[ZoomIndex];

	/// <summary>The world point, <c>(x, z)</c>, at the middle of the map.</summary>
	public Vector2 Center { get; private set; }

	/// <summary>Metres from the middle of the shown square to its edge.</summary>
	public float HalfSpan => FieldHalf / Zoom;

	/// <summary>Pixels a metre is on a map <paramref name="size"/> pixels across.</summary>
	public float PixelsPerMeter(Vector2 size) => size.X / (2f * HalfSpan);

	/// <summary>Where the world point <paramref name="world"/> is on a map of <paramref name="size"/> pixels, from its top-left corner.</summary>
	public Vector2 ToMap(Vector2 world, Vector2 size)
	{
		float span = 2f * HalfSpan;
		return new Vector2(
			(world.X - Center.X + HalfSpan) / span * size.X,
			(world.Y - Center.Y + HalfSpan) / span * size.Y);
	}

	/// <summary>The world point, <c>(x, z)</c>, at <paramref name="map"/> pixels from the top-left corner of a map of <paramref name="size"/>.</summary>
	public Vector2 ToWorld(Vector2 map, Vector2 size)
	{
		float span = 2f * HalfSpan;
		return new Vector2(
			Center.X - HalfSpan + (map.X / size.X * span),
			Center.Y - HalfSpan + (map.Y / size.Y * span));
	}

	/// <summary><paramref name="world"/>, brought inside the field.</summary>
	public Vector2 ClampToField(Vector2 world) =>
		new(Math.Clamp(world.X, -FieldHalf, FieldHalf), Math.Clamp(world.Y, -FieldHalf, FieldHalf));

	/// <summary>Whether <paramref name="world"/> is inside the square the map shows now.</summary>
	public bool Shows(Vector2 world) =>
		MathF.Abs(world.X - Center.X) <= HalfSpan && MathF.Abs(world.Y - Center.Y) <= HalfSpan;

	/// <summary>
	/// Zooms by <paramref name="steps"/> of <see cref="Zooms"/> — in when positive — keeping
	/// the world point under <paramref name="map"/> where it is, as far as the field allows.
	/// False when the map was already as far in or out as it goes.
	/// </summary>
	public bool ZoomBy(int steps, Vector2 map, Vector2 size)
	{
		int index = Math.Clamp(ZoomIndex + steps, 0, Zooms.Length - 1);
		if (index == ZoomIndex)
		{
			return false;
		}

		SetZoom(index, map, size);
		return true;
	}

	/// <summary>
	/// The zoom input over the map: one step further in, and from all the way in back out to
	/// the whole field — one button that goes both ways.
	/// </summary>
	public void CycleZoom(Vector2 map, Vector2 size) =>
		SetZoom(ZoomIndex + 1 < Zooms.Length ? ZoomIndex + 1 : 0, map, size);

	/// <summary>
	/// Brings <paramref name="world"/> back into view when it has left the shown square: moves
	/// the square the least that puts it <see cref="FollowMargin"/> inside the edge, held to the
	/// field. Nothing while it is in view. True when the square moved.
	/// </summary>
	public bool Follow(Vector2 world)
	{
		if (Shows(world))
		{
			return false;
		}

		float inner = HalfSpan * (1f - (2f * FollowMargin));
		var center = new Vector2(
			Math.Clamp(Center.X, world.X - inner, world.X + inner),
			Math.Clamp(Center.Y, world.Y - inner, world.Y + inner));

		Vector2 before = Center;
		Center = HeldToField(center);
		return Center != before;
	}

	private void SetZoom(int index, Vector2 map, Vector2 size)
	{
		Vector2 anchor = ToWorld(map, size);
		ZoomIndex = index;

		// The anchor is where it was on the map: the same share of the way across.
		float span = 2f * HalfSpan;
		Center = HeldToField(new Vector2(
			anchor.X + HalfSpan - (map.X / size.X * span),
			anchor.Y + HalfSpan - (map.Y / size.Y * span)));
	}

	/// <summary>A middle for the shown square that keeps all of it inside the field.</summary>
	private Vector2 HeldToField(Vector2 center)
	{
		float limit = MathF.Max(0f, FieldHalf - HalfSpan);
		return new Vector2(Math.Clamp(center.X, -limit, limit), Math.Clamp(center.Y, -limit, limit));
	}

	/// <summary>
	/// Where a ray from the camera meets the ground, as <c>(x, z)</c>, for the outline of what the
	/// camera sees: the plane at <paramref name="groundY"/>, or — for a ray that never comes
	/// down to it, or meets it further off than <paramref name="maxDistance"/> — the point that
	/// far out along the ray's heading. Straight down from a camera with no heading is under it.
	/// </summary>
	public static Vector2 GroundPoint(Vector3 origin, Vector3 direction, float groundY, float maxDistance)
	{
		var under = new Vector2(origin.X, origin.Z);
		var heading = new Vector2(direction.X, direction.Z);

		if (direction.Y < -1e-5f)
		{
			float t = (groundY - origin.Y) / direction.Y;
			if (t >= 0f)
			{
				var hit = new Vector2(origin.X + (direction.X * t), origin.Z + (direction.Z * t));
				if (hit.DistanceTo(under) <= maxDistance)
				{
					return hit;
				}
			}
		}

		return heading.LengthSquared() < 1e-10f ? under : under + (heading.Normalized() * maxDistance);
	}
}

/// <summary>
/// The picture under the strategist's map: the heights of the world seen from above, one per
/// cell, coloured low to high and lit from the north-west so slopes, walls and roofs read as
/// relief (<see cref="Minimap"/> takes the heights with rays). Engine-free, so what a flat map,
/// a hill, water and a hole look like are answered by <c>dotnet test</c>.
/// </summary>
public static class MinimapTerrain
{
	/// <summary>Where nothing was hit: the map's own background.</summary>
	public static readonly Color Empty = new(0.05f, 0.06f, 0.08f);

	public static readonly Color Low = new(0.26f, 0.34f, 0.22f);
	public static readonly Color High = new(0.66f, 0.63f, 0.55f);
	public static readonly Color Water = new(0.16f, 0.30f, 0.44f);

	/// <summary>The least height range the colours are spread over, so a flat map is not all contrast.</summary>
	public const float MinRangeMeters = 6f;

	/// <summary>The direction the light comes from: the north-west (−X, −Z), and above.</summary>
	private static readonly Vector3 Light = new Vector3(-1f, 1.5f, -1f).Normalized();

	/// <summary>
	/// The colours, as RGBA8 bytes row by row, of <paramref name="size"/> × <paramref name="size"/>
	/// cells <paramref name="cellMeters"/> apart. Row 0 is the north edge and column 0 the west:
	/// <paramref name="heights"/>[row * size + column]. NaN is a cell where nothing was hit.
	/// Cells under <paramref name="waterLevel"/> are water.
	/// </summary>
	public static byte[] Shade(float[] heights, int size, float cellMeters, float waterLevel)
	{
		if (heights.Length != size * size)
		{
			throw new ArgumentException($"{heights.Length} heights for {size} x {size} cells", nameof(heights));
		}

		(float low, float high) = Range(heights);
		float flatLight = Light.Y;
		var bytes = new byte[size * size * 4];

		for (int row = 0; row < size; row++)
		{
			for (int column = 0; column < size; column++)
			{
				int i = (row * size) + column;
				float h = heights[i];
				Color color;

				if (float.IsNaN(h))
				{
					color = Empty;
				}
				else if (h < waterLevel)
				{
					// Darker the deeper, to a floor: a river bed is still a river.
					float depth = Math.Clamp((waterLevel - h) / 4f, 0f, 1f);
					color = Water.Darkened(0.35f * depth);
				}
				else
				{
					float dx = (At(heights, size, row, column + 1, h) - At(heights, size, row, column - 1, h))
						/ (2f * cellMeters);
					float dz = (At(heights, size, row + 1, column, h) - At(heights, size, row - 1, column, h))
						/ (2f * cellMeters);
					var normal = new Vector3(-dx, 1f, -dz).Normalized();

					// Lit against flat ground, so flat ground keeps its colour and only slopes change.
					float lit = Math.Clamp(normal.Dot(Light) / flatLight, 0.35f, 1.35f);
					color = Low.Lerp(High, Math.Clamp((h - low) / (high - low), 0f, 1f));
					color = new Color(color.R * lit, color.G * lit, color.B * lit);
				}

				bytes[(i * 4) + 0] = (byte)Math.Clamp((int)MathF.Round(color.R * 255f), 0, 255);
				bytes[(i * 4) + 1] = (byte)Math.Clamp((int)MathF.Round(color.G * 255f), 0, 255);
				bytes[(i * 4) + 2] = (byte)Math.Clamp((int)MathF.Round(color.B * 255f), 0, 255);
				bytes[(i * 4) + 3] = 255;
			}
		}

		return bytes;
	}

	/// <summary>
	/// The heights the colours run between: the 2nd and 98th percentiles of what was hit, so one
	/// tall wall does not make the rest of the map one colour, at least <see cref="MinRangeMeters"/> apart.
	/// </summary>
	private static (float Low, float High) Range(float[] heights)
	{
		int count = 0;
		for (int i = 0; i < heights.Length; i++)
		{
			count += float.IsNaN(heights[i]) ? 0 : 1;
		}

		if (count == 0)
		{
			return (0f, MinRangeMeters);
		}

		var hit = new float[count];
		int at = 0;
		for (int i = 0; i < heights.Length; i++)
		{
			if (!float.IsNaN(heights[i]))
			{
				hit[at++] = heights[i];
			}
		}

		Array.Sort(hit);
		float low = hit[(int)((count - 1) * 0.02f)];
		float high = hit[(int)((count - 1) * 0.98f)];
		return (low, MathF.Max(high, low + MinRangeMeters));
	}

	/// <summary>The height of a neighbouring cell, or <paramref name="fallback"/> off the edge or where nothing was hit.</summary>
	private static float At(float[] heights, int size, int row, int column, float fallback)
	{
		if (row < 0 || column < 0 || row >= size || column >= size)
		{
			return fallback;
		}

		float h = heights[(row * size) + column];
		return float.IsNaN(h) ? fallback : h;
	}
}
