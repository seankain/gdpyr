using System;

namespace Gdpyr.Sim.Agent;

/// <summary>
/// The optional feature planes: an <c>N × N × C</c> grid over the map, off by
/// default and requested per session (docs/AGENT_API.md §6.3).
///
/// This is SC2's feature-layer idea, and it is what makes a convolutional
/// strategist policy possible at all: a flat 1,092-float vector has no spatial
/// structure for a convolution to exploit, and a policy that has to learn "these
/// two units are next to each other" from two pairs of coordinates is learning
/// the map's metric before it learns the game.
///
/// It costs a scatter over live units per observation, which is why it is opt-in
/// rather than always paid for. Channel-last — <c>[row][column][channel]</c> —
/// because that is the layout every convolution library wants and reshaping 4,096
/// floats per decision is a cost with nothing to show for it.
///
/// Engine-free: the world-to-cell mapping and the scatter are pure, and the one
/// plane that needs the engine (<see cref="Passability"/>) is filled by the
/// caller from a map probe it takes once.
/// </summary>
public static class AgentFeaturePlanes
{
	/// <summary>Cells on a side.</summary>
	public const int Size = 32;

	/// <summary>Channels per cell.</summary>
	public const int Channels = 4;

	/// <summary>Own-unit density: how many of the seat's units are standing in this cell.</summary>
	public const int OwnUnits = 0;

	/// <summary>Contact density, at the same fog the vector's contacts are behind.</summary>
	public const int Contacts = 1;

	/// <summary>Node ownership: 0 neutral, 0.5 ground force, 1 strategist.</summary>
	public const int NodeOwnership = 2;

	/// <summary>Whether a cell can be walked on. Static per map, so the caller probes it once.</summary>
	public const int Passability = 3;

	/// <summary>Floats in one grid.</summary>
	public const int Floats = Size * Size * Channels;

	public const int Bytes = Floats * sizeof(float);

	/// <summary>
	/// Half-extent of the square the grid covers [m], which at
	/// <see cref="Size"/> 32 makes a cell 8 m on a side.
	///
	/// Deliberately *not* the 256 m the vectors normalize positions against. A float
	/// has precision to spare, so the vector can afford a scale with headroom past
	/// the map; a cell does not, and a grid stretched to ±256 m would spend more
	/// than half its resolution on ground the greybox map's 240 m floor does not
	/// reach. This is exactly half that scale, so converting between the two is a
	/// doubling rather than arithmetic. A body outside it is off the grid rather
	/// than clamped to its edge (<see cref="TryCell"/>).
	/// </summary>
	public const float HalfExtentMeters = AgentObservation.PositionScaleMeters * 0.5f;

	/// <summary>Cells a unit's density is divided by before it saturates.</summary>
	public const float DensityScale = 4f;

	/// <summary>The channel names, in order, as <c>welcome</c> publishes them.</summary>
	public static readonly string[] ChannelNames = { "own_units", "contacts", "node_ownership", "passability" };

	/// <summary>
	/// The cell a world position falls in. False when it is off the grid, which a
	/// scatter treats as "not there" rather than clamping — a unit that has fallen
	/// off the map is not standing on the edge of it.
	/// </summary>
	public static bool TryCell(float x, float z, out int row, out int column)
	{
		float u = (x + HalfExtentMeters) / (HalfExtentMeters * 2f);
		float v = (z + HalfExtentMeters) / (HalfExtentMeters * 2f);

		column = (int)(u * Size);
		row = (int)(v * Size);

		return column >= 0 && column < Size && row >= 0 && row < Size;
	}

	/// <summary>The index of one channel of one cell in a packed grid.</summary>
	public static int Index(int row, int column, int channel) =>
		(((row * Size) + column) * Channels) + channel;

	/// <summary>Clears every channel but <see cref="Passability"/>, which is the map's and does not move.</summary>
	public static void ClearDynamic(Span<float> grid)
	{
		if (grid.Length < Floats)
		{
			return;
		}

		for (int cell = 0; cell < Size * Size; cell++)
		{
			int at = cell * Channels;
			grid[at + OwnUnits] = 0f;
			grid[at + Contacts] = 0f;
			grid[at + NodeOwnership] = 0f;
		}
	}

	/// <summary>Adds one body to a density channel, saturating at 1.</summary>
	public static void Scatter(Span<float> grid, int channel, float x, float z, float weight = 1f)
	{
		if (grid.Length < Floats || !TryCell(x, z, out int row, out int column))
		{
			return;
		}

		int at = Index(row, column, channel);
		grid[at] = Math.Clamp(grid[at] + (weight / DensityScale), 0f, 1f);
	}

	/// <summary>Writes a value into one cell of one channel, replacing whatever was there.</summary>
	public static void Set(Span<float> grid, int channel, float x, float z, float value)
	{
		if (grid.Length < Floats || !TryCell(x, z, out int row, out int column))
		{
			return;
		}

		grid[Index(row, column, channel)] = Math.Clamp(value, 0f, 1f);
	}

	/// <summary>
	/// A node's ownership as the channel carries it: neutral in the middle of
	/// nothing, the two sides at either end. One channel rather than three because
	/// ownership is a single ordered question for a convolution — whose ground is
	/// this — and three sparse planes would cost three quarters of the grid to say
	/// it.
	/// </summary>
	public static float OwnershipValue(byte holder) => holder switch
	{
		(byte)NodeHolder.GroundForce => 0.5f,
		(byte)NodeHolder.Strategist => 1f,
		_ => 0f,
	};
}
