namespace Gdpyr.Sim;

/// <summary>
/// Keeps a button that was already down when the game went back to listening to the
/// device out of the input frames until it is let go.
///
/// The sampler sends neutral frames while something else owns the keyboard and the
/// mouse — the pause menu, the console, the loadout panel (docs/LOADOUT.md L-D12) —
/// and whatever is still held on the tick it stops is that thing's press, not the
/// game's. Without this, the <c>E</c> that closes the locker's panel is a use press
/// that ends in a tap, and the tap opens the panel again. A press that begins once
/// the game is listening again is the game's, as always.
///
/// Engine-free, and applied where device state becomes a frame: what is masked is
/// never in a frame, so the server, the owner's prediction and a demo all see the
/// same thing.
/// </summary>
public struct ButtonLatch
{
	private InputButtons _held;
	private bool _away;

	/// <summary>The buttons being kept out of the frames now.</summary>
	public readonly InputButtons Held => _held;

	/// <summary>A tick on which the device was not the game's: the next live sample latches whatever is down.</summary>
	public void Away() => _away = true;

	/// <summary>
	/// One live sample: the buttons down on the device, less those that have been down
	/// since before the game was listening. A latched button is released by letting it
	/// go, and pressing it again is a press.
	/// </summary>
	public InputButtons Filter(InputButtons down)
	{
		if (_away)
		{
			_held = down;
			_away = false;
		}

		_held &= down;
		return down & ~_held;
	}
}
