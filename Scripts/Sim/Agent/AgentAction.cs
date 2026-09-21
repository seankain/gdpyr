using System;
using System.Buffers.Binary;

namespace Gdpyr.Sim.Agent;

/// <summary>
/// One decision from a ground-force policy (docs/AGENT_API.md §7.2).
///
/// The action space *is* <see cref="InputFrame"/> — the same twelve bytes a human
/// client sends, consumed by the same movement FSM and the same
/// <see cref="WeaponSim"/>. This struct is only the unclamped request; what the
/// character is actually fed comes out of <see cref="AgentActionCodec.Resolve"/>,
/// which applies the turn-rate ceiling.
/// </summary>
public struct AgentGroundAction
{
	/// <summary>Strafe axis, right positive, in [-1,1].</summary>
	public float MoveX;

	/// <summary>Forward/back axis, backward positive (Godot's -Z is forward), in [-1,1].</summary>
	public float MoveZ;

	/// <summary>
	/// True when <see cref="Yaw"/> and <see cref="Pitch"/> are deltas from where the
	/// character is looking rather than absolute angles. Deltas are the
	/// parameterization a policy should be learning in; absolutes are what the wire
	/// format carries and what a replay needs.
	/// </summary>
	public bool LookIsDelta;

	public float Yaw;
	public float Pitch;

	public ushort Buttons;

	public readonly bool Held(InputButtons button) => (Buttons & (ushort)button) != 0;

	public static AgentGroundAction Neutral => default;
}

/// <summary>
/// Turns an agent's action into the frame the character is simulated from, and
/// back — the trust boundary for the agent socket.
///
/// Decoding is total: a malformed or hostile payload comes back as false rather
/// than throwing, and every field is clamped into range before anything
/// downstream sees it.
/// </summary>
public static class AgentActionCodec
{
	/// <summary>
	/// Bytes of a packed ground action: seat, flags, two reserved, then an
	/// <see cref="InputFrame"/>.
	///
	/// The design sketched sixteen bytes with a <c>u16</c> seat. A seat is named by
	/// its peer id, and <see cref="BotRoster"/> draws those from the top of the
	/// positive <c>int</c> range so they cannot collide with a Godot client's — so
	/// the field is a <c>u32</c> and the record is twenty bytes. Recorded rather
	/// than truncated: a seat id folded into sixteen bits addresses somebody else's
	/// character, or nobody's.
	/// </summary>
	public const int GroundSizeBytes = 8 + InputFrame.SizeBytes;

	/// <summary><see cref="AgentGroundAction.LookIsDelta"/>, on the wire.</summary>
	public const ushort FlagLookIsDelta = 1 << 0;

	public static int EncodeGround(int seat, in AgentGroundAction action, uint tick, Span<byte> into)
	{
		if (into.Length < GroundSizeBytes)
		{
			return 0;
		}

		BinaryPrimitives.WriteInt32LittleEndian(into, seat);
		BinaryPrimitives.WriteUInt16LittleEndian(into[4..], action.LookIsDelta ? FlagLookIsDelta : (ushort)0);
		BinaryPrimitives.WriteUInt16LittleEndian(into[6..], 0);

		InputFrame frame = InputFrame.Create(tick, action.MoveX, action.MoveZ, action.Yaw, action.Pitch,
			(InputButtons)action.Buttons);

		// The frame body, without InputCodec's count header: one action is one frame.
		BinaryPrimitives.WriteUInt32LittleEndian(into[8..], frame.Tick);
		into[12] = (byte)frame.MoveX;
		into[13] = (byte)frame.MoveZ;
		BinaryPrimitives.WriteUInt16LittleEndian(into[14..], frame.Yaw);
		BinaryPrimitives.WriteInt16LittleEndian(into[16..], frame.Pitch);
		BinaryPrimitives.WriteUInt16LittleEndian(into[18..], frame.Buttons);
		return GroundSizeBytes;
	}

	public static bool TryDecodeGround(ReadOnlySpan<byte> payload, out int seat, out uint tick,
		out AgentGroundAction action)
	{
		seat = 0;
		tick = 0;
		action = AgentGroundAction.Neutral;

		if (payload.Length < GroundSizeBytes)
		{
			return false;
		}

		seat = BinaryPrimitives.ReadInt32LittleEndian(payload);
		ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(payload[4..]);
		tick = BinaryPrimitives.ReadUInt32LittleEndian(payload[8..]);

		// -128 has no positive counterpart; clamped away here rather than left for
		// the movement code, exactly as InputCodec does it.
		sbyte moveX = (sbyte)Math.Max((sbyte)payload[12], (sbyte)(-127));
		sbyte moveZ = (sbyte)Math.Max((sbyte)payload[13], (sbyte)(-127));

		float yaw = Quantize.U16ToYaw(BinaryPrimitives.ReadUInt16LittleEndian(payload[14..]));
		float pitch = Quantize.I16ToPitch(BinaryPrimitives.ReadInt16LittleEndian(payload[16..]));

		bool delta = (flags & FlagLookIsDelta) != 0;

		action = new AgentGroundAction
		{
			MoveX = Quantize.I8ToAxis(moveX),
			MoveZ = Quantize.I8ToAxis(moveZ),
			LookIsDelta = delta,

			// A delta arrives quantized the same way an absolute yaw does, so it comes
			// back in [0, 2π); the shortest way round is the one that was meant.
			Yaw = delta ? AgentLimits.ShortestDelta(0f, yaw) : yaw,
			Pitch = pitch,
			Buttons = BinaryPrimitives.ReadUInt16LittleEndian(payload[18..]),
		};
		return true;
	}

	/// <summary>
	/// Where the action wants the character looking, as an absolute pair.
	/// </summary>
	public static void Target(in AgentGroundAction action, float currentYaw, float currentPitch,
		out float yaw, out float pitch)
	{
		yaw = action.LookIsDelta ? currentYaw + action.Yaw : action.Yaw;
		pitch = action.LookIsDelta ? currentPitch + action.Pitch : action.Pitch;
	}

	/// <summary>
	/// The frame the character is actually simulated from: the action's movement and
	/// buttons verbatim, and a look angle slewed towards the action's target at no
	/// more than the seat's turn-rate ceiling (docs/AGENT_API.md §7.3).
	///
	/// The ceiling is applied per simulated tick rather than per decision, which is
	/// the one place this deviates from §5.3's "held actions are held exactly": the
	/// look *target* is held for the seat's <c>step_mul</c>, and the angle walks
	/// towards it. Holding the angle instead would let a policy at <c>step_mul</c> 4
	/// turn four ticks' worth in one tick, which is the snap the ceiling exists to
	/// forbid. Movement axes and buttons are held exactly, so a fire button held for
	/// four ticks still produces the button edges the FSM would see from a person
	/// holding it.
	/// </summary>
	public static InputFrame Resolve(in AgentGroundAction action, uint tick, float currentYaw, float currentPitch,
		in AgentLimits limits, float dt)
	{
		Target(action, currentYaw, currentPitch, out float targetYaw, out float targetPitch);

		float yaw = limits.SlewYaw(currentYaw, targetYaw, dt);
		float pitch = limits.SlewPitch(currentPitch, targetPitch, dt);

		return InputFrame.Create(tick, Math.Clamp(action.MoveX, -1f, 1f), Math.Clamp(action.MoveZ, -1f, 1f),
			yaw, pitch, (InputButtons)action.Buttons);
	}

	/// <summary>
	/// Parses the button names the JSON control plane uses. Unknown names are
	/// ignored rather than rejected: a policy built against a newer schema must
	/// degrade, not disconnect.
	/// </summary>
	public static InputButtons ButtonFromName(string name) => name switch
	{
		"jump" => InputButtons.Jump,
		"crouch" => InputButtons.Crouch,
		"sprint" => InputButtons.Sprint,
		"fire" => InputButtons.Fire,
		"ads" => InputButtons.Ads,
		"reload" => InputButtons.Reload,
		"use" => InputButtons.Use,
		"melee" => InputButtons.Melee,
		"weapon1" => InputButtons.Weapon1,
		"weapon2" => InputButtons.Weapon2,
		"weapon3" => InputButtons.Weapon3,
		_ => InputButtons.None,
	};

	/// <summary>Every button a policy may name, in the order <c>welcome</c> publishes them.</summary>
	public static readonly string[] ButtonNames =
	{
		"jump", "crouch", "sprint", "fire", "ads", "reload", "use", "melee", "weapon1", "weapon2", "weapon3",
	};
}
