using System;
using Godot;

namespace Gdpyr.Sim;

/// <summary>
/// The ground force's vehicles (docs/GROUND_VEHICLES.md): what a crew vehicle's
/// definition id names. A wire constant — the vehicle record's kind bits, the kiosk's
/// request byte and the agent API's <c>vehicle</c> op all carry it — and the index into
/// <c>CrewVehicleCatalog</c>'s table, so it is appended to and never reordered.
///
/// "Crew vehicle" in code because <c>Vehicle</c>, <c>IsVehicle</c> and
/// <c>VehicleDamageScale</c> already mean a strategist's unit that is a vehicle; the
/// players, the HUD and the agent API call these the 4x4 and the helicopter.
/// </summary>
public enum CrewVehicleKind : byte
{
	/// <summary>The 4x4 technical: a driver, a gunner at the mounted gun, a passenger in the bed.</summary>
	FourByFour = 0,

	/// <summary>The helicopter: a pilot and a passenger in the left door.</summary>
	Helicopter = 1,
}

/// <summary>What a seat is for (GV-D2). The order is the seat byte's on the wire and in events.</summary>
public enum SeatRole : byte
{
	/// <summary>The 4x4's driver or the helicopter's pilot: at the controls, never firing.</summary>
	Driver = 0,

	/// <summary>At the 4x4's mounted gun: its trigger is the gun's.</summary>
	Gunner = 1,

	/// <summary>In the bed or the door, firing their own primary or pistol inside the seat's arc.</summary>
	Passenger = 2,
}

/// <summary>What a seated player fires (GV-D6).</summary>
public enum SeatWeapon : byte
{
	/// <summary>Nothing: a driver's mouse belongs to the camera and a pilot's to the heading.</summary>
	None = 0,

	/// <summary>The vehicle's own gun, traversed and elevated relative to the hull.</summary>
	MountedGun = 1,

	/// <summary>Their own primary and pistol, as on foot: not the hammer, a grenade or a drone.</summary>
	OwnWeapon = 2,
}

/// <summary>
/// The directions a seat may fire in, relative to the hull (GV-D6): a yaw interval
/// measured anticlockwise from the nose — positive yaw is to the left, as it is for a
/// look (<see cref="Aim.Direction"/>) — and a pitch interval, radians. A yaw interval
/// of a full turn or more is every direction.
/// </summary>
public readonly struct SeatArc
{
	public readonly float YawMin;

	public readonly float YawMax;

	public readonly float PitchMin;

	public readonly float PitchMax;

	/// <summary>An arc; one whose maximum is below its minimum, on either axis, holds no direction.</summary>
	public SeatArc(float yawMin, float yawMax, float pitchMin, float pitchMax)
	{
		YawMin = yawMin;
		YawMax = yawMax;
		PitchMin = pitchMin;
		PitchMax = pitchMax;
	}

	/// <summary>From degrees, as a definition authors it.</summary>
	public static SeatArc FromDegrees(float yawMin, float yawMax, float pitchMin, float pitchMax) => new(
		Mathf.DegToRad(yawMin), Mathf.DegToRad(yawMax), Mathf.DegToRad(pitchMin), Mathf.DegToRad(pitchMax));

	/// <summary>No direction at all: a driver's.</summary>
	public static SeatArc None => new(1f, 0f, 1f, 0f);

	/// <summary>Holds no direction: a maximum below its minimum, or a value that is not a number.</summary>
	public bool IsEmpty => !(YawMax >= YawMin) || !(PitchMax >= PitchMin);

	public bool AllRound => !IsEmpty && YawMax - YawMin >= MathF.Tau - 1e-4f;

	/// <summary>
	/// Whether a direction relative to the hull — yaw from the nose, pitch from the
	/// hull's floor — is inside the arc, its edges included.
	/// </summary>
	public bool Contains(float relativeYaw, float relativePitch)
	{
		if (IsEmpty || !float.IsFinite(relativeYaw))
		{
			return false;
		}

		if (!(relativePitch >= PitchMin - 1e-5f && relativePitch <= PitchMax + 1e-5f))
		{
			return false;
		}

		if (AllRound)
		{
			return true;
		}

		float width = YawMax - YawMin;
		float offset = relativeYaw - YawMin;
		offset -= MathF.Floor(offset / MathF.Tau) * MathF.Tau;

		// The far edge, wrapped, can come back as a hair under a full turn.
		return offset <= width + 1e-5f || offset >= MathF.Tau - 1e-5f;
	}
}

/// <summary>
/// One seat of a crew vehicle, in the vehicle's own frame (<see cref="CrewPose"/>):
/// what it is for, whether the hull encloses it, the capsule a seated player's hitbox
/// becomes, their eye, the door they get in by and the point they get out at, and the
/// arc they may fire in.
/// </summary>
public readonly struct CrewSeat
{
	public readonly SeatRole Role;

	/// <summary>
	/// Inside a hull box (GV-D11): a round reaches the hull first and a blast skips the
	/// occupant. A driver's or pilot's seat; a gunner and a passenger are exposed.
	/// </summary>
	public readonly bool Enclosed;

	/// <summary>The seated player's hitbox, in the vehicle's frame.</summary>
	public readonly HitCapsule Capsule;

	public readonly Vector3 Eye;

	/// <summary>Where a player getting in stands: the nearest free seat's door is the one taken.</summary>
	public readonly Vector3 Door;

	/// <summary>Where this seat's occupant is put first when they get out.</summary>
	public readonly Vector3 Exit;

	public readonly SeatArc Arc;

	public CrewSeat(SeatRole role, bool enclosed, in HitCapsule capsule, Vector3 eye, Vector3 door, Vector3 exit,
		in SeatArc arc)
	{
		Role = role;
		Enclosed = enclosed;
		Capsule = capsule;
		Eye = eye;
		Door = door;
		Exit = exit;
		Arc = arc;
	}
}

/// <summary>One box of a hull (GV-D9, §5.4), in the vehicle's own frame.</summary>
public readonly struct HullBox
{
	public readonly Vector3 Center;

	public readonly Vector3 HalfExtents;

	public HullBox(Vector3 center, Vector3 halfExtents)
	{
		Center = center;
		HalfExtents = halfExtents;
	}

	/// <summary>Where it is with the vehicle at <paramref name="pose"/>.</summary>
	public HitObb Place(in CrewPose pose) => pose.Place(Center, HalfExtents);

	/// <summary>The box at rest, in the vehicle's frame.</summary>
	public HitObb Local => HitObb.Aligned(Center, HalfExtents);
}

/// <summary>
/// A crew vehicle's seats and hull as its definition authors them: parallel arrays,
/// which is what a <c>.tres</c> file holds without sub-resources, turned into the
/// structs the rules read. One builder for the engine's definition and for the tests
/// that read the same files, so the two never disagree about what a file means.
/// </summary>
public static class CrewLayout
{
	/// <summary>The most seats one crew vehicle has: the vehicle record carries three peer ids.</summary>
	public const int MaxSeats = 3;

	/// <summary>The most boxes one hull has: the 4x4's cab, bonnet, bed and gun shield.</summary>
	public const int MaxHullBoxes = 4;

	/// <summary>
	/// The seats, as many as every array holds, up to <see cref="MaxSeats"/>. A seat's arc
	/// is in degrees: yaw as (min, max), pitch as (min, max). A missing value is zero —
	/// a seat no file can get wrong into a crash, and one the file tests then fail on.
	/// </summary>
	public static CrewSeat[] Seats(ReadOnlySpan<int> roles, ReadOnlySpan<int> enclosed,
		ReadOnlySpan<Vector3> capsuleBottoms, ReadOnlySpan<Vector3> capsuleTops, ReadOnlySpan<float> capsuleRadii,
		ReadOnlySpan<Vector3> eyes, ReadOnlySpan<Vector3> doors, ReadOnlySpan<Vector3> exits,
		ReadOnlySpan<Vector2> arcYawDegrees, ReadOnlySpan<Vector2> arcPitchDegrees)
	{
		int count = Math.Min(roles.Length, MaxSeats);
		var seats = new CrewSeat[count];
		for (int i = 0; i < count; i++)
		{
			var role = (SeatRole)Math.Clamp(roles[i], 0, (int)SeatRole.Passenger);
			Vector2 yaw = At(arcYawDegrees, i);
			Vector2 pitch = At(arcPitchDegrees, i);
			SeatArc arc = role == SeatRole.Driver ? SeatArc.None : SeatArc.FromDegrees(yaw.X, yaw.Y, pitch.X, pitch.Y);
			seats[i] = new CrewSeat(role, At(enclosed, i) != 0,
				new HitCapsule(At(capsuleBottoms, i), At(capsuleTops, i), At(capsuleRadii, i)),
				At(eyes, i), At(doors, i), At(exits, i), arc);
		}

		return seats;
	}

	/// <summary>The hull's boxes, as many as both arrays hold, up to <see cref="MaxHullBoxes"/>.</summary>
	public static HullBox[] Hull(ReadOnlySpan<Vector3> centres, ReadOnlySpan<Vector3> halfExtents)
	{
		int count = Math.Min(Math.Min(centres.Length, halfExtents.Length), MaxHullBoxes);
		var boxes = new HullBox[count];
		for (int i = 0; i < count; i++)
		{
			boxes[i] = new HullBox(centres[i], halfExtents[i]);
		}

		return boxes;
	}

	/// <summary>
	/// The corners of a box with its twelve edges bevelled by <paramref name="bevel"/>:
	/// the points of the convex hull a crew vehicle collides as (docs/GROUND_VEHICLES.md
	/// §6.1), so it slides off a wall's end rather than catching on it. Three points per
	/// corner, twenty-four in all, each inside the box: each on one of the corner's three
	/// faces, pulled in by the bevel along the other two axes. Their hull keeps every face
	/// flat, inset by the bevel all round, and cuts each edge at 45° — not only the
	/// corners, which a point pulled in along one axis would cut and leave every edge
	/// sharp between them. A bevel is at most half the shortest side.
	/// </summary>
	public static Vector3[] BevelledBox(Vector3 centre, Vector3 halfExtents, float bevel)
	{
		Vector3 h = new(MathF.Max(halfExtents.X, 0f), MathF.Max(halfExtents.Y, 0f), MathF.Max(halfExtents.Z, 0f));
		float b = Math.Clamp(bevel, 0f, MathF.Min(h.X, MathF.Min(h.Y, h.Z)));
		var points = new Vector3[24];
		int at = 0;
		for (int corner = 0; corner < 8; corner++)
		{
			float sx = (corner & 1) == 0 ? -1f : 1f;
			float sy = (corner & 2) == 0 ? -1f : 1f;
			float sz = (corner & 4) == 0 ? -1f : 1f;
			points[at++] = centre + new Vector3(sx * h.X, sy * (h.Y - b), sz * (h.Z - b));
			points[at++] = centre + new Vector3(sx * (h.X - b), sy * h.Y, sz * (h.Z - b));
			points[at++] = centre + new Vector3(sx * (h.X - b), sy * (h.Y - b), sz * h.Z);
		}

		return points;
	}

	private static T At<T>(ReadOnlySpan<T> values, int index) => index < values.Length ? values[index] : default;
}

/// <summary>What the server knows about a player pressing the use key beside a crew vehicle.</summary>
public readonly struct EnterSituation
{
	/// <summary>A living ground-force player.</summary>
	public readonly bool Alive;

	public readonly bool GroundForce;

	public readonly CarryKind Carrying;

	/// <summary>Behind a heavy gun.</summary>
	public readonly bool Mounted;

	/// <summary>Flying an FPV drone.</summary>
	public readonly bool FlyingDrone;

	/// <summary>Already in a seat of any vehicle.</summary>
	public readonly bool Seated;

	public readonly bool VehicleDestroyed;

	/// <summary>The vehicle's speed [m/s], all three axes.</summary>
	public readonly float VehicleSpeed;

	/// <summary>Whether it has a free seat.</summary>
	public readonly bool FreeSeat;

	public readonly CrewVehicleKind Kind;

	/// <summary>A helicopter on the ground (<see cref="HelicopterState.Grounded"/>); ignored for a 4x4.</summary>
	public readonly bool Grounded;

	public EnterSituation(bool alive, bool groundForce, CarryKind carrying, bool mounted, bool flyingDrone, bool seated,
		bool vehicleDestroyed, float vehicleSpeed, bool freeSeat, CrewVehicleKind kind, bool grounded)
	{
		Alive = alive;
		GroundForce = groundForce;
		Carrying = carrying;
		Mounted = mounted;
		FlyingDrone = flyingDrone;
		Seated = seated;
		VehicleDestroyed = vehicleDestroyed;
		VehicleSpeed = vehicleSpeed;
		FreeSeat = freeSeat;
		Kind = kind;
		Grounded = grounded;
	}
}

/// <summary>
/// The rules for getting into, moving about and getting out of a crew vehicle, and for
/// what a seat may fire (docs/GROUND_VEHICLES.md §5.3), as pure functions.
///
/// Engine-free for the reason the use key's table is: both ends of the prediction ask
/// what a press meant, and the server asks again with the world in hand. The engine
/// half checks an exit point against the physics world; everything here has an answer
/// without one.
/// </summary>
public static class CrewSeats
{
	/// <summary>Faster than this a vehicle cannot be got into [m/s]: a walk's pace and a bit.</summary>
	public const float MaxEnterSpeedMetersPerSecond = 3f;

	/// <summary>
	/// The highest a helicopter may be off the ground for anybody to get out [m]: the
	/// game's "on the ground", a capture pad's (<see cref="CapturePad.VerticalToleranceMeters"/>).
	/// There is no fall damage and no parachute, so higher is not a decision (§15).
	/// </summary>
	public const float MaxExitHeightMeters = CapturePad.VerticalToleranceMeters;

	/// <summary>Seats closer than this to a player than each other count as tied, and go to the lower.</summary>
	public const float SeatTieMeters = 1e-3f;

	/// <summary>
	/// Whether a player may get in (§5.3): alive, on the ground force, carrying
	/// nothing, not behind a gun, not flying a drone, not already seated, the vehicle
	/// not destroyed and moving at <see cref="MaxEnterSpeedMetersPerSecond"/> or less,
	/// a free seat, and a helicopter on the ground.
	/// </summary>
	public static bool MayEnter(in EnterSituation situation) =>
		situation.Alive
		&& situation.GroundForce
		&& situation.Carrying == CarryKind.None
		&& !situation.Mounted
		&& !situation.FlyingDrone
		&& !situation.Seated
		&& !situation.VehicleDestroyed
		&& situation.VehicleSpeed <= MaxEnterSpeedMetersPerSecond
		&& situation.FreeSeat
		&& (situation.Kind != CrewVehicleKind.Helicopter || situation.Grounded);

	/// <summary>
	/// The seat a player at <paramref name="feet"/> gets (§5.3): the free one whose door
	/// is nearest, a tie to the lower seat. <paramref name="occupants"/> runs beside
	/// <paramref name="seats"/>, a peer id or 0 for empty; a seat with no entry is free.
	/// −1 when every seat is taken.
	/// </summary>
	public static int PickSeat(ReadOnlySpan<CrewSeat> seats, ReadOnlySpan<int> occupants, in CrewPose pose, Vector3 feet)
	{
		int best = -1;
		float bestDistance = float.PositiveInfinity;
		for (int i = 0; i < seats.Length; i++)
		{
			if (!IsFree(occupants, i))
			{
				continue;
			}

			float distance = pose.ToWorld(seats[i].Door).DistanceTo(feet);
			if (!float.IsFinite(distance))
			{
				distance = float.MaxValue;
			}

			if (best < 0 || distance < bestDistance - SeatTieMeters)
			{
				best = i;
				bestDistance = distance;
			}
		}

		return best;
	}

	/// <summary>
	/// The seat a hold of the use key moves the occupant of <paramref name="current"/>
	/// to (§5.3): the next free one in seat order, round past the last; −1 when none is.
	/// </summary>
	public static int NextSeat(int seatCount, ReadOnlySpan<int> occupants, int current)
	{
		int count = Math.Clamp(seatCount, 0, CrewLayout.MaxSeats);
		for (int step = 1; step < count; step++)
		{
			int seat = (((current + step) % count) + count) % count;
			if (IsFree(occupants, seat))
			{
				return seat;
			}
		}

		return -1;
	}

	/// <summary>Whether anybody may get out (§5.3): out of a helicopter only within <see cref="MaxExitHeightMeters"/> of the ground.</summary>
	public static bool MayExit(CrewVehicleKind kind, float heightAboveGround) =>
		kind != CrewVehicleKind.Helicopter || heightAboveGround <= MaxExitHeightMeters;

	/// <summary>How many places <see cref="ExitCandidate"/> offers before it wraps.</summary>
	public static int ExitCandidateCount(int seatCount, float vehicleRadius, float passengerRadius) =>
		Math.Clamp(seatCount, 0, CrewLayout.MaxSeats) + Cargo.ExitSlotCount(vehicleRadius, passengerRadius);

	/// <summary>
	/// The <paramref name="ordinal"/>-th place the occupant of <paramref name="seat"/> may
	/// be put when they get out (§5.3, §6.3): this seat's exit point, then the other
	/// seats' in seat order, then the ring of exit slots round the vehicle that units
	/// riding in the strategist's vehicles use (<see cref="Cargo.ExitSlot"/>), centred on
	/// <paramref name="pose"/>'s position. Candidates only: the engine half asks the world
	/// whether each is clear, reachable and on the ground, and takes the first that is.
	/// Past the last it wraps.
	/// </summary>
	public static Vector3 ExitCandidate(ReadOnlySpan<CrewSeat> seats, int seat, int ordinal, in CrewPose pose,
		float vehicleRadius, float passengerRadius)
	{
		int seatCount = Math.Min(seats.Length, CrewLayout.MaxSeats);
		int total = ExitCandidateCount(seatCount, vehicleRadius, passengerRadius);
		int index = total > 0 ? Math.Max(ordinal, 0) % total : 0;

		if (index < seatCount)
		{
			int own = Math.Clamp(seat, 0, Math.Max(seatCount - 1, 0));
			int pick;
			if (index == 0)
			{
				pick = own;
			}
			else
			{
				// The others in seat order, this one left out.
				pick = index - 1 < own ? index - 1 : index;
			}

			return pose.ToWorld(seats[pick].Exit);
		}

		return Cargo.ExitSlot(pose.Position, pose.Yaw, vehicleRadius, passengerRadius, index - seatCount);
	}

	/// <summary>What a seat's occupant fires (GV-D6).</summary>
	public static SeatWeapon WeaponOf(SeatRole role) => role switch
	{
		SeatRole.Gunner => SeatWeapon.MountedGun,
		SeatRole.Passenger => SeatWeapon.OwnWeapon,
		_ => SeatWeapon.None,
	};

	/// <summary>
	/// Whether the trigger does anything this tick (§5.3): never for a driver or pilot;
	/// always for the gunner, whose gun is clamped to its mount rather than its trigger
	/// cut (<see cref="GunDirection"/>); for a passenger, while their aim — relative to
	/// the hull, <see cref="RelativeAim"/> — is inside the seat's arc.
	/// </summary>
	public static bool MayFire(in CrewSeat seat, float relativeYaw, float relativePitch) => seat.Role switch
	{
		SeatRole.Gunner => true,
		SeatRole.Passenger => seat.Arc.Contains(relativeYaw, relativePitch),
		_ => false,
	};

	/// <summary>
	/// A world look, as the hull at <paramref name="pose"/> sees it: yaw from its nose,
	/// positive to the left, and pitch from its floor. The gunner's and a passenger's
	/// look stay world angles (GV-D7), so the bounce moves the arc, not the aim.
	/// </summary>
	public static void RelativeAim(in CrewPose pose, float aimYaw, float aimPitch, out float relativeYaw,
		out float relativePitch)
	{
		Vector3 local = pose.DirectionToLocal(Aim.Direction(aimYaw, aimPitch));
		Aim.Angles(local, out relativeYaw, out relativePitch);
	}

	/// <summary>
	/// Where the mounted gun points, given where its gunner looks (§5.8): their look
	/// relative to the hull, its yaw clamped to <paramref name="traverseRadians"/> either
	/// side of the nose — a full turn for the 4x4's gun — and its pitch to
	/// [<paramref name="elevationMin"/>, <paramref name="elevationMax"/>], then back into
	/// the world. The heavy gun's rule (<see cref="EmplacementSim.Traverse"/>) with the
	/// hull's attitude in it.
	/// </summary>
	public static Vector3 GunDirection(in CrewPose pose, float aimYaw, float aimPitch, float traverseRadians,
		float elevationMin, float elevationMax)
	{
		RelativeAim(pose, aimYaw, aimPitch, out float yaw, out float pitch);
		float traverse = MathF.Max(traverseRadians, 0f);
		if (traverse < MathF.PI)
		{
			yaw = Math.Clamp(yaw, -traverse, traverse);
		}

		pitch = Math.Clamp(pitch, elevationMin, MathF.Max(elevationMax, elevationMin));
		return pose.DirectionToWorld(Aim.Direction(yaw, pitch));
	}

	/// <summary>How many of the seats have somebody in them.</summary>
	public static int Occupied(ReadOnlySpan<int> occupants, int seatCount)
	{
		int count = 0;
		for (int i = 0; i < Math.Min(seatCount, occupants.Length); i++)
		{
			if (occupants[i] != 0)
			{
				count++;
			}
		}

		return count;
	}

	/// <summary>Whether any seat is free.</summary>
	public static bool HasFreeSeat(ReadOnlySpan<int> occupants, int seatCount)
	{
		for (int i = 0; i < seatCount; i++)
		{
			if (IsFree(occupants, i))
			{
				return true;
			}
		}

		return false;
	}

	private static bool IsFree(ReadOnlySpan<int> occupants, int seat) => seat >= occupants.Length || occupants[seat] == 0;
}

/// <summary>
/// Where a crew vehicle may be (docs/GROUND_VEHICLES.md §5.1, §5.2): inside the
/// field's square, as a dropship's landing spot and a rider's exit slot are
/// (<see cref="Cargo.InField"/>), and between the projectiles' kill plane and the
/// highest an aircraft cruises. Both well inside what a position can be on the wire
/// (<see cref="SimConfig.MaxPositionMeters"/>), so a vehicle's record never wraps.
///
/// The perimeter walls keep a truck in the field on their own; the step does not rely
/// on them, because a replay, a test and a bad map have none.
/// </summary>
public static class CrewField
{
	public const float HalfExtentMeters = Cargo.FieldHalfExtentMeters;

	public const float FloorMeters = SimConfig.ProjectileKillPlaneY;

	public const float CeilingMeters = FlightTraits.MaxCruiseAltitudeMeters;

	/// <summary>
	/// <paramref name="velocity"/> with whatever of it would carry <paramref name="position"/>
	/// out of the field in <paramref name="dt"/> taken off, axis by axis: a body at the
	/// edge stops there, and one already outside is let back in but no further out.
	/// </summary>
	public static Vector3 Limit(Vector3 position, Vector3 velocity, float dt)
	{
		if (!(dt > 0f))
		{
			return velocity;
		}

		return new Vector3(
			LimitAxis(position.X, velocity.X, -HalfExtentMeters, HalfExtentMeters, dt),
			LimitAxis(position.Y, velocity.Y, FloorMeters, CeilingMeters, dt),
			LimitAxis(position.Z, velocity.Z, -HalfExtentMeters, HalfExtentMeters, dt));
	}

	/// <summary><paramref name="position"/> put back inside the field; a non-finite axis goes to its middle.</summary>
	public static Vector3 Clamp(Vector3 position) => new(
		ClampAxis(position.X, -HalfExtentMeters, HalfExtentMeters),
		ClampAxis(position.Y, FloorMeters, CeilingMeters),
		ClampAxis(position.Z, -HalfExtentMeters, HalfExtentMeters));

	private static float LimitAxis(float at, float speed, float min, float max, float dt)
	{
		if (!float.IsFinite(speed))
		{
			return 0f;
		}

		float next = at + (speed * dt);
		if (speed > 0f && next > max)
		{
			return MathF.Max((max - at) / dt, 0f);
		}

		if (speed < 0f && next < min)
		{
			return MathF.Min((min - at) / dt, 0f);
		}

		return speed;
	}

	private static float ClampAxis(float value, float min, float max) =>
		float.IsFinite(value) ? Math.Clamp(value, min, max) : (min + max) * 0.5f;
}
