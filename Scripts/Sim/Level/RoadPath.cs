using System;
using System.Collections.Generic;
using Godot;

namespace Gdpyr.Sim.Level;

/// <summary>Whether a road may be drawn and graded where it was asked (docs/LEVEL_EDITOR.md §6.6), in the order they are checked.</summary>
public enum RoadResult : byte
{
	/// <summary>The road and the ground it grades are applied, as one step to undo.</summary>
	Ok,

	/// <summary>Fewer than two points or more than 64, a point off the field, or a width or grade limit a road may not have.</summary>
	Malformed,

	/// <summary>The map already holds as many roads as a file does.</summary>
	LimitReached,

	/// <summary>Two points too close together for the turn the road makes between them: a corner rounded to the road's width needs room.</summary>
	CornerTooTight,

	/// <summary>Both ends are joined, further apart in height than the grade limit times the road's length.</summary>
	TooSteepToJoin,

	/// <summary>Round a bend the ribbon's targets are steeper than the 30° skirt (§6.6).</summary>
	BendTooSteep,

	/// <summary>It crosses itself at two heights.</summary>
	CrossesItself,

	/// <summary>Grading it would move a padded object's level ground, or the pads kept after it would move its ribbon.</summary>
	DisturbsNeighbour,
}

/// <summary>What drawing a road would do (§6.6, §8.5): the answer, the line fitted, its ends' joins, and for <see cref="RoadResult.Ok"/> the edit.</summary>
public sealed class RoadCheck
{
	public RoadResult Result { get; internal set; }

	/// <summary>The road as it would be stored.</summary>
	public MapRoad Road { get; internal set; }

	/// <summary>Its line, and after the ends are joined its profile: the preview's.</summary>
	public RoadPath Path { get; internal set; }

	/// <summary>The connector its first point is joined to and pinned at, or null.</summary>
	public Connector? StartJoin { get; internal set; }

	/// <summary>The connector its last point is joined to and pinned at, or null.</summary>
	public Connector? EndJoin { get; internal set; }

	/// <summary>The object whose pad it would move; −1 for none.</summary>
	public int Other { get; internal set; } = -1;

	/// <summary>Every sample the grading and the pads kept after it may have moved: what is drawn again.</summary>
	public SampleRect Area { get; internal set; } = SampleRect.Empty;

	/// <summary>The stored samples it changes: the step's rectangle.</summary>
	public SampleRect Changed { get; internal set; } = SampleRect.Empty;

	/// <summary>For <see cref="RoadResult.Ok"/>: the road and its ground as one step, not yet applied.</summary>
	public MapEdit Edit { get; internal set; }

	/// <summary>Sections whose piers the grading gave a new length (§6.7, LE6): the step replaces them.</summary>
	public int PiersKept { get; internal set; }

	public bool IsOk => Result == RoadResult.Ok;

	public override string ToString() => RoadPath.Describe(Result);
}

/// <summary>
/// A road, engine-free (docs/LEVEL_EDITOR.md §6.6): its centreline, its profile and its
/// ribbon. A road is 2–64 control points, a width (4, 6 or 8 m) and a grade limit g
/// (6–15 %). Its corners are rounded to a radius of its width, and the centreline is
/// sampled at <see cref="SampleMeters"/> or a little less, evenly, end to end. With f(s)
/// the ground along the centreline:
///
/// <code>
///   m(s) = min over t of  f(t) + g · |s − t|        the highest profile of grade g under the ground
///   M(s) = max over t of  f(t) − g · |s − t|        the lowest over it
///   z(s) = (m(s) + M(s)) / 2
/// </code>
///
/// z is no steeper than g, being the mean of two profiles that are not, and no profile of
/// grade g cuts or fills less at its worst: E / 2, with E the largest of
/// |f(s) − f(t)| − g · |s − t|. An end joined to a connector is pinned:
/// z′ = clamp(z, z₀ − g · s, z₀ + g · s), which meets the connector's height exactly and stays
/// within g. The ribbon is the samples within half the width of the centreline, its ends
/// square; each is anchored to the profile's height at its nearest centreline point, level
/// across, and §6.3 levels the ground to them with its 30° skirt.
///
/// On a sampled line m and M are two passes each, forward and back, which is the
/// definition: a point's bound from a point k samples off is no tighter than the bound
/// passed along the k steps between them, since every step adds g times its length.
/// </summary>
public sealed class RoadPath
{
	/// <summary>The most the centreline's samples are apart [m] (§6.6).</summary>
	public const float SampleMeters = 0.5f;

	/// <summary>Over how much past the ribbon's edge a road's drawn coverage falls to none [m]: the picture's alone.</summary>
	public const float CoverageFadeMeters = 1f;

	/// <summary>The shortest a stretch between two points may be [m]: two points closer are one.</summary>
	public const float MinSegmentMeters = 0.01f;

	private const double Tiny = 1e-9;

	private readonly List<Piece> _pieces = new();
	private float[] _ground = Array.Empty<float>();
	private float[] _profile = Array.Empty<float>();

	private RoadPath(float width, float grade)
	{
		Width = width;
		Grade = grade;
	}

	/// <summary>Across [m].</summary>
	public float Width { get; }

	public float HalfWidth => Width * 0.5f;

	/// <summary>The grade limit, rise over run.</summary>
	public float Grade { get; }

	/// <summary>What a corner is rounded to [m]: the road's width, the least §6.6 allows.</summary>
	public float CornerRadius => Width;

	/// <summary><see cref="RoadResult.Ok"/>, or why the line cannot be drawn: it is malformed, or a corner is too tight.</summary>
	public RoadResult Shape { get; private set; }

	/// <summary>The centreline's length [m].</summary>
	public float Length { get; private set; }

	/// <summary>How far apart two samples are [m]: the length over the fewest pieces of at most <see cref="SampleMeters"/>.</summary>
	public float Step { get; private set; }

	/// <summary>The centreline's samples (x, z) [m], from the first point to the last, <see cref="Step"/> apart along it.</summary>
	public Vector2[] Points { get; private set; } = Array.Empty<Vector2>();

	/// <summary>The way the centreline runs at each sample, a unit vector (x, z).</summary>
	public Vector2[] Tangents { get; private set; } = Array.Empty<Vector2>();

	public int Count => Points.Length;

	/// <summary>The ground under each sample [m], as <see cref="Fit"/> last read it.</summary>
	public ReadOnlySpan<float> Ground => _ground;

	/// <summary>The profile at each sample [m], after <see cref="Fit"/>: pinned where an end was joined.</summary>
	public ReadOnlySpan<float> Profile => _profile;

	/// <summary>E: the most the ground along the centreline is steeper than g between two of its samples [m] (§6.6).</summary>
	public float Excess { get; private set; }

	/// <summary>The most the profile is under the ground [m]: the deepest cut.</summary>
	public float DeepestCut { get; private set; }

	/// <summary>The most the profile is over the ground [m]: the deepest fill.</summary>
	public float DeepestFill { get; private set; }

	/// <summary>For <see cref="RoadResult.TooSteepToJoin"/>: the grade joining its ends would take.</summary>
	public float JoinGrade { get; private set; }

	// ---- the line ------------------------------------------------------------------------------------

	/// <summary>A stored road's line: its points in centimetres, its width in decimetres, its grade limit in tenths of a percent.</summary>
	public static RoadPath Of(MapRoad road)
	{
		var points = new Vector2[road.Points.Length];
		for (int i = 0; i < points.Length; i++)
		{
			points[i] = new Vector2(road.Points[i].XMeters, road.Points[i].ZMeters);
		}

		RoadPath path = Of(points, road.WidthDecimeters / 10f, road.GradeLimitPermille / 1000f);
		if (road.Style != MapRoad.StyleDirt)
		{
			path.Shape = RoadResult.Malformed;
		}

		return path;
	}

	/// <summary>
	/// A line through <paramref name="points"/> (x, z) [m], its corners rounded to its width and
	/// sampled evenly. <see cref="Shape"/> says whether it could be: a road is 2–64 points on the
	/// field, 4, 6 or 8 m wide, with a grade limit of 6–15 %; and each stretch between two points
	/// has room for the arcs at its ends.
	/// </summary>
	public static RoadPath Of(ReadOnlySpan<Vector2> points, float width, float grade)
	{
		var path = new RoadPath(width, grade);
		path.Shape = path.Build(points);
		return path;
	}

	/// <summary>Whether a road may be this wide [m] and this steep: 4, 6 or 8 m; 6–15 %.</summary>
	public static bool IsAllowed(float width, float grade)
	{
		int decimeters = (int)MathF.Round(width * 10f);
		int permille = (int)MathF.Round(grade * 1000f);
		return MapRoad.Widths.IndexOf((byte)Math.Clamp(decimeters, 0, 255)) >= 0 && MathF.Abs((decimeters / 10f) - width) < 1e-4f
			&& permille >= MapRoad.MinGradeLimitPermille && permille <= MapRoad.MaxGradeLimitPermille;
	}

	private RoadResult Build(ReadOnlySpan<Vector2> points)
	{
		if (points.Length < MapLimits.MinRoadPoints || points.Length > MapLimits.MaxRoadPoints || !IsAllowed(Width, Grade))
		{
			return RoadResult.Malformed;
		}

		foreach (Vector2 point in points)
		{
			if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) || MathF.Abs(point.X) > MapLimits.FieldHalfMeters
				|| MathF.Abs(point.Y) > MapLimits.FieldHalfMeters)
			{
				return RoadResult.Malformed;
			}
		}

		int last = points.Length - 1;
		var directions = new D2[last];
		var lengths = new double[last];
		for (int j = 0; j < last; j++)
		{
			var apart = new D2(points[j + 1].X - (double)points[j].X, points[j + 1].Y - (double)points[j].Y);
			lengths[j] = apart.Length;
			if (lengths[j] < MinSegmentMeters)
			{
				return RoadResult.CornerTooTight;
			}

			directions[j] = apart / lengths[j];
		}

		// Each corner's arc: how far along either stretch it starts, and which way it turns.
		double radius = CornerRadius;
		var tangent = new double[points.Length];
		var turn = new double[points.Length];
		for (int i = 1; i < last; i++)
		{
			D2 a = directions[i - 1];
			D2 b = directions[i];
			turn[i] = Math.Atan2(a.Cross(b), a.Dot(b));
			tangent[i] = Math.Abs(turn[i]) < 1e-7 ? 0d : radius * Math.Tan(Math.Abs(turn[i]) * 0.5d);
		}

		for (int j = 0; j < last; j++)
		{
			if (tangent[j] + tangent[j + 1] > lengths[j] + 1e-3)
			{
				return RoadResult.CornerTooTight;
			}
		}

		_pieces.Clear();
		for (int j = 0; j < last; j++)
		{
			var from = new D2(points[j].X, points[j].Y) + (directions[j] * tangent[j]);
			double straight = lengths[j] - tangent[j] - tangent[j + 1];
			if (straight > Tiny)
			{
				_pieces.Add(Piece.Line(from, directions[j], straight));
			}

			int corner = j + 1;
			if (corner < last && tangent[corner] > 0d)
			{
				D2 start = new D2(points[corner].X, points[corner].Y) - (directions[j] * tangent[corner]);
				_pieces.Add(Piece.Arc(start, directions[j], radius, turn[corner]));
			}
		}

		double total = 0d;
		foreach (Piece piece in _pieces)
		{
			total += piece.Length;
		}

		Length = (float)total;
		int steps = Math.Max(1, (int)Math.Ceiling((total / SampleMeters) - 1e-9));
		Step = (float)(total / steps);
		Points = new Vector2[steps + 1];
		Tangents = new Vector2[steps + 1];
		int at = 0;
		double before = 0d;
		for (int i = 0; i <= steps; i++)
		{
			double s = i == steps ? total : i * (total / steps);
			while (at < _pieces.Count - 1 && s > before + _pieces[at].Length)
			{
				before += _pieces[at].Length;
				at++;
			}

			_pieces[at].At(Math.Clamp(s - before, 0d, _pieces[at].Length), out D2 point, out D2 heading);
			Points[i] = new Vector2((float)point.X, (float)point.Y);
			Tangents[i] = new Vector2((float)heading.X, (float)heading.Y);
		}

		// The ends are where the points are, not where a sum of pieces lands.
		Points[0] = points[0];
		Points[^1] = points[last];
		return RoadResult.Ok;
	}

	/// <summary>Where the centreline is along it [m]: the sample's distance from the first point.</summary>
	public float DistanceAlong(int sample) => sample * Step;

	// ---- the profile -------------------------------------------------------------------------------

	/// <summary>
	/// Reads the ground along the centreline and fits the profile to it (§6.6): z, pinned at an end
	/// given a height. <see cref="RoadResult.TooSteepToJoin"/> when both ends are pinned further
	/// apart than g times the length, with <see cref="JoinGrade"/> the grade it would take.
	/// </summary>
	public RoadResult Fit(HeightField field, float? startPin = null, float? endPin = null)
	{
		if (Shape != RoadResult.Ok)
		{
			return Shape;
		}

		int n = Count;
		_ground = new float[n];
		_profile = new float[n];
		for (int i = 0; i < n; i++)
		{
			_ground[i] = field.Sample(Points[i].X, Points[i].Y);
		}

		var lower = new float[n];
		var upper = new float[n];
		Envelope(_ground, Step, Grade, lower, upper);
		float excess = 0f;
		for (int i = 0; i < n; i++)
		{
			_profile[i] = (lower[i] + upper[i]) * 0.5f;
			excess = MathF.Max(excess, upper[i] - _ground[i]);
		}

		Excess = excess;
		JoinGrade = 0f;
		if (startPin is float a && endPin is float b)
		{
			JoinGrade = MathF.Abs(a - b) / MathF.Max(Length, MinSegmentMeters);
			if (MathF.Abs(a - b) > (Grade * Length) + 1e-4f)
			{
				return RoadResult.TooSteepToJoin;
			}
		}

		for (int i = 0; i < n; i++)
		{
			float s = DistanceAlong(i);
			float low = float.MinValue;
			float high = float.MaxValue;
			if (startPin is float z0)
			{
				low = MathF.Max(low, z0 - (Grade * s));
				high = MathF.Min(high, z0 + (Grade * s));
			}

			if (endPin is float z1)
			{
				float back = Length - s;
				low = MathF.Max(low, z1 - (Grade * back));
				high = MathF.Min(high, z1 + (Grade * back));
			}

			_profile[i] = Math.Clamp(_profile[i], low, MathF.Max(low, high));
		}

		// The pinned ends meet their heights exactly, not to a float's sum.
		if (startPin is float first)
		{
			_profile[0] = first;
		}

		if (endPin is float end)
		{
			_profile[^1] = end;
		}

		DeepestCut = 0f;
		DeepestFill = 0f;
		for (int i = 0; i < n; i++)
		{
			DeepestCut = MathF.Max(DeepestCut, _ground[i] - _profile[i]);
			DeepestFill = MathF.Max(DeepestFill, _profile[i] - _ground[i]);
		}

		return RoadResult.Ok;
	}

	/// <summary>
	/// m and M of a line of heights <paramref name="step"/> apart: the highest profile of grade
	/// <paramref name="grade"/> under them and the lowest over them. Two passes each.
	/// </summary>
	public static void Envelope(ReadOnlySpan<float> heights, float step, float grade, Span<float> lower, Span<float> upper)
	{
		int n = heights.Length;
		float rise = grade * step;
		for (int i = 0; i < n; i++)
		{
			lower[i] = i == 0 ? heights[i] : MathF.Min(heights[i], lower[i - 1] + rise);
			upper[i] = i == 0 ? heights[i] : MathF.Max(heights[i], upper[i - 1] - rise);
		}

		for (int i = n - 2; i >= 0; i--)
		{
			lower[i] = MathF.Min(lower[i], lower[i + 1] + rise);
			upper[i] = MathF.Max(upper[i], upper[i + 1] - rise);
		}
	}

	/// <summary>
	/// E of the ground along this line as it is now (§6.6): the most it is steeper than g between two
	/// of its samples [m]. What the validator's warning of a road steeper than its limit asks (§6.9).
	/// </summary>
	public float ExcessOf(HeightField field) => ExcessOf(field, out _);

	/// <summary>E of the ground along this line as it is now, and the sample it is worst at: below the most it is over the lowest profile of grade g.</summary>
	public float ExcessOf(HeightField field, out int worst)
	{
		worst = 0;
		if (Shape != RoadResult.Ok)
		{
			return 0f;
		}

		int n = Count;
		var ground = new float[n];
		for (int i = 0; i < n; i++)
		{
			ground[i] = field.Sample(Points[i].X, Points[i].Y);
		}

		var lower = new float[n];
		var upper = new float[n];
		Envelope(ground, Step, Grade, lower, upper);
		float excess = 0f;
		for (int i = 0; i < n; i++)
		{
			if (upper[i] - ground[i] > excess)
			{
				excess = upper[i] - ground[i];
				worst = i;
			}
		}

		return excess;
	}

	/// <summary>The profile's height at a distance along the centreline [m], between its samples linearly.</summary>
	public float ProfileAt(float s)
	{
		if (_profile.Length == 0)
		{
			return 0f;
		}

		float at = Math.Clamp(s / Step, 0f, Count - 1);
		int i = Math.Min((int)at, Count - 2);
		if (i < 0)
		{
			return _profile[0];
		}

		float t = at - i;
		return _profile[i] + ((_profile[i + 1] - _profile[i]) * t);
	}

	// ---- the ribbon -------------------------------------------------------------------------------

	/// <summary>
	/// Each sample of the ribbon: within half the width of the centreline, past neither end, with
	/// the distance along the centreline of its nearest point and how far it is from it.
	/// <paramref name="reach"/> more than half the width is asked too, for a road's drawn edge.
	/// </summary>
	public void Near(HeightField field, float reach, Action<int, int, float, float> each)
	{
		if (Shape != RoadResult.Ok || Count < 2)
		{
			return;
		}

		// Each sample's nearest point of the whole line, and whether that is before the first point or past
		// the last: those are not the road's, its ends being square. Asked of the whole line and not piece by
		// piece, since a point behind the start is also near the second piece, and the outside of a bend is
		// nearest a corner of two pieces, past the one and before the other.
		float limit = HalfWidth + MathF.Max(reach, 0f);
		var best = new Dictionary<int, (float Distance, float Along, bool Outside)>();
		int segments = Count - 1;
		for (int i = 0; i < segments; i++)
		{
			Vector2 a = Points[i];
			Vector2 b = Points[i + 1];
			Vector2 ab = b - a;
			float length2 = ab.LengthSquared();
			int minX = Math.Max((int)MathF.Floor(MathF.Min(a.X, b.X) - limit - field.OriginX), 0);
			int maxX = Math.Min((int)MathF.Ceiling(MathF.Max(a.X, b.X) + limit - field.OriginX), field.Width - 1);
			int minZ = Math.Max((int)MathF.Floor(MathF.Min(a.Y, b.Y) - limit - field.OriginZ), 0);
			int maxZ = Math.Min((int)MathF.Ceiling(MathF.Max(a.Y, b.Y) + limit - field.OriginZ), field.Depth - 1);
			for (int z = minZ; z <= maxZ; z++)
			{
				for (int x = minX; x <= maxX; x++)
				{
					var p = new Vector2(field.WorldX(x), field.WorldZ(z));
					float t = length2 > 0f ? (p - a).Dot(ab) / length2 : 0f;
					bool outside = (i == 0 && t < 0f) || (i == segments - 1 && t > 1f);
					t = Math.Clamp(t, 0f, 1f);
					float distance = (a + (ab * t) - p).Length();
					if (distance > limit)
					{
						continue;
					}

					int key = (z * field.Width) + x;
					if (!best.TryGetValue(key, out var held) || distance < held.Distance)
					{
						best[key] = (distance, (i + t) * Step, outside);
					}
				}
			}
		}

		var keys = new List<int>(best.Keys);
		keys.Sort();
		foreach (int key in keys)
		{
			(float distance, float along, bool outside) = best[key];
			if (!outside)
			{
				each(key % field.Width, key / field.Width, distance, along);
			}
		}
	}

	/// <summary>
	/// The ribbon's anchors (§6.6): every sample within half the width of the centreline, past
	/// neither end, at the profile's height at its nearest centreline point, level across. In the
	/// order of their index on the field. <see cref="Fit"/> first.
	/// </summary>
	public List<ConformAnchor> Ribbon(HeightField field)
	{
		var anchors = new List<ConformAnchor>();
		Near(field, 0f, (x, z, distance, along) =>
		{
			if (distance <= HalfWidth)
			{
				anchors.Add(new ConformAnchor(x, z, ProfileAt(along)));
			}
		});
		return anchors;
	}

	/// <summary>Whether sample (x, z) is on the ribbon: within half the width of the centreline, past neither end.</summary>
	public bool OnRibbon(HeightField field, int x, int z) => OnRibbon(field.WorldX(x), field.WorldZ(z));

	/// <summary>Whether a point (x, z) [m] is within half the width of the centreline, past neither end.</summary>
	public bool OnRibbon(float x, float z) => DistanceTo(x, z, true) <= HalfWidth;

	/// <summary>
	/// How far a point (x, z) is from the centreline [m], flat; with <paramref name="squareEnds"/>,
	/// infinite where its nearest point is before the first or past the last.
	/// </summary>
	public float DistanceTo(float x, float z, bool squareEnds = false)
	{
		if (Shape != RoadResult.Ok || Count < 2)
		{
			return float.PositiveInfinity;
		}

		var p = new Vector2(x, z);
		float nearest = float.PositiveInfinity;
		bool beyond = false;
		int segments = Count - 1;
		for (int i = 0; i < segments; i++)
		{
			Vector2 a = Points[i];
			Vector2 ab = Points[i + 1] - a;
			float length2 = ab.LengthSquared();
			float t = length2 > 0f ? (p - a).Dot(ab) / length2 : 0f;
			float distance = (a + (ab * Math.Clamp(t, 0f, 1f)) - p).Length();
			if (distance < nearest)
			{
				nearest = distance;
				beyond = (i == 0 && t < 0f) || (i == segments - 1 && t > 1f);
			}
		}

		return squareEnds && beyond ? float.PositiveInfinity : nearest;
	}

	/// <summary>
	/// How much of a sample is road, for the terrain's shader (§6.6, §7.2): 1 on the ribbon to
	/// <see cref="CoverageFadeMeters"/> inside its edge, none that far past it, and between linearly.
	/// Writes the greater of what is there and this road's into <paramref name="coverage"/>, a value a
	/// sample of the field. Nothing reads it but the picture.
	/// </summary>
	public void Cover(HeightField field, float[] coverage)
	{
		float fade = CoverageFadeMeters * 0.5f;
		Near(field, fade, (x, z, distance, _) =>
		{
			float value = Math.Clamp((HalfWidth + fade - distance) / CoverageFadeMeters, 0f, 1f);
			int i = (z * field.Width) + x;
			coverage[i] = MathF.Max(coverage[i], value);
		});
	}

	/// <summary>Every road of a document's coverage, a value a sample (§7.2): what the terrain's mesh is coloured by.</summary>
	public static float[] Coverage(MapDocument document)
	{
		HeightField field = document.Terrain;
		var coverage = new float[field.Heights.Length];
		foreach (MapRoad road in document.Roads)
		{
			Of(road).Cover(field, coverage);
		}

		return coverage;
	}

	/// <summary>The rectangle of samples the road reaches, <paramref name="reach"/> past its ribbon: empty for a line that cannot be drawn.</summary>
	public SampleRect Bounds(HeightField field, float reach = 0f)
	{
		if (Shape != RoadResult.Ok || Count == 0)
		{
			return SampleRect.Empty;
		}

		float minX = float.MaxValue;
		float minZ = float.MaxValue;
		float maxX = float.MinValue;
		float maxZ = float.MinValue;
		foreach (Vector2 point in Points)
		{
			minX = MathF.Min(minX, point.X);
			minZ = MathF.Min(minZ, point.Y);
			maxX = MathF.Max(maxX, point.X);
			maxZ = MathF.Max(maxZ, point.Y);
		}

		float grow = HalfWidth + reach;
		return new SampleRect((int)MathF.Floor(minX - grow - field.OriginX), (int)MathF.Floor(minZ - grow - field.OriginZ),
			(int)MathF.Ceiling(maxX + grow - field.OriginX), (int)MathF.Ceiling(maxZ + grow - field.OriginZ))
			.Intersect(field.Bounds);
	}

	// ---- the ends --------------------------------------------------------------------------------

	/// <summary>
	/// One end as a connector (§6.6: each end is one): where it is, at the profile's height after
	/// <see cref="Fit"/> or else the ground's, and the heading out of the road there, as a yaw.
	/// </summary>
	public void End(int which, HeightField field, out Vector3 point, out float heading)
	{
		int i = which == 0 ? 0 : Count - 1;
		Vector2 at = Points[i];
		Vector2 tangent = Tangents[i] * (which == 0 ? -1f : 1f);
		float y = _profile.Length == Count ? _profile[i] : field.Sample(at.X, at.Y);
		point = new Vector3(at.X, y, at.Y);
		heading = MathF.Atan2(tangent.X, tangent.Y);
	}

	/// <summary>
	/// Whether the centreline crosses itself in plan: two of its pieces, not neighbours, meet. What
	/// tells <see cref="RoadResult.CrossesItself"/> from <see cref="RoadResult.BendTooSteep"/> when the
	/// ribbon's targets are steeper than the skirt.
	/// </summary>
	public bool CrossesItself()
	{
		if (Shape != RoadResult.Ok || Count < 4)
		{
			return false;
		}

		// Pieces into cells of a few metres, and each pair in a cell asked once.
		const float Cell = 4f;
		var cells = new Dictionary<long, List<int>>();
		int segments = Count - 1;
		for (int i = 0; i < segments; i++)
		{
			Vector2 a = Points[i];
			Vector2 b = Points[i + 1];
			int x0 = (int)MathF.Floor(MathF.Min(a.X, b.X) / Cell);
			int x1 = (int)MathF.Floor(MathF.Max(a.X, b.X) / Cell);
			int z0 = (int)MathF.Floor(MathF.Min(a.Y, b.Y) / Cell);
			int z1 = (int)MathF.Floor(MathF.Max(a.Y, b.Y) / Cell);
			for (int cz = z0; cz <= z1; cz++)
			{
				for (int cx = x0; cx <= x1; cx++)
				{
					long key = ((long)cz << 32) ^ (uint)cx;
					if (!cells.TryGetValue(key, out List<int> held))
					{
						held = new List<int>();
						cells[key] = held;
					}

					foreach (int j in held)
					{
						if (i - j >= 2 && Meet(Points[j], Points[j + 1], a, b))
						{
							return true;
						}
					}

					held.Add(i);
				}
			}
		}

		return false;
	}

	private static bool Meet(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
	{
		float d1 = Side(c, d, a);
		float d2 = Side(c, d, b);
		float d3 = Side(a, b, c);
		float d4 = Side(a, b, d);
		return ((d1 > 0f && d2 < 0f) || (d1 < 0f && d2 > 0f)) && ((d3 > 0f && d4 < 0f) || (d3 < 0f && d4 > 0f));
	}

	private static float Side(Vector2 a, Vector2 b, Vector2 p) => ((b.X - a.X) * (p.Y - a.Y)) - ((b.Y - a.Y) * (p.X - a.X));

	// ---- a road placed ---------------------------------------------------------------------------------

	/// <summary>
	/// What drawing a road would do, without doing it (§6.6, §8.5): its line, its ends joined to the
	/// connectors within <see cref="BridgeRules.RoadJointMeters"/> of them and pinned to their height,
	/// its profile, and its ribbon levelled by §6.3 with the 30° skirt — §6.3's condition checked first
	/// — then rounded, and every pad kept (§6.5). A road whose grading would move a padded object's
	/// level ground, or whose ribbon the pads kept after it would move, is
	/// <see cref="RoadResult.DisturbsNeighbour"/>. For <see cref="RoadResult.Ok"/> the check carries the
	/// edit: the road put in, or put in place of <paramref name="replacing"/>, and the ground it
	/// graded, which nothing has applied. The old line's ground stays as it is.
	/// </summary>
	public static RoadCheck Check(MapDocument document, MapRoad road, int replacing = -1)
	{
		var check = new RoadCheck { Road = road };
		bool moving = replacing >= 0 && replacing < document.Roads.Count;
		replacing = moving ? replacing : -1;
		if (!moving && document.Roads.Count >= MapLimits.MaxRoads)
		{
			check.Result = RoadResult.LimitReached;
			return check;
		}

		RoadPath path = Of(road);
		check.Path = path;
		if (path.Shape != RoadResult.Ok)
		{
			check.Result = path.Shape;
			return check;
		}

		HeightField field = document.Terrain;
		List<Connector> connectors = BridgeRules.Connectors(document, -1, replacing);
		var start = new Vector3(path.Points[0].X, 0f, path.Points[0].Y);
		var end = new Vector3(path.Points[^1].X, 0f, path.Points[^1].Y);
		if (BridgeRules.Nearest(connectors, start.X, start.Z, BridgeRules.RoadJointMeters, out Connector first))
		{
			check.StartJoin = first;
		}

		if (BridgeRules.Nearest(connectors, end.X, end.Z, BridgeRules.RoadJointMeters, out Connector last))
		{
			check.EndJoin = last;
		}

		check.Result = path.Fit(field, check.StartJoin?.Point.Y, check.EndJoin?.Point.Y);
		if (check.Result != RoadResult.Ok)
		{
			return check;
		}

		ConformAnchor[] anchors = path.Ribbon(field).ToArray();
		MapDocument trial = document.Clone();
		if (TerrainConform.Apply(trial.Terrain, anchors, out SampleRect touched) != ConformResult.Ok)
		{
			check.Result = path.CrossesItself() ? RoadResult.CrossesItself : RoadResult.BendTooSteep;
			return check;
		}

		trial.Terrain.Round(touched);
		int disturbed = PrefabPlacement.Disturbed(document, field, trial.Terrain, touched, -1);
		if (disturbed >= 0)
		{
			check.Result = RoadResult.DisturbsNeighbour;
			check.Other = disturbed;
			return check;
		}

		// Every pad kept, as after a stroke; and the ribbon must come through it as it was.
		var graded = new float[anchors.Length];
		for (int i = 0; i < anchors.Length; i++)
		{
			graded[i] = trial.Terrain[anchors[i].X, anchors[i].Z];
		}

		SampleRect kept = PrefabPlacement.KeepPads(trial, touched);
		for (int i = 0; i < anchors.Length; i++)
		{
			if (trial.Terrain[anchors[i].X, anchors[i].Z] != graded[i])
			{
				check.Result = RoadResult.DisturbsNeighbour;
				check.Other = NearestPad(document, field.WorldX(anchors[i].X), field.WorldZ(anchors[i].Z));
				return check;
			}
		}

		check.Area = touched.Union(kept);
		SampleRect changed = MapEditSession.Differences(field, trial.Terrain, check.Area);
		MapEdit edit = changed.IsEmpty ? new MapEdit() : MapEdit.Terrain(field, trial.Terrain, changed);
		foreach (ObjectChange pier in BridgeRules.KeepPiers(document, trial.Terrain, changed))
		{
			edit.Replace(pier.Index, pier.Before, pier.After);
			check.PiersKept++;
		}

		check.Edit = moving
			? edit.ReplaceRoad(replacing, document.Roads[replacing], road)
			: edit.InsertRoad(document.Roads.Count, road);
		check.Changed = changed;
		check.Result = RoadResult.Ok;
		return check;
	}

	/// <summary>Taking a road up: the record goes, and the ground it graded stays, as any sculpted ground does.</summary>
	public static MapEdit TakeUp(MapDocument document, int index) =>
		new MapEdit().RemoveRoad(index, document.Roads[index]);

	/// <summary>
	/// The first road whose ribbon a footprint is on (§6.5: a pad's prefab on a road is in the way), or
	/// −1: a centreline sample within half the road's width of it, which is the ribbon to the quarter
	/// metre its samples are apart.
	/// </summary>
	public static int RoadUnder(MapDocument document, in PadArea area)
	{
		for (int r = 0; r < document.Roads.Count; r++)
		{
			RoadPath path = Of(document.Roads[r]);
			if (path.Shape != RoadResult.Ok)
			{
				continue;
			}

			foreach (Vector2 point in path.Points)
			{
				float dx = point.X - area.X;
				float dz = point.Y - area.Z;
				float reach = area.Radius + path.HalfWidth;
				if ((dx * dx) + (dz * dz) <= reach * reach && area.DistanceTo(point.X, point.Y) < path.HalfWidth)
				{
					return r;
				}
			}
		}

		return -1;
	}

	/// <summary>The padded object whose footprint is nearest a point, or −1.</summary>
	private static int NearestPad(MapDocument document, float x, float z)
	{
		int best = -1;
		float nearest = float.MaxValue;
		for (int i = 0; i < document.Objects.Count; i++)
		{
			MapObject placed = document.Objects[i];
			if (PrefabSpecs.TryGet(placed.PrefabId, out PrefabSpec spec) && spec.MakesPad
				&& PrefabSpecs.TryGetPadArea(placed, out PadArea area) && area.DistanceTo(x, z) < nearest)
			{
				nearest = area.DistanceTo(x, z);
				best = i;
			}
		}

		return best;
	}

	/// <summary>
	/// A road as a file stores it, from its points in metres, its width [m] and its grade limit (rise
	/// over run): the first <see cref="MapLimits.MaxRoadPoints"/> points, which is as many as a road has.
	/// </summary>
	public static MapRoad Record(ReadOnlySpan<Vector2> points, float width, float grade)
	{
		var stored = new RoadPoint[Math.Min(points.Length, MapLimits.MaxRoadPoints)];
		for (int i = 0; i < stored.Length; i++)
		{
			stored[i] = RoadPoint.At(points[i].X, points[i].Y);
		}

		return new MapRoad(MapRoad.StyleDirt, (byte)Math.Clamp((int)MathF.Round(width * 10f), 0, 255),
			(byte)Math.Clamp((int)MathF.Round(grade * 1000f), 0, 255), stored);
	}

	public static string Describe(RoadResult result) => result switch
	{
		RoadResult.Ok => "ok",
		RoadResult.Malformed => "a road is 2 to 64 points on the field, 4, 6 or 8 m wide, at 6 to 15 %",
		RoadResult.LimitReached => $"the map holds {MapLimits.MaxRoads} roads, as many as it may",
		RoadResult.CornerTooTight => "a corner is too tight: its points are too close for the turn at the road's width",
		RoadResult.TooSteepToJoin => "its two ends are too far apart in height for its grade limit",
		RoadResult.BendTooSteep => "too steep round a bend: the inside edge passes the 30° skirt",
		RoadResult.CrossesItself => "it crosses itself at two heights",
		RoadResult.DisturbsNeighbour => "it would move an object's pad",
		_ => result.ToString(),
	};

	// ---- pieces of the line ------------------------------------------------------------------------

	/// <summary>A point or a direction in plan, in doubles while the line is laid out.</summary>
	private readonly struct D2
	{
		public readonly double X;
		public readonly double Y;

		public D2(double x, double y)
		{
			X = x;
			Y = y;
		}

		public double Length => Math.Sqrt((X * X) + (Y * Y));

		public double Dot(D2 other) => (X * other.X) + (Y * other.Y);

		public double Cross(D2 other) => (X * other.Y) - (Y * other.X);

		public static D2 operator +(D2 a, D2 b) => new(a.X + b.X, a.Y + b.Y);

		public static D2 operator -(D2 a, D2 b) => new(a.X - b.X, a.Y - b.Y);

		public static D2 operator *(D2 a, double k) => new(a.X * k, a.Y * k);

		public static D2 operator /(D2 a, double k) => new(a.X / k, a.Y / k);

		/// <summary>Turned by <paramref name="angle"/>, from +x towards +z.</summary>
		public D2 Turned(double angle)
		{
			double cos = Math.Cos(angle);
			double sin = Math.Sin(angle);
			return new D2((X * cos) - (Y * sin), (X * sin) + (Y * cos));
		}
	}

	/// <summary>A straight, or an arc of a corner.</summary>
	private readonly struct Piece
	{
		private readonly bool _arc;
		private readonly D2 _start;
		private readonly D2 _direction;
		private readonly double _radius;
		private readonly double _turn;

		public readonly double Length;

		private Piece(bool arc, D2 start, D2 direction, double radius, double turn, double length)
		{
			_arc = arc;
			_start = start;
			_direction = direction;
			_radius = radius;
			_turn = turn;
			Length = length;
		}

		public static Piece Line(D2 start, D2 direction, double length) => new(false, start, direction, 0d, 0d, length);

		/// <summary>From <paramref name="start"/> heading <paramref name="direction"/>, turning <paramref name="turn"/> radians on <paramref name="radius"/>.</summary>
		public static Piece Arc(D2 start, D2 direction, double radius, double turn) =>
			new(true, start, direction, radius, turn, radius * Math.Abs(turn));

		public void At(double s, out D2 point, out D2 heading)
		{
			if (!_arc)
			{
				point = _start + (_direction * s);
				heading = _direction;
				return;
			}

			double sign = Math.Sign(_turn);
			D2 toCentre = _direction.Turned(sign * Math.PI * 0.5d) * _radius;
			D2 centre = _start + toCentre;
			double angle = sign * (s / _radius);
			point = centre + (toCentre * -1d).Turned(angle);
			heading = _direction.Turned(angle);
		}
	}
}
