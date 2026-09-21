using System;
using Gdpyr.Match;
using Gdpyr.Net;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Ui;

/// <summary>
/// The table at the end of a round (docs/IMPLEMENTATION_PLAN.md §M5): who won,
/// what it cost both sides, and one row per player.
///
/// It is on screen for the intermission and then gone, because the next round
/// starts by itself — a playtest that needs somebody to press a key between rounds
/// gets fewer rounds per session, which is the same argument
/// <see cref="GameModeDefinition.IntermissionSeconds"/> already makes.
///
/// It draws from <see cref="CombatManager.Scores"/>, which is the round's own
/// table on the server and a decoded reliable message on a client: kills and
/// deaths are server-side counters all round and this is the one time they are on
/// the wire.
///
/// Built in code and not as a scene, like every other HUD here.
/// </summary>
public partial class Scoreboard : CanvasLayer
{
	private Label _title;
	private Label _summary;
	private Label _table;
	private Panel _panel;

	public override void _Ready()
	{
		// Above the combat HUD and the strategist's, both of which stay up.
		ProcessMode = ProcessModeEnum.Always;
		Layer = 3;
		Visible = false;

		var root = new Control
		{
			MouseFilter = Control.MouseFilterEnum.Ignore,
			AnchorRight = 1f,
			AnchorBottom = 1f,
		};
		AddChild(root);

		_panel = new Panel
		{
			MouseFilter = Control.MouseFilterEnum.Ignore,
			AnchorLeft = 0.5f,
			AnchorTop = 0.5f,
			Position = new Vector2(-320f, -220f),
			Size = new Vector2(640f, 440f),
		};
		_panel.SelfModulate = new Color(0f, 0f, 0f, 0.65f);
		root.AddChild(_panel);

		_title = NewLabel(_panel, new Vector2(24f, 16f), new Vector2(592f, 40f));
		_summary = NewLabel(_panel, new Vector2(24f, 56f), new Vector2(592f, 64f));
		_table = NewLabel(_panel, new Vector2(24f, 128f), new Vector2(592f, 290f));
	}

	/// <summary>Called once per simulation tick by the combat manager.</summary>
	public void Refresh(CombatManager combat)
	{
		bool over = combat != null && combat.Match.Phase == RoundPhase.Ended;
		Visible = over;

		if (!over)
		{
			return;
		}

		MatchState match = combat.Match;
		RoundSummary summary = combat.Summary;

		_title.Text = match.Winner switch
		{
			Team.GroundForce => "ground force holds",
			Team.Strategist => "strategist wins",
			_ => "round over",
		};

		_summary.Text = $"{Describe(match.Outcome)}   {summary.Minutes:0.0} min\n"
			+ $"tickets left {summary.GroundTickets}   points left {summary.StrategistPoints}"
			+ $"   units {summary.UnitsBuilt} built / {summary.UnitsLost} lost";

		_table.Text = Table(combat);
	}

	private static string Table(CombatManager combat)
	{
		ReadOnlySpan<ScoreEntry> scores = combat.Scores;
		if (scores.Length == 0)
		{
			return "no players";
		}

		int local = NetworkManager.Instance?.LocalPeerId ?? 0;
		var text = new System.Text.StringBuilder(256);
		text.Append("player                 side          K    D\n");

		for (int i = 0; i < scores.Length; i++)
		{
			ref readonly ScoreEntry entry = ref scores[i];
			string name = entry.IsBot ? BotRoster.NameOf(entry.PeerId) : $"peer {entry.PeerId}";
			if (entry.PeerId == local)
			{
				name += " (you)";
			}

			string side = entry.Team == Team.Strategist ? "strategist" : "ground";
			text.Append($"{name,-22} {side,-12} {entry.Kills,3}  {entry.Deaths,3}\n");
		}

		return text.ToString();
	}

	private static string Describe(RoundOutcome outcome) => outcome switch
	{
		RoundOutcome.GroundForceEliminated => "the ground force ran out of tickets",
		RoundOutcome.TimeExpired => "the clock ran out",
		RoundOutcome.StrategistEliminated => "the strategist ran out of units, points and ground",
		_ => "undecided",
	};

	private static Label NewLabel(Control parent, Vector2 position, Vector2 size)
	{
		var label = new Label
		{
			MouseFilter = Control.MouseFilterEnum.Ignore,
			Position = position,
			Size = size,
		};

		label.AddThemeColorOverride("font_outline_color", Colors.Black);
		label.AddThemeConstantOverride("outline_size", 4);
		parent.AddChild(label);
		return label;
	}
}
