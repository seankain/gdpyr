using System;
using Godot;

namespace Gdpyr.Sim;

/// <summary>What a player is carrying, and therefore how slowly they are walking.</summary>
public enum CarryKind : byte
{
	None = 0,

	/// <summary>A heavy gun. Slow, and the reason to have walked out here.</summary>
	Gun = 1,

	/// <summary>An ammunition can. Lighter, and useless until it reaches a deployed gun.</summary>
	AmmoCan = 2,
}

/// <summary>Where one heavy gun is in its life (docs/IMPLEMENTATION_PLAN.md §M5).</summary>
public enum EmplacementStateId : byte
{
	/// <summary>On the ground where the map put it, or where somebody left it.</summary>
	Deployed = 0,

	/// <summary>On somebody's back.</summary>
	Carried = 1,

	/// <summary>Deployed, with somebody behind it.</summary>
	Mounted = 2,
}

/// <summary>What one press of the use key turned out to mean.</summary>
public enum UseAction : byte
{
	None = 0,
	Mount,
	Dismount,
	PickUpGun,
	Deploy,
	PickUpCan,
	DropCan,
	Resupply,

	/// <summary>Swap the large weapon for the next one in a weapon locker (docs/NETCODE.md §10.6).</summary>
	SwapWeapon,
}

/// <summary>A press of the use key, as the two ends of the prediction both read it.</summary>
public enum UseIntent : byte
{
	None = 0,

	/// <summary>Pressed and released inside <see cref="UseTracker.HoldTicks"/>.</summary>
	Tap,

	/// <summary>Held past it. Emitted once, on the tick it crosses, not on release.</summary>
	Hold,
}

/// <summary>
/// Turns the use button into a tap or a hold, from the recorded input frames and
/// nothing else.
///
/// One key does everything a player does with a heavy gun, which needs two
/// intents: a tap mounts, deploys, resupplies; a hold picks the thing up and
/// carries it away. Counting the ticks rather than the seconds is what makes the
/// two ends agree — the owning client predicts its own press, and a hold that
/// crossed on a different tick on the server would be a mount the client never
/// sees (docs/NETCODE.md §3.1).
/// </summary>
public struct UseTracker
{
	/// <summary>Ticks of holding that turn a tap into a hold. Half a second.</summary>
	public const int HoldTicks = SimConfig.TickRate / 2;

	private int _heldTicks;
	private bool _emitted;

	/// <summary>How long the key has been down, for a progress ring on the HUD.</summary>
	public readonly int HeldTicks => _heldTicks;

	public readonly float HoldProgress => Mathf.Clamp(_heldTicks / (float)HoldTicks, 0f, 1f);

	/// <summary>
	/// Advances one tick and reports the press that completed on it, if any.
	///
	/// Called every tick for every player, alive or not, so that the tracker is a
	/// function of the recorded frames and nothing else — a press begun while dead
	/// resolves to an action the caller discards, rather than to a tracker that has
	/// to be reset by hand and can therefore be left out of step.
	/// </summary>
	public UseIntent Sample(in InputContext input)
	{
		bool held = input.Held(InputButtons.Use);

		if (!held)
		{
			// A release only means something if the hold had not already fired.
			bool tapped = input.WasHeld(InputButtons.Use) && !_emitted;
			_heldTicks = 0;
			_emitted = false;
			return tapped ? UseIntent.Tap : UseIntent.None;
		}

		if (!input.WasHeld(InputButtons.Use))
		{
			// A fresh press starts from zero whatever the tracker was left holding —
			// its owner may have died halfway through the last one, and the frames say
			// so. This is what keeps the tracker a function of the frames rather than
			// of something a caller has to remember to reset.
			_heldTicks = 0;
			_emitted = false;
		}

		_heldTicks++;

		if (_emitted || _heldTicks < HoldTicks)
		{
			return UseIntent.None;
		}

		_emitted = true;
		return UseIntent.Hold;
	}
}

/// <summary>What the world looks like to one player pressing the use key.</summary>
public readonly struct UseSituation
{
	public readonly UseIntent Intent;
	public readonly CarryKind Carrying;

	/// <summary>True while this player is behind a gun.</summary>
	public readonly bool Mounted;

	/// <summary>A deployed gun is within <see cref="SimConfig.EmplacementReachMeters"/>.</summary>
	public readonly bool GunInReach;

	/// <summary>An ammunition can is within reach, and nearer than any gun.</summary>
	public readonly bool CanInReach;

	/// <summary>False in the air. A gun put down in mid-air would land wherever the physics felt like.</summary>
	public readonly bool OnGround;

	/// <summary>
	/// A weapon locker is within <see cref="SimConfig.LockerReachMeters"/>, and nearer
	/// than any gun or can that is.
	/// </summary>
	public readonly bool LockerInReach;

	public UseSituation(UseIntent intent, CarryKind carrying, bool mounted, bool gunInReach, bool canInReach,
		bool onGround, bool lockerInReach = false)
	{
		Intent = intent;
		Carrying = carrying;
		Mounted = mounted;
		GunInReach = gunInReach;
		CanInReach = canInReach;
		OnGround = onGround;
		LockerInReach = lockerInReach;
	}
}

/// <summary>
/// The rules for heavy guns and ammunition cans, as pure functions
/// (docs/IMPLEMENTATION_PLAN.md §M5).
///
/// Engine-free for the usual reason and one extra one: every decision here is made
/// twice — once by the server and once by the owning client, a round trip apart,
/// from the same recorded input — so it has to be a function of its arguments and
/// nothing else. Where the two do disagree (the client thought it had a gun in
/// reach and the server did not), the disagreement corrects itself the way every
/// other misprediction does, because all it can change is the player's move speed.
/// </summary>
public static class EmplacementSim
{
	/// <summary>
	/// What one press means. The table is the whole design: a tap is every
	/// interaction that leaves things where they are, and a hold is the one that
	/// picks something up and walks off with it.
	/// </summary>
	public static UseAction Resolve(in UseSituation situation)
	{
		if (situation.Intent == UseIntent.None)
		{
			return UseAction.None;
		}

		bool hold = situation.Intent == UseIntent.Hold;

		if (situation.Mounted)
		{
			// Holding the key behind a gun takes it with you: it is the one way to
			// move a gun somebody has already set up, including your own.
			return hold ? UseAction.PickUpGun : UseAction.Dismount;
		}

		switch (situation.Carrying)
		{
			case CarryKind.Gun:
				// Either press puts it down; there is nothing else a carried gun does.
				return situation.OnGround ? UseAction.Deploy : UseAction.None;

			case CarryKind.AmmoCan:
				if (situation.GunInReach && !hold)
				{
					return UseAction.Resupply;
				}
				return situation.OnGround ? UseAction.DropCan : UseAction.None;
		}

		// Empty-handed at a locker, a tap swaps the large weapon. Only empty-handed:
		// a gun or a can on your back is put down by the key, wherever you are
		// standing, and a hold is what picks things up — there is nothing here to
		// carry off.
		if (situation.LockerInReach)
		{
			return hold ? UseAction.None : UseAction.SwapWeapon;
		}

		if (situation.CanInReach)
		{
			return UseAction.PickUpCan;
		}

		if (situation.GunInReach)
		{
			return hold ? UseAction.PickUpGun : UseAction.Mount;
		}

		return UseAction.None;
	}

	/// <summary>
	/// What carrying something does to a player's speed. Applied to every movement
	/// state's authored speed, so sprinting with a gun is slower than walking
	/// without one — which is the whole cost of taking one anywhere.
	/// </summary>
	public static float MoveScale(CarryKind carrying) => carrying switch
	{
		CarryKind.Gun => SimConfig.CarryMoveScale,
		CarryKind.AmmoCan => SimConfig.AmmoCanMoveScale,
		_ => 1f,
	};

	/// <summary>
	/// Where a mounted gun is actually pointed, given where its gunner is looking.
	///
	/// A heavy gun that turns like a rifle is a rifle, and where it was put down
	/// stops being a decision. The clamp is about the yaw it was deployed at and is
	/// computed on both ends from the same replicated yaw, so the owner's predicted
	/// tracer is on the authoritative line (docs/NETCODE.md §4.3).
	/// </summary>
	public static Vector3 Traverse(float aimYaw, float aimPitch, float deployYaw)
	{
		float yaw = deployYaw + Mathf.Clamp(Mathf.AngleDifference(deployYaw, aimYaw),
			-SimConfig.EmplacementTraverseRadians, SimConfig.EmplacementTraverseRadians);

		float pitch = Mathf.Clamp(aimPitch, -SimConfig.EmplacementElevationRadians,
			SimConfig.EmplacementElevationRadians);

		return Aim.Direction(yaw, pitch);
	}

	/// <summary>
	/// What a can does to a belt: fills it, and wastes whatever does not fit. A
	/// player who walks a can out to a gun that is nearly full has wasted the walk,
	/// which is the decision the can exists to create.
	/// </summary>
	public static short Resupply(short ammo, int magazineSize, int canRounds)
	{
		int filled = ammo + Mathf.Max(canRounds, 0);
		return (short)Mathf.Clamp(filled, 0, Mathf.Max(magazineSize, 0));
	}

	/// <summary>Whether a can is worth spending on this gun at all.</summary>
	public static bool NeedsAmmo(short ammo, int magazineSize) => ammo < magazineSize;
}

/// <summary>
/// What a weapon locker hands over (docs/NETCODE.md §10.6), as a pure function.
///
/// A locker holds every large weapon, and one tap of the use key swaps the one in
/// the player's hands for the next in the list — rifle, launcher, DMR, and round
/// again. A cycle rather than a menu because a menu would need keys, and the keys a
/// living player presses are the recorded input the simulation runs on: 1, 2 and 3
/// already switch slots. One button, from the frames, is what lets a bot, a policy
/// on the agent socket and a demo all use it the way a person does.
/// </summary>
public static class LockerSim
{
	/// <summary>
	/// The weapon after <paramref name="current"/> in <paramref name="choices"/>,
	/// wrapping. Something not in the list — which a sanitized loadout never holds —
	/// gets the first choice, so the answer is always a weapon the locker has.
	/// </summary>
	public static byte Next(byte current, ReadOnlySpan<byte> choices)
	{
		if (choices.Length == 0)
		{
			return current;
		}

		for (int i = 0; i < choices.Length; i++)
		{
			if (choices[i] == current)
			{
				return choices[(i + 1) % choices.Length];
			}
		}

		return choices[0];
	}

	/// <summary>Where <paramref name="id"/> is in <paramref name="choices"/>, or -1.</summary>
	public static int IndexOf(byte id, ReadOnlySpan<byte> choices)
	{
		for (int i = 0; i < choices.Length; i++)
		{
			if (choices[i] == id)
			{
				return i;
			}
		}

		return -1;
	}
}
