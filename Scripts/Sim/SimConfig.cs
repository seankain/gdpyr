namespace Gdpyr.Sim;

/// <summary>
/// Constants the whole simulation agrees on. Engine-free by design: everything
/// under Scripts/Sim is unit-testable without Godot (docs/IMPLEMENTATION_PLAN.md §3).
/// </summary>
public static class SimConfig
{
	/// <summary>
	/// Fixed simulation rate, in hertz. The server owns the tick; clients predict
	/// against it. Changing this changes the meaning of every recorded tick index,
	/// so server and clients must be built from the same value
	/// (docs/NETCODE.md §2).
	/// </summary>
	public const int TickRate = 60;

	/// <summary>Seconds per simulation tick.</summary>
	public const float TickDelta = 1.0f / TickRate;
}
