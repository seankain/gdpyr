namespace Gdpyr.Core;

/// <summary>
/// Whether a text field or a menu owns the keyboard.
///
/// Godot's <c>Input</c> singleton reports the physical state of a key whatever
/// has focus: a <c>LineEdit</c> swallowing an event stops it reaching
/// <c>_UnhandledInput</c>, but it does not make <c>Input.IsActionPressed</c> say
/// no. Everything in this project that reads the device does so by polling —
/// <see cref="Gdpyr.Fps.LocalInputSampler"/> and the strategist's camera — so
/// without this flag, typing <c>record</c> into the console would reload the
/// weapon, and typing a demo's name would walk into a wall.
///
/// The loadout panel is the other owner (docs/LOADOUT.md L-D12): its digits, its
/// <c>D</c> and its Enter mean panel things, and the character stands still while it
/// is up, so the sampler treats it as it treats a text field.
///
/// Static for the same reason <see cref="Session"/> is: process state, written in
/// one place and read in two, where an autoload would add an ordering question
/// to answer.
/// </summary>
public static class InputFocus
{
	/// <summary>True while a text field is up. The pollers treat it as "no input this tick".</summary>
	public static bool TextEntry { get; private set; }

	/// <summary>
	/// True while the loadout panel is up (docs/LOADOUT.md §7.2). The sampler sends
	/// neutral frames, and the pause menu leaves Escape to the panel.
	/// </summary>
	public static bool Menu { get; private set; }

	public static void BeginTextEntry() => TextEntry = true;

	public static void EndTextEntry() => TextEntry = false;

	public static void BeginMenu() => Menu = true;

	public static void EndMenu() => Menu = false;
}
