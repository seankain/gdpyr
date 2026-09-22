using System.Collections.Generic;
using Gdpyr.Bots;
using Gdpyr.Fps;
using Gdpyr.Match;
using Gdpyr.Net;
using Gdpyr.Rts;
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
		FillDemo();
		FillPlayers();
		FillCombat();
		FillUnits();
		FillFog();
		FillEconomy();
		FillEmplacements();
	}

	public override void _Input(InputEvent @event)
	{
		// Exact, because `~` is shift and this key: the console is bound to the same
		// physical key with shift held (Scripts/Ui/GameConsole.cs), and an inexact
		// match here would open both at once.
		if (@event.IsActionPressed("debug", exactMatch: true))
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
	/// Whether a demo is being written, and how much of one (docs/DEMOS.md).
	///
	/// One row, and only while it is true, for the reason every other row here
	/// exists: "the file is two hundred bytes and the round has been going for five
	/// minutes" is a bug you cannot see any other way. There is no row for a demo
	/// being *watched*, because this panel lives in the local player's HUD and a
	/// replay has no local player — the read head is reported by the replay bar
	/// instead (Scripts/Ui/GameConsole.cs).
	/// </summary>
	private void FillDemo()
	{
		if (PlayerManager.Instance?.Recorder is not { } recorder)
		{
			return;
		}

		SetProperty("demo", $"{recorder.FileName}  {recorder.Kind}"
			+ $"  {recorder.TickCount} ticks ({recorder.Seconds:0.0}s)"
			+ $"  {DemoRecorder.FormatBytes(recorder.Bytes)}"
			+ (recorder.Failed == null ? string.Empty : $"  FAILED: {recorder.Failed}"));
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

	/// <summary>
	/// The replicated-unit count docs/NETCODE.md §8 asks for, plus what the
	/// strategist is spending. The unit count against the cap is the number that
	/// answers "is the 50-unit budget in §6 of the plan real".
	/// </summary>
	private void FillUnits()
	{
		UnitManager units = UnitManager.Instance;
		if (units == null)
		{
			return;
		}

		SetProperty("units", $"{units.LiveUnitCount}/{SimConfig.MaxUnits} live"
			+ $"  {units.UnitsProduced} built  {units.UnitsLost} lost"
			+ (units.NavigationReady ? string.Empty : "  (no navmesh)"));

		if (CombatManager.Instance is { } combat)
		{
			SetProperty("points", $"{combat.Match.StrategistPoints}"
				+ $"  strategists {combat.Teams.StrategistCount}/{TeamService.MaxStrategists}");
		}

		if (NetworkManager.Instance is { IsServer: true })
		{
			SetProperty("rejected orders", $"{units.RejectedOrders}");
		}

		// Builders' work (docs/NETCODE.md §10.5). "0 built" across a session means the
		// feature is not being used; a rebake count far above it means sites are being
		// knocked down as fast as they go up.
		SetProperty("structures", $"{units.StructureCount}/{SimConfig.MaxStructures} standing"
			+ $"  {units.SitesUnderConstruction} going up"
			+ (NetworkManager.Instance is { IsServer: true }
				? $"  {units.StructuresBuilt} built  {units.StructuresLost} lost  {units.NavigationRebakes} rebakes"
				: string.Empty));
	}

	/// <summary>
	/// What the fog of war is doing (docs/IMPLEMENTATION_PLAN.md §M4).
	///
	/// On the authority: how many of the ground force its sensors have out of how
	/// many are on the field, how many sensors that is, what the filter has kept off
	/// the wire and what the line-of-sight rays have cost. "0 withheld" next to a
	/// live round with a strategist in it is the fog not being applied.
	///
	/// On a strategist's client there is no field to report, only the other side of
	/// the same story: how many players it is still being told about, and how many it
	/// is drawing from memory (docs/NETCODE.md §6.2).
	/// </summary>
	private void FillFog()
	{
		if (CombatManager.Instance is not { Visibility: not null } combat)
		{
			return;
		}

		if (NetworkManager.Instance is { IsClient: true })
		{
			if (combat.Local is { Team: Team.Strategist })
			{
				FillClientContacts(combat);
			}
			return;
		}

		VisibilityService fog = combat.Visibility;
		SetProperty("fog", $"{fog.VisibleContacts}/{fog.TrackedContacts} seen"
			+ $"  {fog.SensorCount} sensors  {fog.WithheldRecords} withheld"
			+ $"  {fog.LineOfSightRays} rays");
	}

	/// <summary>
	/// The economy (docs/IMPLEMENTATION_PLAN.md §M5). "held" against the node count
	/// is the strategist's income as a rate; "contested" is the ground force being
	/// somewhere that costs the strategist something, which is the thing M5 exists
	/// to find out whether anybody bothers to do.
	///
	/// Shown on every peer, because who holds a node is not fogged: both sides can
	/// see somebody standing on a pad in any RTS anyone has played.
	/// </summary>
	private void FillEconomy()
	{
		if (CombatManager.Instance is not { Economy: { NodeCount: > 0 } economy } combat)
		{
			return;
		}

		SetProperty("nodes", $"{economy.StrategistNodes} strategist  {economy.GroundNodes} ground"
			+ $"  {economy.ContestedNodes} contested  of {economy.NodeCount}");

		if (NetworkManager.Instance is { IsServer: true })
		{
			SetProperty("income", $"{economy.IncomePaid} paid  balance {combat.Match.StrategistPoints}");
		}
	}

	/// <summary>
	/// The heavy guns (docs/IMPLEMENTATION_PLAN.md §M5). "0 cans spent" next to a
	/// live round is the resupply loop nobody is playing — which is a finding, not
	/// a bug, but it is one worth being able to see.
	/// </summary>
	private void FillEmplacements()
	{
		if (EmplacementManager.Instance is not { GunCount: > 0 } emplacements)
		{
			return;
		}

		SetProperty("guns", $"{emplacements.MountedGuns}/{emplacements.GunCount} manned"
			+ $"  {emplacements.CarriedItems} carried"
			+ $"  {emplacements.CansSpent} cans spent  {emplacements.RoundsResupplied} rounds");
	}

	private void FillClientContacts(CombatManager combat)
	{
		int seen = 0;
		int ghosts = 0;

		for (int i = 0; i < combat.PlayerCount; i++)
		{
			PlayerCombat player = combat.PlayerAt(i);
			if (player == null || player.Team != Team.GroundForce)
			{
				continue;
			}

			float age = PlayerManager.Instance?.SnapshotAgeTicks(player.PeerId) ?? float.MaxValue;
			if (!Fog.IsLost(age))
			{
				seen++;
			}
			else if (!Fog.IsForgotten(age))
			{
				ghosts++;
			}
		}

		SetProperty("fog", $"{seen} seen  {ghosts} ghosts");
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

		// Only the authority has any; a client's roster does not distinguish them
		// from anyone else (docs/NETCODE.md §9).
		if (players.Bots is { Enabled: true } bots)
		{
			SetProperty("bots", $"{bots.GroundBots}/{bots.GroundTarget} ground"
				+ $"  {bots.StrategistBots}/{bots.StrategistTarget} strategist");
		}

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
