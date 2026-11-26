using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Gdpyr.Sim;
using Gdpyr.Sim.Agent;
using Xunit;
using static Gdpyr.Tests.MapSceneTests;

namespace Gdpyr.Tests;

/// <summary>
/// The ladders as the scenes place them, and the climbing state's place in the movement FSM
/// (docs/LADDERS.md), read from the .tscn text with no Godot.
/// </summary>
public class LadderSceneTests
{
	private const string LadderScript = "res://Scripts/Fps/Ladder.cs";

	[Fact]
	public void TheGreybox_HasTheScaffoldBuildingsFourLadders_Level_Unscaled_AndOnTheField()
	{
		MapScene scene = MapScene.Load("res://Scenes/Test.tscn");
		var ladders = scene.WithScript(LadderScript).ToList();

		Assert.Equal(
			new[] { "LadderEndWall", "LadderHatch", "LadderScaffold", "LadderToRoof" },
			ladders.Select(n => n.Path[(n.Path.LastIndexOf('/') + 1)..]).OrderBy(n => n, StringComparer.Ordinal));
		Assert.All(ladders, n => Assert.StartsWith("ScaffoldBuilding/", n.Path));

		foreach (SceneNode ladder in ladders)
		{
			// Turned about the vertical only: the normal its +Z gives is level, as LadderSpan has it.
			Vec3 scale = scene.GlobalScale(ladder.Path);
			Assert.True(MathF.Abs(scale.X - 1f) < 1e-4f && MathF.Abs(scale.Y - 1f) < 1e-4f && MathF.Abs(scale.Z - 1f) < 1e-4f,
				$"{ladder.Path} is scaled");
			if (ladder.Transform != null)
			{
				Assert.Equal(new[] { 0f, 1f, 0f }, ladder.Transform[3..6]);
				Assert.Equal(0f, ladder.Transform[1]);
				Assert.Equal(0f, ladder.Transform[7]);
			}

			Vec3 at = scene.GlobalOrigin(ladder.Path);
			Assert.True(MathF.Abs(at.X) < Cargo.FieldHalfExtentMeters && MathF.Abs(at.Z) < Cargo.FieldHalfExtentMeters,
				$"{ladder.Path} is off the field at ({at.X}, {at.Z})");
		}
	}

	[Fact]
	public void TheBuilding_IsNavigationSource_SoItsFloorsAreBakedAndItsWallsGoneRound()
	{
		MapScene scene = MapScene.Load("res://Scenes/Environment/ScaffoldBuilding.tscn");
		Assert.Contains(scene.InGroup("navmesh"), n => n.Path == "Building");
		Assert.Contains(scene.InGroup("navmesh"), n => n.Path == "Scaffold");
	}

	[Fact]
	public void Climbing_IsTheLastMovementState_SoTheOthersKeepTheirIndices()
	{
		// The index of a movement state is its place among the FSM's children (StateMachine),
		// and it is what snapshots, demos and the agent observation carry. The seven before
		// climbing are where they were; climbing is the eighth.
		string text = File.ReadAllText(Repo("Scenes/fps_controller.tscn"));
		string[] states = Regex.Matches(text, @"^\[node name=""(\w+)"" type=""Node"" parent=""PlayerStateMachine""",
				RegexOptions.Multiline)
			.Select(m => m.Groups[1].Value)
			.ToArray();

		Assert.Equal(new[]
		{
			"PlayerWalkingState", "PlayerIdleState", "PlayerSprintingState", "PlayerCrouchingState",
			"PlayerSlidingState", "PlayerJumpingState", "PlayerFallingState", "PlayerClimbingState",
		}, states);

		// gdpyr-agent-obs-4's one-hot is the seven before it; a climbing body's is all zeros
		// (docs/LADDERS.md §6). Widening it is a schema change of its own.
		Assert.Equal(states.Length - 1, AgentObservation.MovementStates);
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

		throw new FileNotFoundException(relative);
	}
}
