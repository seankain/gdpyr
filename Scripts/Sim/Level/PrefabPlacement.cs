using System;
using System.Collections.Generic;
using Godot;

namespace Gdpyr.Sim.Level;

/// <summary>Why a prefab may or may not go where it was asked (docs/LEVEL_EDITOR.md §6.5), in the order they are checked.</summary>
public enum PrefabPlacementResult : byte
{
	/// <summary>The object and its pad are applied, as one step to undo.</summary>
	Ok,

	/// <summary>No prefab of that id in this build.</summary>
	UnknownPrefab,

	/// <summary>The map already holds as many of the kind as it may, or as many objects.</summary>
	LimitReached,

	/// <summary>A box leaves ±120 m, a round's piece has its origin outside ±115 m, or the height is outside LE-D4.</summary>
	OutOfBounds,

	/// <summary>A box overlaps a box of another object or a pier, in plan by more than 0.05 m and in height (LE-D11).</summary>
	InTheWay,

	/// <summary>The average grade under the footprint is over the prefab's.</summary>
	TooSteep,

	/// <summary>The pad's level, or a round's piece, is under the water.</summary>
	InWater,

	/// <summary>Levelling would move a sample inside another object's pad.</summary>
	DisturbsNeighbour,

	/// <summary>A structure within 10 m of a barracks door, or a barracks whose door is within 10 m of one.</summary>
	BlocksDoor,
}

/// <summary>What is asked to be placed: a prefab, where, which way, and for a bridge section at what height.</summary>
public struct PlacementRequest
{
	public ushort PrefabId;

	/// <summary>Where [m]. Quantized to the centimetre as the file stores it, before anything is checked.</summary>
	public float X;

	public float Z;

	/// <summary>Which way [radians]: free, or in 15° steps as the editor offers (§6.5).</summary>
	public float Yaw;

	/// <summary>The deck's top at its −Z end [m], for a prefab whose height is free (§6.7). Otherwise unread.</summary>
	public float Height;

	public MapParameter[] Parameters;

	public PlacementRequest(ushort prefabId, float x, float z, float yaw, float height = 0f,
		MapParameter[] parameters = null)
	{
		PrefabId = prefabId;
		X = x;
		Z = z;
		Yaw = yaw;
		Height = height;
		Parameters = parameters;
	}
}

/// <summary>The answer the ghost shows (§8.4), and for <see cref="PrefabPlacementResult.Ok"/> the edit that places it.</summary>
public sealed class PlacementCheck
{
	public PrefabPlacementResult Result { get; internal set; }

	/// <summary>The object as it would be stored. Null only for <see cref="PrefabPlacementResult.UnknownPrefab"/>.</summary>
	public MapObject Object { get; internal set; }

	/// <summary>The average grade under the footprint [degrees]: what the ghost shows and the limit is checked against.</summary>
	public float GradeDegrees { get; internal set; }

	/// <summary>The pad's level [m], or the height the object stands at when it makes none.</summary>
	public float Level { get; internal set; }

	/// <summary>The object in the way, disturbed, or whose door is blocked; −1 for none.</summary>
	public int Other { get; internal set; } = -1;

	/// <summary>For <see cref="PrefabPlacementResult.Ok"/>: the object and its pad as one step, not yet applied.</summary>
	public MapEdit Edit { get; internal set; }

	public bool IsOk => Result == PrefabPlacementResult.Ok;

	public override string ToString() => PrefabPlacement.Describe(Result);
}

/// <summary>
/// May a prefab go here, and the pad it makes (docs/LEVEL_EDITOR.md §6.5). One
/// function for the editor's ghost and its click, and the generator; the validator
/// asks the same questions of a placed object with the same arithmetic.
///
/// <b>The average grade and the pad.</b> A plane <c>h = a + b·x + c·z</c> is fitted by
/// least squares to the samples under the footprint: (b, c) is the average grade, and
/// atan √(b² + c²) is what the limit is checked against. A least-squares plane passes
/// through its samples' centroid, so their mean is the plane's height there, and
/// levelling to it cuts as much as it fills. The object's height is that mean to the
/// centimetre, as the file stores it, and the pad's level is the step nearest that
/// (<see cref="PadLevelSample"/>): a function of what is stored, so the pad can be
/// levelled again from the file alone and lands on the same step. Then §6.3: every
/// sample within 1.5 m of the footprint is at the level, exactly, and the skirt runs
/// out at 30°. A footprint over fewer than three samples, or over samples in one
/// line, takes the triangle under its centre. A prefab without a pad stands at the
/// ground's height under its origin; a bridge section where it is put.
/// </summary>
public static class PrefabPlacement
{
	/// <summary>Ground kept clear in front of a barracks door [m]: the strategist's own rule.</summary>
	public const float DoorClearanceMeters = StructurePlacement.DoorClearanceMeters;

	/// <summary>
	/// The step a padded object's pad is levelled to, from its height in centimetres:
	/// the nearest, round(y · 256 / 100), in integers. There is no tie: 256·y + 50 is
	/// never a multiple of 100 that rounding would have to break.
	/// </summary>
	public static short PadLevelSample(short yCentimeters)
	{
		int steps = (int)Math.Floor(((yCentimeters * 256) + 50) / 100.0);
		return (short)Math.Clamp(steps, MapLimits.MinHeightSample, MapLimits.MaxHeightSample);
	}

	/// <summary>A padded object's pad's level [m].</summary>
	public static float PadLevel(MapObject placed) => HeightField.ToMeters(PadLevelSample(placed.Y));

	/// <summary>
	/// The height an object stands at [m]: its pad's level when it makes one, else its
	/// stored height. What is under the water is asked of this, by placement and by the
	/// validator alike: the two differ by up to 2 mm, and must not give two answers.
	/// </summary>
	public static float StandingHeight(MapObject placed) =>
		PrefabSpecs.TryGet(placed.PrefabId, out PrefabSpec spec) && spec.MakesPad
			? PadLevel(placed)
			: Quantize.I16ToMeters(placed.Y);

	/// <summary>
	/// The plane fitted to the samples under a footprint: the average grade along x
	/// and z, and the samples' mean. Over fewer than three samples, or samples in a
	/// line, the triangle under the footprint's centre and the ground's height there.
	/// </summary>
	public static void Fit(HeightField field, in PadArea area, out float gradeX, out float gradeZ, out float mean,
		out int samples)
	{
		SampleRect bounds = area.Bounds(field, 0f);
		double sum = 0d;
		double sumX = 0d;
		double sumZ = 0d;
		samples = 0;
		for (int z = bounds.MinZ; z <= bounds.MaxZ; z++)
		{
			for (int x = bounds.MinX; x <= bounds.MaxX; x++)
			{
				float wx = field.WorldX(x);
				float wz = field.WorldZ(z);
				if (!area.Covers(wx, wz))
				{
					continue;
				}

				sum += field[x, z];
				sumX += wx;
				sumZ += wz;
				samples++;
			}
		}

		if (samples >= 3)
		{
			double mx = sumX / samples;
			double mz = sumZ / samples;
			double mh = sum / samples;
			double sxx = 0d;
			double szz = 0d;
			double sxz = 0d;
			double sxh = 0d;
			double szh = 0d;
			for (int z = bounds.MinZ; z <= bounds.MaxZ; z++)
			{
				for (int x = bounds.MinX; x <= bounds.MaxX; x++)
				{
					float wx = field.WorldX(x);
					float wz = field.WorldZ(z);
					if (!area.Covers(wx, wz))
					{
						continue;
					}

					double dx = wx - mx;
					double dz = wz - mz;
					double dh = field[x, z] - mh;
					sxx += dx * dx;
					szz += dz * dz;
					sxz += dx * dz;
					sxh += dx * dh;
					szh += dz * dh;
				}
			}

			double determinant = (sxx * szz) - (sxz * sxz);
			if (determinant > 1e-6 * Math.Max(1d, sxx * szz))
			{
				gradeX = (float)(((sxh * szz) - (szh * sxz)) / determinant);
				gradeZ = (float)(((szh * sxx) - (sxh * sxz)) / determinant);
				mean = (float)mh;
				return;
			}
		}

		Triangle(field, area.X, area.Z, out gradeX, out gradeZ);
		mean = field.Sample(area.X, area.Z);
	}

	/// <summary>The gradient of the collider's triangle under a point: §6.1's pair, the first where the fractions sum to at most 1.</summary>
	public static void Triangle(HeightField field, float x, float z, out float gradeX, out float gradeZ)
	{
		float gx = Math.Clamp(x - field.OriginX, 0f, field.Width - 1);
		float gz = Math.Clamp(z - field.OriginZ, 0f, field.Depth - 1);
		int cx = Math.Min((int)gx, field.Width - 2);
		int cz = Math.Min((int)gz, field.Depth - 2);
		float h00 = field[cx, cz];
		float h10 = field[cx + 1, cz];
		float h01 = field[cx, cz + 1];
		float h11 = field[cx + 1, cz + 1];
		if ((gx - cx) + (gz - cz) <= 1f)
		{
			gradeX = h10 - h00;
			gradeZ = h01 - h00;
		}
		else
		{
			gradeX = h11 - h01;
			gradeZ = h11 - h10;
		}
	}

	/// <summary>
	/// The checks, in order; the first that fails is the answer. For
	/// <see cref="PrefabPlacementResult.Ok"/> the check carries the edit — the object
	/// appended, and the samples its pad moved, rounded — which nothing has applied:
	/// <see cref="EditHistory.Do"/> applies it as one step to undo.
	///
	/// With <paramref name="replacing"/>, the object at that index is taken up and placed
	/// again as asked — turned, or given other parameters (§8.4, LE5): it is not counted,
	/// in the way, a neighbour or a door to itself, and the edit replaces it where it is in
	/// the file, so the world names it as before. The ground its old pad levelled stays.
	/// </summary>
	public static PlacementCheck Check(MapDocument document, in PlacementRequest request, int replacing = -1)
	{
		var check = new PlacementCheck();
		if (!PrefabSpecs.TryGet(request.PrefabId, out PrefabSpec spec))
		{
			check.Result = PrefabPlacementResult.UnknownPrefab;
			return check;
		}

		HeightField field = document.Terrain;
		short x = Quantize.MetersToI16(request.X);
		short z = Quantize.MetersToI16(request.Z);
		ushort yaw = Quantize.YawToU16(request.Yaw);
		var provisional = new MapObject(spec.Id, x, 0, z, yaw, 0, request.Parameters);
		bool hasArea = PrefabSpecs.TryGetPadArea(provisional, out PadArea area);
		float mean = 0f;
		if (hasArea)
		{
			Fit(field, area, out float gradeX, out float gradeZ, out mean, out _);
			check.GradeDegrees = HeightField.Degrees(MathF.Sqrt((gradeX * gradeX) + (gradeZ * gradeZ)));
		}

		short y = Quantize.MetersToI16(spec.MakesPad
			? mean
			: spec.FreeHeight
				? request.Height
				: field.Sample(Quantize.I16ToMeters(x), Quantize.I16ToMeters(z)));
		MapObject placed = provisional.WithY(y);
		check.Object = placed;
		check.Level = StandingHeight(placed);

		bool moving = replacing >= 0 && replacing < document.Objects.Count;
		replacing = moving ? replacing : -1;
		int alreadyOfGroup = document.CountOf(spec.Group);
		if (moving && PrefabSpecs.TryGet(document.Objects[replacing].PrefabId, out PrefabSpec was) && was.Group == spec.Group)
		{
			alreadyOfGroup--;
		}

		if ((!moving && document.Objects.Count >= MapLimits.MaxObjects) || alreadyOfGroup >= PrefabSpecs.MaxOf(spec.Group))
		{
			check.Result = PrefabPlacementResult.LimitReached;
			return check;
		}

		if (IsOutOfBounds(placed))
		{
			check.Result = PrefabPlacementResult.OutOfBounds;
			return check;
		}

		List<PlacedBox> boxes = PrefabSpecs.BoxesOf(placed);
		var others = new List<PlacedBox>(16);
		for (int i = 0; i < document.Objects.Count; i++)
		{
			if (i == replacing)
			{
				continue;
			}

			others.Clear();
			PrefabSpecs.AddBoxes(document.Objects[i], others);
			if (PrefabSpecs.InTheWay(boxes, others))
			{
				check.Result = PrefabPlacementResult.InTheWay;
				check.Other = i;
				return check;
			}
		}

		if (spec.HasGradeLimit && check.GradeDegrees > spec.SteepestDegrees)
		{
			check.Result = PrefabPlacementResult.TooSteep;
			return check;
		}

		if ((spec.MakesPad || spec.IsRoundPiece) && check.Level < document.Bands.WaterLevelMeters)
		{
			check.Result = PrefabPlacementResult.InWater;
			return check;
		}

		HeightField after = null;
		SampleRect touched = SampleRect.Empty;
		if (spec.MakesPad)
		{
			after = field.Clone();
			touched = area.Apply(after, check.Level);
			after.Round(touched);
			int disturbed = Disturbed(document, field, after, touched, replacing);
			if (disturbed >= 0)
			{
				check.Result = PrefabPlacementResult.DisturbsNeighbour;
				check.Other = disturbed;
				return check;
			}
		}

		// No object blocks its own door: a barracks is not a structure, so there is nothing to skip.
		int door = BlockedDoor(document, placed, -1);
		if (door >= 0)
		{
			check.Result = PrefabPlacementResult.BlocksDoor;
			check.Other = door;
			return check;
		}

		MapEdit edit = after == null ? new MapEdit() : MapEdit.Terrain(field, after, touched);
		check.Edit = moving
			? edit.Replace(replacing, document.Objects[replacing], placed)
			: edit.Insert(document.Objects.Count, placed);
		check.Result = PrefabPlacementResult.Ok;
		return check;
	}

	/// <summary>Taking an object up: the record goes, and the ground it levelled stays, as any sculpted ground does (§6.5).</summary>
	public static MapEdit TakeUp(MapDocument document, int index) =>
		new MapEdit().Remove(index, document.Objects[index]);

	/// <summary>
	/// A pad is kept (§6.5): when a stroke or a road ends and its rectangle reaches the
	/// level ground of any pad, the pads are levelled again and rounded — every one, in
	/// the order the objects were placed, as placing them did. Only the pads it reaches
	/// would not do: pads overlap, and one levelled again can move samples a pad placed
	/// after it had set, which only that pad levelled again puts back.
	///
	/// Each pad is, sample by sample, a clamp to its band, and on the lattice of steps
	/// rounding keeps it one (§6.3); clamps composed are a clamp or a constant, so doing
	/// this twice is doing it once, and it changes nothing unless an edit had moved a pad
	/// or its skirt. No pad moves another's level ground — each was refused
	/// <see cref="PrefabPlacementResult.DisturbsNeighbour"/> at placing that would — so
	/// every object still stands on its pad. Returns the rectangle of the stored samples
	/// it changed, which belong to the same step as the edit: empty when none moved.
	/// </summary>
	public static SampleRect KeepPads(MapDocument document, in SampleRect edited)
	{
		HeightField field = document.Terrain;
		SampleRect reached = edited.Grown(1);
		bool any = false;
		foreach (MapObject placed in document.Objects)
		{
			any |= PrefabSpecs.TryGet(placed.PrefabId, out PrefabSpec spec) && spec.MakesPad
				&& PrefabSpecs.TryGetPadArea(placed, out PadArea area)
				&& !area.Bounds(field, TerrainConform.PadMarginMeters).Intersect(reached).IsEmpty;
		}

		if (!any)
		{
			return SampleRect.Empty;
		}

		HeightField before = field.Clone();
		SampleRect touched = SampleRect.Empty;
		foreach (MapObject placed in document.Objects)
		{
			if (PrefabSpecs.TryGet(placed.PrefabId, out PrefabSpec spec) && spec.MakesPad
				&& PrefabSpecs.TryGetPadArea(placed, out PadArea area))
			{
				SampleRect levelled = area.Apply(field, PadLevel(placed));
				field.Round(levelled);
				touched = touched.Union(levelled);
			}
		}

		// A height the operator put on a bound that rounds back to its own step was not moved.
		SampleRect moved = SampleRect.Empty;
		for (int z = touched.MinZ; z <= touched.MaxZ; z++)
		{
			for (int x = touched.MinX; x <= touched.MaxX; x++)
			{
				if (field[x, z] != before[x, z])
				{
					moved = moved.With(x, z);
				}
			}
		}

		return moved;
	}

	/// <summary>
	/// Whether an object is outside what a map holds: a box past ±120 m, a node's disc
	/// past it, a round's piece's origin past ±115 m, or a height outside LE-D4.
	/// </summary>
	public static bool IsOutOfBounds(MapObject placed)
	{
		if (!PrefabSpecs.TryGet(placed.PrefabId, out PrefabSpec spec))
		{
			return false;
		}

		Vector3 at = placed.Position;
		if (at.Y < MapLimits.MinHeightMeters || at.Y > MapLimits.MaxHeightMeters)
		{
			return true;
		}

		if (spec.IsRoundPiece && (MathF.Abs(at.X) > MapLimits.PlayHalfMeters || MathF.Abs(at.Z) > MapLimits.PlayHalfMeters))
		{
			return true;
		}

		if (spec.DiscFootprint && PrefabSpecs.TryGetPadArea(placed, out PadArea disc)
			&& (MathF.Abs(disc.X) + disc.Radius > MapLimits.FieldHalfMeters
				|| MathF.Abs(disc.Z) + disc.Radius > MapLimits.FieldHalfMeters))
		{
			return true;
		}

		foreach (PlacedBox box in PrefabSpecs.BoxesOf(placed))
		{
			for (int corner = 0; corner < 4; corner++)
			{
				Vector3 point = box.Plan.Corner(corner);
				if (MathF.Abs(point.X) > MapLimits.FieldHalfMeters || MathF.Abs(point.Z) > MapLimits.FieldHalfMeters)
				{
					return true;
				}
			}
		}

		return false;
	}

	/// <summary>
	/// The first object, other than <paramref name="skip"/>, with a sample of its level
	/// ground — under its footprint or within 1.5 m of it — that differs between
	/// <paramref name="before"/> and <paramref name="after"/> inside the rectangle; −1 for none.
	/// </summary>
	public static int Disturbed(MapDocument document, HeightField before, HeightField after, in SampleRect changed,
		int skip)
	{
		if (changed.IsEmpty)
		{
			return -1;
		}

		for (int i = 0; i < document.Objects.Count; i++)
		{
			MapObject other = document.Objects[i];
			if (i == skip || !PrefabSpecs.TryGet(other.PrefabId, out PrefabSpec spec) || !spec.MakesPad
				|| !PrefabSpecs.TryGetPadArea(other, out PadArea area))
			{
				continue;
			}

			SampleRect both = area.Bounds(before, TerrainConform.PadMarginMeters).Intersect(changed);
			for (int z = both.MinZ; z <= both.MaxZ; z++)
			{
				for (int x = both.MinX; x <= both.MaxX; x++)
				{
					if (before[x, z] != after[x, z]
						&& area.DistanceTo(before.WorldX(x), before.WorldZ(z)) <= TerrainConform.PadMarginMeters)
					{
						return i;
					}
				}
			}
		}

		return -1;
	}

	/// <summary>
	/// The first object whose door <paramref name="placed"/> blocks, or that blocks its
	/// door: a structure's footprint within 10 m of a barracks door. −1 for none.
	/// </summary>
	public static int BlockedDoor(MapDocument document, MapObject placed, int skip)
	{
		if (!PrefabSpecs.TryGet(placed.PrefabId, out PrefabSpec spec))
		{
			return -1;
		}

		bool structure = spec.Kind == PrefabKind.Structure;
		bool barracks = PrefabSpecs.TryGetDoor(placed, out Vector3 ownDoor);
		if (!structure && !barracks)
		{
			return -1;
		}

		PrefabSpecs.TryGetPadArea(placed, out PadArea own);
		for (int i = 0; i < document.Objects.Count; i++)
		{
			MapObject other = document.Objects[i];
			if (i == skip || !PrefabSpecs.TryGet(other.PrefabId, out PrefabSpec otherSpec))
			{
				continue;
			}

			if (structure && PrefabSpecs.TryGetDoor(other, out Vector3 door)
				&& own.DistanceTo(door.X, door.Z) < DoorClearanceMeters)
			{
				return i;
			}

			if (barracks && otherSpec.Kind == PrefabKind.Structure && PrefabSpecs.TryGetPadArea(other, out PadArea area)
				&& area.DistanceTo(ownDoor.X, ownDoor.Z) < DoorClearanceMeters)
			{
				return i;
			}
		}

		return -1;
	}

	public static string Describe(PrefabPlacementResult result) => result switch
	{
		PrefabPlacementResult.Ok => "ok",
		PrefabPlacementResult.UnknownPrefab => "no such prefab",
		PrefabPlacementResult.LimitReached => "the map holds as many as it may",
		PrefabPlacementResult.OutOfBounds => "off the map",
		PrefabPlacementResult.InTheWay => "something is in the way",
		PrefabPlacementResult.TooSteep => "the ground is too steep",
		PrefabPlacementResult.InWater => "under the water",
		PrefabPlacementResult.DisturbsNeighbour => "it would move another object's pad",
		PrefabPlacementResult.BlocksDoor => "in front of a barracks door",
		_ => result.ToString(),
	};
}
