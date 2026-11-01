using System;
using System.Buffers.Binary;
using Godot;

namespace Gdpyr.Sim;

/// <summary>
/// What a resource node gives the ground force while they hold it
/// (docs/CONTROL_POINTS.md §4). A map's node says which in its <c>Perk</c>. The order
/// is a wire constant: none goes on the wire yet, but a perk is the sort of byte that
/// ends up in a state message.
/// </summary>
public enum NodePerk : byte
{
	None = 0,

	/// <summary>FPV drones: one-way, first-person, armour-killing (<see cref="DroneSim"/>).</summary>
	Drone = 1,
}

/// <summary>
/// The FPV drone's numbers (docs/CONTROL_POINTS.md §4). Constants rather than a
/// resource because every one of them is a rule both sides of the wire and the tests
/// read; its warhead is a round in the weapon catalog like any other
/// (<c>Weapons/projectiles/fpv_drone.tres</c>), because what a blast does to a tank is
/// <see cref="Armour"/>'s rule and should be decided in one place.
/// </summary>
public static class DroneRules
{
	/// <summary>Airspeed [m/s]. Faster than anything on the field; a tank cannot drive away from one.</summary>
	public const float SpeedMetersPerSecond = 24f;

	/// <summary>How fast it comes round to where its pilot is looking [rad/s]: about 170° a second.</summary>
	public const float TurnRadiansPerSecond = 3f;

	/// <summary>Flight time before it comes down and goes off where it is: 15 s, 360 m.</summary>
	public const int BatteryTicks = SimConfig.TickRate * 15;

	/// <summary>
	/// Drones in the air at once, the whole ground force's. More than the stock can ever
	/// hold, so it is a pool size and never the rule that stops a launch.
	/// </summary>
	public const int MaxInFlight = 4;

	/// <summary>
	/// Drones the ground force can hold ready: one. A drone kills a tank, and a shelf of
	/// two let bot-only rounds kill the strategist's first tank and the one bought to
	/// replace it back to back (docs/CONTROL_POINTS.md §5.3).
	/// </summary>
	public const int MaxReady = 1;

	/// <summary>
	/// Ticks of holding one drone node to build a drone — two minutes; two such nodes build
	/// at twice the rate. The first is built too: taking a node gives nothing until it has
	/// been held, which is the strategist's time to take it back (docs/CONTROL_POINTS.md §4.2).
	/// </summary>
	public const int RechargeTicks = SimConfig.TickRate * 120;

	/// <summary>
	/// Ticks after a pilot's drone is gone before they may launch another, so a key still
	/// held when one goes off does not put the next one straight up.
	/// </summary>
	public const int RelaunchTicks = SimConfig.TickRate * 2;

	/// <summary>Its own radius for hit tests [m]: a quadcopter's, not a bullet's.</summary>
	public const float SweepRadiusMeters = 0.3f;

	/// <summary>How far in front of the pilot's eye it starts [m], so the first chord leaves the pilot's capsule.</summary>
	public const float LaunchForwardMeters = 0.6f;

	/// <summary>The steepest it climbs or dives [rad]: short of straight up, where yaw stops meaning anything.</summary>
	public const float MaxPitchRadians = 1.45f;

	/// <summary>How far outside the field it may go before it is lost rather than flown [m].</summary>
	public const float FieldMarginMeters = 20f;

	/// <summary>Above this it is lost: nothing on the map is up there to hit [m].</summary>
	public const float CeilingMeters = 120f;
}

/// <summary>Why a drone stopped flying, for the server log and the tests.</summary>
public enum DroneEnd : byte
{
	None = 0,

	/// <summary>It flew into something: the world, a player, a unit, a structure.</summary>
	Impact = 1,

	/// <summary>Its pilot pressed fire.</summary>
	Detonated = 2,

	/// <summary>Its battery ran out; it goes off where it is.</summary>
	Battery = 3,

	/// <summary>Its pilot died, left or changed side: it falls out of the sky and does nothing.</summary>
	Lost = 4,

	/// <summary>It left the field or climbed out of everything's reach.</summary>
	OutOfBounds = 5,
}

/// <summary>One drone in the air. Data, like a projectile.</summary>
public struct Drone
{
	public bool Active;

	/// <summary>The ground-force player flying it: whose input steers it, and whose kill it is.</summary>
	public int PilotPeerId;

	public Vector3 Position;

	/// <summary>Its heading, in the look-angle convention (<see cref="Aim.Direction"/>).</summary>
	public float Yaw;

	public float Pitch;

	public uint LaunchTick;

	public readonly Vector3 Forward => Aim.Direction(Yaw, Pitch);

	public readonly Vector3 Velocity => Forward * DroneRules.SpeedMetersPerSecond;

	/// <summary>The tick its battery runs out on.</summary>
	public readonly uint ExpireTick => LaunchTick + (uint)DroneRules.BatteryTicks;

	public readonly float SecondsLeft(uint tick) =>
		tick >= ExpireTick ? 0f : (ExpireTick - tick) * SimConfig.TickDelta;
}

/// <summary>
/// How an FPV drone flies (docs/CONTROL_POINTS.md §4): at a constant airspeed, nose
/// first, turning towards wherever its pilot is looking at a capped rate. The pilot's
/// look angles are already on the wire every tick in their <see cref="InputFrame"/>, so
/// a drone needs no input of its own — the mouse that aims a rifle flies it — and the
/// pilot's body stands still meanwhile, fed a look-only frame as a dead player's is.
///
/// Engine-free: what it hits is the server's question, asked of the chord this returns.
/// </summary>
public static class DroneSim
{
	/// <summary>A drone leaving a pilot's eye, heading where they look.</summary>
	public static Drone Launch(int pilotPeerId, Vector3 eye, float yaw, float pitch, uint tick)
	{
		float clamped = Math.Clamp(pitch, -DroneRules.MaxPitchRadians, DroneRules.MaxPitchRadians);
		return new Drone
		{
			Active = true,
			PilotPeerId = pilotPeerId,
			Position = eye + (Aim.Direction(yaw, clamped) * DroneRules.LaunchForwardMeters),
			Yaw = WrapAngle(yaw),
			Pitch = clamped,
			LaunchTick = tick,
		};
	}

	/// <summary>
	/// One tick: turn towards the pilot's look angles by at most the turn rate, each axis
	/// on its own, then fly. Returns where it was, so the caller tests the chord from
	/// there to <see cref="Drone.Position"/>.
	/// </summary>
	public static Vector3 Step(ref Drone drone, float targetYaw, float targetPitch, float dt)
	{
		float turn = DroneRules.TurnRadiansPerSecond * MathF.Max(dt, 0f);
		drone.Yaw = WrapAngle(drone.Yaw + Math.Clamp(AngleBetween(drone.Yaw, targetYaw), -turn, turn));

		float pitch = Math.Clamp(targetPitch, -DroneRules.MaxPitchRadians, DroneRules.MaxPitchRadians);
		drone.Pitch += Math.Clamp(pitch - drone.Pitch, -turn, turn);

		Vector3 from = drone.Position;
		drone.Position += drone.Velocity * MathF.Max(dt, 0f);
		return from;
	}

	/// <summary>Whether its battery is spent on <paramref name="tick"/>.</summary>
	public static bool BatteryOut(in Drone drone, uint tick) => tick >= drone.ExpireTick;

	/// <summary>Whether it has flown off the field, under it or out of reach above it.</summary>
	public static bool OutOfBounds(Vector3 position, float fieldHalfExtentMeters)
	{
		float limit = fieldHalfExtentMeters + DroneRules.FieldMarginMeters;
		return MathF.Abs(position.X) > limit || MathF.Abs(position.Z) > limit
			|| position.Y < -DroneRules.FieldMarginMeters || position.Y > DroneRules.CeilingMeters;
	}

	/// <summary>Whether a drone that ended this way goes off. A lost drone falls and does nothing.</summary>
	public static bool Explodes(DroneEnd end) => end is DroneEnd.Impact or DroneEnd.Detonated or DroneEnd.Battery;

	/// <summary>
	/// Whether a player may put a drone up on <paramref name="tick"/>: alive, on the
	/// ground force, not already flying one or behind a mounted gun, past the relaunch
	/// lockout since their last, and with one ready while their side holds a drone node.
	/// </summary>
	public static bool MayLaunch(bool alive, bool groundForce, bool flying, bool mounted, uint tick,
		uint lastEndedTick, bool stockReady)
	{
		if (!alive || !groundForce || flying || mounted || !stockReady)
		{
			return false;
		}

		return lastEndedTick == 0 || tick >= lastEndedTick + (uint)DroneRules.RelaunchTicks;
	}

	/// <summary>The signed shortest turn from <paramref name="from"/> to <paramref name="to"/>, in (-π, π].</summary>
	public static float AngleBetween(float from, float to) => WrapAngle(to - from);

	/// <summary>An angle in (-π, π].</summary>
	public static float WrapAngle(float angle)
	{
		float wrapped = MathF.IEEERemainder(angle, MathF.Tau);
		return wrapped <= -MathF.PI ? wrapped + MathF.Tau : wrapped;
	}
}

/// <summary>
/// The ground force's drones on the shelf (docs/CONTROL_POINTS.md §4): one built every
/// <see cref="DroneRules.RechargeTicks"/> for each drone node they hold, up to
/// <see cref="DroneRules.MaxReady"/>; and none launched while they hold no drone node,
/// whatever is on the shelf. Taking a node gives none at once: the first is built by
/// holding it, as the rest are. Shared by the whole side,
/// like the ticket pool, so a drone is a team decision rather than a cooldown each.
///
/// Counted in ticks, server-side; a client is told the count and the progress.
/// </summary>
public sealed class DroneStock
{
	/// <summary>Drones ready to launch.</summary>
	public int Ready { get; private set; }

	/// <summary>Ticks of building towards the next one.</summary>
	public int ProgressTicks { get; private set; }

	/// <summary>Drone nodes the ground force held at the last tick: what unlocks a launch.</summary>
	public int HeldNodes { get; private set; }

	/// <summary>Drones taken off the shelf this round. For the server log and the tests.</summary>
	public int Launched { get; private set; }

	/// <summary>Whether a launch would be allowed now.</summary>
	public bool CanLaunch => HeldNodes > 0 && Ready > 0;

	/// <summary>Progress towards the next drone in [0,1]; 0 when the shelf is full or nothing is held.</summary>
	public float Progress => Ready >= DroneRules.MaxReady || HeldNodes == 0
		? 0f
		: Math.Clamp(ProgressTicks / (float)DroneRules.RechargeTicks, 0f, 1f);

	/// <summary>One tick, with how many drone nodes the ground force holds now.</summary>
	public void Tick(int heldDroneNodes)
	{
		HeldNodes = Math.Max(heldDroneNodes, 0);
		if (Ready >= DroneRules.MaxReady)
		{
			ProgressTicks = 0;
			return;
		}

		// Building stops while nothing is held, but what was built so far is kept: a node
		// retaken finishes the drone it had started.
		ProgressTicks += HeldNodes;
		if (ProgressTicks >= DroneRules.RechargeTicks)
		{
			ProgressTicks -= DroneRules.RechargeTicks;
			Ready++;
			if (Ready >= DroneRules.MaxReady)
			{
				ProgressTicks = 0;
			}
		}
	}

	/// <summary>Takes one off the shelf, when a launch is allowed.</summary>
	public bool TryTake()
	{
		if (!CanLaunch)
		{
			return false;
		}

		Ready--;
		Launched++;
		return true;
	}

	/// <summary>Applies the server's account on a client.</summary>
	public void Apply(int ready, int heldNodes, float progress)
	{
		Ready = Math.Clamp(ready, 0, DroneRules.MaxReady);
		HeldNodes = Math.Max(heldNodes, 0);
		ProgressTicks = (int)(Math.Clamp(progress, 0f, 1f) * DroneRules.RechargeTicks);
	}

	public void Reset()
	{
		Ready = 0;
		ProgressTicks = 0;
		HeldNodes = 0;
		Launched = 0;
	}
}

/// <summary>
/// How a ground bot flies a drone (docs/CONTROL_POINTS.md §4.5): it puts one up at a tank
/// it can see, far enough off that the blast is not its own and with nothing on foot close
/// enough to shoot it while it stands still, and flies it at the tank's hull — the look
/// angles from the drone to the hull are the whole of the steering.
/// </summary>
public static class DroneAutopilot
{
	/// <summary>The furthest tank a bot puts a drone up at [m]: six seconds of flight.</summary>
	public const float LaunchRangeMeters = 140f;

	/// <summary>The nearest [m]: well outside the warhead's 4 m, and the launcher's job closer in.</summary>
	public const float MinLaunchRangeMeters = 15f;

	/// <summary>Anything of the strategist's on foot this close and the bot fights it instead [m].</summary>
	public const float ClearOfInfantryMeters = 20f;

	/// <summary>How far up a tank's hull a drone is aimed, from its feet [m]: the middle of a 3 m body.</summary>
	public const float HullAimHeightMeters = 1.2f;

	public static bool ShouldLaunch(bool stockReady, float tankDistance, float nearestInfantryDistance) =>
		stockReady && tankDistance >= MinLaunchRangeMeters && tankDistance <= LaunchRangeMeters
		&& nearestInfantryDistance > ClearOfInfantryMeters;

	/// <summary>The look angles that fly a drone at <paramref name="drone"/> straight at <paramref name="target"/>.</summary>
	public static void Steer(Vector3 drone, Vector3 target, out float yaw, out float pitch) =>
		Aim.Angles(target - drone, out yaw, out pitch);
}

/// <summary>
/// What the ground force's HUD says about drones (docs/CONTROL_POINTS.md §4.4): one short
/// line under the round's, empty while there is nothing to say.
/// </summary>
public static class DroneHud
{
	/// <summary>
	/// <c>drone 12 s · fire sets it off</c> while flying one; <c>[Q] drone ×2</c> with one
	/// to launch; <c>drone 40 %</c> while one is building; <c>2 drones — hold a drone node
	/// to launch</c> with some on the shelf and no drone node held; nothing otherwise.
	/// </summary>
	public static string Line(int ready, int heldNodes, float progress, bool flying, float secondsLeft,
		string key = "Q")
	{
		if (flying)
		{
			return $"drone {MathF.Ceiling(MathF.Max(secondsLeft, 0f)):0} s · fire sets it off";
		}

		if (heldNodes > 0 && ready > 0)
		{
			return ready == 1 ? $"[{key}] drone" : $"[{key}] drone ×{ready}";
		}

		if (heldNodes > 0)
		{
			return $"drone {MathF.Round(Math.Clamp(progress, 0f, 1f) * 100f):0} %";
		}

		return ready > 0
			? $"{ready} drone{(ready == 1 ? string.Empty : "s")} — hold a drone node to launch"
			: string.Empty;
	}
}

/// <summary>
/// The drone state message (docs/CONTROL_POINTS.md §4.3): the shelf, then every drone
/// the recipient may see, at <see cref="SimConfig.DroneStateIntervalTicks"/>,
/// unreliable — a sample, like a snapshot, and the next one supersedes it. A drone
/// absent from it has stopped flying; where it went off is the hit message's.
///
/// <code>
/// header  4 B : ready u8 · held drone nodes u8 · progress u8 (×255) · count u8
/// drone  20 B : pilot i32 · x, y, z f32 · yaw u16 · pitch i16 · seconds left u8 · pad u8
/// </code>
/// </summary>
public static class DroneCodec
{
	public const int HeaderBytes = 4;

	public const int DroneBytes = 20;

	public static int SizeFor(int drones) => HeaderBytes + (Math.Clamp(drones, 0, DroneRules.MaxInFlight) * DroneBytes);

	/// <summary>Writes the header and up to <see cref="DroneRules.MaxInFlight"/> drones; returns the bytes written.</summary>
	public static int Encode(Span<byte> into, int ready, int heldNodes, float progress, ReadOnlySpan<Drone> drones,
		uint tick)
	{
		int count = Math.Min(drones.Length, DroneRules.MaxInFlight);
		if (into.Length < SizeFor(count))
		{
			return 0;
		}

		into[0] = (byte)Math.Clamp(ready, 0, byte.MaxValue);
		into[1] = (byte)Math.Clamp(heldNodes, 0, byte.MaxValue);
		into[2] = (byte)MathF.Round(Math.Clamp(progress, 0f, 1f) * byte.MaxValue);
		into[3] = (byte)count;

		int at = HeaderBytes;
		for (int i = 0; i < count; i++, at += DroneBytes)
		{
			Drone drone = drones[i];
			Span<byte> slot = into.Slice(at, DroneBytes);
			BinaryPrimitives.WriteInt32LittleEndian(slot, drone.PilotPeerId);
			BinaryPrimitives.WriteSingleLittleEndian(slot[4..], drone.Position.X);
			BinaryPrimitives.WriteSingleLittleEndian(slot[8..], drone.Position.Y);
			BinaryPrimitives.WriteSingleLittleEndian(slot[12..], drone.Position.Z);
			BinaryPrimitives.WriteUInt16LittleEndian(slot[16..], Quantize.YawToU16(drone.Yaw));
			slot[18] = (byte)MathF.Round(Math.Clamp(drone.SecondsLeft(tick), 0f, byte.MaxValue));
			slot[19] = (byte)(sbyte)Math.Clamp(MathF.Round(drone.Pitch / DroneRules.MaxPitchRadians * 127f), -127f, 127f);
		}

		return at;
	}

	/// <summary>
	/// Reads a message into <paramref name="drones"/> and, per drone, the whole seconds
	/// its battery has left into <paramref name="secondsLeft"/>. False for one too short
	/// for what its header claims, claiming more drones than either span holds, or with a
	/// position that is not a number; a client drops it and waits for the next.
	/// </summary>
	public static bool TryDecode(ReadOnlySpan<byte> bytes, out int ready, out int heldNodes, out float progress,
		Span<Drone> drones, Span<byte> secondsLeft, out int count)
	{
		ready = 0;
		heldNodes = 0;
		progress = 0f;
		count = 0;

		if (bytes.Length < HeaderBytes)
		{
			return false;
		}

		int claimed = bytes[3];
		if (claimed > drones.Length || claimed > DroneRules.MaxInFlight || claimed > secondsLeft.Length
			|| bytes.Length < SizeFor(claimed))
		{
			return false;
		}

		int at = HeaderBytes;
		for (int i = 0; i < claimed; i++, at += DroneBytes)
		{
			ReadOnlySpan<byte> slot = bytes.Slice(at, DroneBytes);
			float x = BinaryPrimitives.ReadSingleLittleEndian(slot[4..]);
			float y = BinaryPrimitives.ReadSingleLittleEndian(slot[8..]);
			float z = BinaryPrimitives.ReadSingleLittleEndian(slot[12..]);
			if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
			{
				return false;
			}

			drones[i] = new Drone
			{
				Active = true,
				PilotPeerId = BinaryPrimitives.ReadInt32LittleEndian(slot),
				Position = new Vector3(x, y, z),
				Yaw = DroneSim.WrapAngle(Quantize.U16ToYaw(BinaryPrimitives.ReadUInt16LittleEndian(slot[16..]))),
				Pitch = (sbyte)slot[19] / 127f * DroneRules.MaxPitchRadians,
			};
			secondsLeft[i] = slot[18];
		}

		ready = bytes[0];
		heldNodes = bytes[1];
		progress = bytes[2] / (float)byte.MaxValue;
		count = claimed;
		return true;
	}
}
