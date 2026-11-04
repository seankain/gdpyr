using System;
using Godot;

namespace Gdpyr.Sim;

/// <summary>
/// Where a crew vehicle is and how it is turned at one tick (docs/GROUND_VEHICLES.md
/// §5.6): its position, its heading and the attitude its sprung chassis or its tilt
/// gives it. Everything carried on it — a seat's capsule and eye, the gun's muzzle, a
/// hull box — is authored in the vehicle's own frame and placed by one of these.
///
/// The rotation is the character's convention extended by a roll: yaw about +Y, then
/// pitch about the yawed +X (nose up positive, as <see cref="Aim.Direction"/> looks
/// up), then roll about the pitched +Z (positive lifts the right side). Forward is −Z.
/// </summary>
public readonly struct CrewPose
{
	public readonly Vector3 Position;

	public readonly float Yaw;

	public readonly float Pitch;

	public readonly float Roll;

	public CrewPose(Vector3 position, float yaw, float pitch = 0f, float roll = 0f)
	{
		Position = position;
		Yaw = yaw;
		Pitch = pitch;
		Roll = roll;
	}

	/// <summary>The vehicle's rotation: its columns are its right, up and back in the world.</summary>
	public Basis Basis => Rotation(Yaw, Pitch, Roll);

	/// <summary>A point in the vehicle's frame, in the world.</summary>
	public Vector3 ToWorld(Vector3 local) => Position + (Basis * local);

	/// <summary>A direction in the vehicle's frame, in the world.</summary>
	public Vector3 DirectionToWorld(Vector3 local) => Basis * local;

	/// <summary>A point in the world, in the vehicle's frame.</summary>
	public Vector3 ToLocal(Vector3 world) => Basis.Transposed() * (world - Position);

	/// <summary>A direction in the world, in the vehicle's frame.</summary>
	public Vector3 DirectionToLocal(Vector3 world) => Basis.Transposed() * world;

	/// <summary>Where the nose points.</summary>
	public Vector3 Forward => Basis * Vector3.Forward;

	/// <summary>A hull box authored in the vehicle's frame, where it is now.</summary>
	public HitObb Place(Vector3 localCenter, Vector3 halfExtents) =>
		HitObb.Place(Basis, Position, localCenter, halfExtents);

	/// <summary>A seat's capsule authored in the vehicle's frame, where it is now.</summary>
	public HitCapsule Place(in HitCapsule local) =>
		new(ToWorld(local.Bottom), ToWorld(local.Top), local.Radius);

	/// <summary>Yaw, then pitch, then roll, written out so no Euler-order default can change it.</summary>
	public static Basis Rotation(float yaw, float pitch, float roll)
	{
		var yawed = new Basis(Vector3.Up, yaw);
		var pitched = new Basis(Vector3.Right, pitch);
		var rolled = new Basis(Vector3.Back, roll);
		return yawed * pitched * rolled;
	}

	public override string ToString() =>
		$"({Position.X:0.00}, {Position.Y:0.00}, {Position.Z:0.00}) yaw {Yaw:0.000} pitch {Pitch:0.000} roll {Roll:0.000}";
}

/// <summary>
/// The last half second of one crew vehicle's pose, as a fixed ring
/// (docs/GROUND_VEHICLES.md §5.6): what a seated shooter's round leaves from.
///
/// A client draws every vehicle it does not drive at its render clock, behind the
/// server's tick by its latency, its input buffer and the interpolation delay. A round
/// fired from a seat on a moving vehicle has to leave from the seat that client drew —
/// at 18 m/s and 100 ms RTT, 3.6 m behind where the server has the vehicle now — or it
/// leaves from somewhere its tracer never was. So the server keeps each vehicle's pose
/// for the size of the hitbox ring and takes a shooter's muzzle, eye and fire arc from
/// the tick their client was drawing (<see cref="ViewTick"/>).
///
/// Fixed storage, no allocation after construction: this runs per vehicle per tick.
/// </summary>
public sealed class PoseHistory
{
	/// <summary>Ticks retained: the hitbox ring's, a power of two so the index is a mask.</summary>
	public const int Capacity = SimConfig.HitboxHistoryTicks;

	private const int Mask = Capacity - 1;

	private readonly CrewPose[] _poses = new CrewPose[Capacity];
	private readonly uint[] _ticks = new uint[Capacity];
	private readonly bool[] _filled = new bool[Capacity];

	/// <summary>The newest tick recorded, or 0 before anything has been.</summary>
	public uint NewestTick { get; private set; }

	/// <summary>True once at least one pose has been recorded.</summary>
	public bool HasSamples { get; private set; }

	public void Record(uint tick, in CrewPose pose)
	{
		int slot = (int)(tick & Mask);
		_poses[slot] = pose;
		_ticks[slot] = tick;
		_filled[slot] = true;

		if (!HasSamples || tick >= NewestTick)
		{
			NewestTick = tick;
		}

		HasSamples = true;
	}

	/// <summary>The pose recorded for exactly <paramref name="tick"/>; false when the ring no longer holds it.</summary>
	public bool TryGet(uint tick, out CrewPose pose)
	{
		int slot = (int)(tick & Mask);
		if (_filled[slot] && _ticks[slot] == tick)
		{
			pose = _poses[slot];
			return true;
		}

		pose = default;
		return false;
	}

	/// <summary>
	/// The pose at <paramref name="tick"/>, or the nearest one the ring still holds:
	/// the newest for a tick after it, the oldest for one before what is left. A
	/// vehicle that has just appeared has only its first pose, and a seated shooter's
	/// view of it can be no older than that.
	/// </summary>
	public CrewPose Sample(uint tick)
	{
		if (!HasSamples)
		{
			return default;
		}

		if (tick >= NewestTick)
		{
			TryGet(NewestTick, out CrewPose newest);
			return newest;
		}

		uint oldest = NewestTick >= Capacity - 1 ? NewestTick - (Capacity - 1) : 0;
		for (uint at = Math.Max(tick, oldest); at <= NewestTick; at++)
		{
			if (TryGet(at, out CrewPose pose))
			{
				return pose;
			}
		}

		TryGet(NewestTick, out CrewPose fallback);
		return fallback;
	}

	public void Clear()
	{
		Array.Clear(_poses);
		Array.Clear(_ticks);
		Array.Clear(_filled);
		NewestTick = 0;
		HasSamples = false;
	}

	/// <summary>
	/// The client samples a frame on the physics step after the one it drew the field
	/// on: what a player reacts to is the picture the step before put up [ticks].
	/// </summary>
	public const float SampleAfterDrawTicks = 1f;

	/// <summary>
	/// A frame waits in the server's buffer between the depth reported after it is
	/// consumed and a tick more — the frame after it is already there, the one after
	/// that not yet — so half a tick more than the depth on average [ticks].
	/// </summary>
	public const float BufferHalfTick = 0.5f;

	/// <summary>
	/// The tick a client was drawing when it sampled the frame the server consumes at
	/// <paramref name="serverTick"/> (docs/GROUND_VEHICLES.md §5.6), fractional:
	///
	/// <code>
	/// view = N − LagCompensationTicks − InputBufferDepth − InterpolationDelayTicks − 1 − ½
	/// </code>
	///
	/// the one-way latency the frame took to arrive, the ticks it waited in the buffer —
	/// the depth, smoothed, and the half tick it leaves out (<see cref="BufferHalfTick"/>)
	/// — the interpolation delay the client draws behind its estimate of the server's
	/// tick, and the step between drawing and sampling (<see cref="SampleAfterDrawTicks"/>).
	/// Clamped so that it is never more than the ring holds behind <c>N</c>, nor before
	/// tick 0. A seat with no remote client — a bot, an agent, the listen host — sees the
	/// vehicle where the server has it, at <c>N</c>. A negative latency or depth, which
	/// only a lying client could report, counts as none.
	///
	/// What it cannot know is how far the client's own estimate of the server's tick is
	/// off — up to a tick either way, which its clock walks off only at two (NETCODE.md
	/// §2) — so a client's view and this one differ by up to about a tick: which is why a
	/// client says what it drew, and this is only the fallback for one that has not.
	/// </summary>
	public static float ViewTick(uint serverTick, int lagCompensationTicks, float inputBufferDepth, bool remoteClient) =>
		ViewTick(serverTick, float.NaN, lagCompensationTicks, inputBufferDepth, remoteClient);

	/// <summary>
	/// The same, given <paramref name="drawnTick"/> — the tick the client says it was drawing
	/// when it sampled the frame (the input packet's header, <c>InputCodec.DrawnBehind</c>):
	/// that, clamped into the ring and to no later than <paramref name="serverTick"/>. The
	/// estimate above only when the client has not said (NaN), as a client from before the
	/// header carried it does not.
	/// </summary>
	public static float ViewTick(uint serverTick, float drawnTick, int lagCompensationTicks, float inputBufferDepth,
		bool remoteClient)
	{
		if (remoteClient && float.IsFinite(drawnTick))
		{
			float oldest = serverTick > Capacity - 1 ? serverTick - (Capacity - 1) : 0f;
			return Math.Clamp(drawnTick, oldest, serverTick);
		}

		return Estimate(serverTick, lagCompensationTicks, inputBufferDepth, remoteClient);
	}

	private static float Estimate(uint serverTick, int lagCompensationTicks, float inputBufferDepth, bool remoteClient)
	{
		if (!remoteClient)
		{
			return serverTick;
		}

		// Each term clamped before the sum, so no reported latency can wrap it.
		float lag = Math.Clamp(lagCompensationTicks, 0, Capacity);
		float depth = float.IsFinite(inputBufferDepth) ? Math.Clamp(inputBufferDepth, 0f, Capacity) : 0f;
		float back = MathF.Min(lag + depth + SimConfig.InterpolationDelayTicks + SampleAfterDrawTicks + BufferHalfTick,
			Capacity - 1);
		return serverTick > back ? serverTick - back : 0f;
	}

	/// <summary>
	/// The pose at a fractional <paramref name="tick"/>: between the two recorded ticks
	/// either side of it, as a client draws a vehicle between two records; the nearest the
	/// ring holds when either is missing (<see cref="Sample(uint)"/>).
	/// </summary>
	public CrewPose Sample(float tick)
	{
		if (!HasSamples || !float.IsFinite(tick) || tick <= 0f)
		{
			return Sample(0u);
		}

		uint below = (uint)MathF.Floor(tick);
		float t = tick - below;
		if (t <= 1e-4f || !TryGet(below, out CrewPose a) || !TryGet(below + 1, out CrewPose b))
		{
			return Sample(t > 0.5f ? below + 1 : below);
		}

		return new CrewPose(a.Position.Lerp(b.Position, t), Mathf.LerpAngle(a.Yaw, b.Yaw, t),
			Mathf.Lerp(a.Pitch, b.Pitch, t), Mathf.Lerp(a.Roll, b.Roll, t));
	}
}
