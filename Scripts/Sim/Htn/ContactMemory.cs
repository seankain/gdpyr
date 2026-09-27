using System;
using Godot;

namespace Gdpyr.Sim.Htn;

/// <summary>What a remembered contact is, as far as a planner cares (docs/HTN_BOTS.md §4.3).</summary>
public enum ContactKind : byte
{
	/// <summary>A unit bullets hurt: a rifleman, a technical, a builder.</summary>
	Infantry = 0,

	/// <summary>A unit only explosives hurt: a tank (docs/NETCODE.md §10.6).</summary>
	Armour = 1,

	/// <summary>A structure with a gun: a pillbox, a sniper tower.</summary>
	ArmedStructure = 2,

	/// <summary>A ground-force player, which is the only thing the strategist's side has to look for.</summary>
	Player = 3,
}

/// <summary>One thing a side has seen, as it was when it was last seen.</summary>
public struct KnownContact
{
	/// <summary>Who it is, as an <see cref="Sim.OwnerId"/>: a peer, a unit or a structure. Never 0.</summary>
	public int OwnerId;

	public ContactKind Kind;

	/// <summary>True when only an explosive hurts it: a tank, a pillbox, a tower.</summary>
	public bool BulletProof;

	/// <summary>
	/// Where the side's sensor measured it: the feet for the strategist's fog, as
	/// its ghost markers are drawn; the hitbox centre for a ground scan, which is
	/// what a bot aims at.
	/// </summary>
	public Vector3 Position;

	public Vector3 Velocity;

	public float HealthFraction;

	/// <summary>Set by <see cref="ContactMemory.Observe"/>; whatever a sighting carries in it is ignored.</summary>
	public uint LastSeenTick;
}

/// <summary>
/// What one side knows about the other: every contact any of its observers has
/// reported, where it was last seen, and by how many of them (docs/HTN_BOTS.md
/// §4.3). The strategist's is fed by <c>VisibilityService</c> after each fog
/// refresh; the ground force's by each ground bot's <c>GroundSensor</c> scan.
/// Nothing here spends a ray: it remembers what the existing scans already paid
/// for.
///
/// A contact is <i>live</i> while an observer has reported it within
/// <see cref="LiveWindowTicks"/> — the interval its observers re-report on, so a
/// contact every scan still finds never flickers — and a <i>ghost</i> after that,
/// until <see cref="LifetimeTicks"/> have passed since it was last seen: eight
/// seconds, as the strategist's ghost markers last (<see cref="SimConfig.GhostLifetimeTicks"/>).
///
/// Engine-free and fixed-size like everything under <c>Scripts/Sim</c>: pre-sized
/// arrays, a linear search over at most a hundred entries, nothing allocated after
/// construction (§4.2 rule 3). Server-only, and single-threaded with the tick.
/// </summary>
public sealed class ContactMemory
{
	private readonly KnownContact[] _contacts;

	/// <summary>
	/// Per contact and observer, one more than the tick that observer last reported
	/// it on; 0 for never. <see cref="Capacity"/> rows of <see cref="Observers"/>.
	/// </summary>
	private readonly uint[] _reports;

	/// <param name="capacity">Contacts held at once; sized to everything the other side can field.</param>
	/// <param name="observers">
	/// Distinct observers that can report, numbered from 0 — a ground bot's roster
	/// slot, or the one fog refresh the strategist has.
	/// </param>
	/// <param name="liveWindowTicks">How often every observer re-reports: its scan interval.</param>
	/// <param name="lifetimeTicks">How long after it was last seen a contact is forgotten.</param>
	public ContactMemory(int capacity, int observers, int liveWindowTicks,
		int lifetimeTicks = SimConfig.GhostLifetimeTicks)
	{
		_contacts = new KnownContact[Math.Max(capacity, 1)];
		Observers = Math.Max(observers, 1);
		_reports = new uint[_contacts.Length * Observers];
		LiveWindowTicks = Math.Max(liveWindowTicks, 0);
		LifetimeTicks = Math.Max(lifetimeTicks, LiveWindowTicks);
	}

	public int Capacity => _contacts.Length;

	/// <summary>Contacts remembered, live or ghost.</summary>
	public int Count { get; private set; }

	public int Observers { get; }

	public int LiveWindowTicks { get; }

	public int LifetimeTicks { get; }

	/// <summary>
	/// Sightings refused because every entry was live. Anything but 0 is a sizing
	/// bug, as <see cref="VisionField.Overflows"/> is.
	/// </summary>
	public int Overflows { get; private set; }

	/// <summary>Ghosts dropped early to make room for a new contact. Also a sizing bug if it is not 0.</summary>
	public int Evictions { get; private set; }

	public KnownContact At(int index) => index >= 0 && index < Count ? _contacts[index] : default;

	/// <summary>The index of the contact with this owner id, or -1.</summary>
	public int IndexOf(int ownerId)
	{
		for (int i = 0; i < Count; i++)
		{
			if (_contacts[i].OwnerId == ownerId)
			{
				return i;
			}
		}

		return -1;
	}

	/// <summary>Ticks since the contact at <paramref name="index"/> was last seen.</summary>
	public uint AgeOf(int index, uint tick)
	{
		if (index < 0 || index >= Count)
		{
			return uint.MaxValue;
		}

		uint seen = _contacts[index].LastSeenTick;
		return tick >= seen ? tick - seen : 0u;
	}

	/// <summary>Whether an observer has reported it within <see cref="LiveWindowTicks"/>.</summary>
	public bool IsLive(int index, uint tick) => AgeOf(index, tick) <= (uint)LiveWindowTicks;

	/// <summary>
	/// Seen within <see cref="LifetimeTicks"/>. Exact whenever it is asked, where
	/// <see cref="Count"/> can hold an expired contact until the next <see cref="Age"/>.
	/// </summary>
	public bool IsRemembered(int index, uint tick) => AgeOf(index, tick) <= (uint)LifetimeTicks;

	/// <summary>Remembered, and no longer live.</summary>
	public bool IsGhost(int index, uint tick) => IsRemembered(index, tick) && !IsLive(index, tick);

	/// <summary>How many distinct observers have reported it within <see cref="LiveWindowTicks"/>.</summary>
	public int SeenBy(int index, uint tick)
	{
		if (index < 0 || index >= Count)
		{
			return 0;
		}

		int seenBy = 0;
		int row = index * Observers;
		for (int o = 0; o < Observers; o++)
		{
			uint report = _reports[row + o];
			if (report != 0 && tick >= report - 1 && tick - (report - 1) <= (uint)LiveWindowTicks)
			{
				seenBy++;
			}
		}

		return seenBy;
	}

	public int LiveCount(uint tick)
	{
		int live = 0;
		for (int i = 0; i < Count; i++)
		{
			if (IsLive(i, tick))
			{
				live++;
			}
		}

		return live;
	}

	public int GhostCount(uint tick)
	{
		int ghosts = 0;
		for (int i = 0; i < Count; i++)
		{
			if (IsGhost(i, tick))
			{
				ghosts++;
			}
		}

		return ghosts;
	}

	/// <summary>
	/// Records that <paramref name="observer"/> saw <paramref name="sighting"/> on
	/// <paramref name="tick"/>. A contact already remembered is updated in place; a
	/// new one takes a free entry, else the stalest ghost's
	/// (<see cref="Evictions"/>), else it is refused (<see cref="Overflows"/>) —
	/// what every observer can see right now is worth more than a newcomer.
	///
	/// An observer outside <see cref="Observers"/> still updates the contact, and is
	/// not counted in <see cref="SeenBy"/>. Returns false for a refused sighting, or
	/// one with no owner.
	/// </summary>
	public bool Observe(uint tick, int observer, in KnownContact sighting)
	{
		if (sighting.OwnerId == Sim.OwnerId.None)
		{
			return false;
		}

		int index = IndexOf(sighting.OwnerId);
		if (index < 0)
		{
			index = Admit(tick);
			if (index < 0)
			{
				Overflows++;
				return false;
			}
		}

		_contacts[index] = sighting;
		_contacts[index].LastSeenTick = tick;

		if (observer >= 0 && observer < Observers)
		{
			_reports[(index * Observers) + observer] = tick + 1;
		}

		return true;
	}

	/// <summary>Forgets every contact last seen more than <see cref="LifetimeTicks"/> ago.</summary>
	public void Age(uint tick)
	{
		for (int i = Count - 1; i >= 0; i--)
		{
			if (AgeOf(i, tick) > (uint)LifetimeTicks)
			{
				RemoveAt(i);
			}
		}
	}

	/// <summary>Forgets one contact now: it died, or its player left. Returns false when it was not remembered.</summary>
	public bool Forget(int ownerId)
	{
		int index = IndexOf(ownerId);
		if (index < 0)
		{
			return false;
		}

		RemoveAt(index);
		return true;
	}

	/// <summary>Forgets everything and zeroes the counters, between rounds. Keeps the storage.</summary>
	public void Clear()
	{
		Count = 0;
		Array.Clear(_reports);
		Overflows = 0;
		Evictions = 0;
	}

	/// <summary>A fresh entry for a new contact, with no reports: a free one, else the stalest ghost's, else -1.</summary>
	private int Admit(uint tick)
	{
		int index;
		if (Count < _contacts.Length)
		{
			index = Count++;
		}
		else
		{
			index = -1;
			uint stalest = 0;
			for (int i = 0; i < Count; i++)
			{
				uint age = AgeOf(i, tick);
				if (!IsLive(i, tick) && (index < 0 || age > stalest))
				{
					index = i;
					stalest = age;
				}
			}

			if (index < 0)
			{
				return -1;
			}

			Evictions++;
		}

		Array.Clear(_reports, index * Observers, Observers);
		return index;
	}

	/// <summary>Moves the last entry into the hole, so the live ones stay packed at the front.</summary>
	private void RemoveAt(int index)
	{
		int last = Count - 1;
		if (index != last)
		{
			_contacts[index] = _contacts[last];
			Array.Copy(_reports, last * Observers, _reports, index * Observers, Observers);
		}

		_contacts[last] = default;
		Array.Clear(_reports, last * Observers, Observers);
		Count--;
	}
}
