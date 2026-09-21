using Godot;

namespace Gdpyr.Match;

/// <summary>
/// The round's tuning, as an editable resource.
///
/// Ported from <c>pyrrhic/Assets/Scripts/Game/GameMode.cs</c> — a
/// <c>ScriptableObject</c> with the same three numbers — with <c>BootLives</c>
/// renamed to what it actually is in this design: a shared pool of respawns for
/// the whole ground team, not lives per player.
///
/// <see cref="StrategistTickets"/> is carried but unused until the strategist
/// exists (M3/M5); it is here so the round's tuning lives in one resource rather
/// than accumulating a second one later.
/// </summary>
[GlobalClass]
public partial class GameModeDefinition : Resource
{
	/// <summary>Respawns the ground team shares. At zero the round ends.</summary>
	[Export]
	public int GroundForceTickets = 50;

	/// <summary>The strategist's starting points. Unused in M2.</summary>
	[Export]
	public int StrategistTickets = 1000;

	[Export]
	public int RoundDurationMinutes = 20;

	[Export]
	public float RespawnSeconds = 3f;

	/// <summary>
	/// Seconds between a round ending and the next one starting. A playtest that
	/// needs someone to press a key between rounds gets fewer rounds per session.
	/// </summary>
	[Export]
	public float IntermissionSeconds = 10f;
}
