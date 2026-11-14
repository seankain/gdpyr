using System;
using System.Collections.Generic;
using Godot;

namespace Gdpyr.Sim.Level;

/// <summary>
/// Where a bridge section or a road joins (docs/LEVEL_EDITOR.md §6.6, §6.7): a point, at the
/// deck's top or the road's surface, and the heading out of what it is on. Two join where they
/// are the same point with opposite headings.
/// </summary>
public readonly struct Connector
{
	/// <summary>Where it is [m].</summary>
	public readonly Vector3 Point;

	/// <summary>Out of what it is on, as a yaw [radians]: 0 along +Z.</summary>
	public readonly float Heading;

	/// <summary>The section it is on, by its index in the file; −1 for a road's end.</summary>
	public readonly int Object;

	/// <summary>The road whose end it is, by its index; −1 for a section's.</summary>
	public readonly int Road;

	/// <summary>Which of its owner's two: 0 a section's −Z end or a road's first point, 1 the other.</summary>
	public readonly int End;

	public Connector(Vector3 point, float heading, int placed, int road, int end)
	{
		Point = point;
		Heading = heading;
		Object = placed;
		Road = road;
		End = end;
	}

	public bool IsRoad => Road >= 0;

	/// <summary>The way out of it, flat: (sin, cos) of the heading in (x, z).</summary>
	public Vector2 Direction => new(MathF.Sin(Heading), MathF.Cos(Heading));

	public override string ToString() =>
		(IsRoad ? $"road {Road}'s {(End == 0 ? "first" : "last")} end" : $"section {Object}'s {(End == 0 ? "-Z" : "+Z")} end")
		+ $" at ({Point.X:0.00}, {Point.Y:0.00}, {Point.Z:0.00})";
}

/// <summary>What placing a bridge section would do (§6.7): §6.5's answer, the connector it joins, and the ends it lands.</summary>
public sealed class SectionCheck
{
	/// <summary>§6.5's checks of the section, its piers among its boxes, and the edit with its landings.</summary>
	public PlacementCheck Placement { get; internal set; }

	/// <summary>The free connector the section was put on; null for one that floats.</summary>
	public Connector? Target { get; internal set; }

	/// <summary>Its ends whose ground was levelled to the deck: 0, 1 or 2.</summary>
	public int Landed { get; internal set; }

	/// <summary>Its piers: 0, 1 or 2.</summary>
	public int Piers { get; internal set; }

	public PrefabPlacementResult Result => Placement.Result;

	public bool IsOk => Placement.IsOk;

	public override string ToString() => PrefabPlacement.Describe(Result);
}

/// <summary>
/// Bridges (docs/LEVEL_EDITOR.md §6.7): sections that connect at their ends.
///
/// <list type="bullet">
/// <item><b>Connecting.</b> A section being placed takes the pose that puts one of its connectors
/// on the nearest free connector within <see cref="SnapMeters"/> of the cursor — the same point,
/// the opposite heading. Turned end for end, it joins by its other end, which for a ramp is up or
/// down. A road's end is a connector like any other.</item>
/// <item><b>Free-floating.</b> With no connector near, the section goes where the cursor is, at a
/// height it is given: the last section placed's, moved <see cref="NudgeMeters"/> at a time.</item>
/// <item><b>Piers.</b> A section with supports on has a pier under each end that is not joined to a
/// section with a pier there already: <see cref="PrefabSpecs.PierSideMeters"/> square, from the
/// deck's underside to <see cref="PierFootMeters"/> under the lowest ground beneath it. Its length
/// is written in the file (LE-D14). A pier in another object's box is <c>InTheWay</c>.</item>
/// <item><b>Landing.</b> An end neither joined nor more than <see cref="LandingReachMeters"/> from
/// the ground in height is landed when the section is placed: a pad
/// <see cref="PrefabSpecs.DeckWidthMeters"/> × <see cref="LandingLengthMeters"/> at the deck's top is
/// levelled beyond it by §6.3, so that the ground meets the deck flush.</item>
/// </list>
/// </summary>
public static class BridgeRules
{
	/// <summary>How near the cursor a free connector is taken [m].</summary>
	public const float SnapMeters = 3f;

	/// <summary>What <c>PgUp</c> and <c>PgDn</c> move a free-floating section by [m].</summary>
	public const float NudgeMeters = 0.25f;

	/// <summary>An end this near the ground in height is landed [m].</summary>
	public const float LandingReachMeters = 3f;

	/// <summary>How far past an end its landing reaches [m].</summary>
	public const float LandingLengthMeters = 4f;

	/// <summary>How far under the lowest ground beneath it a pier's foot is [m].</summary>
	public const float PierFootMeters = 0.5f;

	/// <summary>Two sections' connectors this near, in plan and in height, are one joint [m]: the validator's.</summary>
	public const float JointMeters = MapValidator.JointMeters;

	/// <summary>A road's end this near a connector in plan joins it [m]: the validator's.</summary>
	public const float RoadJointMeters = MapValidator.RoadJointMeters;

	// ---- connectors -------------------------------------------------------------------------------

	/// <summary>
	/// Every connector of a document: each section's two, then each road's two ends, at the ground's
	/// height there — which the road graded to its profile. <paramref name="skipObject"/> and
	/// <paramref name="skipRoad"/> leave one out: what is being placed again.
	/// </summary>
	public static List<Connector> Connectors(MapDocument document, int skipObject = -1, int skipRoad = -1)
	{
		var all = new List<Connector>();
		Span<Vector3> points = stackalloc Vector3[2];
		Span<float> headings = stackalloc float[2];
		for (int i = 0; i < document.Objects.Count; i++)
		{
			if (i == skipObject)
			{
				continue;
			}

			int count = PrefabSpecs.Connectors(document.Objects[i], points, headings);
			for (int c = 0; c < count; c++)
			{
				all.Add(new Connector(points[c], Wrap(headings[c]), i, -1, c));
			}
		}

		for (int r = 0; r < document.Roads.Count; r++)
		{
			if (r == skipRoad)
			{
				continue;
			}

			RoadPath path = RoadPath.Of(document.Roads[r]);
			if (path.Shape != RoadResult.Ok)
			{
				continue;
			}

			for (int end = 0; end < 2; end++)
			{
				path.End(end, document.Terrain, out Vector3 point, out float heading);
				all.Add(new Connector(point, Wrap(heading), -1, r, end));
			}
		}

		return all;
	}

	/// <summary>
	/// Whether a connector is joined: to another section's within <see cref="JointMeters"/> in plan
	/// and height, or — a section's to a road's end, or two roads' ends — within
	/// <see cref="RoadJointMeters"/> in plan.
	/// </summary>
	public static bool IsJoined(in Connector connector, List<Connector> all)
	{
		foreach (Connector other in all)
		{
			if (SameOwner(connector, other))
			{
				continue;
			}

			float plan = Plan(connector.Point, other.Point);
			bool joined = connector.IsRoad || other.IsRoad
				? plan <= RoadJointMeters
				: plan <= JointMeters && MathF.Abs(connector.Point.Y - other.Point.Y) <= JointMeters;
			if (joined)
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>The connectors nothing is joined to: what a section or a road's end may be put on.</summary>
	public static List<Connector> Free(MapDocument document, int skipObject = -1, int skipRoad = -1)
	{
		List<Connector> all = Connectors(document, skipObject, skipRoad);
		var free = new List<Connector>();
		foreach (Connector connector in all)
		{
			if (!IsJoined(connector, all))
			{
				free.Add(connector);
			}
		}

		return free;
	}

	/// <summary>The free connector nearest (x, z) in plan within <paramref name="within"/>, or false.</summary>
	public static bool Nearest(List<Connector> connectors, float x, float z, float within, out Connector found)
	{
		found = default;
		float best = within;
		bool any = false;
		foreach (Connector connector in connectors)
		{
			float plan = Plan(connector.Point, new Vector3(x, 0f, z));
			if (plan <= best)
			{
				best = plan;
				found = connector;
				any = true;
			}
		}

		return any;
	}

	// ---- a section's pose ---------------------------------------------------------------------------

	/// <summary>
	/// The request that puts a section's connector <paramref name="which"/> — 0 its −Z end, 1 its +Z
	/// end — on <paramref name="target"/>: the same point, the opposite heading. Its height is what
	/// puts that connector's height on the target's.
	/// </summary>
	public static PlacementRequest Attach(ushort prefabId, int which, in Connector target, bool supports)
	{
		PrefabSpec spec = PrefabSpecs.Get(prefabId);
		PrefabConnector own = spec.Connectors[Math.Clamp(which, 0, spec.Connectors.Count - 1)];
		float yaw = Wrap(target.Heading + MathF.PI - own.Heading);
		Vector3 local = own.Local.Rotated(Vector3.Up, yaw);
		Vector3 origin = target.Point - local;
		return new PlacementRequest(prefabId, origin.X, origin.Z, yaw, origin.Y, Supports(supports));
	}

	/// <summary>A section that floats: where the cursor is, turned by <paramref name="yaw"/>, its deck's top at its −Z end at <paramref name="height"/>.</summary>
	public static PlacementRequest Float(ushort prefabId, float x, float z, float yaw, float height, bool supports) =>
		new(prefabId, x, z, Wrap(yaw), height, Supports(supports));

	/// <summary>
	/// Where a section being placed goes (§6.7): on the nearest free connector within
	/// <see cref="SnapMeters"/> of (x, z) by its −Z end, or by its +Z end turned end for end; else
	/// floating there at <paramref name="height"/> and <paramref name="yaw"/>.
	/// </summary>
	public static PlacementRequest Snap(MapDocument document, ushort prefabId, bool endForEnd, float x, float z, float yaw,
		float height, bool supports, int replacing, out Connector? target)
	{
		target = null;
		if (Nearest(Free(document, replacing), x, z, SnapMeters, out Connector found))
		{
			target = found;
			return Attach(prefabId, endForEnd ? 1 : 0, found, supports);
		}

		return Float(prefabId, x, z, yaw, height, supports);
	}

	private static MapParameter[] Supports(bool on) =>
		on ? new[] { new MapParameter(PrefabSpecs.SupportsKey, 1) } : null;

	// ---- piers ---------------------------------------------------------------------------------------

	/// <summary>A section's connector <paramref name="end"/> in the world: its point at the deck's top there.</summary>
	public static Vector3 EndPoint(MapObject section, int end)
	{
		PrefabSpec spec = PrefabSpecs.Get(section.PrefabId);
		return new StructurePose(section.Position, section.YawRadians).ToWorld(spec.Connectors[end].Local);
	}

	/// <summary>Where a pier under end <paramref name="end"/> stands, in plan: its footprint.</summary>
	public static Footprint PierFootprint(MapObject section, int end)
	{
		float along = (end == 0 ? -1f : 1f) * ((PrefabSpecs.DeckLengthMeters - PrefabSpecs.PierSideMeters) * 0.5f);
		Vector3 centre = new StructurePose(section.Position, section.YawRadians).ToWorld(new Vector3(0f, 0f, along));
		float half = PrefabSpecs.PierSideMeters * 0.5f;
		return new Footprint(centre, section.YawRadians, half, half);
	}

	/// <summary>
	/// The lowest the ground is under a footprint [m]: its samples inside it, and its edges every
	/// 0.2 m, which is where the triangles' lowest point under a rectangle is to a few millimetres.
	/// </summary>
	public static float LowestUnder(HeightField field, in Footprint footprint)
	{
		float lowest = float.MaxValue;
		for (int corner = 0; corner < 4; corner++)
		{
			Vector3 a = footprint.Corner(corner);
			Vector3 b = footprint.Corner((corner + 1) % 4);
			int steps = Math.Max(1, (int)MathF.Ceiling((b - a).Length() / 0.2f));
			for (int k = 0; k < steps; k++)
			{
				Vector3 p = a + ((b - a) * (k / (float)steps));
				lowest = MathF.Min(lowest, field.Sample(p.X, p.Z));
			}
		}

		float reach = footprint.HalfWidth + footprint.HalfDepth;
		int minX = Math.Max((int)MathF.Floor(footprint.Center.X - reach - field.OriginX), 0);
		int maxX = Math.Min((int)MathF.Ceiling(footprint.Center.X + reach - field.OriginX), field.Width - 1);
		int minZ = Math.Max((int)MathF.Floor(footprint.Center.Z - reach - field.OriginZ), 0);
		int maxZ = Math.Min((int)MathF.Ceiling(footprint.Center.Z + reach - field.OriginZ), field.Depth - 1);
		for (int z = minZ; z <= maxZ; z++)
		{
			for (int x = minX; x <= maxX; x++)
			{
				if (footprint.Contains(new Vector3(field.WorldX(x), 0f, field.WorldZ(z))))
				{
					lowest = MathF.Min(lowest, field[x, z]);
				}
			}
		}

		return lowest;
	}

	/// <summary>
	/// A pier's length under end <paramref name="end"/> [cm]: from the deck's underside there to
	/// <see cref="PierFootMeters"/> under the lowest ground beneath it, rounded up to the centimetre so
	/// that the foot is at least that deep. 0 where the deck's underside is that deep already.
	/// </summary>
	public static int PierCentimeters(HeightField field, MapObject section, int end)
	{
		float top = EndPoint(section, end).Y - PrefabSpecs.DeckThicknessMeters;
		float foot = LowestUnder(field, PierFootprint(section, end)) - PierFootMeters;
		float length = top - foot;
		return length > 0f ? (int)MathF.Ceiling((length * 100f) - 1e-3f) : 0;
	}

	/// <summary>
	/// Whether end <paramref name="end"/> of a section stands on a pier of its own: it has supports on,
	/// and it is not joined to a section that has a pier there already (§6.7).
	/// </summary>
	public static bool NeedsPier(MapDocument document, MapObject section, int end, int skip)
	{
		PrefabSpec spec = PrefabSpecs.Get(section.PrefabId);
		if (spec.ValueOf(section, PrefabSpecs.SupportsKey) == 0)
		{
			return false;
		}

		Vector3 at = EndPoint(section, end);
		for (int i = 0; i < document.Objects.Count; i++)
		{
			MapObject other = document.Objects[i];
			if (i == skip || ReferenceEquals(other, section) || !PrefabSpecs.TryGet(other.PrefabId, out PrefabSpec otherSpec)
				|| otherSpec.Kind != PrefabKind.Bridge)
			{
				continue;
			}

			for (int c = 0; c < otherSpec.Connectors.Count; c++)
			{
				Vector3 there = EndPoint(other, c);
				byte key = c == 0 ? PrefabSpecs.PierBackKey : PrefabSpecs.PierFrontKey;
				if (Plan(at, there) <= JointMeters && MathF.Abs(at.Y - there.Y) <= JointMeters
					&& otherSpec.ValueOf(other, key) > 0)
				{
					return false;
				}
			}
		}

		return true;
	}

	/// <summary>
	/// The section's parameters with its piers worked out on <paramref name="ground"/>: supports as
	/// asked, and a length under each end that needs one. False when a pier would be longer than a
	/// file holds.
	/// </summary>
	public static bool WithPiers(MapDocument document, HeightField ground, ref PlacementRequest request, int replacing,
		out int piers)
	{
		piers = 0;
		bool supports = false;
		foreach (MapParameter parameter in request.Parameters ?? Array.Empty<MapParameter>())
		{
			supports |= parameter.Key == PrefabSpecs.SupportsKey && parameter.Value != 0;
		}

		MapObject provisional = Provisional(request, supports);
		var parameters = new List<MapParameter>();
		if (supports)
		{
			parameters.Add(new MapParameter(PrefabSpecs.SupportsKey, 1));
		}

		bool fits = true;
		for (int end = 0; end < 2 && supports; end++)
		{
			if (!NeedsPier(document, provisional, end, replacing))
			{
				continue;
			}

			int length = PierCentimeters(ground, provisional, end);
			fits &= length <= PrefabSpecs.MaxPierCentimeters;
			if (length > 0)
			{
				parameters.Add(new MapParameter(end == 0 ? PrefabSpecs.PierBackKey : PrefabSpecs.PierFrontKey,
					Math.Min(length, PrefabSpecs.MaxPierCentimeters)));
				piers++;
			}
		}

		request.Parameters = parameters.Count > 0 ? parameters.ToArray() : null;
		return fits;
	}

	/// <summary>The section a request asks for, as it would be stored, with its supports and no piers yet.</summary>
	private static MapObject Provisional(in PlacementRequest request, bool supports) =>
		new(request.PrefabId, Quantize.MetersToI16(request.X), Quantize.MetersToI16(request.Height), Quantize.MetersToI16(request.Z),
			Quantize.YawToU16(request.Yaw), 0, Supports(supports));

	/// <summary>
	/// Piers are kept (§6.7, LE6), as pads are (§6.5): once an edit has moved the ground under
	/// <paramref name="edited"/>, every pier that stands on it is given its length again on
	/// <paramref name="ground"/>, so that its foot is still <see cref="PierFootMeters"/> under the lowest
	/// ground beneath it. The sections whose piers changed, as they would be after, by index; the edit
	/// replaces them. <paramref name="skip"/> is left as it is: what the edit places again itself.
	/// </summary>
	public static List<ObjectChange> KeepPiers(MapDocument document, HeightField ground, in SampleRect edited, int skip = -1)
	{
		var changes = new List<ObjectChange>();
		if (edited.IsEmpty)
		{
			return changes;
		}

		for (int i = 0; i < document.Objects.Count; i++)
		{
			MapObject section = document.Objects[i];
			if (i == skip || !PrefabSpecs.TryGet(section.PrefabId, out PrefabSpec spec) || spec.Kind != PrefabKind.Bridge)
			{
				continue;
			}

			MapObject kept = section;
			for (int end = 0; end < 2; end++)
			{
				byte key = end == 0 ? PrefabSpecs.PierBackKey : PrefabSpecs.PierFrontKey;
				if (section.Parameter(key, 0) <= 0 || !Reaches(ground, PierFootprint(section, end), edited))
				{
					continue;
				}

				int length = Math.Min(PierCentimeters(ground, section, end), PrefabSpecs.MaxPierCentimeters);
				if (length != section.Parameter(key, 0))
				{
					kept = WithParameter(kept, key, length);
				}
			}

			if (!ReferenceEquals(kept, section))
			{
				changes.Add(new ObjectChange(i, section, kept));
			}
		}

		return changes;
	}

	/// <summary>Whether an edit's rectangle reaches the ground under a footprint: a sample of it, or one a triangle under it hangs from.</summary>
	private static bool Reaches(HeightField field, in Footprint footprint, in SampleRect edited)
	{
		float reach = footprint.HalfWidth + footprint.HalfDepth;
		var under = new SampleRect((int)MathF.Floor(footprint.Center.X - reach - field.OriginX) - 1,
			(int)MathF.Floor(footprint.Center.Z - reach - field.OriginZ) - 1, (int)MathF.Ceiling(footprint.Center.X + reach - field.OriginX) + 1,
			(int)MathF.Ceiling(footprint.Center.Z + reach - field.OriginZ) + 1);
		return !under.Intersect(edited).IsEmpty;
	}

	/// <summary>An object with one parameter set, or taken off where <paramref name="value"/> is 0.</summary>
	private static MapObject WithParameter(MapObject placed, byte key, int value)
	{
		var parameters = new List<MapParameter>();
		foreach (MapParameter parameter in placed.Parameters)
		{
			if (parameter.Key != key)
			{
				parameters.Add(parameter);
			}
		}

		if (value != 0)
		{
			parameters.Add(new MapParameter(key, value));
		}

		return new MapObject(placed.PrefabId, placed.X, placed.Y, placed.Z, placed.Yaw, placed.Flags, parameters.ToArray());
	}

	// ---- placing a section ----------------------------------------------------------------------------

	/// <summary>
	/// What placing a bridge section would do (§6.5, §6.7), without doing it. Its ends are landed
	/// first — every end neither joined nor more than <see cref="LandingReachMeters"/> off the ground —
	/// then its piers are worked out on the ground as landed and are among its boxes for §6.5's checks;
	/// the piers of other sections the landing reached are kept. All of it one edit, not applied. A
	/// landing that would move a padded object's level ground is <c>DisturbsNeighbour</c>.
	/// </summary>
	public static SectionCheck Check(MapDocument document, PlacementRequest request, int replacing = -1,
		Connector? target = null)
	{
		var result = new SectionCheck { Target = target };
		if (!PrefabSpecs.TryGet(request.PrefabId, out PrefabSpec spec) || spec.Kind != PrefabKind.Bridge)
		{
			result.Placement = PrefabPlacement.Check(document, request, replacing);
			return result;
		}

		bool moving = replacing >= 0 && replacing < document.Objects.Count;
		replacing = moving ? replacing : -1;

		// Its ends, with every other connector of the map: which are joined, and which land.
		MapObject provisional = Provisional(request, false);
		HeightField field = document.Terrain;
		List<Connector> others = Connectors(document, replacing);
		HeightField after = null;
		SampleRect touched = SampleRect.Empty;
		for (int end = 0; end < 2; end++)
		{
			var own = new Connector(EndPoint(provisional, end), Wrap(provisional.YawRadians + spec.Connectors[end].Heading), -2, -1,
				end);
			if (IsJoined(own, others) || MathF.Abs(own.Point.Y - field.Sample(own.Point.X, own.Point.Z)) > LandingReachMeters)
			{
				continue;
			}

			after ??= field.Clone();
			touched = touched.Union(Land(after, own));
			result.Landed++;
		}

		after?.Round(touched);
		bool fits = WithPiers(document, after ?? field, ref request, replacing, out int piers);
		result.Piers = piers;
		PlacementCheck check = PrefabPlacement.Check(document, request, replacing);
		result.Placement = check;
		if (!fits && check.Result != PrefabPlacementResult.LimitReached)
		{
			// A pier longer than a file holds: the deck is too far over the ground for one.
			check.Result = PrefabPlacementResult.OutOfBounds;
			check.Edit = null;
			return result;
		}

		if (!check.IsOk)
		{
			return result;
		}

		MapObject placed = check.Object;
		if (after != null)
		{
			int disturbed = PrefabPlacement.Disturbed(document, field, after, touched, replacing);
			if (disturbed >= 0)
			{
				check.Result = PrefabPlacementResult.DisturbsNeighbour;
				check.Other = disturbed;
				check.Edit = null;
				return result;
			}
		}

		MapEdit edit = after == null ? new MapEdit() : MapEdit.Terrain(field, after, touched);
		if (after != null)
		{
			foreach (ObjectChange kept in KeepPiers(document, after, touched, replacing))
			{
				edit.Replace(kept.Index, kept.Before, kept.After);
			}
		}

		check.Edit = moving
			? edit.Replace(replacing, document.Objects[replacing], placed)
			: edit.Insert(document.Objects.Count, placed);
		return result;
	}

	/// <summary>The landing beyond an end: a pad at the deck's top, the deck's width and <see cref="LandingLengthMeters"/> long, skirted at 30°.</summary>
	public static SampleRect Land(HeightField field, in Connector end)
	{
		Footprint pad = LandingFootprint(end);
		return TerrainConform.Pad(field, pad, end.Point.Y, 0f, TerrainConform.SkirtGrade);
	}

	/// <summary>The rectangle an end's landing levels: beyond it, the deck's width across and <see cref="LandingLengthMeters"/> along.</summary>
	public static Footprint LandingFootprint(in Connector end)
	{
		Vector2 out_ = end.Direction;
		Vector3 centre = end.Point + (new Vector3(out_.X, 0f, out_.Y) * (LandingLengthMeters * 0.5f));
		return new Footprint(centre, end.Heading, PrefabSpecs.DeckWidthMeters * 0.5f, LandingLengthMeters * 0.5f);
	}

	/// <summary>
	/// Taking a section up (§6.5, §6.7): the record goes, and the ground it landed stays. A section
	/// joined to it whose end stood on its pier has a pier of its own put there, if it has supports
	/// on, in the same step: a neighbour is not left on nothing.
	/// </summary>
	public static MapEdit TakeUp(MapDocument document, int index)
	{
		var edit = new MapEdit();
		MapObject gone = document.Objects[index];
		if (PrefabSpecs.TryGet(gone.PrefabId, out PrefabSpec goneSpec) && goneSpec.Kind == PrefabKind.Bridge)
		{
			var trial = document.Clone();
			trial.Objects.RemoveAt(index);
			for (int i = 0; i < document.Objects.Count; i++)
			{
				MapObject other = document.Objects[i];
				if (i == index || !PrefabSpecs.TryGet(other.PrefabId, out PrefabSpec spec) || spec.Kind != PrefabKind.Bridge
					|| spec.ValueOf(other, PrefabSpecs.SupportsKey) == 0)
				{
					continue;
				}

				MapObject repiered = Repiered(trial, other, i > index ? i - 1 : i);
				if (repiered != null)
				{
					edit.Replace(i, other, repiered);
				}
			}
		}

		return edit.Remove(index, gone);
	}

	/// <summary>A section with a pier under every end of it that needs one and has none; null when it lacks none.</summary>
	private static MapObject Repiered(MapDocument document, MapObject section, int index)
	{
		var parameters = new List<MapParameter>();
		foreach (MapParameter parameter in section.Parameters)
		{
			parameters.Add(parameter);
		}

		bool changed = false;
		for (int end = 0; end < 2; end++)
		{
			byte key = end == 0 ? PrefabSpecs.PierBackKey : PrefabSpecs.PierFrontKey;
			if (section.Parameter(key, 0) > 0 || !NeedsPier(document, section, end, index))
			{
				continue;
			}

			int length = Math.Min(PierCentimeters(document.Terrain, section, end), PrefabSpecs.MaxPierCentimeters);
			if (length > 0)
			{
				parameters.RemoveAll(p => p.Key == key);
				parameters.Add(new MapParameter(key, length));
				changed = true;
			}
		}

		return changed
			? new MapObject(section.PrefabId, section.X, section.Y, section.Z, section.Yaw, section.Flags, parameters.ToArray())
			: null;
	}

	// ---- small things ---------------------------------------------------------------------------------

	private static bool SameOwner(in Connector a, in Connector b) =>
		(a.Object >= 0 && a.Object == b.Object) || (a.Road >= 0 && a.Road == b.Road)
		|| (a.Object == -2 && b.Object == -2);

	private static float Plan(Vector3 a, Vector3 b)
	{
		float dx = a.X - b.X;
		float dz = a.Z - b.Z;
		return MathF.Sqrt((dx * dx) + (dz * dz));
	}

	/// <summary>An angle in [0, 2π).</summary>
	public static float Wrap(float angle)
	{
		float tau = 2f * MathF.PI;
		angle %= tau;
		return angle < 0f ? angle + tau : angle;
	}
}
