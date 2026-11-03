using System;
using Godot;

namespace Gdpyr.Sim;

/// <summary>What a client does with a slot's node on a record for it (docs/GROUND_VEHICLES.md §8.1).</summary>
public enum PuppetChange : byte
{
	/// <summary>The node stays as it is; the record goes to its buffer.</summary>
	Keep,

	/// <summary>No node for the slot: make one, of the kind the record says.</summary>
	Create,

	/// <summary>The slot's node is of another kind, or a wreck the slot has been given again: free it and make one afresh.</summary>
	Replace,

	/// <summary>
	/// The slot's last record had been lost to the fog: the node stays, and its buffer starts
	/// again at this record, so it appears where it is rather than sliding there from where it
	/// was lost.
	/// </summary>
	Restart,

	/// <summary>A record no newer than the slot's node was freed at: a straggler, dropped.</summary>
	Ignore,
}

/// <summary>
/// A client's account of which crew vehicles it has a node for (docs/GROUND_VEHICLES.md
/// §8.1), from the records it is sent and nothing else:
///
/// <list type="bullet">
/// <item>a node is made on the first record that names its slot, of the kind it says, and
/// made again when a record names another kind there;</item>
/// <item>it is freed <see cref="DestroyedLingerTicks"/> after the first record that marks it
/// destroyed — the wreck burns, then goes;</item>
/// <item>on a client nothing is fogged for, it is freed after
/// <see cref="MissedPacketsToFree"/> packets without it: the server took it away;</item>
/// <item>on a strategist's, a record that stops arriving is the fog (§7.3): the node is
/// hidden once <see cref="Fog.IsLost"/> says so, and kept.</item>
/// </list>
///
/// Engine-free, so what a lost packet, a reordered one or a slot given to another vehicle
/// does to the nodes is a question for <c>dotnet test</c>.
/// </summary>
public sealed class CrewPuppetRoster
{
	/// <summary>How long a wreck is drawn after the first record that marked it destroyed: GV-D12's two seconds.</summary>
	public const int DestroyedLingerTicks = SimConfig.TickRate * 2;

	/// <summary>Packets without a vehicle, on a client nothing is fogged for, before its node goes: half a second at 30 Hz.</summary>
	public const int MissedPacketsToFree = 15;

	private struct Entry
	{
		public bool Live;
		public byte Kind;
		public uint LastTick;
		public bool Destroyed;
		public uint DestroyedTick;
		public int Missed;
		public bool Freed;
		public uint FreedTick;
	}

	private readonly Entry[] _slots = new Entry[SimConfig.MaxCrewVehicles];

	/// <summary>The newest packet's tick: the reference a slot's record ages against, as a player's does (docs/NETCODE.md §6.2).</summary>
	public uint NewestPacketTick { get; private set; }

	private bool _anyPacket;

	public bool IsLive(int slot) => InRange(slot) && _slots[slot].Live;

	public byte KindOf(int slot) => InRange(slot) ? _slots[slot].Kind : (byte)0;

	public bool IsDestroyed(int slot) => InRange(slot) && _slots[slot].Live && _slots[slot].Destroyed;

	/// <summary>The newest record's tick for a live slot.</summary>
	public uint LastTick(int slot) => InRange(slot) ? _slots[slot].LastTick : 0;

	/// <summary>How far behind the newest packet the slot's newest record is [ticks]; <see cref="float.MaxValue"/> for a slot with no node.</summary>
	public float AgeTicks(int slot)
	{
		if (!IsLive(slot))
		{
			return float.MaxValue;
		}

		uint last = _slots[slot].LastTick;
		return last >= NewestPacketTick ? 0f : NewestPacketTick - last;
	}

	/// <summary>
	/// One decoded packet: what each record it carries does to its slot's node, in
	/// <paramref name="changes"/> by slot. A slot that is live and not named in a packet
	/// newer than any before it has missed one.
	/// </summary>
	public void Receive(uint tick, ReadOnlySpan<CrewVehicleRecord> records, Span<PuppetChange> changes)
	{
		bool newest = !_anyPacket || tick > NewestPacketTick;
		uint reference = NewestPacketTick;
		int named = 0;

		for (int i = 0; i < records.Length; i++)
		{
			CrewVehicleRecord record = records[i];
			int slot = record.Slot;
			if (!InRange(slot))
			{
				continue;
			}

			named |= 1 << slot;
			PuppetChange change = Note(slot, tick, record, reference);
			if (slot < changes.Length)
			{
				changes[slot] = change;
			}
		}

		if (!newest)
		{
			return;
		}

		NewestPacketTick = tick;
		_anyPacket = true;
		for (int slot = 0; slot < _slots.Length; slot++)
		{
			if (_slots[slot].Live && (named & (1 << slot)) == 0)
			{
				_slots[slot].Missed++;
			}
		}
	}

	private PuppetChange Note(int slot, uint tick, in CrewVehicleRecord record, uint reference)
	{
		ref Entry entry = ref _slots[slot];
		if (entry.Freed && !entry.Live && tick <= entry.FreedTick)
		{
			return PuppetChange.Ignore;
		}

		PuppetChange change;
		if (!entry.Live)
		{
			change = PuppetChange.Create;
		}
		else if (entry.Kind != record.KindId || (entry.Destroyed && !record.Destroyed && tick > entry.DestroyedTick))
		{
			change = PuppetChange.Replace;
		}
		else if (tick > entry.LastTick && reference > entry.LastTick && Fog.IsLost(reference - entry.LastTick))
		{
			change = PuppetChange.Restart;
		}
		else
		{
			change = PuppetChange.Keep;
		}

		if (change is PuppetChange.Create or PuppetChange.Replace)
		{
			entry = new Entry { Live = true, Kind = record.KindId, LastTick = tick };
		}
		else if (tick > entry.LastTick)
		{
			entry.LastTick = tick;
		}

		entry.Missed = 0;
		if (record.Destroyed && !entry.Destroyed)
		{
			entry.Destroyed = true;
			entry.DestroyedTick = tick;
		}

		return change;
	}

	/// <summary>
	/// Whether the slot's node is due to go at <paramref name="nowTick"/>, the client's
	/// estimate of the server's tick: <see cref="DestroyedLingerTicks"/> after the first
	/// record that marked it destroyed; or, when <paramref name="fogged"/> is false, after
	/// <see cref="MissedPacketsToFree"/> packets without it.
	/// </summary>
	public bool DueToFree(int slot, uint nowTick, bool fogged)
	{
		if (!IsLive(slot))
		{
			return false;
		}

		Entry entry = _slots[slot];
		if (entry.Destroyed && nowTick >= entry.DestroyedTick && nowTick - entry.DestroyedTick >= DestroyedLingerTicks)
		{
			return true;
		}

		return !fogged && entry.Missed >= MissedPacketsToFree;
	}

	/// <summary>The slot's node is gone: any record no newer than what it last had is a straggler from now on.</summary>
	public void Free(int slot)
	{
		if (!InRange(slot))
		{
			return;
		}

		uint at = Math.Max(_slots[slot].LastTick, NewestPacketTick);
		_slots[slot] = new Entry { Freed = true, FreedTick = at };
	}

	/// <summary>Everything forgotten: a new connection, whose server's ticks do not continue the last one's.</summary>
	public void Clear()
	{
		Array.Clear(_slots);
		NewestPacketTick = 0;
		_anyPacket = false;
	}

	private bool InRange(int slot) => slot >= 0 && slot < _slots.Length;
}

/// <summary>
/// The driver's correction, shown rather than jumped (docs/GROUND_VEHICLES.md §8.2): an
/// offset on the hull's drawn pose — position and attitude — that decays at the
/// character's half-life, <see cref="SimConfig.VisualErrorHalfLifeSeconds"/>. A correction
/// that moves the hull more than <see cref="SimConfig.MaxSmoothedErrorMeters"/> is shown at
/// once: that is a teleport, not a misprediction to hide.
/// </summary>
public struct CrewVisualOffset
{
	/// <summary>Below this, in metres or radians, what is left of an offset is dropped.</summary>
	public const float Epsilon = 1e-4f;

	public Vector3 Position;

	public float Yaw;

	public float Pitch;

	public float Roll;

	public readonly bool IsZero => Position == Vector3.Zero && Yaw == 0f && Pitch == 0f && Roll == 0f;

	/// <summary>
	/// The offset that keeps a hull drawn at <paramref name="drawnBefore"/> — its pose and
	/// whatever offset it had — once its state has been corrected to <paramref name="after"/>.
	/// </summary>
	public static CrewVisualOffset Absorb(in CrewPose drawnBefore, in CrewPose after)
	{
		Vector3 position = drawnBefore.Position - after.Position;
		if (!(position.Length() <= SimConfig.MaxSmoothedErrorMeters))
		{
			return default;
		}

		return new CrewVisualOffset
		{
			Position = position,
			Yaw = DroneSim.AngleBetween(after.Yaw, drawnBefore.Yaw),
			Pitch = drawnBefore.Pitch - after.Pitch,
			Roll = drawnBefore.Roll - after.Roll,
		};
	}

	/// <summary>Where the hull is drawn: <paramref name="pose"/> with what is left of the offset on it.</summary>
	public readonly CrewPose Apply(in CrewPose pose) =>
		new(pose.Position + Position, pose.Yaw + Yaw, pose.Pitch + Pitch, pose.Roll + Roll);

	/// <summary>Halves every <see cref="SimConfig.VisualErrorHalfLifeSeconds"/>; gone once it is all under <see cref="Epsilon"/>.</summary>
	public void Decay(float dt)
	{
		if (IsZero)
		{
			return;
		}

		float keep = MathF.Pow(0.5f, MathF.Max(dt, 0f) / SimConfig.VisualErrorHalfLifeSeconds);
		Position *= keep;
		Yaw *= keep;
		Pitch *= keep;
		Roll *= keep;

		if (Position.Length() < Epsilon && MathF.Abs(Yaw) < Epsilon && MathF.Abs(Pitch) < Epsilon
			&& MathF.Abs(Roll) < Epsilon)
		{
			this = default;
		}
	}
}

/// <summary>
/// The clock a client draws crew vehicles at (docs/GROUND_VEHICLES.md §8.1): its render
/// clock, followed without its jumps. <see cref="SimClock"/> walks a small error in its
/// estimate of the server's tick off a whole tick at a time, which moves everything drawn
/// at the render clock by a tick's travel at once — 8 cm for a walking player, 30 cm for a
/// 4x4 at 18 m/s, and the 4x4's passenger is looking out of it. This advances a tick a
/// tick and slews towards the render clock by at most <see cref="MaxSlewTicks"/> a tick, so
/// a correction is a tenth faster or slower for ten ticks rather than a jump; an error past
/// <see cref="SimConfig.ClockResyncThresholdTicks"/> — a resync — is taken at once.
/// </summary>
public sealed class SmoothRenderClock
{
	/// <summary>The most it moves towards the render clock in a tick, on top of the tick itself.</summary>
	public const float MaxSlewTicks = 0.1f;

	public float Tick { get; private set; }

	public bool Started { get; private set; }

	/// <summary>One client tick, the render clock at <paramref name="renderTick"/>: where vehicles are drawn this tick.</summary>
	public float Step(float renderTick)
	{
		float next = Tick + 1f;
		if (!Started || !(MathF.Abs(renderTick - next) <= SimConfig.ClockResyncThresholdTicks))
		{
			Tick = renderTick;
			Started = true;
			return Tick;
		}

		Tick = next + Math.Clamp(renderTick - next, -MaxSlewTicks, MaxSlewTicks);
		return Tick;
	}

	public void Reset()
	{
		Tick = 0f;
		Started = false;
	}
}
