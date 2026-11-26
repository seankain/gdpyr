using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Gdpyr.Bots;
using Gdpyr.Core;
using Gdpyr.Fps;
using Gdpyr.Match;
using Gdpyr.Net;
using Gdpyr.Rts;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.EngineChecks;

/// <summary>
/// Ladders and the scaffold building in the game (docs/LADDERS.md §7), run by
/// <c>scripts/ladder-checks.sh</c> on a copy of the tree with this file added as an autoload;
/// not part of the game.
///
/// listen: a listen host on <c>Test.tscn</c> at <c>--fixed-fps 60</c>, its own player through
/// the input map. The building's four ladders are where the scene puts them; each is walked
/// into, climbed, gone over onto its landing, taken again from the landing and come down to
/// its foot; a jump pushes off one, and with the walk still into it catches it again; walking
/// past one does not take it, nor does strafing on it move the body off its middle. The
/// navigation mesh joins every level for a ground bot's agent and none for a unit's; and the
/// player, steered by the ground bot's own ladder steering along its agent's path, goes from
/// the ground up to the roof, down to the first floor and out onto the ground. With
/// <c>LADDER_SHOTS</c> and a display: screenshots.
///
/// server, client: a dedicated server puts a client's player at the foot of the wall ladder;
/// the client climbs it up and over and comes back down, predicted, and the corrections the
/// server's records make to it are counted.
///
/// One line per fact, <c>PASS</c> or <c>FAIL</c>, and <c>[checks] measured …</c> lines; the
/// process exits with the number of its facts that failed.
/// </summary>
public partial class LadderChecks : Node
{
	private const int Second = SimConfig.TickRate;

	private string _role;
	private string _dir;
	private string _shots;
	private IEnumerator<bool> _run;
	private int _failures;
	private int _facts;
	private bool _timedOut;

	public override void _Ready()
	{
		_role = System.Environment.GetEnvironmentVariable("LADDER_CHECK");
		_dir = System.Environment.GetEnvironmentVariable("LADDER_DIR");
		_shots = System.Environment.GetEnvironmentVariable("LADDER_SHOTS");
		_run = _role switch
		{
			"listen" => Listen(),
			"server" when !string.IsNullOrEmpty(_dir) => Server(),
			"client" when !string.IsNullOrEmpty(_dir) => Client(),
			_ => null,
		};

		if (_run == null)
		{
			SetPhysicsProcess(false);
			return;
		}

		ProcessMode = ProcessModeEnum.Always;
		GD.Print($"[checks] ladder check: {_role}");
	}

	public override void _PhysicsProcess(double delta)
	{
		bool more;
		try
		{
			more = _run.MoveNext();
		}
		catch (Exception e)
		{
			GD.PrintErr($"[checks] EXCEPTION {e}");
			_failures++;
			more = false;
		}

		if (!more)
		{
			ReleaseAll();
			GD.Print($"[checks] {_role}: {_facts - _failures} of {_facts} facts hold");
			Mark($"finished-{_role}");
			SetPhysicsProcess(false);
			GetTree().Quit(_failures);
		}
	}

	// ---- plumbing ----------------------------------------------------------------------

	private void Fact(bool ok, string what)
	{
		_facts++;
		if (!ok)
		{
			_failures++;
		}

		GD.Print($"[checks] {(ok ? "PASS" : "FAIL")} {_role}: {what}");
	}

	private void Measured(string what) => GD.Print($"[checks] measured {_role}: {what}");

	private static string F(float v, string format = "0.00") => v.ToString(format, CultureInfo.InvariantCulture);

	private static string V(Vector3 v) => $"({F(v.X)}, {F(v.Y)}, {F(v.Z)})";

	private void Mark(string name, string content = "")
	{
		if (!string.IsNullOrEmpty(_dir))
		{
			File.WriteAllText(Path.Combine(_dir, name), content);
		}
	}

	private bool Has(string name) => File.Exists(Path.Combine(_dir, name));

	private static IEnumerable<bool> Wait(int ticks)
	{
		for (int i = 0; i < ticks; i++)
		{
			yield return true;
		}
	}

	private IEnumerable<bool> Until(Func<bool> condition, int maxTicks)
	{
		_timedOut = false;
		int n = 0;
		while (!condition())
		{
			if (n++ >= maxTicks)
			{
				_timedOut = true;
				yield break;
			}

			yield return true;
		}
	}

	private static readonly string[] Moves = { "move_forward", "move_backward", "move_left", "move_right", "jump" };

	private static void ReleaseAll()
	{
		foreach (string action in Moves)
		{
			Input.ActionRelease(action);
		}
	}

	private static void Hold(string action)
	{
		ReleaseAll();
		Input.ActionPress(action);
	}

	private static IEnumerable<bool> Tap(string action)
	{
		Input.ActionPress(action);
		foreach (bool _ in Wait(4)) yield return true;
		Input.ActionRelease(action);
		foreach (bool _ in Wait(2)) yield return true;
	}

	private static LocalInputSampler Sampler => PlayerManager.Instance.GetNode<LocalInputSampler>("LocalInputSampler");

	/// <summary>The yaw that looks along the level <paramref name="direction"/>.</summary>
	private static float YawAlong(Vector3 direction)
	{
		Aim.Angles(new Vector3(direction.X, 0f, direction.Z), out float yaw, out _);
		return yaw;
	}

	private static void Face(Vector3 direction) => Sampler.Align(YawAlong(direction), 0f);

	private static void Put(fps_controller me, Vector3 at, Vector3 facing)
	{
		ReleaseAll();
		float yaw = YawAlong(facing);
		me.Teleport(new Transform3D(new Basis(Vector3.Up, yaw), at));
		Sampler.Align(yaw, 0f);
	}

	private static Ladder Find(string name)
	{
		for (int i = 0; i < Ladder.Count; i++)
		{
			if (Ladder.NodeAt(i).Name == name)
			{
				return Ladder.NodeAt(i);
			}
		}

		return null;
	}

	private IEnumerable<bool> Onto(Team team)
	{
		while (!Session.InGame || CombatManager.Instance?.Local?.Character == null)
		{
			yield return true;
		}

		foreach (bool _ in Wait(30)) yield return true;
		PlayerCombat local = CombatManager.Instance.Local;
		if (local.AwaitingRole || local.Team != team)
		{
			CombatManager.Instance.RequestTeam(team);
		}

		foreach (bool _ in Until(() => local.IsAlive && local.Team == team, 20 * Second)) yield return true;
	}

	/// <summary>Saves what the screen shows, when there is a display and somewhere to put it.</summary>
	private void Shot(string name)
	{
		if (string.IsNullOrEmpty(_shots) || DisplayServer.GetName() == "headless")
		{
			return;
		}

		Image image = GetViewport().GetTexture()?.GetImage();
		if (image == null || image.IsEmpty())
		{
			Measured($"no picture for {name}");
			return;
		}

		string path = Path.Combine(_shots, $"{name}.png");
		image.SavePng(path);
		Measured($"saved {path}");
	}

	// ---- listen --------------------------------------------------------------------------

	/// <summary>Where the scene puts each ladder, in the building's own frame: foot, normal, height.</summary>
	private static readonly (string Name, Vector3 Foot, Vector3 Normal, float Height, float Approach)[] Expected =
	{
		("LadderScaffold", new Vector3(-4.3f, 0f, 5.9f), Vector3.Back, 3.5f, 1.2f),
		("LadderToRoof", new Vector3(4.3f, 3.5f, 4.25f), Vector3.Back, 3.5f, 1.05f),
		("LadderEndWall", new Vector3(6f, 0f, 0f), Vector3.Right, 7f, 1.2f),
		("LadderHatch", new Vector3(-4.4f, 0f, -2.95f), Vector3.Left, 3.5f, 0.85f),
	};

	private IEnumerator<bool> Listen()
	{
		foreach (bool _ in Onto(Team.GroundForce)) yield return true;
		PlayerCombat local = CombatManager.Instance.Local;
		Fact(!_timedOut && local.IsAlive, "the listen host's player is alive on the ground force");
		fps_controller me = local.Character;

		// ---- the building and its ladders ----
		var building = GetTree().CurrentScene?.GetNodeOrNull<Node3D>("ScaffoldBuilding");
		Fact(building != null && building.SceneFilePath == "res://Scenes/Environment/ScaffoldBuilding.tscn",
			$"the greybox has the scaffold building ({building?.GlobalPosition})");
		if (building == null)
		{
			yield break;
		}

		Transform3D frame = building.GlobalTransform;
		Fact(Ladder.Count == Expected.Length, $"{Ladder.Count} ladders are registered, the building's {Expected.Length}");
		foreach (var e in Expected)
		{
			Ladder ladder = Find(e.Name);
			Vector3 foot = frame * e.Foot;
			Vector3 normal = frame.Basis * e.Normal;
			LadderSpan span = ladder?.Span ?? default;
			Fact(ladder != null && span.Foot.DistanceTo(foot) < 0.01f && span.Normal.DistanceTo(normal) < 0.01f
				&& Mathf.IsEqualApprox(span.Height, e.Height),
				$"{e.Name}: foot {V(span.Foot)}, facing {V(span.Normal)}, {F(span.Height, "0.0")} m");
		}

		while (UnitManager.Instance is not { NavigationReady: true })
		{
			yield return true;
		}

		foreach (bool _ in Shots(me, frame)) yield return true;

		// ---- each ladder: up, over, back onto it from the landing, down ----
		foreach (var e in Expected)
		{
			Ladder ladder = Find(e.Name);
			if (ladder != null)
			{
				foreach (bool _ in UpAndDown(me, ladder, e.Approach)) yield return true;
			}
		}

		// ---- a jump off, walking past, strafing ----
		Ladder wall = Find("LadderEndWall");
		if (wall != null)
		{
			foreach (bool _ in JumpOff(me, wall.Span)) yield return true;
			foreach (bool _ in Strafe(me, wall.Span)) yield return true;
			foreach (bool _ in WalkPast(me, wall.Span)) yield return true;
		}

		Ladder scaffold = Find("LadderScaffold");
		if (scaffold != null)
		{
			foreach (bool _ in WalkPast(me, scaffold.Span)) yield return true;
		}

		// ---- the navigation mesh, and a bot's way round the building ----
		Paths(me, frame);
		foreach (bool _ in BotWay(me, frame)) yield return true;
	}

	/// <summary>Walks into the ladder from <paramref name="approach"/> m out, climbs it, goes over, takes it from the landing and comes down.</summary>
	private IEnumerable<bool> UpAndDown(fps_controller me, Ladder ladder, float approach)
	{
		LadderSpan span = ladder.Span;
		string name = ladder.Name;
		Put(me, span.Foot + (span.Normal * approach) + (Vector3.Up * 0.05f), -span.Normal);
		foreach (bool _ in Wait(15)) yield return true;

		Hold("move_forward");
		foreach (bool _ in Until(() => me.IsClimbing, Second)) yield return true;
		Fact(!_timedOut, $"{name}: walked into from {F(approach, "0.0")} m out, it is taken ({me.StateName})");
		if (_timedOut)
		{
			ReleaseAll();
			yield break;
		}

		uint start = NetworkManager.Instance.Tick;
		float y0 = me.SimPosition.Y;
		foreach (bool _ in Wait(30)) yield return true;
		float speed = (me.SimPosition.Y - y0) / 0.5f;
		Fact(speed > 2.8f && speed < 3.05f, $"{name}: it climbs at {F(speed)} m/s");
		LadderPlace mid = span.Place(me.SimPosition);
		Fact(Mathf.Abs(mid.Out - Ladders.HoldOutMeters) < 0.03f && Mathf.Abs(mid.Side) < 0.03f,
			$"{name}: held {F(mid.Out)} m off its face, {F(mid.Side)} m off its middle");

		foreach (bool _ in Until(() => !me.IsClimbing, (int)(((span.Height / Ladders.ClimbSpeed) + 3f) * Second)))
			yield return true;
		float seconds = (NetworkManager.Instance.Tick - start) / (float)Second;
		bool overTop = !_timedOut;
		foreach (bool _ in Wait(20)) yield return true;
		ReleaseAll();
		foreach (bool _ in Wait(20)) yield return true;

		LadderPlace top = span.Place(me.SimPosition);
		Fact(overTop && Mathf.Abs(me.SimPosition.Y - span.TopY) < 0.1f && top.Out < -0.6f && me.IsOnFloor(),
			$"{name}: up and over onto its landing in {F(seconds, "0.0")} s, standing {F(top.Out)} m past its face at"
			+ $" {F(me.SimPosition.Y)} (landing {F(span.TopY)}), {me.StateName}");

		// Back the way it came: walking off the landing towards the ladder takes it from above.
		Face(span.Normal);
		foreach (bool _ in Wait(2)) yield return true;
		Hold("move_forward");
		foreach (bool _ in Until(() => me.IsClimbing, 2 * Second)) yield return true;
		Fact(!_timedOut, $"{name}: walking off its landing towards it takes it from above ({me.StateName})");

		foreach (bool _ in Until(() => !me.IsClimbing, (int)(((span.Height / Ladders.ClimbSpeed) + 3f) * Second)))
			yield return true;
		bool letGo = !_timedOut;
		ReleaseAll();
		foreach (bool _ in Wait(20)) yield return true;

		LadderPlace foot = span.Place(me.SimPosition);
		Fact(letGo && Mathf.Abs(me.SimPosition.Y - span.Foot.Y) < 0.1f && foot.Out > 0.4f && foot.Out < 1.5f
				&& me.IsOnFloor() && !me.IsClimbing,
			$"{name}: down to its foot and let go, {F(foot.Out)} m out at {F(me.SimPosition.Y)} (foot {F(span.Foot.Y)}),"
			+ $" {me.StateName}");
	}

	private IEnumerable<bool> JumpOff(fps_controller me, LadderSpan span)
	{
		Put(me, span.Foot + (span.Normal * 1.2f) + (Vector3.Up * 0.05f), -span.Normal);
		foreach (bool _ in Wait(10)) yield return true;
		Hold("move_forward");
		foreach (bool _ in Until(() => span.Place(me.SimPosition).Up > 3f, 4 * Second)) yield return true;
		ReleaseAll();
		foreach (bool _ in Wait(5)) yield return true;
		Fact(me.IsClimbing && !_timedOut, $"3 m up the wall ladder it hangs there with nothing held ({me.StateName})");

		foreach (bool _ in Tap("jump")) yield return true;
		bool off = !me.IsClimbing;
		foreach (bool _ in Until(() => me.IsOnFloor() && !me.IsClimbing, 3 * Second)) yield return true;
		LadderPlace landed = span.Place(me.SimPosition);
		Fact(off && !_timedOut && landed.Out > 1f && landed.Up < 0.1f,
			$"a jump pushes off it: down on the ground {F(landed.Out)} m out ({me.StateName})");

		// With the walk still into it, the jump comes down onto it again.
		Put(me, span.Foot + (span.Normal * 1.2f) + (Vector3.Up * 0.05f), -span.Normal);
		foreach (bool _ in Wait(10)) yield return true;
		Hold("move_forward");
		foreach (bool _ in Until(() => span.Place(me.SimPosition).Up > 3f, 4 * Second)) yield return true;
		Input.ActionPress("jump");
		foreach (bool _ in Until(() => !me.IsClimbing, 10)) yield return true;
		Input.ActionRelease("jump");
		float peak = 0f;
		foreach (bool _ in Until(() =>
		{
			peak = Mathf.Max(peak, span.Place(me.SimPosition).Up);
			return me.IsClimbing;
		}, 2 * Second)) yield return true;
		float caught = span.Place(me.SimPosition).Up;
		ReleaseAll();
		Fact(!_timedOut, $"jumping off with the walk still into it, the ladder is caught again on the way down"
			+ $" (up to {F(peak)} m, caught at {F(caught)} m)");
		Hold("move_backward");
		foreach (bool _ in Until(() => !me.IsClimbing, 4 * Second)) yield return true;
		ReleaseAll();
	}

	private IEnumerable<bool> Strafe(fps_controller me, LadderSpan span)
	{
		Put(me, span.Foot + (span.Normal * 1.2f) + (Vector3.Up * 0.05f), -span.Normal);
		foreach (bool _ in Wait(10)) yield return true;
		Hold("move_forward");
		foreach (bool _ in Until(() => span.Place(me.SimPosition).Up > 2f, 4 * Second)) yield return true;
		ReleaseAll();
		foreach (bool _ in Wait(5)) yield return true;

		LadderPlace before = span.Place(me.SimPosition);
		Hold("move_right");
		foreach (bool _ in Wait(30)) yield return true;
		LadderPlace after = span.Place(me.SimPosition);
		ReleaseAll();
		Fact(me.IsClimbing && Mathf.Abs(after.Side) < 0.02f && Mathf.Abs(after.Up - before.Up) < 0.02f,
			$"strafing on it for half a second moves it neither along the rungs ({F(after.Side)} m) nor up"
			+ $" ({F(after.Up - before.Up)} m)");

		Hold("move_backward");
		foreach (bool _ in Until(() => !me.IsClimbing, 3 * Second)) yield return true;
		ReleaseAll();
		Fact(!_timedOut && me.IsOnFloor(), $"back walks it down and off at the foot ({me.StateName})");
	}

	private IEnumerable<bool> WalkPast(fps_controller me, LadderSpan span)
	{
		// Along its face, 0.7 m out — inside its reach — from 2.5 m to one side to 2.5 m to the other.
		Put(me, span.Foot + (span.Normal * 0.7f) - (span.Right * 2.5f) + (Vector3.Up * 0.05f), span.Right);
		foreach (bool _ in Wait(10)) yield return true;
		Hold("move_forward");
		bool took = false;
		foreach (bool _ in Until(() =>
		{
			took |= me.IsClimbing;
			return span.Place(me.SimPosition).Side > 2.5f;
		}, 3 * Second)) yield return true;
		ReleaseAll();
		Fact(!_timedOut && !took, $"walking along its face past a ladder does not take it ({span.Foot})");
	}

	private void Paths(fps_controller me, Transform3D frame)
	{
		Rid map = me.GetWorld3D().NavigationMap;
		Vector3 ground = frame * new Vector3(9f, 0f, 0f);
		(string Name, Vector3 At)[] levels =
		{
			("the roof", frame * new Vector3(-1f, 7f, -1f)),
			("the top lift", frame * new Vector3(-4f, 7f, 5.05f)),
			("the first lift", frame * new Vector3(0f, 3.5f, 5.05f)),
			("the first floor", frame * new Vector3(2f, 3.5f, 0f)),
		};

		foreach (var level in levels)
		{
			Vector3[] bot = NavigationServer3D.MapGetPath(map, ground, level.At, true, 1u | Ladder.NavigationLayer);
			Vector3[] unit = NavigationServer3D.MapGetPath(map, ground, level.At, true, 1u);
			Vector3 botEnd = bot.Length > 0 ? bot[^1] : Vector3.Inf;
			Vector3 unitEnd = unit.Length > 0 ? unit[^1] : Vector3.Inf;
			Fact(botEnd.DistanceTo(level.At) < 0.6f,
				$"a ground bot's agent paths from the ground to {level.Name} ({bot.Length} points, ending {V(botEnd)})");
			Fact(unitEnd.Y < frame.Origin.Y + 0.6f,
				$"a unit's agent does not: its path to {level.Name} ends on the ground ({V(unitEnd)})");
		}
	}

	/// <summary>
	/// The player steered as a ground bot is (<see cref="BotPilot.ClimbDirection"/>,
	/// <see cref="BotPilot.OffLadders"/>), along a bot's agent's path
	/// (<see cref="BotPilot.NewNavigationAgent"/>): ground to roof, roof to first floor, first
	/// floor to the ground on the far side.
	/// </summary>
	private IEnumerable<bool> BotWay(fps_controller me, Transform3D frame)
	{
		NavigationAgent3D agent = BotPilot.NewNavigationAgent();
		me.AddChild(agent);

		Put(me, frame * new Vector3(14f, 0.05f, 2f), frame.Basis * Vector3.Left);
		foreach (bool _ in Wait(10)) yield return true;

		(string Name, Vector3 At)[] legs =
		{
			("the roof", frame * new Vector3(-1f, 7f, -1f)),
			("the first floor", frame * new Vector3(2f, 3.5f, -1f)),
			("the ground beyond the scaffold", frame * new Vector3(-2f, 0f, 11f)),
		};

		foreach (var leg in legs)
		{
			agent.TargetPosition = leg.At;
			foreach (bool _ in Wait(2)) yield return true;

			var used = new List<string>();
			bool reached = false;
			int ticks = 0;
			while (ticks++ < 60 * Second)
			{
				if (me.SimPosition.DistanceTo(leg.At) < 1.5f && me.IsOnFloor())
				{
					reached = true;
					break;
				}

				if (me.IsClimbing)
				{
					int held = Ladders.Holding(Ladder.All, me.SimPosition);
					// Off its reach for the tick between going over the top and the state letting go.
					string name = held >= 0 ? Ladder.NodeAt(held).Name : null;
					if (name != null && (used.Count == 0 || used[^1] != name))
					{
						used.Add(name);
					}
				}

				Vector3 next = agent.GetNextPathPosition();
				if (!BotPilot.ClimbDirection(me, next, out Vector3 direction))
				{
					direction = next - me.SimPosition;
					direction.Y = 0f;
					direction = direction.LengthSquared() < 1e-4f ? Vector3.Zero : direction.Normalized();
					direction = BotPilot.OffLadders(me, next, direction);
				}

				if (direction == Vector3.Zero)
				{
					ReleaseAll();
				}
				else
				{
					Face(direction);
					Input.ActionPress("move_forward");
				}

				yield return true;
			}

			ReleaseAll();
			Fact(reached, $"steered as a ground bot, the player gets to {leg.Name} in {F(ticks / (float)Second, "0.0")} s"
				+ $" by {(used.Count == 0 ? "no ladder" : string.Join(", ", used))} ({V(me.SimPosition)})");
			if (!reached)
			{
				break;
			}

			foreach (bool _ in Wait(10)) yield return true;
		}

		agent.QueueFree();
	}

	/// <summary>Looks from <paramref name="eyeFeet"/> (feet) at <paramref name="target"/>.</summary>
	private static void Look(fps_controller me, Vector3 eyeFeet, Vector3 target)
	{
		Put(me, eyeFeet, target - eyeFeet);
		Aim.Angles(target - (eyeFeet + (Vector3.Up * 1.6f)), out float yaw, out float pitch);
		Sampler.Align(yaw, pitch);
	}

	/// <summary>With <c>LADDER_SHOTS</c> and a display: the building, each kind of ladder, and the views from the top.</summary>
	private IEnumerable<bool> Shots(fps_controller me, Transform3D frame)
	{
		if (string.IsNullOrEmpty(_shots) || DisplayServer.GetName() == "headless")
		{
			yield break;
		}

		(string Name, Vector3 Feet, Vector3 Target)[] views =
		{
			("ladders_building", new Vector3(17f, 0.05f, 19f), new Vector3(0f, 3.5f, 2f)),
			("ladders_building_west", new Vector3(20f, 0.05f, -9f), new Vector3(0f, 3.5f, 0f)),
			("ladders_scaffold_ladder", new Vector3(-4.3f, 0.05f, 9f), new Vector3(-4.3f, 3.2f, 5.9f)),
			("ladders_first_lift", new Vector3(-5.2f, 3.55f, 5f), new Vector3(6f, 4.6f, 5f)),
			("ladders_to_roof", new Vector3(1.5f, 3.55f, 5.4f), new Vector3(4.3f, 6.5f, 4.25f)),
			("ladders_top_lift", new Vector3(-5.2f, 7.05f, 5f), new Vector3(-5.2f, 5f, 40f)),
			("ladders_roof", new Vector3(1.5f, 7.05f, 0.5f), new Vector3(6f, 7.8f, 0f)),
			("ladders_hatch", new Vector3(-1.5f, 0.05f, -0.5f), new Vector3(-4.4f, 1.8f, -2.95f)),
			("ladders_first_floor", new Vector3(3f, 3.55f, 0.5f), new Vector3(-4.6f, 3.2f, -2.95f)),
		};

		foreach (var view in views)
		{
			Look(me, frame * view.Feet, frame * view.Target);
			foreach (bool _ in Wait(30)) yield return true;
			Shot(view.Name);
		}

		// On the wall ladder, halfway up, looking up the rungs.
		Ladder wall = Find("LadderEndWall");
		if (wall != null)
		{
			LadderSpan span = wall.Span;
			Put(me, span.Foot + (span.Normal * 1.2f) + (Vector3.Up * 0.05f), -span.Normal);
			foreach (bool _ in Wait(10)) yield return true;
			Hold("move_forward");
			foreach (bool _ in Until(() => span.Place(me.SimPosition).Up > 3.5f, 4 * Second)) yield return true;
			ReleaseAll();
			Sampler.Align(YawAlong(-span.Normal), 0.6f);
			foreach (bool _ in Wait(30)) yield return true;
			Shot("ladders_climbing");
			Sampler.Align(YawAlong(span.Normal), -0.5f);
			foreach (bool _ in Wait(30)) yield return true;
			Shot("ladders_climbing_down");
			Hold("move_backward");
			foreach (bool _ in Until(() => !me.IsClimbing, 4 * Second)) yield return true;
			ReleaseAll();
		}
	}

	// ---- net -----------------------------------------------------------------------------

	private const string NetLadder = "LadderEndWall";

	private IEnumerator<bool> Server()
	{
		foreach (bool _ in Until(() => Has("client.peer"), 120 * Second)) yield return true;
		int peer = int.TryParse(File.ReadAllText(Path.Combine(_dir, "client.peer")).Trim(), out int p) ? p : 0;
		PlayerCombat client = null;
		foreach (bool _ in Until(() => (client = CombatManager.Instance?.Find(peer)) is { IsAlive: true, Character: not null },
			60 * Second)) yield return true;
		Fact(!_timedOut, $"the client's player {peer} is on the field");
		Ladder ladder = Find(NetLadder);
		Fact(Ladder.Count == Expected.Length && ladder != null, $"the server has the building's {Ladder.Count} ladders");
		if (_timedOut || ladder == null)
		{
			yield break;
		}

		LadderSpan span = ladder.Span;
		int starvedBefore = Starvations(peer);
		float yaw = YawAlong(-span.Normal);
		client.Character.Teleport(new Transform3D(new Basis(Vector3.Up, yaw),
			span.Foot + (span.Normal * 1.2f) + (Vector3.Up * 0.05f)));
		Mark("placed");

		foreach (string stop in new[] { "at-top", "at-foot" })
		{
			foreach (bool _ in Until(() => Has(stop), 60 * Second)) yield return true;
			if (_timedOut)
			{
				Fact(false, $"the client reports {stop}");
				yield break;
			}

			foreach (bool _ in Wait(20)) yield return true;
			Vector3 theirs = ParseVector(File.ReadAllText(Path.Combine(_dir, stop)));
			Vector3 ours = client.Character.SimPosition;
			Fact(ours.DistanceTo(theirs) < 0.02f,
				$"{stop}: the server has the client's player where the client does ({V(ours)} and {V(theirs)})");
			Mark($"{stop}-checked");
		}

		// Every tick the server ran with no frame of the client's in hand, repeating its last
		// (ServerInputQueue.TryPop): a misprediction the climb did not make.
		int starved = Starvations(peer) - starvedBefore;
		Measured($"the client's input ran out {starved} times over the climb");
		Mark("starved", starved.ToString(CultureInfo.InvariantCulture));
		foreach (bool _ in Until(() => Has("finished-client"), 60 * Second)) yield return true;
	}

	/// <summary>How many ticks the server has stepped <paramref name="peer"/> on a repeated frame.</summary>
	private static int Starvations(int peer)
	{
		var players = typeof(PlayerManager).GetField("_players",
			System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(PlayerManager.Instance)
			as System.Collections.IDictionary;
		object player = players?[peer];
		var queue = player?.GetType().GetField("Queue")?.GetValue(player) as ServerInputQueue;
		return queue?.Starvations ?? -1;
	}

	private IEnumerator<bool> Client()
	{
		foreach (bool _ in Onto(Team.GroundForce)) yield return true;
		PlayerCombat local = CombatManager.Instance.Local;
		Fact(!_timedOut && local.IsAlive, "the client's player is alive on the ground force");
		fps_controller me = local.Character;
		Ladder ladder = Find(NetLadder);
		Fact(Ladder.Count == Expected.Length && ladder != null, $"the client has the building's {Ladder.Count} ladders");
		if (ladder == null)
		{
			yield break;
		}

		LadderSpan span = ladder.Span;
		Mark("client.peer", NetworkManager.Instance.LocalPeerId.ToString(CultureInfo.InvariantCulture));
		Vector3 placed = span.Foot + (span.Normal * 1.2f);
		foreach (bool _ in Until(() => Has("placed") && Flat(me.SimPosition - placed) < 0.3f, 30 * Second))
			yield return true;
		Fact(!_timedOut, $"the server puts it in front of the wall ladder ({V(me.SimPosition)})");
		Face(-span.Normal);

		// The HUD's worst error is the last whole second's (NetStats.Advance): two of them and a
		// little more go by before it is read, so the correction the placing made is not in it.
		foreach (bool _ in Wait((2 * Second) + 10)) yield return true;

		NetStats stats = NetworkManager.Instance.Stats;
		long mispredictedBefore = stats.TotalMispredictions;
		float worst = 0f;
		long seen = mispredictedBefore;
		void Note()
		{
			worst = Mathf.Max(worst, stats.MaxErrorMeters);
			if (stats.TotalMispredictions != seen)
			{
				seen = stats.TotalMispredictions;
				LadderPlace at = span.Place(me.SimPosition);
				Measured($"a misprediction at tick {NetworkManager.Instance.Tick}: {me.StateName}, {F(at.Up)} m up,"
					+ $" {F(at.Out)} m out");
			}
		}

		Hold("move_forward");
		bool climbed = false;
		foreach (bool _ in Until(() =>
		{
			Note();
			climbed |= me.IsClimbing;
			return climbed && !me.IsClimbing;
		}, 10 * Second)) yield return true;
		bool overTop = !_timedOut;
		foreach (bool _ in Wait(15)) yield return true;
		ReleaseAll();
		foreach (bool _ in Wait(20)) yield return true;
		Fact(overTop && Mathf.Abs(me.SimPosition.Y - span.TopY) < 0.1f,
			$"predicted, it climbs the wall ladder and goes over onto the roof ({V(me.SimPosition)})");
		Mark("at-top", Format(me.SimPosition));
		foreach (bool _ in Until(() => Has("at-top-checked"), 10 * Second)) yield return true;

		Face(span.Normal);
		foreach (bool _ in Wait(2)) yield return true;
		Hold("move_forward");
		climbed = false;
		foreach (bool _ in Until(() =>
		{
			Note();
			climbed |= me.IsClimbing;
			return climbed && !me.IsClimbing;
		}, 10 * Second)) yield return true;
		bool down = !_timedOut;
		ReleaseAll();
		foreach (bool _ in Wait(20)) yield return true;
		Fact(down && Mathf.Abs(me.SimPosition.Y - span.Foot.Y) < 0.1f,
			$"and takes it again from the roof and comes down to its foot ({V(me.SimPosition)})");
		Mark("at-foot", Format(me.SimPosition));
		foreach (bool _ in Until(() => Has("at-foot-checked"), 10 * Second)) yield return true;

		long mispredicted = stats.TotalMispredictions - mispredictedBefore;
		foreach (bool _ in Until(() => Has("starved"), 10 * Second)) yield return true;
		int starved = Has("starved") && int.TryParse(File.ReadAllText(Path.Combine(_dir, "starved")), out int n) ? n : -1;
		Measured($"up and down a 7 m ladder: {mispredicted} mispredictions, worst error {F(worst * 100f, "0.0")} cm;"
			+ $" the server ran short of this client's input {starved} times");

		// A tick the server stepped on a repeated frame is a misprediction the netcode makes on any
		// ground: it is not the climb's. The climb's own are the rest.
		Fact(starved >= 0 && mispredicted <= starved,
			$"the server's records agree with the prediction up and down the ladder: {mispredicted} mispredictions,"
			+ $" no more than the {starved} ticks the server had no input for");
	}

	private static float Flat(Vector3 v) => new Vector2(v.X, v.Z).Length();

	private static string Format(Vector3 v) =>
		string.Join(' ', v.X.ToString("R", CultureInfo.InvariantCulture), v.Y.ToString("R", CultureInfo.InvariantCulture),
			v.Z.ToString("R", CultureInfo.InvariantCulture));

	private static Vector3 ParseVector(string text)
	{
		string[] parts = text.Trim().Split(' ');
		return parts.Length == 3
			? new Vector3(float.Parse(parts[0], CultureInfo.InvariantCulture), float.Parse(parts[1], CultureInfo.InvariantCulture),
				float.Parse(parts[2], CultureInfo.InvariantCulture))
			: Vector3.Inf;
	}
}
