using System.Collections.Generic;
using Gdpyr.AgentClient;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// The discretization a strategist policy learns
/// (<c>tools/Gdpyr.AgentClient/StrategistActionSpace.cs</c>).
///
/// This file is the one place a research decision was made — which slice of the
/// command space a policy may reach — and it is worth testing for the same reason
/// the reward is: nothing else in the repository would notice if it quietly
/// stopped meaning what it says. The schema below is deliberately *not* the
/// shipped one. The action space reads block offsets, widths and field names out
/// of what <c>welcome</c> published (docs/AGENT_API.md §6.2), so a test that built
/// the real 1,092-float vector would be testing the encoder instead.
/// </summary>
public class StrategistActionSpaceTests
{
	private const int RoundFloats = 8;
	private const int MaxUnits = 4;
	private const int UnitFloats = 14;
	private const int MaxBarracks = 2;
	private const int BarracksFloats = 7;
	private const int MaxNodes = 2;
	private const int NodeFloats = 8;
	private const int MaxContacts = 2;
	private const int ContactFloats = 6;
	private const float Scale = 256f;

	private static readonly string[] UnitFields =
	{
		"alive", "tier_infantry", "tier_technical", "tier_tank", "x", "z", "health",
		"order_none", "order_move", "order_attack", "order_patrol", "order_defend",
		"has_target", "target_distance",
	};

	private static readonly string[] BarracksFields =
	{
		"x", "z", "queue_depth", "head_tier", "head_progress", "rally_x", "rally_z",
	};

	private static readonly string[] NodeFields =
	{
		"x", "z", "owner_neutral", "owner_ground", "owner_strategist", "contested",
		"capture_progress", "income_paid",
	};

	private static readonly string[] ContactFields =
	{
		"x", "z", "visible", "ticks_since_seen", "is_player", "is_strategist",
	};

	private static GdpyrSchema Schema()
	{
		int units = RoundFloats;
		int barracks = units + (MaxUnits * UnitFloats);
		int nodes = barracks + (MaxBarracks * BarracksFloats);
		int contacts = nodes + (MaxNodes * NodeFloats);

		var schema = new GdpyrSchema
		{
			StrategistFloats = contacts + (MaxContacts * ContactFloats),
			PositionScaleMeters = Scale,
		};

		schema.AddStrategist("units", units, MaxUnits * UnitFloats);
		schema.AddStrategist("barracks", barracks, MaxBarracks * BarracksFloats);
		schema.AddStrategist("nodes", nodes, MaxNodes * NodeFloats);
		schema.AddStrategist("contacts", contacts, MaxContacts * ContactFloats);

		schema.AddBlock("units", new GdpyrBlock("units", units, MaxUnits, UnitFloats, UnitFields));
		schema.AddBlock("barracks",
			new GdpyrBlock("barracks", barracks, MaxBarracks, BarracksFloats, BarracksFields));
		schema.AddBlock("nodes", new GdpyrBlock("nodes", nodes, MaxNodes, NodeFloats, NodeFields));
		schema.AddBlock("contacts",
			new GdpyrBlock("contacts", contacts, MaxContacts, ContactFloats, ContactFields));

		return schema;
	}

	private static void Set(GdpyrSchema schema, float[] values, string block, int record, string field,
		float value)
	{
		GdpyrBlock at = schema.Block(block);
		values[at.Offset + (record * at.FloatsEach) + at.IndexOf(field)] = value;
	}

	/// <summary>
	/// A map with two nodes 200 m apart, one owned barracks, and nothing else. The
	/// grid the action space fits is therefore [-130, 130] on both axes: the
	/// bounding box of the nodes and the barracks, padded by 15%.
	/// </summary>
	private static float[] Map(GdpyrSchema schema)
	{
		var values = new float[schema.StrategistFloats];

		Set(schema, values, "nodes", 0, "x", -100f / Scale);
		Set(schema, values, "nodes", 0, "z", -100f / Scale);
		Set(schema, values, "nodes", 0, "owner_strategist", 1f);

		Set(schema, values, "nodes", 1, "x", 100f / Scale);
		Set(schema, values, "nodes", 1, "z", 100f / Scale);
		Set(schema, values, "nodes", 1, "owner_neutral", 1f);

		// Slot 0 is a barracks this seat does not own and is left zeroed; slot 1 is
		// its own (docs/AGENT_API.md §6.2).
		Set(schema, values, "barracks", 1, "x", 0f);
		Set(schema, values, "barracks", 1, "z", -50f / Scale);
		Set(schema, values, "barracks", 1, "queue_depth", 0.25f);

		return values;
	}

	private static List<GdpyrUnit> Army() => new()
	{
		new GdpyrUnit { Id = 11, Alive = true, Tier = 0, X = -90f, Z = -90f, Health = 1f, Order = 0 },
		new GdpyrUnit { Id = 12, Alive = true, Tier = 2, X = 0f, Z = 0f, Health = 0.2f, Order = 1 },
		new GdpyrUnit { Id = 13, Alive = true, Tier = 1, X = 90f, Z = 90f, Health = 1f, Order = 2 },
		new GdpyrUnit { Id = 14, Alive = false, Tier = 0, X = 0f, Z = -50f, Health = 0f, Order = 0 },
	};

	/// <summary>The cell centre the grid puts column/row at, given the map above.</summary>
	private static float Cell(int index) => -130f + ((index + 0.5f) * (260f / 8f));

	private static List<GdpyrCommand> Decode(params int[] heads)
	{
		GdpyrSchema schema = Schema();
		var into = new List<GdpyrCommand>();
		new StrategistActionSpace(schema).Decode(heads, Map(schema), Army(), into);
		return into;
	}

	[Fact]
	public void EveryHeadIsTheSameWidthBecauseRLMatrixRefusesARaggedSpace()
	{
		Assert.Equal(StrategistActionSpace.HeadNames.Length, StrategistActionSpace.Heads.Length);
		foreach (int head in StrategistActionSpace.Heads)
		{
			Assert.Equal(StrategistActionSpace.HeadWidth, head);
		}

		Assert.Equal(StrategistActionSpace.HeadWidth, StrategistActionSpace.ProductionNames.Length);
		Assert.Equal(StrategistActionSpace.HeadWidth, StrategistActionSpace.GroupNames.Length);
		Assert.Equal(StrategistActionSpace.HeadWidth, StrategistActionSpace.OrderNames.Length);
	}

	[Fact]
	public void TheNullActionIsAnEmptyList()
	{
		// A strategist that submits nothing has decided to do nothing, which is not
		// the same as a strategist that has stopped deciding (docs/AGENT_API.md §7.4).
		Assert.Empty(Decode(0, 0, 0, 0, 3, 3));
	}

	[Fact]
	public void ADecisionIsAtMostOneProductionAndOneOrder()
	{
		List<GdpyrCommand> commands = Decode(1, 0, 1, 1, 4, 4);

		Assert.Equal(StrategistActionSpace.MaxCommandsPerDecision, commands.Count);
		Assert.Equal("build", commands[0].Cmd);
		Assert.Equal("order", commands[1].Cmd);
	}

	[Fact]
	public void BuildNamesTheTierAndAnOwnedBarracks()
	{
		// The only owned door is slot 1, so every value of the barracks head lands on
		// it rather than on a command the server would refuse and count.
		for (int door = 0; door < StrategistActionSpace.HeadWidth; door++)
		{
			List<GdpyrCommand> commands = Decode(3, door, 0, 0, 0, 0);

			GdpyrCommand build = Assert.Single(commands);
			Assert.Equal("build", build.Cmd);
			Assert.Equal(2, build.Tier);
			Assert.Equal(1, build.Barracks);
		}
	}

	[Fact]
	public void CancelAndRallyAddressTheSameDoor()
	{
		GdpyrCommand cancel = Assert.Single(Decode(4, 0, 0, 0, 0, 0));
		Assert.Equal("cancel", cancel.Cmd);
		Assert.Equal(1, cancel.Barracks);

		GdpyrCommand rally = Assert.Single(Decode(5, 0, 0, 0, 7, 7));
		Assert.Equal("rally", rally.Cmd);
		Assert.Equal(1, rally.Barracks);
		Assert.Equal(Cell(7), rally.Target[0], 2);
		Assert.Equal(Cell(7), rally.Target[1], 2);
	}

	[Fact]
	public void RallyToANodePrefersOneThisSideHolds()
	{
		GdpyrCommand rally = Assert.Single(Decode(6, 0, 0, 0, 0, 0));

		Assert.Equal("rally", rally.Cmd);
		Assert.Equal(-100f, rally.Target[0], 2);
		Assert.Equal(-100f, rally.Target[1], 2);
	}

	[Fact]
	public void RallyHomeIsTheDoorItself()
	{
		GdpyrCommand rally = Assert.Single(Decode(7, 0, 0, 0, 0, 0));

		Assert.Equal(0f, rally.Target[0], 2);
		Assert.Equal(-50f, rally.Target[1], 2);
	}

	[Fact]
	public void TheGridIsFittedToTheMapRatherThanToTheEncodersHalfExtent()
	{
		// The normalized square is 512 m across; this map is 200. A grid over the
		// former would spend most of its cells off the navmesh.
		GdpyrCommand first = Assert.Single(Decode(0, 0, 1, 1, 0, 0));
		GdpyrCommand last = Assert.Single(Decode(0, 0, 1, 1, 7, 7));

		Assert.Equal(Cell(0), first.Target[0], 2);
		Assert.Equal(Cell(7), last.Target[0], 2);
		Assert.True(last.Target[0] - first.Target[0] < Scale);
	}

	[Fact]
	public void AnOrderNamesLiveUnitsAndNeverACorpse()
	{
		GdpyrCommand order = Assert.Single(Decode(0, 0, 1, 1, 4, 4));

		Assert.Equal("move", order.Kind);
		Assert.Equal(new[] { 11, 12, 13 }, order.Units);
	}

	[Fact]
	public void TheGroupHeadPicksUnitsByPredicate()
	{
		Assert.Equal(new[] { 11 }, Assert.Single(Decode(0, 0, 2, 1, 4, 4)).Units);
		Assert.Equal(new[] { 12 }, Assert.Single(Decode(0, 0, 5, 1, 4, 4)).Units);
		Assert.Equal(new[] { 12, 13 }, Assert.Single(Decode(0, 0, 6, 1, 4, 4)).Units);
		Assert.Equal(new[] { 11 }, Assert.Single(Decode(0, 0, 7, 1, 4, 4)).Units);
	}

	[Fact]
	public void NearAndFarSplitTheArmyAroundTheCell()
	{
		// Column and row 0 is the bottom-left cell, which unit 11 is standing in.
		Assert.Equal(new[] { 11 }, Assert.Single(Decode(0, 0, 3, 1, 0, 0)).Units);
		Assert.Equal(new[] { 13 }, Assert.Single(Decode(0, 0, 4, 1, 0, 0)).Units);
	}

	[Fact]
	public void AnEmptySelectionIsNoCommandRatherThanAnEmptyOrder()
	{
		GdpyrSchema schema = Schema();
		var into = new List<GdpyrCommand>();

		// Nobody is damaged, so the damaged group names nobody.
		var healthy = new List<GdpyrUnit>
		{
			new() { Id = 21, Alive = true, Tier = 0, X = 0f, Z = 0f, Health = 1f, Order = 0 },
		};

		new StrategistActionSpace(schema).Decode(new[] { 0, 0, 5, 1, 4, 4 }, Map(schema), healthy, into);

		Assert.Empty(into);
	}

	[Fact]
	public void AttackTheNearestContactPrefersALiveOneOverTheCell()
	{
		GdpyrSchema schema = Schema();
		float[] values = Map(schema);

		Set(schema, values, "contacts", 0, "x", 60f / Scale);
		Set(schema, values, "contacts", 0, "z", 60f / Scale);
		Set(schema, values, "contacts", 0, "visible", 1f);
		Set(schema, values, "contacts", 0, "is_player", 1f);

		// A ghost: seen once, not visible now. Never a target for this head.
		Set(schema, values, "contacts", 1, "x", -20f / Scale);
		Set(schema, values, "contacts", 1, "z", -20f / Scale);
		Set(schema, values, "contacts", 1, "ticks_since_seen", 0.5f);
		Set(schema, values, "contacts", 1, "is_player", 1f);

		var into = new List<GdpyrCommand>();
		new StrategistActionSpace(schema).Decode(new[] { 0, 0, 1, 6, 0, 0 }, values, Army(), into);

		GdpyrCommand order = Assert.Single(into);
		Assert.Equal("attack", order.Kind);
		Assert.Equal(60f, order.Target[0], 2);
		Assert.Equal(60f, order.Target[1], 2);
	}

	[Fact]
	public void DefendFallsBackToANodeWhenTheHeadAsksForOne()
	{
		GdpyrCommand order = Assert.Single(Decode(0, 0, 1, 7, 7, 7));

		Assert.Equal("defend", order.Kind);

		// The army's centroid is the origin and the only node this side holds is at
		// (-100, -100), so the order goes there rather than to cell 7.
		Assert.Equal(-100f, order.Target[0], 2);
		Assert.Equal(-100f, order.Target[1], 2);
	}

	[Fact]
	public void StopIsAnOrderLikeAnyOther()
	{
		GdpyrCommand order = Assert.Single(Decode(0, 0, 1, 5, 4, 4));

		Assert.Equal("stop", order.Kind);
		Assert.Equal(new[] { 11, 12, 13 }, order.Units);
	}

	[Fact]
	public void AHeadOutOfRangeIsClampedRatherThanThrowing()
	{
		Assert.Empty(Decode(-3, -1, 0, 0, 99, 99));
		Assert.Equal(2, Decode(99, 99, 99, 99, 99, 99).Count);
	}

	[Fact]
	public void AShortHeadVectorDecodesToNothing()
	{
		GdpyrSchema schema = Schema();
		var into = new List<GdpyrCommand> { GdpyrCommand.Cancel(0) };

		new StrategistActionSpace(schema).Decode(new[] { 1, 1 }, Map(schema), Army(), into);

		Assert.Empty(into);
	}
}
