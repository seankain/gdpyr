using System;

namespace Gdpyr.Sim;

/// <summary>
/// Where one kind of crew vehicle stands at the kiosk (docs/GROUND_VEHICLES.md §5.5).
/// A wire constant: the vehicle message's header carries one per kind (§7.1).
/// </summary>
public enum KioskStatus : byte
{
	/// <summary>One can be taken now.</summary>
	Ready = 0,

	/// <summary>As many are out as the kind's cap allows.</summary>
	Out = 1,

	/// <summary>The last one was lost, and the next is not ready yet.</summary>
	Counting = 2,

	/// <summary>The map's pad for it failed its check at the start (§6.6): this kiosk does not offer it.</summary>
	Disabled = 3,
}

/// <summary>
/// One kind's timer at the kiosk (GV-D13): ready when the round starts; at most
/// <see cref="Cap"/> alive; and once one is lost — destroyed, or abandoned and removed
/// — the next is ready <see cref="RespawnTicks"/> later. Counted from the loss, never
/// from the spawn: a 4x4 kept alive for ten minutes is not owed a replacement the
/// moment it goes.
///
/// Counted in ticks on the server; a client is told the status and the seconds left.
/// </summary>
public struct KioskTimer
{
	/// <summary>Ticks from a loss to the next being ready.</summary>
	public readonly int RespawnTicks;

	/// <summary>The most of this kind alive at once.</summary>
	public readonly int Cap;

	/// <summary>The tick the next one is ready on; 0 for at once.</summary>
	public uint ReadyTick { get; private set; }

	/// <summary>The kind's pad failed its check: nothing is offered, whatever the timer says.</summary>
	public bool Disabled { get; private set; }

	/// <summary>Taken from this kiosk this round. For the server log and the tests.</summary>
	public int Spawned { get; private set; }

	/// <summary>Lost this round.</summary>
	public int Lost { get; private set; }

	public KioskTimer(int respawnTicks, int cap)
	{
		RespawnTicks = Math.Max(respawnTicks, 0);
		Cap = Math.Max(cap, 0);
		ReadyTick = 0;
		Disabled = false;
		Spawned = 0;
		Lost = 0;
	}

	/// <summary>A timer for a kind that respawns <paramref name="respawnSeconds"/> after a loss.</summary>
	public static KioskTimer FromSeconds(float respawnSeconds, int cap) =>
		new(WeaponStats.SecondsToTicks(respawnSeconds), cap);

	/// <summary>The round starts: ready at once, nothing lost, nothing out, the pad's verdict kept.</summary>
	public void Reset()
	{
		ReadyTick = 0;
		Spawned = 0;
		Lost = 0;
	}

	/// <summary>The pad failed, or passed, its check (§6.6).</summary>
	public void SetDisabled(bool disabled) => Disabled = disabled;

	/// <summary>One was taken on <paramref name="tick"/>. Counted; the timer is the loss's, not this.</summary>
	public void OnSpawned(uint tick)
	{
		Spawned++;
	}

	/// <summary>One was lost on <paramref name="tick"/>: the next is ready <see cref="RespawnTicks"/> from now.</summary>
	public void OnLost(uint tick)
	{
		Lost++;
		ReadyTick = tick + (uint)RespawnTicks;
	}

	/// <summary>
	/// Where the kind stands on <paramref name="tick"/> with <paramref name="alive"/> of it
	/// on the field: disabled before anything else, then out at the cap, then counting
	/// while the timer runs, else ready.
	/// </summary>
	public readonly KioskStatus Status(uint tick, int alive)
	{
		if (Disabled)
		{
			return KioskStatus.Disabled;
		}

		if (alive >= Cap)
		{
			return KioskStatus.Out;
		}

		return tick < ReadyTick ? KioskStatus.Counting : KioskStatus.Ready;
	}

	/// <summary>Seconds until the next is ready, rounded up to a whole one; 0 once it is.</summary>
	public readonly int SecondsLeft(uint tick) =>
		tick >= ReadyTick ? 0 : (int)((ReadyTick - tick + SimConfig.TickRate - 1) / SimConfig.TickRate);
}

/// <summary>
/// Why a vehicle asked for at the kiosk was not taken (docs/GROUND_VEHICLES.md §5.5).
/// A protocol constant: <c>ServerKioskResult</c> carries it as a byte, and the agent
/// API's <c>vehicle</c> op names it (<see cref="KioskRules.CodeOf"/>). In the order
/// <see cref="KioskRules.Check"/> asks.
/// </summary>
public enum KioskRefusal : byte
{
	/// <summary>Not refused: the vehicle was taken.</summary>
	None = 0,

	/// <summary>Not alive.</summary>
	Dead = 1,

	/// <summary>Not on the ground force: a strategist, or still choosing a side.</summary>
	NotGround = 2,

	/// <summary>No kiosk's footprint within its reach and the latency slack.</summary>
	NotAtKiosk = 3,

	/// <summary>Already in a crew vehicle's seat.</summary>
	Seated = 4,

	/// <summary>Behind a heavy gun.</summary>
	Mounted = 5,

	/// <summary>A heavy gun or an ammunition can on their back.</summary>
	Carrying = 6,

	/// <summary>Flying an FPV drone.</summary>
	Piloting = 7,

	/// <summary>A kind this build has no vehicle for, or one this kiosk does not offer (<see cref="KioskStatus.Disabled"/>).</summary>
	Invalid = 8,

	/// <summary>As many of the kind are out as its cap allows.</summary>
	Out = 9,

	/// <summary>The last one was lost, and the next is not ready yet.</summary>
	NotReady = 10,

	/// <summary>Something is standing on the kind's pad.</summary>
	PadBlocked = 11,

	/// <summary>The last request was less than <see cref="KioskRules.MinIntervalTicks"/> ago.</summary>
	TooSoon = 12,
}

/// <summary>What the server knows about a player asking for a vehicle at the kiosk.</summary>
public readonly struct KioskSituation
{
	public readonly bool Alive;

	public readonly bool GroundForce;

	/// <summary>Flat distance from their feet to the nearest kiosk's footprint; infinite on a map without one.</summary>
	public readonly float KioskDistance;

	public readonly bool Seated;

	public readonly bool Mounted;

	public readonly CarryKind Carrying;

	/// <summary>Flying an FPV drone.</summary>
	public readonly bool Piloting;

	/// <summary>The kind asked for is one this build has.</summary>
	public readonly bool KnownKind;

	/// <summary>That kind's status at the kiosk (<see cref="KioskTimer.Status"/>).</summary>
	public readonly KioskStatus Status;

	/// <summary>Its pad is clear of bodies, vehicles and anything else.</summary>
	public readonly bool PadClear;

	public readonly uint Tick;

	/// <summary>The tick this player last asked at a kiosk; 0 for never.</summary>
	public readonly uint LastRequestTick;

	public KioskSituation(bool alive, bool groundForce, float kioskDistance, bool seated, bool mounted,
		CarryKind carrying, bool piloting, bool knownKind, KioskStatus status, bool padClear, uint tick,
		uint lastRequestTick)
	{
		Alive = alive;
		GroundForce = groundForce;
		KioskDistance = kioskDistance;
		Seated = seated;
		Mounted = mounted;
		Carrying = carrying;
		Piloting = piloting;
		KnownKind = knownKind;
		Status = status;
		PadClear = padClear;
		Tick = tick;
		LastRequestTick = lastRequestTick;
	}
}

/// <summary>
/// The one rule for taking a vehicle at the kiosk, and for when one left alone goes
/// (docs/GROUND_VEHICLES.md §5.5, GV-D13). A person's panel, an agent's <c>vehicle</c> op
/// and a scenario all ask through <see cref="Check"/>, as a kit at the locker is asked
/// through <see cref="KitRequest.Check"/>.
/// </summary>
public static class KioskRules
{
	/// <summary>The latency slack past the footprint's reach a request is allowed: the locker's.</summary>
	public const float SlackMeters = KitRequest.SlackMeters;

	/// <summary>Ticks between two requests by one player: a quarter-second, the locker's pace.</summary>
	public const int MinIntervalTicks = KitRequest.MinIntervalTicks;

	/// <summary>Nobody of the ground force within this of an empty vehicle [m]: one condition of its being abandoned.</summary>
	public const float AbandonRadiusMeters = 15f;

	/// <summary>How long a vehicle must be left before it is removed: 60 s.</summary>
	public const int AbandonTicks = SimConfig.TickRate * 60;

	/// <summary>Each refusal as the agent API and the log spell it, by <see cref="KioskRefusal"/>.</summary>
	private static readonly string[] Codes =
	{
		"taken", "dead", "not_ground", "not_at_kiosk", "seated", "mounted", "carrying", "piloting", "invalid",
		"out", "not_ready", "pad_blocked", "too_soon",
	};

	/// <summary>
	/// Whether the vehicle may be taken, and if not, why — the first of §5.5's reasons
	/// that applies, in order: the asker's life and side, where they stand, what they are
	/// doing, the kind, its cap, its timer, its pad, and last the pace, which only a
	/// request that would otherwise have been honoured is held to.
	/// </summary>
	public static KioskRefusal Check(in KioskSituation situation)
	{
		if (!situation.Alive)
		{
			return KioskRefusal.Dead;
		}

		if (!situation.GroundForce)
		{
			return KioskRefusal.NotGround;
		}

		if (!(situation.KioskDistance <= SimConfig.LockerReachMeters + SlackMeters))
		{
			return KioskRefusal.NotAtKiosk;
		}

		if (situation.Seated)
		{
			return KioskRefusal.Seated;
		}

		if (situation.Mounted)
		{
			return KioskRefusal.Mounted;
		}

		if (situation.Carrying != CarryKind.None)
		{
			return KioskRefusal.Carrying;
		}

		if (situation.Piloting)
		{
			return KioskRefusal.Piloting;
		}

		if (!situation.KnownKind || situation.Status == KioskStatus.Disabled)
		{
			return KioskRefusal.Invalid;
		}

		if (situation.Status == KioskStatus.Out)
		{
			return KioskRefusal.Out;
		}

		if (situation.Status == KioskStatus.Counting)
		{
			return KioskRefusal.NotReady;
		}

		if (!situation.PadClear)
		{
			return KioskRefusal.PadBlocked;
		}

		if (situation.LastRequestTick != 0 && situation.Tick >= situation.LastRequestTick
			&& situation.Tick - situation.LastRequestTick < (uint)MinIntervalTicks)
		{
			return KioskRefusal.TooSoon;
		}

		return KioskRefusal.None;
	}

	/// <summary>Whether a footprint distance puts the use key's reach on the kiosk: the locker's metre.</summary>
	public static bool InReach(float footprintDistance) => LockerSupply.InReach(footprintDistance);

	/// <summary>
	/// Whether a vehicle is being left alone this tick (GV-D13): nobody in it, and nobody
	/// of the ground force within <see cref="AbandonRadiusMeters"/>.
	/// <paramref name="nearestGroundForceMeters"/> is infinite with nobody alive.
	/// </summary>
	public static bool Unattended(int occupants, float nearestGroundForceMeters) =>
		occupants <= 0 && !(nearestGroundForceMeters <= AbandonRadiusMeters);

	/// <summary>
	/// Whether a vehicle left alone since <paramref name="unattendedSinceTick"/> has been
	/// for <see cref="AbandonTicks"/> by <paramref name="tick"/>, and goes.
	/// </summary>
	public static bool Abandoned(uint tick, uint unattendedSinceTick) =>
		tick >= unattendedSinceTick && tick - unattendedSinceTick >= (uint)AbandonTicks;

	/// <summary>A refusal's code, or <c>taken</c> for none.</summary>
	public static string CodeOf(KioskRefusal refusal) =>
		(byte)refusal < Codes.Length ? Codes[(byte)refusal] : "unknown";

	/// <summary>A refusal read off the wire; false for a byte this build has no refusal for.</summary>
	public static bool TryRead(byte value, out KioskRefusal refusal)
	{
		refusal = (KioskRefusal)value;
		return value < Codes.Length;
	}
}

/// <summary>
/// How long one vehicle has been left alone (GV-D13): the tick it last stopped being
/// attended, kept across the once-a-second checks so that a player who walks past it
/// starts the minute again.
/// </summary>
public struct AbandonClock
{
	private uint _since;
	private bool _running;

	/// <summary>Whether the vehicle has been left alone since a tick; false while somebody is in it or near it.</summary>
	public readonly bool Running => _running;

	/// <summary>
	/// The check on <paramref name="tick"/>: true when the vehicle has been unattended
	/// (<see cref="KioskRules.Unattended"/>) at every check for <see cref="KioskRules.AbandonTicks"/>.
	/// </summary>
	public bool Check(uint tick, int occupants, float nearestGroundForceMeters)
	{
		if (!KioskRules.Unattended(occupants, nearestGroundForceMeters))
		{
			_running = false;
			return false;
		}

		if (!_running)
		{
			_running = true;
			_since = tick;
		}

		return KioskRules.Abandoned(tick, _since);
	}

	public void Reset() => _running = false;
}
