using System;
using Godot;

namespace Gdpyr.Sim;

/// <summary>
/// The operative's numbers (docs/OUTPOST.md §4): how close it gets before it stops
/// stalking and rushes, how long it waits in cover for fire to cover its next bound, and
/// how far ahead it lays smoke over open ground. Constants rather than a resource
/// because they are rules the tests hold it to; its body, speed and weapons are its
/// <c>UnitDefinition</c>'s.
/// </summary>
public static class OperativeRules
{
	/// <summary>Inside this of its quarry [m], measured flat, it stops going from cover to cover and rushes them.</summary>
	public const float CloseMeters = 10f;

	/// <summary>
	/// Further than this from its quarry [m] it holds its fire, so a shot does not give it
	/// away before it is close, unless it is being shot at. The pistol reaches further.
	/// </summary>
	public const float OpenFireMeters = 14f;

	/// <summary>How far on towards its quarry the ground it would cross is looked at from their eye [m].</summary>
	public const float AheadMeters = 3f;

	/// <summary>
	/// The cosine of half the arc its quarry watches, either side of where they look, flat:
	/// 65°, a screen's width and a little more. Behind that a player sees nothing, however
	/// clear the line.
	/// </summary>
	public const float WatchCos = 0.4226183f;

	/// <summary>How close to a cover spot or a dash point counts as there [m].</summary>
	public const float ArriveMeters = 1f;

	/// <summary>The shortest bound worth making: a cover spot at least this much nearer the quarry [m].</summary>
	public const float MinBoundMeters = 3f;

	/// <summary>How far from where it stands a cover spot for its next bound may be [m].</summary>
	public const float BoundSearchMeters = 16f;

	/// <summary>
	/// The least time it stays in cover between bounds [ticks]: half a second to look and
	/// to let the supporting fire land.
	/// </summary>
	public const int MinHoldTicks = SimConfig.TickRate / 2;

	/// <summary>
	/// The longest it waits in cover for covering fire, a screen of smoke, or its quarry to
	/// look away before it goes anyway [ticks]: it is aggressive, not patient.
	/// </summary>
	public const int MaxHoldTicks = SimConfig.TickRate * 4;

	/// <summary>The longest a bound to one spot may take before it gives up on it and holds where it is [ticks].</summary>
	public const int MaxBoundTicks = SimConfig.TickRate * 6;

	/// <summary>The longest one dash across open ground lasts before it goes to ground and looks again [ticks].</summary>
	public const int DashTicks = SimConfig.TickRate * 2;

	/// <summary>How long after its smoke leaves its hand it waits for the cloud to stand before it runs [ticks].</summary>
	public const int SmokeWaitTicks = (int)(1.2f * SimConfig.TickRate) + (SmokeRules.BloomTicks / 2);

	/// <summary>How far towards its quarry it throws its smoke [m]: far enough to screen the run, near enough to reach.</summary>
	public const float SmokeLeadMeters = 9f;

	/// <summary>The nearest it throws smoke [m]: no nearer than a hand grenade is thrown, so the arc is solvable.</summary>
	public const float MinSmokeMeters = 6f;

	/// <summary>Ticks between two smoke grenades: one screen per bound, not one per tick.</summary>
	public const int SmokeIntervalTicks = SimConfig.TickRate * 5;

	/// <summary>How long one call for covering fire stands [ticks]: refreshed every scan while it holds or runs.</summary>
	public const int CoverRequestTicks = SimConfig.TickRate * 2;

	/// <summary>Ticks between two stabs.</summary>
	public const int KnifeIntervalTicks = (int)(0.8f * SimConfig.TickRate);
}

/// <summary>Where an operative is in closing on its quarry (docs/OUTPOST.md §4.2).</summary>
public enum OperativePhase : byte
{
	/// <summary>No quarry: it carries out its order as any unit does.</summary>
	None = 0,

	/// <summary>Running to the next cover spot nearer its quarry.</summary>
	Bound = 1,

	/// <summary>In cover, crouched, waiting for covering fire, smoke, or its quarry to look away.</summary>
	Hold = 2,

	/// <summary>Has thrown smoke over the open ground ahead, and waits for it to stand.</summary>
	Smoke = 3,

	/// <summary>Running across open ground towards its quarry.</summary>
	Dash = 4,

	/// <summary>Inside <see cref="OperativeRules.CloseMeters"/>: rushing in to shoot and stab.</summary>
	Close = 5,
}

/// <summary>One operative's progress against its quarry. Server-side, per unit.</summary>
public struct OperativeState
{
	public OperativePhase Phase;

	/// <summary>The tick it went into <see cref="Phase"/>.</summary>
	public uint PhaseTick;

	/// <summary>Where it is running to: a cover spot, or a dash point.</summary>
	public Vector3 Point;

	/// <summary>Who it is after, as an <see cref="OwnerId"/>; <see cref="OwnerId.None"/> for nobody.</summary>
	public int QuarryOwnerId;

	/// <summary>The tick its last smoke grenade left its hand on; 0 for none yet.</summary>
	public uint LastSmokeTick;

	/// <summary>The tick of its last stab; 0 for none yet.</summary>
	public uint LastKnifeTick;

	/// <summary>Bounds, dashes and smokes this life, for the debug line and the tests.</summary>
	public int Bounds;

	public int Dashes;

	public int Smokes;

	public readonly int HeldTicks(uint tick) => tick >= PhaseTick ? (int)(tick - PhaseTick) : 0;
}

/// <summary>What an operative knows when it decides (<see cref="OperativeSim.Step"/>).</summary>
public struct OperativeSituation
{
	public uint Tick;

	/// <summary>Its own feet.</summary>
	public Vector3 Feet;

	/// <summary>It has somebody to go after.</summary>
	public bool HasQuarry;

	/// <summary>Who, as an <see cref="OwnerId"/>.</summary>
	public int QuarryOwnerId;

	/// <summary>Their feet, where it last knew them.</summary>
	public Vector3 QuarryFeet;

	/// <summary>
	/// Its quarry has a clear line to it as it is now — wall and smoke both — and is looking
	/// its way (<see cref="OperativeSim.Watches"/>). Unseen, it holds its fire and goes for
	/// the knife.
	/// </summary>
	public bool QuarrySeesIt;

	/// <summary>
	/// Its quarry, looking the way they look, would have a clear line to it standing a few
	/// metres on towards them (<see cref="OperativeRules.AheadMeters"/>): the ground it has to
	/// cross is ground they watch. Asked of it standing whatever its posture — crouched
	/// behind a low wall it is hidden, and that is not the question.
	/// </summary>
	public bool PathExposed;

	/// <summary>A box stands between it and its quarry where it is, low enough that it is hidden crouched.</summary>
	public bool InCover;

	/// <summary>A cover spot for its next bound: on the way, nearer its quarry, hidden from them.</summary>
	public bool HasBound;

	public Vector3 BoundSpot;

	/// <summary>Its own side's infantry, tanks or heavy gunners are firing at its quarry right now.</summary>
	public bool Covered;

	/// <summary>A cloud stands between its quarry and it, or the ground ahead of it.</summary>
	public bool Screened;

	/// <summary>It has a smoke grenade, and its hand is free to throw it.</summary>
	public bool HasSmoke;

	/// <summary>It has been shot at in the last moment.</summary>
	public bool FiredUpon;

	/// <summary>Its knife reaches its quarry from where it stands.</summary>
	public bool InKnifeReach;

	public readonly float Distance => Flat(Feet, QuarryFeet);

	public readonly Vector3 Toward
	{
		get
		{
			Vector3 to = QuarryFeet - Feet;
			to.Y = 0f;
			return to.LengthSquared() > 1e-6f ? to.Normalized() : Vector3.Forward;
		}
	}

	private static float Flat(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Z - b.Z).Length();
}

/// <summary>What an operative does this tick, decided by <see cref="OperativeSim.Step"/>.</summary>
public struct OperativeDecision
{
	/// <summary>False: carry out the order as any unit does — it has no quarry.</summary>
	public bool Hunting;

	/// <summary>Where to go. Its feet when it is to stay put.</summary>
	public Vector3 Point;

	/// <summary>Whether it is to move at all.</summary>
	public bool Moves;

	/// <summary>Down behind its cover.</summary>
	public bool Crouch;

	/// <summary>Leave a target in sight alone: it is still too far, and a shot gives it away.</summary>
	public bool HoldFire;

	/// <summary>Throw a smoke grenade at <see cref="SmokePoint"/> this tick.</summary>
	public bool ThrowSmoke;

	public Vector3 SmokePoint;

	/// <summary>Stab its quarry this tick.</summary>
	public bool Knife;

	/// <summary>Ask its side's infantry, tanks and heavy gunners to fire on its quarry.</summary>
	public bool CallCover;
}

/// <summary>
/// The operative's tactics (docs/OUTPOST.md §4.2), as a pure function of what it knows:
///
/// <code>
///             cover spot ahead                       arrived / too long
///   ┌──────────────────────────► Bound ──────────────────────────────┐
///   │                                                                ▼
///  (new quarry) ── in cover ───────────────────────────────────────► Hold ◄─┐
///   │               covered, screened or the way on unwatched, or waited too long │
///   │                       ├─ a cover spot ahead ───────────────► Bound    │
///   │                       ├─ open ground, smoke in hand ───────► Smoke ─► Dash
///   │                       └─ open ground, no smoke ────────────► Dash ────┘
///   └── inside CloseMeters, from anywhere ─────────────────────► Close: shoot, stab
/// </code>
///
/// It calls for covering fire whenever it holds or crosses open ground, holds its fire
/// until it is close, and stabs whoever its knife reaches. Engine-free: where the cover
/// is, who can see whom and whether its side is firing are the engine half's to find out,
/// and what it does with them is decided here and answered by <c>dotnet test</c>.
/// </summary>
public static class OperativeSim
{
	public static OperativeDecision Step(ref OperativeState state, in OperativeSituation s)
	{
		var decision = new OperativeDecision { Point = s.Feet };
		if (!s.HasQuarry)
		{
			if (state.Phase != OperativePhase.None)
			{
				Enter(ref state, OperativePhase.None, s.Tick);
			}

			state.QuarryOwnerId = OwnerId.None;
			return decision;
		}

		// A new quarry is a new approach.
		if (state.QuarryOwnerId != s.QuarryOwnerId)
		{
			state.QuarryOwnerId = s.QuarryOwnerId;
			Enter(ref state, OperativePhase.None, s.Tick);
		}

		// Too far to be sure of it, or unseen and stalking for the knife: it does not give
		// itself away — unless it is shot at, when it shoots back.
		decision.Hunting = true;
		float distance = s.Distance;
		decision.HoldFire = !s.FiredUpon && (distance > OperativeRules.OpenFireMeters || !s.QuarrySeesIt);

		if (distance <= OperativeRules.CloseMeters)
		{
			if (state.Phase != OperativePhase.Close)
			{
				Enter(ref state, OperativePhase.Close, s.Tick);
			}

			// Unseen — behind its smoke, or at their back — it holds its fire and goes for the
			// knife; seen, or shot at, it shoots (docs/OUTPOST.md §4.4).
			decision.Moves = true;
			decision.Point = s.QuarryFeet;
			decision.CallCover = !s.InKnifeReach;
			if (s.InKnifeReach && (state.LastKnifeTick == 0
				|| s.Tick - state.LastKnifeTick >= OperativeRules.KnifeIntervalTicks))
			{
				decision.Knife = true;
				state.LastKnifeTick = Math.Max(s.Tick, 1u);
			}

			return decision;
		}

		// Out of reach again: whoever it was rushing got away, or it was never this close.
		if (state.Phase == OperativePhase.Close)
		{
			Enter(ref state, OperativePhase.None, s.Tick);
		}

		switch (state.Phase)
		{
			case OperativePhase.None:
				Approach(ref state, s, ref decision);
				break;

			case OperativePhase.Bound:
				if (Flat(s.Feet, state.Point) <= OperativeRules.ArriveMeters
					|| state.HeldTicks(s.Tick) >= OperativeRules.MaxBoundTicks)
				{
					Hold(ref state, s, ref decision);
				}
				else
				{
					Run(state.Point, ref decision);
				}
				break;

			case OperativePhase.Hold:
				Hold(ref state, s, ref decision);
				break;

			case OperativePhase.Smoke:
				if (state.HeldTicks(s.Tick) >= OperativeRules.SmokeWaitTicks)
				{
					Dash(ref state, s, ref decision);
				}
				else
				{
					decision.Crouch = s.InCover;
					decision.CallCover = true;
				}
				break;

			default:
				// Dashing: on until it reaches its point or the dash is spent, then down
				// where it is and look again.
				if (Flat(s.Feet, state.Point) <= OperativeRules.ArriveMeters
					|| state.HeldTicks(s.Tick) >= OperativeRules.DashTicks)
				{
					Enter(ref state, OperativePhase.Hold, s.Tick);
					decision.Crouch = s.InCover;
					decision.CallCover = true;
				}
				else
				{
					Run(state.Point, ref decision);
					decision.CallCover = true;
				}
				break;
		}

		return decision;
	}

	/// <summary>
	/// A fresh approach: hold where it is if that is cover, else take the nearest cover
	/// ahead, else cross the open — unseen as it is, behind smoke when it has some, and
	/// regardless when it has none.
	/// </summary>
	private static void Approach(ref OperativeState state, in OperativeSituation s, ref OperativeDecision decision)
	{
		if (s.InCover)
		{
			Enter(ref state, OperativePhase.Hold, s.Tick);
			decision.Crouch = true;
			decision.CallCover = true;
			return;
		}

		Leave(ref state, s, ref decision);
	}

	/// <summary>
	/// In cover, crouched, calling for fire. It goes when its side's fire is on its quarry,
	/// smoke stands between them, or its quarry cannot see it — after a short look — or
	/// when it has waited as long as it will.
	/// </summary>
	private static void Hold(ref OperativeState state, in OperativeSituation s, ref OperativeDecision decision)
	{
		if (state.Phase != OperativePhase.Hold)
		{
			Enter(ref state, OperativePhase.Hold, s.Tick);
		}

		// Open ground ahead, its quarry watching and smoke in hand: lay the screen now rather
		// than wait out the hold for fire that may not come.
		int held = state.HeldTicks(s.Tick);
		bool chance = s.Covered || s.Screened || !s.PathExposed;
		bool smoke = !s.HasBound && s.PathExposed && !s.Screened && !s.Covered && SmokeReady(state, s);
		bool go = held >= OperativeRules.MaxHoldTicks || (held >= OperativeRules.MinHoldTicks && (chance || smoke));
		if (!go)
		{
			decision.Crouch = s.InCover;
			decision.CallCover = true;
			return;
		}

		Leave(ref state, s, ref decision);
	}

	/// <summary>The next leg: cover ahead if there is any; otherwise across the open, smoke first if it would help.</summary>
	private static void Leave(ref OperativeState state, in OperativeSituation s, ref OperativeDecision decision)
	{
		if (s.HasBound)
		{
			Enter(ref state, OperativePhase.Bound, s.Tick);
			state.Point = s.BoundSpot;
			state.Bounds++;
			Run(state.Point, ref decision);
			return;
		}

		// Under its side's fire it runs and keeps its smoke; watched and uncovered, it smokes first.
		bool smokeHelps = s.PathExposed && !s.Screened && !s.Covered;
		if (smokeHelps && SmokeReady(state, s))
		{
			Enter(ref state, OperativePhase.Smoke, s.Tick);
			state.LastSmokeTick = Math.Max(s.Tick, 1u);
			state.Smokes++;
			decision.ThrowSmoke = true;
			decision.SmokePoint = SmokePoint(s);
			decision.Crouch = s.InCover;
			decision.CallCover = true;
			return;
		}

		Dash(ref state, s, ref decision);
	}

	/// <summary>It has smoke, its hand is free, and its last screen was long enough ago.</summary>
	private static bool SmokeReady(in OperativeState state, in OperativeSituation s) =>
		s.HasSmoke && (state.LastSmokeTick == 0 || s.Tick - state.LastSmokeTick >= OperativeRules.SmokeIntervalTicks);

	private static void Dash(ref OperativeState state, in OperativeSituation s, ref OperativeDecision decision)
	{
		Enter(ref state, OperativePhase.Dash, s.Tick);
		state.Dashes++;

		// Towards its quarry, no further than the edge of rushing distance: the rest it
		// covers in Close.
		float run = MathF.Max(s.Distance - OperativeRules.CloseMeters + OperativeRules.ArriveMeters, OperativeRules.ArriveMeters * 2f);
		state.Point = s.Feet + (s.Toward * run);
		state.Point.Y = s.Feet.Y;
		Run(state.Point, ref decision);
		decision.CallCover = true;
	}

	/// <summary>
	/// Whether a quarry at <paramref name="eye"/> looking along <paramref name="look"/> has
	/// <paramref name="point"/> in front of them, measured flat (<see cref="OperativeRules.WatchCos"/>).
	/// A point on top of them, or a look that is no direction, is watched.
	/// </summary>
	public static bool Watches(Vector3 eye, Vector3 look, Vector3 point)
	{
		var to = new Vector2(point.X - eye.X, point.Z - eye.Z);
		var facing = new Vector2(look.X, look.Z);
		if (to.LengthSquared() < 1e-6f || facing.LengthSquared() < 1e-6f || !float.IsFinite(facing.X))
		{
			return true;
		}

		return to.Normalized().Dot(facing.Normalized()) >= OperativeRules.WatchCos;
	}

	/// <summary>
	/// Where it throws its smoke: along the line to its quarry, <see cref="OperativeRules.SmokeLeadMeters"/>
	/// out, but never past halfway and never nearer than a grenade can be aimed.
	/// </summary>
	public static Vector3 SmokePoint(in OperativeSituation s)
	{
		float lead = Math.Clamp(MathF.Min(OperativeRules.SmokeLeadMeters, s.Distance * 0.5f),
			OperativeRules.MinSmokeMeters, MathF.Max(OperativeRules.MinSmokeMeters, s.Distance));
		Vector3 at = s.Feet + (s.Toward * lead);
		at.Y = s.Feet.Y;
		return at;
	}

	private static void Run(Vector3 point, ref OperativeDecision decision)
	{
		decision.Moves = true;
		decision.Point = point;
		decision.Crouch = false;
	}

	private static void Enter(ref OperativeState state, OperativePhase phase, uint tick)
	{
		state.Phase = phase;
		state.PhaseTick = tick;
	}

	private static float Flat(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Z - b.Z).Length();
}

/// <summary>A unit's knife (docs/OUTPOST.md §4.4), as a pure function: whether its blade reaches a body.</summary>
public static class UnitMelee
{
	/// <summary>
	/// Whether a swing from <paramref name="origin"/> reaches <paramref name="capsule"/>: the
	/// nearest of the body is within <paramref name="rangeMeters"/>, as a player's swing is
	/// tested along its length.
	/// </summary>
	public static bool Reaches(Vector3 origin, in HitCapsule capsule, float rangeMeters) =>
		origin.IsFinite() && rangeMeters > 0f
		&& MathF.Sqrt(Hitbox.DistanceToAxisSquared(capsule, origin)) - capsule.Radius <= rangeMeters;
}

/// <summary>
/// Covering fire for operatives (docs/OUTPOST.md §4.5): where the side's infantry, tanks and
/// heavy gunners have just fired, so an operative can tell whether its quarry is under
/// fire; and where operatives have asked for fire, so the units that give it can answer.
/// One board per side, rebuilt from what happens each tick. Engine-free and fixed-size.
/// </summary>
public sealed class CoveringFireBoard
{
	/// <summary>How long a shot counts as covering fire after it was fired [ticks]: about one rifleman's burst gap and a half.</summary>
	public const int WindowTicks = SimConfig.TickRate * 3 / 2;

	/// <summary>How near its quarry a shot has to be aimed to cover it [m].</summary>
	public const float CoverMeters = 6f;

	/// <summary>How far from the operative that asked a unit may be and still answer [m].</summary>
	public const float SupportMeters = 70f;

	public const int ShotCapacity = 64;

	public const int RequestCapacity = 16;

	private readonly Vector3[] _shotAt = new Vector3[ShotCapacity];
	private readonly uint[] _shotTick = new uint[ShotCapacity];
	private int _nextShot;

	private readonly Vector3[] _requestAt = new Vector3[RequestCapacity];
	private readonly Vector3[] _requestFrom = new Vector3[RequestCapacity];
	private readonly uint[] _requestUntil = new uint[RequestCapacity];
	private readonly int[] _requestBy = new int[RequestCapacity];
	private int _requests;

	/// <summary>Calls answered since the round started, for the debug HUD.</summary>
	public int Answered { get; private set; }

	/// <summary>A supporting unit fired at <paramref name="at"/>.</summary>
	public void NoteShot(Vector3 at, uint tick)
	{
		_shotAt[_nextShot] = at;
		_shotTick[_nextShot] = Math.Max(tick, 1u);
		_nextShot = (_nextShot + 1) % ShotCapacity;
	}

	/// <summary>Whether a supporting unit has fired within <see cref="CoverMeters"/> of <paramref name="quarry"/> in the last <see cref="WindowTicks"/>.</summary>
	public bool Covers(Vector3 quarry, uint tick)
	{
		for (int i = 0; i < ShotCapacity; i++)
		{
			uint at = _shotTick[i];
			if (at != 0 && at <= tick && tick - at <= WindowTicks
				&& Flat(_shotAt[i], quarry) <= CoverMeters)
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// Operative <paramref name="unitId"/>, at <paramref name="from"/>, asks for fire on
	/// <paramref name="quarry"/> until <paramref name="untilTick"/>. One standing call per
	/// operative: a second replaces the first.
	/// </summary>
	public void Call(int unitId, Vector3 from, Vector3 quarry, uint untilTick)
	{
		int slot = -1;
		for (int i = 0; i < _requests; i++)
		{
			if (_requestBy[i] == unitId)
			{
				slot = i;
				break;
			}
		}

		if (slot < 0)
		{
			if (_requests >= RequestCapacity)
			{
				return;
			}

			slot = _requests++;
		}

		_requestBy[slot] = unitId;
		_requestFrom[slot] = from;
		_requestAt[slot] = quarry;
		_requestUntil[slot] = untilTick;
	}

	/// <summary>Takes back operative <paramref name="unitId"/>'s call: it is dead, or after nobody.</summary>
	public void Withdraw(int unitId)
	{
		for (int i = 0; i < _requests; i++)
		{
			if (_requestBy[i] == unitId)
			{
				RemoveAt(i);
				return;
			}
		}
	}

	/// <summary>Standing calls for fire, after the last <see cref="Expire"/>.</summary>
	public int Calls => _requests;

	/// <summary>Drops every call that has run out.</summary>
	public void Expire(uint tick)
	{
		for (int i = _requests - 1; i >= 0; i--)
		{
			if (tick > _requestUntil[i])
			{
				RemoveAt(i);
			}
		}
	}

	/// <summary>
	/// The call a supporting unit at <paramref name="feet"/>, which reaches
	/// <paramref name="rangeMeters"/>, should answer: the nearest standing call whose quarry
	/// is in its reach and whose operative is within <see cref="SupportMeters"/> of it.
	/// </summary>
	public bool TryAnswer(Vector3 feet, float rangeMeters, uint tick, out Vector3 point)
	{
		point = Vector3.Zero;
		float best = float.MaxValue;
		for (int i = 0; i < _requests; i++)
		{
			if (tick > _requestUntil[i])
			{
				continue;
			}

			float reach = Flat(feet, _requestAt[i]);
			if (reach > rangeMeters || Flat(feet, _requestFrom[i]) > SupportMeters || reach >= best)
			{
				continue;
			}

			best = reach;
			point = _requestAt[i];
		}

		if (best < float.MaxValue)
		{
			Answered++;
			return true;
		}

		return false;
	}

	public void Clear()
	{
		Array.Clear(_shotTick);
		_nextShot = 0;
		_requests = 0;
		Answered = 0;
	}

	private void RemoveAt(int index)
	{
		_requests--;
		_requestBy[index] = _requestBy[_requests];
		_requestFrom[index] = _requestFrom[_requests];
		_requestAt[index] = _requestAt[_requests];
		_requestUntil[index] = _requestUntil[_requests];
	}

	private static float Flat(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Z - b.Z).Length();
}
