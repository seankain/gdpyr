using System;
using Godot;

namespace Gdpyr.Sim;

/// <summary>
/// Everything a character can be told to do in one tick, as a bit field.
/// Fire/Ads/Reload/Use/Melee are on the wire from M1 but unsampled until M2 —
/// adding a bit later would be a protocol break for no reason.
/// </summary>
[Flags]
public enum InputButtons : ushort
{
	None = 0,
	Jump = 1 << 0,
	Crouch = 1 << 1,
	Sprint = 1 << 2,
	Fire = 1 << 3,
	Ads = 1 << 4,
	Reload = 1 << 5,
	Use = 1 << 6,
	Melee = 1 << 7,
	Weapon1 = 1 << 8,
	Weapon2 = 1 << 9,
	Weapon3 = 1 << 10,
}

/// <summary>
/// One tick of player intent — the only thing a client is trusted to send
/// (docs/NETCODE.md §1). Angles are absolute rather than deltas so that replaying
/// a frame during reconciliation reproduces exactly the orientation the server
/// simulated; a delta would accumulate differently on each replay.
///
/// Lives under Scripts/Sim rather than Scripts/Fps so the codec and the movement
/// step stay testable without an engine.
/// </summary>
public struct InputFrame
{
	/// <summary>Bytes on the wire: 4 + 1 + 1 + 2 + 2 + 2.</summary>
	public const int SizeBytes = 12;

	/// <summary>The server tick this input is meant for, as the client estimates it.</summary>
	public uint Tick;

	/// <summary>Strafe axis, right positive, quantized to [-127,127].</summary>
	public sbyte MoveX;

	/// <summary>Forward/back axis, backward positive (Godot's -Z is forward).</summary>
	public sbyte MoveZ;

	public ushort Yaw;
	public short Pitch;
	public ushort Buttons;

	public readonly float MoveXAxis => Quantize.I8ToAxis(MoveX);
	public readonly float MoveZAxis => Quantize.I8ToAxis(MoveZ);
	public readonly Vector2 MoveAxes => new(MoveXAxis, MoveZAxis);
	public readonly float YawRadians => Quantize.U16ToYaw(Yaw);
	public readonly float PitchRadians => Quantize.I16ToPitch(Pitch);

	public readonly bool Held(InputButtons button) => (Buttons & (ushort)button) != 0;

	public static InputFrame Create(uint tick, float moveX, float moveZ, float yaw, float pitch, InputButtons buttons) =>
		new()
		{
			Tick = tick,
			MoveX = Quantize.AxisToI8(moveX),
			MoveZ = Quantize.AxisToI8(moveZ),
			Yaw = Quantize.YawToU16(yaw),
			Pitch = Quantize.PitchToI16(pitch),
			Buttons = (ushort)buttons,
		};

	/// <summary>
	/// A neutral frame that holds the given orientation: what the server feeds a
	/// character whose owner has not sent anything yet, so it still falls to the
	/// floor instead of hanging in the air.
	/// </summary>
	public static InputFrame Neutral(uint tick, float yaw = 0f, float pitch = 0f) =>
		Create(tick, 0f, 0f, yaw, pitch, InputButtons.None);

	public override readonly string ToString() =>
		$"tick {Tick} move ({MoveX},{MoveZ}) yaw {YawRadians:0.00} buttons 0x{Buttons:x}";
}

/// <summary>
/// An <see cref="InputFrame"/> plus the previous tick's buttons, which is what a
/// movement state needs to answer "was jump pressed *this* tick". This replaces
/// <c>Input.IsActionJustPressed</c> in the simulation: edge detection has to come
/// from the recorded frames, or a replayed tick would see a different edge than
/// the original (docs/NETCODE.md §3.1).
/// </summary>
public readonly struct InputContext
{
	public readonly InputFrame Frame;
	public readonly ushort PreviousButtons;
	public readonly float Dt;

	public InputContext(in InputFrame frame, ushort previousButtons, float dt)
	{
		Frame = frame;
		PreviousButtons = previousButtons;
		Dt = dt;
	}

	public Vector2 MoveAxes => Frame.MoveAxes;
	public float Yaw => Frame.YawRadians;
	public float Pitch => Frame.PitchRadians;

	public bool Held(InputButtons button) => Frame.Held(button);
	public bool WasHeld(InputButtons button) => (PreviousButtons & (ushort)button) != 0;
	public bool JustPressed(InputButtons button) => Held(button) && !WasHeld(button);
	public bool JustReleased(InputButtons button) => !Held(button) && WasHeld(button);
}
