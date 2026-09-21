using System.Collections.Generic;
using Gdpyr.Match;
using Gdpyr.Net;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Ui;

/// <summary>
/// The net debug panel, toggled with <c>`</c>. Built in M1 rather than later on
/// purpose: every hard bug in a prediction system is a timing bug, and without
/// these numbers the later milestones' bugs are unfalsifiable
/// (docs/NETCODE.md §8).
///
/// Lives in the local player's HUD, so there is exactly one per process and none
/// at all on a dedicated server, which logs a heartbeat instead.
/// </summary>
public partial class Debug : PanelContainer
{
	private VBoxContainer _propertyContainer;
	private readonly Dictionary<string, Label> _labels = new();

	public override void _Ready()
	{
		Visible = false;
		_propertyContainer = GetNode<VBoxContainer>("MarginContainer/VBoxContainer");
	}

	public override void _Process(double delta)
	{
		if (!Visible)
		{
			return;
		}

		SetProperty("fps", $"{Engine.GetFramesPerSecond()}");
		SetProperty("phys ms", $"{Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000.0:0.00}");

		FillNetwork();
		FillPlayers();
		FillCombat();
	}

	public override void _Input(InputEvent @event)
	{
		if (@event.IsActionPressed("debug"))
		{
			Visible = !Visible;
		}
	}

	/// <summary>
	/// Adds the row or updates it in place. The pre-M1 version added a new label per
	/// call, which was fine for one-shot logging and a leak for anything per-frame.
	/// </summary>
	public void SetProperty(string title, string value)
	{
		if (!_labels.TryGetValue(title, out Label label))
		{
			label = new Label();
			_propertyContainer.AddChild(label);
			_labels[title] = label;
		}
		label.Text = $"{title}: {value}";
	}

	public void AddDebugProperty(string title, string value) => SetProperty(title, value);

	private void FillNetwork()
	{
		NetworkManager net = NetworkManager.Instance;
		if (net == null)
		{
			return;
		}

		SetProperty("net", net.IsClient && !net.Connected ? $"{net.Mode} (connecting)" : net.Mode.ToString());
		SetProperty("tick", $"{net.Tick}");
		SetProperty("net in", NetStats.FormatRate(net.Stats.BytesInPerSecond));
		SetProperty("net out", NetStats.FormatRate(net.Stats.BytesOutPerSecond));

		if (!net.IsClient)
		{
			return;
		}

		SimClock clock = net.Clock;
		SetProperty("rtt", $"{clock.RttSeconds * 1000f:0} ms  jitter {clock.RttJitterSeconds * 1000f:0} ms");
		SetProperty("clock", $"lead {clock.InputLeadTicks}  render -{SimConfig.InterpolationDelayTicks}");
		SetProperty("input buffer", $"{clock.InputBufferDepth} frames");
		SetProperty("clock fixes", $"{clock.Nudges} nudges  {clock.Resyncs} resyncs");
		SetProperty("server frame ms", $"{net.ServerFrameMilliseconds:0.00}");
		SetProperty("mispredict/s", $"{net.Stats.MispredictionsPerSecond:0.0}");
		SetProperty("pred error", $"mean {net.Stats.MeanErrorMeters * 100f:0.00} cm  max {net.Stats.MaxErrorMeters * 100f:0.00} cm");
	}

	/// <summary>
	/// The combat counters docs/NETCODE.md §8 asks for. Live projectile count is the
	/// one that answers "is the pool leaking"; overflows answer "is it too small".
	/// </summary>
	private void FillCombat()
	{
		CombatManager combat = CombatManager.Instance;
		if (combat == null)
		{
			return;
		}

		SetProperty("projectiles", $"{combat.LiveProjectiles} live  {combat.ShotsFired} fired"
			+ (combat.ProjectileOverflows > 0 ? $"  {combat.ProjectileOverflows} dropped" : string.Empty));

		MatchState match = combat.Match;
		SetProperty("round", $"{match.Phase}  tickets {match.GroundTickets}/{match.StartingGroundTickets}"
			+ $"  deaths {match.GroundDeaths}");

		if (combat.Local is { } local)
		{
			SetProperty("combat", $"hp {local.Health}  slot {local.Slot}"
				+ $"  ammo {local.Equipped.Ammo}  k/d {local.Kills}/{local.Deaths}");
		}

		if (NetworkManager.Instance is { IsServer: true })
		{
			SetProperty("hits", $"{combat.HitsResolved}");
		}
	}

	private void FillPlayers()
	{
		PlayerManager players = PlayerManager.Instance;
		if (players == null)
		{
			return;
		}

		SetProperty("players", $"{players.PlayerCount}");
		SetProperty("move state", players.LocalStateName);

		NetworkManager net = NetworkManager.Instance;
		if (net != null && net.IsClient)
		{
			SetProperty("replayed", $"{players.LastReplayTicks} ticks");
		}
		else if (net != null && net.IsServer)
		{
			SetProperty("rejected input", $"{players.RejectedInputPackets}");
		}
	}
}
