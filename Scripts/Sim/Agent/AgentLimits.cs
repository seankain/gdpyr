using System;

namespace Gdpyr.Sim.Agent;

/// <summary>
/// The fairness ceilings an attached seat plays under (docs/AGENT_API.md §7.3).
///
/// A policy that snap-aims across 180° in one tick and fires the instant its
/// crosshair crosses a head is not playing the game a person plays, and a
/// playtest against one measures nothing — so every attached seat carries the
/// ceilings a bot carries, from <see cref="BotTraits"/>, and they are enforced by
/// clamping the submitted frame on the server rather than by trusting the policy
/// to have clamped it (the posture docs/NETCODE.md §1 takes towards every other
/// byte a client chooses).
///
/// <c>--agent-unbounded</c> lifts them for research runs and is stamped into
/// every observation and every trace.
/// </summary>
public readonly struct AgentLimits
{
	/// <summary>Commands a strategist seat may issue per second. An APM cap, and a bound on server cost.</summary>
	public const int DefaultCommandsPerSecond = 8;

	/// <summary>How fast the seat may swing its aim [rad/s]. Infinite when unbounded.</summary>
	public readonly float TurnRateRadians;

	public readonly int CommandsPerSecond;

	/// <summary>True when <c>--agent-unbounded</c> lifted the first two.</summary>
	public readonly bool Unbounded;

	/// <summary>True when <c>--agent-omniscient</c> dropped the fog. Labelled as cheating everywhere it appears.</summary>
	public readonly bool Omniscient;

	public AgentLimits(float turnRateRadians, int commandsPerSecond, bool unbounded, bool omniscient)
	{
		TurnRateRadians = MathF.Max(turnRateRadians, 0.01f);
		CommandsPerSecond = Math.Max(commandsPerSecond, 1);
		Unbounded = unbounded;
		Omniscient = omniscient;
	}

	/// <summary>The ceilings a bot plays under, which is the default for an attached seat.</summary>
	public static AgentLimits From(in BotTraits traits, bool unbounded = false, bool omniscient = false) =>
		new(unbounded ? float.MaxValue : traits.TurnRateRadians,
			unbounded ? int.MaxValue : DefaultCommandsPerSecond,
			unbounded,
			omniscient);

	public static AgentLimits Default => From(BotTraits.Default);

	/// <summary>
	/// The signed shortest way round from <paramref name="from"/> to
	/// <paramref name="to"/>, in [-π, π). Yaw is an angle on a circle, and a policy
	/// that asks for 0.01 rad while the character sits at 6.27 is asking for a
	/// nudge, not for a full turn.
	/// </summary>
	public static float ShortestDelta(float from, float to)
	{
		float delta = to - from;
		return delta - (Quantize.TwoPi * MathF.Floor((delta + MathF.PI) / Quantize.TwoPi));
	}

	/// <summary>
	/// Where the seat's yaw actually lands this tick: towards
	/// <paramref name="target"/>, by at most the turn-rate ceiling.
	/// </summary>
	public float SlewYaw(float current, float target, float dt)
	{
		float delta = ShortestDelta(current, target);
		if (Unbounded)
		{
			return current + delta;
		}

		float allowance = TurnRateRadians * MathF.Max(dt, 0f);
		return current + Math.Clamp(delta, -allowance, allowance);
	}

	/// <summary>Pitch has no wrap: it is clamped to the look limits and slewed the same way.</summary>
	public float SlewPitch(float current, float target, float dt)
	{
		float clamped = Math.Clamp(target, -Quantize.HalfPi, Quantize.HalfPi);
		if (Unbounded)
		{
			return clamped;
		}

		float allowance = TurnRateRadians * MathF.Max(dt, 0f);
		return Math.Clamp(clamped, current - allowance, current + allowance);
	}
}

/// <summary>
/// The strategist seat's APM cap as a token bucket: a full second's worth of
/// commands is available at once, and it refills at
/// <see cref="AgentLimits.CommandsPerSecond"/>.
///
/// A bucket rather than a per-batch divisor because a strategist's decisions are
/// bursty by nature — selecting six units and ordering them is one thought — and
/// the thing worth bounding is the sustained rate, not the shape of one burst.
/// </summary>
public struct AgentCommandBudget
{
	private float _tokens;
	private uint _lastTick;
	private bool _started;

	/// <summary>Commands accepted so far. For the debug HUD and the trace.</summary>
	public int Accepted { get; private set; }

	/// <summary>Commands refused by the cap. A policy emitting garbage shows up rather than silently doing nothing.</summary>
	public int Refused { get; private set; }

	/// <summary>
	/// How many of <paramref name="requested"/> may run on this tick. Refills first,
	/// then spends, so a seat that has been quiet for a second gets its full second
	/// back and no more.
	/// </summary>
	public int Take(uint tick, int requested, in AgentLimits limits)
	{
		if (requested <= 0)
		{
			return 0;
		}

		if (limits.Unbounded)
		{
			Accepted += requested;
			return requested;
		}

		if (!_started)
		{
			_started = true;
			_lastTick = tick;
			_tokens = limits.CommandsPerSecond;
		}
		else if (tick > _lastTick)
		{
			float seconds = (tick - _lastTick) * SimConfig.TickDelta;
			_tokens = MathF.Min(_tokens + (seconds * limits.CommandsPerSecond), limits.CommandsPerSecond);
			_lastTick = tick;
		}

		int allowed = Math.Min(requested, (int)MathF.Floor(_tokens));
		_tokens -= allowed;
		Accepted += allowed;
		Refused += requested - allowed;
		return allowed;
	}
}
