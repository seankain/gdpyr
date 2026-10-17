using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// The strategist's vehicle keys as the input map and the controller write them
/// (docs/VEHICLES.md §7): <c>U</c> unloads, <c>T</c> queues a dropship, and nothing else
/// is on either. The engine is the only thing that reads both files, so this reads their
/// text.
/// </summary>
public class VehicleControlsTests
{
	[Fact]
	public void TheUnloadKeyIsU_AndNothingElseIsOnIt()
	{
		string project = File.ReadAllText(Repo("project.godot"));
		System.Text.RegularExpressions.Match action = Regex.Match(project, @"rts_unload=\{[^}]*""physical_keycode"":(\d+)");

		Assert.True(action.Success);
		Assert.Equal(85, int.Parse(action.Groups[1].Value)); // U
		Assert.Single(Regex.Matches(project, @"""physical_keycode"":85\b"));
		Assert.Contains("\"rts_unload\"", File.ReadAllText(Repo("Scripts/Rts/StrategistController.cs")));
	}

	[Fact]
	public void TheDropshipKeyIsT_AndNothingElseIsOnIt()
	{
		string project = File.ReadAllText(Repo("project.godot"));
		System.Text.RegularExpressions.Match action = Regex.Match(project,
			@"rts_build_dropship=\{[^}]*""physical_keycode"":(\d+)");

		Assert.True(action.Success);
		Assert.Equal(84, int.Parse(action.Groups[1].Value)); // T
		Assert.Single(Regex.Matches(project, @"""physical_keycode"":84\b"));

		string controller = File.ReadAllText(Repo("Scripts/Rts/StrategistController.cs"));
		Assert.Matches(@"""rts_build_dropship""\)\) \{ QueueUnit\(UnitCatalog\.Dropship\); \}", controller);
	}

	[Fact]
	public void TheCardOffersTheDropship_OnT()
	{
		// Every unit on the card has its key printed beside it, in the same order.
		string hud = File.ReadAllText(Repo("Scripts/Ui/StrategistHud.cs"));
		Assert.Contains("UnitCatalog.HeavyGunner, UnitCatalog.Dropship,", hud);
		Assert.Contains("{ \"1\", \"2\", \"3\", \"4\", \"8\", \"9\", \"0\", \"T\" }", hud);
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
