using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// The strategist's vehicle key as the input map and the controller write it
/// (docs/VEHICLES.md §7): <c>U</c> unloads, nothing else is on it, and <c>T</c> is still
/// free for the dropship. The engine is the only thing that reads both files, so this
/// reads their text.
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
	public void TIsStillFree_ForTheDropship()
	{
		string project = File.ReadAllText(Repo("project.godot"));

		Assert.Empty(Regex.Matches(project, @"""physical_keycode"":84\b"));
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
