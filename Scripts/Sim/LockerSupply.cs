using System;
using Godot;

namespace Gdpyr.Sim;

/// <summary>
/// What the locker's resupply tops up on a player (docs/LOADOUT.md §4.4): the
/// primary's spare rounds, the grenades and the vest's points. Copied out of the
/// player's weapons, grenades and vest for <see cref="LockerSupply.Step"/> and back.
/// </summary>
public struct KitState
{
	/// <summary>The primary's spare rounds; -1 for a weapon that reloads forever, which nothing tops up.</summary>
	public int Reserve;

	public int Grenades;

	public int ArmourPoints;

	public KitState(int reserve, int grenades, int armourPoints)
	{
		Reserve = reserve;
		Grenades = grenades;
		ArmourPoints = armourPoints;
	}
}

/// <summary>
/// When the resupply may next give each thing (docs/LOADOUT.md L-D7): the earliest
/// tick for the next pouch of rounds, the next grenade and the next armour points.
/// Kept on the player for the life, so stepping out of the reach and back in does
/// not buy a pouch sooner than the pace allows; the default is "at once".
/// </summary>
public struct SupplyPacing
{
	public uint NextRoundsTick;
	public uint NextGrenadeTick;
	public uint NextArmourTick;
}

/// <summary>What one <see cref="LockerSupply.Step"/> gave: rounds, grenades and armour points.</summary>
public readonly struct SupplyGiven
{
	public readonly int Rounds;
	public readonly int Grenades;
	public readonly int ArmourPoints;

	public SupplyGiven(int rounds, int grenades, int armourPoints)
	{
		Rounds = rounds;
		Grenades = grenades;
		ArmourPoints = armourPoints;
	}

	public bool Any => Rounds > 0 || Grenades > 0 || ArmourPoints > 0;
}

/// <summary>
/// Where a weapon locker reaches, and what it gives there (docs/LOADOUT.md §4.4,
/// L-D7, L-D8): a metre, flat, from the cabinet's footprint rather than from its
/// origin, and inside it a player's kit topped up at a pace, and not under fire.
///
/// The cabinet has no collider and a player can stand in it, and from the origin a
/// metre would reach 0.75 m past its front face but only 0.4 m past either end. The
/// footprint is the 1.2 × 0.5 m rectangle under it, turned with its node; the front —
/// the face with the racks — is the node's +Z.
///
/// Engine-free because the server's kit validator, the use key's resolution on both
/// ends of the prediction, a ground bot's walk to the locker and the resupply all
/// measure the same distance, and what the resupply gives is a question for
/// <c>dotnet test</c>.
/// </summary>
public static class LockerSupply
{
	/// <summary>The cabinet along the node's X: its width.</summary>
	public const float CabinetWidthMeters = 1.2f;

	/// <summary>The cabinet along the node's Z: its depth, racks to back.</summary>
	public const float CabinetDepthMeters = 0.5f;

	/// <summary>
	/// How far in front of the racks a bot sent to the locker stands: half its reach,
	/// so a bot that stops a little short is still at it.
	/// </summary>
	public const float StandOffMeters = SimConfig.LockerReachMeters * 0.5f;

	/// <summary>
	/// The flat distance from <paramref name="feet"/> to the footprint of a locker at
	/// <paramref name="origin"/> turned <paramref name="yaw"/> radians about +Y; 0 inside it.
	/// Height is ignored: a locker reaches whoever stands on the floor in front of it.
	/// </summary>
	public static float FootprintDistance(Vector3 feet, Vector3 origin, float yaw)
	{
		float dx = feet.X - origin.X;
		float dz = feet.Z - origin.Z;

		// Into the node's frame: its X axis is (cos, -sin) in world X/Z and its Z axis
		// (sin, cos), which is Godot's rotation about +Y.
		float cos = MathF.Cos(yaw);
		float sin = MathF.Sin(yaw);
		float localX = (dx * cos) - (dz * sin);
		float localZ = (dx * sin) + (dz * cos);

		float outX = MathF.Max(MathF.Abs(localX) - (CabinetWidthMeters * 0.5f), 0f);
		float outZ = MathF.Max(MathF.Abs(localZ) - (CabinetDepthMeters * 0.5f), 0f);
		return MathF.Sqrt((outX * outX) + (outZ * outZ));
	}

	/// <summary>Whether a footprint distance is within the locker's reach (L-D8).</summary>
	public static bool InReach(float distance) => distance <= SimConfig.LockerReachMeters;

	/// <summary>
	/// Where to stand to use a locker: <see cref="StandOffMeters"/> in front of its
	/// racks, on its centre line, at the origin's height.
	/// </summary>
	public static Vector3 StandPoint(Vector3 origin, float yaw)
	{
		float ahead = (CabinetDepthMeters * 0.5f) + StandOffMeters;
		return new Vector3(origin.X + (MathF.Sin(yaw) * ahead), origin.Y, origin.Z + (MathF.Cos(yaw) * ahead));
	}

	/// <summary>
	/// The line <paramref name="offsetMeters"/> out from the footprint all round, in the
	/// locker's own frame (X along the cabinet, Z through it, Y 0): the four sides, and a
	/// quarter circle of that radius round each corner in <paramref name="cornerSegments"/>
	/// chords. What the floor ring is drawn along (§7.5), so the ring is where the reach
	/// is. A closed loop of four times <c>cornerSegments + 1</c> points, the last joining
	/// the first.
	/// </summary>
	public static Vector3[] ReachOutline(float offsetMeters, int cornerSegments)
	{
		int segments = Math.Max(cornerSegments, 1);
		float halfWidth = CabinetWidthMeters * 0.5f;
		float halfDepth = CabinetDepthMeters * 0.5f;
		float radius = MathF.Max(offsetMeters, 0f);

		// Corners in turn round +Y, each sweeping its own quarter: +X+Z from +X to +Z,
		// then −X+Z, −X−Z and +X−Z. A side is the chord from one corner's last point to
		// the next corner's first, which share an angle.
		var points = new Vector3[4 * (segments + 1)];
		for (int corner = 0; corner < 4; corner++)
		{
			float cx = corner is 0 or 3 ? halfWidth : -halfWidth;
			float cz = corner is 0 or 1 ? halfDepth : -halfDepth;
			for (int i = 0; i <= segments; i++)
			{
				float angle = (corner + (i / (float)segments)) * (MathF.PI * 0.5f);
				points[(corner * (segments + 1)) + i] =
					new Vector3(cx + (MathF.Cos(angle) * radius), 0f, cz + (MathF.Sin(angle) * radius));
			}
		}

		return points;
	}

	// ---- resupply (L-D7) ---------------------------------------------------

	/// <summary>Ticks between two pouches' rounds: half a second.</summary>
	public const int PouchIntervalTicks = SimConfig.TickRate / 2;

	/// <summary>Ticks between two grenades: a second.</summary>
	public const int GrenadeIntervalTicks = SimConfig.TickRate;

	/// <summary>Armour points a second.</summary>
	public const int ArmourPerSecond = 30;

	/// <summary>
	/// Ticks between two handfuls of armour points: a tenth of a second. A tick's worth
	/// of thirty a second is half a point, and points are whole; given as three every
	/// six ticks, the vest still fills smoothly, and the event stream carries ten
	/// records a second for it rather than thirty.
	/// </summary>
	public const int ArmourIntervalTicks = SimConfig.TickRate / 10;

	/// <summary>Armour points one grant gives: <see cref="ArmourPerSecond"/> over the grants a second holds.</summary>
	public const int ArmourPerGrant = ArmourPerSecond * ArmourIntervalTicks / SimConfig.TickRate;

	/// <summary>
	/// Whether a player last hurt on <paramref name="lastDamagedTick"/> (0 for never)
	/// gets nothing at a locker on <paramref name="tick"/>: anything that hurt them in
	/// the last three seconds, as a unit's resupply waits (<see cref="Resupply.DamageLockoutTicks"/>).
	/// </summary>
	public static bool IsLockedOut(uint tick, uint lastDamagedTick) =>
		!Resupply.CanHeal(tick, lastDamagedTick, Resupply.DamageLockoutTicks);

	/// <summary>Ticks since a player was last hurt, -1 for never this life: a resupply record's <c>since_hurt</c>.</summary>
	public static int TicksSinceHurt(uint tick, uint lastDamagedTick) =>
		lastDamagedTick == 0 || tick < lastDamagedTick ? -1 : (int)Math.Min(tick - lastDamagedTick, int.MaxValue);

	/// <summary>The most each part of <paramref name="loadout"/> holds: what the resupply tops up to.</summary>
	public static KitState Ceiling(in LoadoutSelection loadout, in CarryStats primary) => new(
		Loadout.ReserveFor(loadout, primary),
		Math.Min((int)loadout.Grenades, SimConfig.MaxGrenades),
		BodyArmour.MaxPoints(loadout.Armour));

	/// <summary>Whether anything the resupply gives is short of what <paramref name="loadout"/> holds.</summary>
	public static bool NeedsAnything(in KitState kit, in LoadoutSelection loadout, in CarryStats primary)
	{
		KitState full = Ceiling(loadout, primary);
		return (kit.Reserve >= 0 && kit.Reserve < full.Reserve) || kit.Grenades < full.Grenades
			|| kit.ArmourPoints < full.ArmourPoints;
	}

	/// <summary>
	/// One tick of resupply for a living player in a locker's reach (L-D7): the next
	/// pouch's rounds — a magazine, or two launcher grenades — once
	/// <see cref="PouchIntervalTicks"/> have passed since the last, one grenade every
	/// <see cref="GrenadeIntervalTicks"/> and <see cref="ArmourPerGrant"/> points every
	/// <see cref="ArmourIntervalTicks"/>; each the moment it falls short and is due, so a
	/// player who walks up with three pouches empty has the first at once and the third a
	/// second later. Never past what <paramref name="loadout"/> holds, never taking
	/// anything away, nothing for a weapon that reloads forever, and nothing at all while
	/// <see cref="IsLockedOut"/>. Health is not resupplied. The magazine is not either: a
	/// pouch goes to the reserve, as a pouch picked up would.
	///
	/// Returns what it gave, for the server's counters and the <c>resupply</c> record.
	/// </summary>
	public static SupplyGiven Step(ref KitState kit, ref SupplyPacing pacing, in LoadoutSelection loadout,
		in CarryStats primary, uint tick, uint lastDamagedTick)
	{
		if (IsLockedOut(tick, lastDamagedTick))
		{
			return default;
		}

		KitState full = Ceiling(loadout, primary);

		int rounds = 0;
		if (kit.Reserve >= 0 && kit.Reserve < full.Reserve && tick >= pacing.NextRoundsTick)
		{
			rounds = Math.Min(Math.Max(primary.PouchRounds, 1), full.Reserve - kit.Reserve);
			kit.Reserve += rounds;
			pacing.NextRoundsTick = tick + PouchIntervalTicks;
		}

		int grenades = 0;
		if (kit.Grenades < full.Grenades && tick >= pacing.NextGrenadeTick)
		{
			grenades = 1;
			kit.Grenades++;
			pacing.NextGrenadeTick = tick + GrenadeIntervalTicks;
		}

		int points = 0;
		if (kit.ArmourPoints < full.ArmourPoints && tick >= pacing.NextArmourTick)
		{
			points = Math.Min(ArmourPerGrant, full.ArmourPoints - kit.ArmourPoints);
			kit.ArmourPoints += points;
			pacing.NextArmourTick = tick + ArmourIntervalTicks;
		}

		return new SupplyGiven(rounds, grenades, points);
	}
}
