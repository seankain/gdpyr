using System;

namespace Gdpyr.AgentClient;

/// <summary>
/// The discrete action heads a ground policy learns, and how they become the
/// twelve bytes the server simulates (docs/AGENT_API.md §7.2).
///
/// The game's action space *is* <c>InputFrame</c>: continuous look deltas and a
/// button field. RLMatrix's discrete agents want a vector of small integers and
/// require every head to be the same width, so this is the one place the two meet
/// — a fixed, documented discretization rather than something hidden inside the
/// environment.
///
/// It is deliberately coarse. The turn-rate ceiling an attached seat plays under
/// is about half a mouse (docs/AGENT_API.md §7.3), so the widest useful turn is
/// one decision's worth of that ceiling and the finest is a fraction of it;
/// anything finer is a harder credit-assignment problem for no extra control.
/// </summary>
public sealed class GroundActionSpace
{
	/// <summary>Every head is this wide. RLMatrix refuses a ragged action space.</summary>
	public const int HeadWidth = 5;

	/// <summary>Movement axes, as fractions of full deflection.</summary>
	private static readonly float[] AxisSteps = { -1f, -0.5f, 0f, 0.5f, 1f };

	/// <summary>Turn steps, as fractions of what the ceiling allows in one decision.</summary>
	private static readonly float[] TurnSteps = { -1f, -0.35f, 0f, 0.35f, 1f };

	/// <summary>Pitch steps. Smaller than the turn steps: the map is wide and short.</summary>
	private static readonly float[] PitchSteps = { -0.5f, -0.2f, 0f, 0.2f, 0.5f };

	private readonly float _turnPerDecision;

	/// <param name="turnRateRadians">
	/// The seat's ceiling [rad/s], from <c>welcome</c>. Zero means the server was
	/// started with <c>--agent-unbounded</c>, in which case a mouse-like 4.5 rad/s
	/// is used anyway: a policy that learned to snap would not transfer to a round
	/// with the ceilings on.
	/// </param>
	/// <param name="stepMul">Ticks one decision is held for.</param>
	/// <param name="tickRate">Server tick rate, from <c>welcome</c>.</param>
	public GroundActionSpace(float turnRateRadians, int stepMul, int tickRate = 60)
	{
		float ceiling = turnRateRadians > 0f ? turnRateRadians : 4.5f;
		_turnPerDecision = ceiling * (Math.Max(stepMul, 1) / (float)Math.Max(tickRate, 1));
	}

	/// <summary>
	/// The heads, in order: forward/back · strafe · turn · pitch · trigger ·
	/// stance. This is what <c>actionSize</c> hands RLMatrix.
	/// </summary>
	public static readonly int[] Heads = { HeadWidth, HeadWidth, HeadWidth, HeadWidth, HeadWidth, HeadWidth };

	/// <summary>Human-readable names, for a trace and for the log.</summary>
	public static readonly string[] HeadNames =
	{
		"forward", "strafe", "turn", "pitch", "trigger", "stance",
	};

	/// <summary>What each index of the trigger head means.</summary>
	public static readonly string[] TriggerNames = { "none", "fire", "ads", "ads+fire", "reload" };

	/// <summary>What each index of the stance head means.</summary>
	public static readonly string[] StanceNames = { "none", "sprint", "crouch", "jump", "use" };

	/// <summary>Turns one head vector into the action the server is sent.</summary>
	public AgentGroundAction Decode(int[] heads)
	{
		var action = new AgentGroundAction();
		if (heads == null || heads.Length < Heads.Length)
		{
			return action;
		}

		// Godot's -Z is forward, and InputFrame's MoveZ is backward-positive, so a
		// head index of 4 ("forward" at full deflection) is MoveZ of -1. Getting this
		// the wrong way round produces a policy that runs away from everything, which
		// is a slow bug to notice.
		action.MoveZ = -AxisSteps[Clamp(heads[0])];
		action.MoveX = AxisSteps[Clamp(heads[1])];

		action.DeltaYaw = TurnSteps[Clamp(heads[2])] * _turnPerDecision;
		action.DeltaPitch = PitchSteps[Clamp(heads[3])] * _turnPerDecision;

		action.Buttons = Trigger(Clamp(heads[4])) | Stance(Clamp(heads[5]));
		return action;
	}

	private static AgentButtons Trigger(int index) => index switch
	{
		1 => AgentButtons.Fire,
		2 => AgentButtons.Ads,
		3 => AgentButtons.Ads | AgentButtons.Fire,
		4 => AgentButtons.Reload,
		_ => AgentButtons.None,
	};

	private static AgentButtons Stance(int index) => index switch
	{
		1 => AgentButtons.Sprint,
		2 => AgentButtons.Crouch,
		3 => AgentButtons.Jump,

		// Picking a heavy gun up, and mounting one that is already deployed
		// (docs/IMPLEMENTATION_PLAN.md §M5). Held rather than tapped, which is what
		// the pickup wants anyway.
		4 => AgentButtons.Use,
		_ => AgentButtons.None,
	};

	private static int Clamp(int value) => value < 0 ? 0 : value >= HeadWidth ? HeadWidth - 1 : value;
}
