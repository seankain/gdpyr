using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Gdpyr.Sim;
using Gdpyr.Ui;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// The strategist's command card (<see cref="StrategistCards"/>): which card a selection
/// gets, that its buttons print the keys the input map really has, and that every key
/// the strategist had before the card took the key list's place is still bound to the
/// same action. The engine is the only thing that reads the input map, so the last two
/// read its text, as <see cref="VehicleControlsTests"/> does.
/// </summary>
public class StrategistCardTests
{
	// ---- which card --------------------------------------------------------------

	[Fact]
	public void NothingSelected_HasNoCard()
	{
		Assert.Equal(StrategistCardMode.None, StrategistCards.Choose(false, false, selected: 0, builders: 0, vehicles: 0));
	}

	[Fact]
	public void OnlyBuilders_GetTheBuilderCard()
	{
		Assert.Equal(StrategistCardMode.Builders, StrategistCards.Choose(false, false, selected: 1, builders: 1, vehicles: 0));
		Assert.Equal(StrategistCardMode.Builders, StrategistCards.Choose(false, false, selected: 4, builders: 4, vehicles: 0));
	}

	[Fact]
	public void BuildersWithAnythingElse_GetTheCardForTheRest()
	{
		Assert.Equal(StrategistCardMode.Units, StrategistCards.Choose(false, false, selected: 3, builders: 2, vehicles: 0));
		Assert.Equal(StrategistCardMode.Vehicles, StrategistCards.Choose(false, false, selected: 3, builders: 2, vehicles: 1));
	}

	[Fact]
	public void UnitsAndVehicles_GetTheirCards()
	{
		Assert.Equal(StrategistCardMode.Units, StrategistCards.Choose(false, false, selected: 5, builders: 0, vehicles: 0));
		Assert.Equal(StrategistCardMode.Vehicles, StrategistCards.Choose(false, false, selected: 1, builders: 0, vehicles: 1));
		Assert.Equal(StrategistCardMode.Vehicles, StrategistCards.Choose(false, false, selected: 6, builders: 0, vehicles: 1));
	}

	[Fact]
	public void AClickedBuilding_WinsOverEverythingElse()
	{
		Assert.Equal(StrategistCardMode.Barracks, StrategistCards.Choose(false, true, selected: 0, builders: 0, vehicles: 0));
		Assert.Equal(StrategistCardMode.Structure, StrategistCards.Choose(true, false, selected: 0, builders: 0, vehicles: 0));
		Assert.Equal(StrategistCardMode.Structure, StrategistCards.Choose(true, true, selected: 2, builders: 2, vehicles: 0));
		Assert.Equal(StrategistCardMode.Barracks, StrategistCards.Choose(false, true, selected: 2, builders: 2, vehicles: 0));
	}

	[Theory]
	[InlineData(StrategistCardMode.None, false)]
	[InlineData(StrategistCardMode.Barracks, false)]
	[InlineData(StrategistCardMode.Structure, false)]
	[InlineData(StrategistCardMode.Builders, true)]
	[InlineData(StrategistCardMode.Vehicles, true)]
	[InlineData(StrategistCardMode.Units, true)]
	public void OnlyCardsForUnits_HaveTheOrderRow(StrategistCardMode mode, bool orders)
	{
		Assert.Equal(orders, StrategistCards.HasOrders(mode));
	}

	// ---- what the buttons do -------------------------------------------------------

	[Fact]
	public void AnOrderButton_Arms_AndPressedAgain_PutsItAway()
	{
		Assert.Equal(OrderKind.Attack, StrategistCards.ArmFromCard(OrderKind.Move, OrderKind.Attack));
		Assert.Equal(OrderKind.Move, StrategistCards.ArmFromCard(OrderKind.Attack, OrderKind.Attack));
		Assert.Equal(OrderKind.Patrol, StrategistCards.ArmFromCard(OrderKind.Attack, OrderKind.Patrol));
	}

	[Fact]
	public void TheFooter_SaysWhatTheNextRightClickDoes()
	{
		Assert.Equal("right-click: move · on a site: build or mend it",
			StrategistCards.RightClick(StrategistCardMode.Builders, OrderKind.Move, null));
		Assert.Equal("right-click: attack-move", StrategistCards.RightClick(StrategistCardMode.Builders, OrderKind.Attack, null));
		Assert.Equal("right-click: place the pillbox · turn the view to turn it",
			StrategistCards.RightClick(StrategistCardMode.Builders, OrderKind.Move, "pillbox"));
		Assert.Equal("right-click: place the sandbag wall · turn the view to turn it",
			StrategistCards.RightClick(StrategistCardMode.Units, OrderKind.Move, "sandbag wall"));
		Assert.Equal("right-click: drive there", StrategistCards.RightClick(StrategistCardMode.Vehicles, OrderKind.Move, null));
		Assert.Equal("right-click: patrol", StrategistCards.RightClick(StrategistCardMode.Units, OrderKind.Patrol, null));
		Assert.Equal("right-click the ground: rally point",
			StrategistCards.RightClick(StrategistCardMode.Barracks, OrderKind.Move, null));
		Assert.Equal("right-click the ground: rally point",
			StrategistCards.RightClick(StrategistCardMode.Structure, OrderKind.Move, "pillbox"));
		Assert.Equal(string.Empty, StrategistCards.RightClick(StrategistCardMode.None, OrderKind.Move, null));
	}

	[Fact]
	public void TheBuilderCard_OffersEveryStructure_Once()
	{
		Assert.Equal(StructureKinds.Count, StrategistCards.Structures.Length);
		Assert.Equal(StrategistCards.Structures.Length, StrategistCards.StructureKeys.Length);
		for (byte kind = 0; kind < StructureKinds.Count; kind++)
		{
			Assert.Single(StrategistCards.Structures, s => s == kind);
		}
	}

	// ---- the keys ----------------------------------------------------------------

	[Fact]
	public void TheCardsKeys_AreTheInputMapsKeys()
	{
		Dictionary<string, int> keys = Keys();

		string[] orders = { "rts_order_attack", "rts_order_patrol", "rts_order_defend", "rts_order_stop" };
		Assert.Equal(orders.Length, StrategistCards.Orders.Length);
		Assert.Equal(orders.Length, StrategistCards.OrderKeys.Length);
		for (int i = 0; i < orders.Length; i++)
		{
			Assert.Equal(StrategistCards.OrderKeys[i][0], (char)keys[orders[i]]);
		}

		var places = new Dictionary<byte, string>
		{
			[StructureKinds.Pillbox] = "rts_place_pillbox",
			[StructureKinds.SandbagWall] = "rts_place_sandbags",
			[StructureKinds.SniperTower] = "rts_place_tower",
		};
		for (int i = 0; i < StrategistCards.Structures.Length; i++)
		{
			Assert.Equal(StrategistCards.StructureKeys[i][0], (char)keys[places[StrategistCards.Structures[i]]]);
		}
	}

	/// <summary>
	/// Every strategist key as it was when the key list was always on screen. The card
	/// was to take the list's place without taking a key away or moving one.
	/// </summary>
	[Fact]
	public void EveryStrategistKey_IsWhereItWas()
	{
		var before = new Dictionary<string, int>
		{
			["rts_rotate_left"] = 'Q',
			["rts_rotate_right"] = 'E',
			["rts_order_attack"] = 'X',
			["rts_order_patrol"] = 'C',
			["rts_order_defend"] = 'B',
			["rts_order_stop"] = 'Z',
			["rts_select_all"] = 'F',
			["rts_build_infantry"] = '1',
			["rts_build_technical"] = '2',
			["rts_build_tank"] = '3',
			["rts_cancel_build"] = 4194308, // Backspace
			["rts_build_builder"] = '4',
			["rts_place_pillbox"] = '5',
			["rts_place_sandbags"] = '6',
			["rts_place_tower"] = '7',
			["rts_build_supply"] = '8',
			["rts_build_sniper"] = '9',
			["rts_build_gunner"] = '0',
			["rts_unload"] = 'U',
			["rts_toggle_tactics"] = 'H',
			["rts_build_dropship"] = 'T',
		};

		Dictionary<string, int> keys = Keys();
		foreach ((string action, int key) in before)
		{
			Assert.True(keys.TryGetValue(action, out int now), $"{action} is gone from the input map");
			Assert.Equal(key, now);
		}

		// And the controller still reads every one of them, the mouse's included.
		string controller = File.ReadAllText(Repo("Scripts/Rts/StrategistController.cs"));
		foreach (System.Text.RegularExpressions.Match action in Regex.Matches(File.ReadAllText(Repo("project.godot")), @"^(rts_\w+)=\{", RegexOptions.Multiline))
		{
			Assert.Contains($"\"{action.Groups[1].Value}\"", controller);
		}
	}

	[Fact]
	public void TheKeyListKeyIsK_AndNothingElseIsOnIt()
	{
		Dictionary<string, int> keys = Keys();
		Assert.Equal('K', keys["rts_toggle_keys"]);
		Assert.Single(Regex.Matches(File.ReadAllText(Repo("project.godot")), @"""physical_keycode"":75\b"));
		Assert.Contains("K hide this list", StrategistCards.KeyList);
	}

	[Fact]
	public void TheKeyList_NamesEveryCardKey()
	{
		foreach (string key in StrategistCards.OrderKeys)
		{
			Assert.Contains($"{key} ", StrategistCards.KeyList);
		}

		foreach (string key in StrategistCards.StructureKeys)
		{
			Assert.Contains($"{key} ", StrategistCards.KeyList);
		}
	}

	/// <summary>Each <c>rts_*</c> action in the input map that is on a key, and its physical keycode.</summary>
	private static Dictionary<string, int> Keys()
	{
		var keys = new Dictionary<string, int>();
		string project = File.ReadAllText(Repo("project.godot"));
		foreach (System.Text.RegularExpressions.Match action in Regex.Matches(project, @"^(rts_\w+)=\{[^}]*?""physical_keycode"":(\d+)", RegexOptions.Multiline))
		{
			keys[action.Groups[1].Value] = int.Parse(action.Groups[2].Value);
		}

		return keys;
	}

	private static string Repo(string relative)
	{
		for (var at = new DirectoryInfo(AppContext.BaseDirectory); at != null; at = at.Parent)
		{
			string candidate = Path.Combine(at.FullName, relative);
			if (File.Exists(candidate) && File.Exists(Path.Combine(at.FullName, "project.godot")))
			{
				return candidate;
			}
		}

		throw new InvalidOperationException($"no {relative} above {AppContext.BaseDirectory}");
	}
}
