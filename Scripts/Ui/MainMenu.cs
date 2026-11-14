using System;
using System.Collections.Generic;
using Gdpyr.Core;
using Gdpyr.Match;
using Gdpyr.Net;
using Godot;

namespace Gdpyr.Ui;

/// <summary>
/// The first thing a build with no flags shows: what is on this network, a box to
/// type an address into, and the two ways to play on your own
/// (docs/LAN.md §4).
///
/// It is the main scene, so every launch passes through it — but a launch that
/// already said what it wanted on the command line passes straight through, before
/// a single widget is built. That keeps the headless server, the playtest harness
/// and the training runs on exactly the path they had before there was a menu.
///
/// The list is LAN discovery and nothing more (<see cref="ServerBrowser"/>): it
/// finds what a broadcast reaches. A server anywhere else is typed into the address
/// box, which is what the deployment runbook hands out and what a Tailscale address
/// is (docs/IMPLEMENTATION_PLAN.md §5).
///
/// **Host** opens a second panel in place of the list: the map, the round's length,
/// the ground force's tickets and how many players each side is filled to
/// (<see cref="HostSettings"/>, docs/LAN.md §3.4). A client is told the host's map
/// as soon as it connects, and loads that one.
/// </summary>
public partial class MainMenu : Control
{
	/// <summary>How often a query goes out while the menu is up.</summary>
	private const float RefreshIntervalSeconds = 2.5f;

	/// <summary>How often the rows are rebuilt. Faster than this only moves the cursor about.</summary>
	private const float RedrawIntervalSeconds = 0.25f;

	/// <summary>
	/// How long a connected client waits for the server to name its map before giving
	/// up. The server names it on the tick the link comes up, ahead of everything
	/// else, so only a server from before the host panel stays silent this long.
	/// </summary>
	private const float MapWaitSeconds = 5f;

	/// <summary>
	/// The servers the rows currently stand for. A copy rather than an index into the
	/// browser: rows come and go between redraws, and "join row 2" has to mean the
	/// server row 2 is showing.
	/// </summary>
	private readonly List<ServerInfo> _rows = new();

	private ServerBrowser _browser;
	private ItemList _list;
	private LineEdit _address;
	private Label _status;
	private Button _join;
	private Button _joinByAddress;
	private Button _host;
	private Button _offline;
	private Button _watch;
	private Button _refresh;

	private VBoxContainer _browserColumn;
	private VBoxContainer _hostColumn;
	private OptionButton _mapPicker;
	private Label _mapDescription;
	private SpinBox _roundMinutes;
	private SpinBox _groundTickets;
	private SpinBox _groundForceSize;
	private SpinBox _strategistSize;
	private Button _startHosting;
	private Label _hostStatus;

	/// <summary>Seconds connected without the server having named its map.</summary>
	private float _mapWait;

	/// <summary>
	/// A `--client` launch: no widgets, and the map is loaded once the server has
	/// named it rather than before the link is up.
	/// </summary>
	private bool _launchClient;

	private float _refreshAge = float.MaxValue;
	private float _redrawAge = float.MaxValue;

	/// <summary>The rows as they are drawn. Null before the first draw, which is not the same as empty.</summary>
	private string _drawn;
	private bool _leaving;
	private bool _busy;

	public override void _Ready()
	{
		// A command line that named a mode has already said what it wants, and a
		// dedicated server has nobody to ask. Both skip the menu entirely — including
		// building it, because a headless process should not be laying out widgets.
		// `--playdemo` names no mode and still skips it: a demo is watched in the map
		// and there is no server to pick (docs/DEMOS.md §5.1). A bare `--spectate` is
		// the menu's "Watch the bots" said on the command line (docs/AI_DEBUG.md §1).
		// `--practice` is the menu's "Practice offline" said on the command line
		// (docs/LEVEL_EDITOR.md §8.7): the round on one's own, on `--map`'s map.
		bool watchBots = Bootstrap.Options.Spectate && Bootstrap.Options.Mode == LaunchMode.Offline;
		bool practice = Bootstrap.Options.Practice;
		if (Bootstrap.IsDedicatedServer || Bootstrap.Options.Mode != LaunchMode.Offline
			|| Bootstrap.Options.PlayDemo != null || watchBots || practice)
		{
			// No peer, no packets: a replay is a file being read, and the transport
			// would otherwise sit waiting for a menu that is not going to be built.
			if (Bootstrap.Options.PlayDemo != null || watchBots || practice)
			{
				NetworkManager.Instance?.PlayOffline();
			}

			// A client plays the map its server is playing, which it learns once the
			// link is up (docs/LAN.md §3.4). Still no widgets: _Process waits for it.
			if (Bootstrap.Options.Mode == LaunchMode.Client)
			{
				_launchClient = true;
				return;
			}

			// Deferred, because a scene cannot be changed while the outgoing one is
			// still being readied.
			Callable.From(EnterWorld).CallDeferred();
			return;
		}

		Session.EnterMenu();
		Input.MouseMode = Input.MouseModeEnum.Visible;

		Build();

		_browser = new ServerBrowser { Name = "ServerBrowser" };
		AddChild(_browser);
	}

	public override void _Process(double delta)
	{
		if (_leaving)
		{
			return;
		}

		if (_launchClient)
		{
			WatchLaunchConnection((float)delta);
			return;
		}

		if (_browser == null)
		{
			return;
		}

		_refreshAge += (float)delta;
		_redrawAge += (float)delta;

		if (_refreshAge >= RefreshIntervalSeconds)
		{
			_refreshAge = 0f;
			_browser.Refresh();
		}

		if (_redrawAge >= RedrawIntervalSeconds)
		{
			_redrawAge = 0f;
			Redraw();
		}

		WatchConnection((float)delta);
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		// Escape backs out of the host panel the way its button does.
		if (_hostColumn is { Visible: true } && @event.IsActionPressed("ui_cancel"))
		{
			ShowHostPanel(false);
			GetViewport().SetInputAsHandled();
		}
	}

	// ---- the two things the menu does --------------------------------------

	/// <summary>
	/// Waits out the handshake. ENet answers on its own schedule and the transport
	/// has no callback to hang a scene change off, so the menu asks every frame
	/// whether the answer has arrived (docs/NETCODE.md §2).
	/// </summary>
	private void WatchConnection(float delta)
	{
		NetworkManager net = NetworkManager.Instance;
		if (net == null || !net.IsClient || net.IsConnecting)
		{
			return;
		}

		if (net.Connected)
		{
			switch (TakeServerMap(net, delta, out string error))
			{
				case MapAnswer.Waiting:
					_status.Text = "connected; waiting for the host to say which map it is playing…";
					return;

				case MapAnswer.Refused:
					// Dropped rather than kept, as a link that never came up is: the next
					// address has to be allowed a socket of its own, and whatever of this
					// server's roster already arrived is about a world this build cannot load.
					PlayerManager.Instance?.ForgetClientRoster();
					net.AbandonConnection();
					SetBusy(false);
					_status.Text = error;
					return;
			}

			_status.Text = $"connected — {Session.Map.Name}";
			EnterWorld();
			return;
		}

		if (net.ConnectionFailed)
		{
			// Dropped rather than kept: the next address a player types has to be
			// allowed to open a socket of its own.
			net.AbandonConnection();
			SetBusy(false);
			_status.Text = "no answer. Check the address, that the host is running,"
				+ " and that UDP is open on it (docs/LAN.md §3.2).";
		}
	}

	/// <summary>What <see cref="TakeServerMap"/> found.</summary>
	private enum MapAnswer
	{
		/// <summary>The server has not named its map yet.</summary>
		Waiting,

		/// <summary>The server's map is this build's, and is now <see cref="Session.Map"/>.</summary>
		Taken,

		/// <summary>The server named a map this build does not have, or never named one.</summary>
		Refused,
	}

	/// <summary>
	/// Makes the map a connected server named the one this process loads
	/// (docs/LAN.md §3.4). A map this build does not have is refused rather than
	/// replaced with the default: a client on a different floor from its server's is
	/// a desync from the first tick, and "update the checkout" is something a person
	/// can do.
	/// </summary>
	private MapAnswer TakeServerMap(NetworkManager net, float delta, out string error)
	{
		error = null;

		if (net.ServerMapId == null)
		{
			_mapWait += delta;
			if (_mapWait < MapWaitSeconds)
			{
				return MapAnswer.Waiting;
			}

			error = "connected, but the host never said which map it is playing — it is probably"
				+ " running an older build. Update one checkout to match the other.";
			return MapAnswer.Refused;
		}

		if (net.ServerMapId.Length == 0)
		{
			// A map from the host's own library: fetching one is docs/LEVEL_EDITOR.md LE7's.
			error = "the host is playing a map from its own library, which this build cannot fetch yet.";
			return MapAnswer.Refused;
		}

		if (!MapCatalog.TryFind(net.ServerMapId, out MapInfo map))
		{
			error = $"the host is playing map '{net.ServerMapId}', which this build does not have."
				+ " Update this checkout to match the host's.";
			return MapAnswer.Refused;
		}

		if (!Session.TrySelectMap(map, out string unreadable))
		{
			error = $"the host is playing {map.Name}, and this build's copy of it does not play: {unreadable}";
			return MapAnswer.Refused;
		}

		return MapAnswer.Taken;
	}

	/// <summary>
	/// The `--client` launch's half of <see cref="WatchConnection"/>: no widgets to
	/// report to, so a refusal is the log's and the process's exit code, as every
	/// other command-line failure is. A link that never comes up still loads the
	/// default map, as it did before a client waited for one: the log says why, and
	/// the scripts that launch clients read the log.
	/// </summary>
	private void WatchLaunchConnection(float delta)
	{
		NetworkManager net = NetworkManager.Instance;
		if (net == null || net.IsConnecting)
		{
			return;
		}

		if (!net.Connected)
		{
			EnterWorld();
			return;
		}

		switch (TakeServerMap(net, delta, out string error))
		{
			case MapAnswer.Waiting:
				return;

			case MapAnswer.Refused:
				_leaving = true;
				GD.PrintErr($"gdpyr: {error}");
				GetTree().Quit(1);
				return;
		}

		GD.Print($"[menu] loading the server's map, {Session.Map}");
		EnterWorld();
	}

	private void Join(string host, int port)
	{
		NetworkManager net = NetworkManager.Instance;
		if (net == null || _leaving)
		{
			return;
		}

		SetBusy(true);
		_mapWait = 0f;
		_status.Text = $"connecting to {host}:{port}…";

		if (!net.JoinServer(host, port))
		{
			SetBusy(false);
			_status.Text = $"could not open a socket to {host}:{port}";
		}
	}

	private void JoinSelected()
	{
		int[] selected = _list.GetSelectedItems();
		if (selected.Length == 0 || selected[0] >= _rows.Count)
		{
			_status.Text = "pick a server, or type an address";
			return;
		}

		ServerInfo info = _rows[selected[0]];
		Join(info.Address, info.GamePort);
	}

	private void JoinTyped()
	{
		if (!LaunchOptions.TryParseAddress(_address.Text, out string host, out int port, out string error))
		{
			_status.Text = error;
			return;
		}

		Join(host, port);
	}

	/// <summary>
	/// Opens the port and the round on what the host panel says
	/// (docs/LAN.md §3.4). The settings are put in place after the port is bound and
	/// before the map loads: nobody can connect in between — a connection is only
	/// accepted on a later frame's poll — and the first round is played on them.
	/// </summary>
	private void StartHosting()
	{
		NetworkManager net = NetworkManager.Instance;
		int port = Bootstrap.Options.Port;

		if (net == null || _leaving)
		{
			return;
		}

		HostSettings settings = ReadHostPanel();

		// Before the port: a data map is read and validated here, and one that does not
		// play is said so with the port still free (docs/LEVEL_EDITOR.md §9.1).
		if (!Session.HostWith(settings, out string mapError))
		{
			_hostStatus.Text = $"cannot host {settings.Map.Name}: {mapError}";
			return;
		}

		if (!net.HostListen(port))
		{
			_hostStatus.Text = $"could not listen on UDP {port}: something else is already on it."
				+ " Launch with `-- --listen <port>` to host on another one.";
			return;
		}

		CombatManager.Instance?.ApplyHostSettings(settings);
		GD.Print($"[menu] hosting on UDP {port}: {settings}");
		EnterWorld();
	}

	/// <summary>What the host panel's widgets say, pulled into range.</summary>
	private HostSettings ReadHostPanel()
	{
		// A number typed into a box counts once it is submitted or the box loses
		// focus; a click on Start may beat the second of those.
		_roundMinutes.Apply();
		_groundTickets.Apply();
		_groundForceSize.Apply();
		_strategistSize.Apply();

		return new HostSettings(SelectedMapId(), (int)_roundMinutes.Value, (int)_groundTickets.Value,
			(int)_groundForceSize.Value, (int)_strategistSize.Value);
	}

	private string SelectedMapId()
	{
		int index = _mapPicker.Selected;
		return index >= 0 && index < MapCatalog.Count ? MapCatalog.All[index].Id : MapCatalog.Default.Id;
	}

	private void ShowHostPanel(bool show)
	{
		if (_leaving)
		{
			return;
		}

		_browserColumn.Visible = !show;
		_hostColumn.Visible = show;
		_hostStatus.Text = string.Empty;

		if (show)
		{
			_startHosting.GrabFocus();
		}
		else
		{
			_host.GrabFocus();
		}
	}

	private void Offline()
	{
		NetworkManager.Instance?.PlayOffline();
		EnterWorld();
	}

	/// <summary>
	/// An offline round with nobody on the field: both sides bots, and this process
	/// spectating them with the AI debugger (docs/AI_DEBUG.md §2). F3 joins the round.
	/// </summary>
	private void WatchBots()
	{
		PlayerManager.SpectateOnStart();
		Offline();
	}

	/// <summary>
	/// Hands over to the map. <see cref="Core.WorldRoot"/> is what actually opens the
	/// session, on the far side of the scene change, so that a map reached from a
	/// command line opens it the same way.
	/// </summary>
	private void EnterWorld()
	{
		if (_leaving)
		{
			return;
		}

		_leaving = true;

		Error error = GetTree().ChangeSceneToFile(Session.WorldScenePath);
		if (error != Error.Ok)
		{
			// The map is a constant in this build, so this is a broken export rather
			// than anything a player did. Say which file, and say it once.
			GD.PrintErr($"[menu] could not load {Session.WorldScenePath}: {error}");
		}
	}

	// ---- the list ----------------------------------------------------------

	private void Redraw()
	{
		string signature = Signature();
		if (signature == _drawn)
		{
			return;
		}

		// Selection is restored by endpoint rather than by row: rows come and go as
		// servers answer, and a cursor that lands on a different server than the one
		// it was on is how somebody joins the wrong game.
		string selected = SelectedEndpoint();
		_drawn = signature;
		_list.Clear();
		_rows.Clear();

		for (int i = 0; i < _browser.Count; i++)
		{
			ServerInfo info = _browser[i];
			_rows.Add(info);
			_list.AddItem(Describe(info));

			if (info.Endpoint == selected)
			{
				_list.Select(i);
			}
		}

		if (_busy)
		{
			// A handshake is in flight and the status line is saying so. The list
			// keeps refreshing underneath it; it does not get to talk over it.
			return;
		}

		// One query is not an answer: a reply takes a moment to come back, and a list
		// that says "nothing here" in the first frame is a list nobody believes.
		_status.Text = _browser.Count > 0
			? string.Empty
			: _browser.Refreshes <= 1
				? "searching…"
				: "nothing on this network. Type an address for a server anywhere else.";
	}

	/// <summary>Everything a row draws, so a redraw happens when a row would change and not otherwise.</summary>
	private string Signature()
	{
		var text = new System.Text.StringBuilder();
		for (int i = 0; i < _browser.Count; i++)
		{
			text.Append(Describe(_browser[i])).Append('\n');
		}

		return text.ToString();
	}

	private string SelectedEndpoint()
	{
		int[] selected = _list.GetSelectedItems();
		return selected.Length > 0 && selected[0] < _rows.Count
			? _rows[selected[0]].Endpoint
			: string.Empty;
	}

	private static string Describe(in ServerInfo info)
	{
		string round = info.Phase switch
		{
			RoundPhase.Live => $"live {Clock(info.SecondsRemaining)}",
			RoundPhase.Ended => "between rounds",
			_ => "warmup",
		};

		// People and bots are counted apart: "6 players" that are all bots is the one
		// number a server browser must not round off.
		return $"{info.Name}   —   {info.Endpoint}   —   {info.People}/{info.MaxPlayers} players"
			+ (info.Bots > 0 ? $" (+{info.Bots} bots)" : string.Empty)
			+ $"   —   {round}";
	}

	private static string Clock(int seconds) => $"{seconds / 60:00}:{seconds % 60:00}";

	/// <summary>Buttons off while a handshake is in flight, so a second click cannot start a second one.</summary>
	private void SetBusy(bool busy)
	{
		_busy = busy;
		_join.Disabled = busy;
		_joinByAddress.Disabled = busy;
		_host.Disabled = busy;
		_offline.Disabled = busy;
		_watch.Disabled = busy;
		_refresh.Disabled = busy;
	}

	// ---- layout ------------------------------------------------------------

	private void Build()
	{
		SetAnchorsPreset(LayoutPreset.FullRect);

		var background = new ColorRect
		{
			Color = new Color(0.06f, 0.07f, 0.09f),
			MouseFilter = MouseFilterEnum.Ignore,
		};
		background.SetAnchorsPreset(LayoutPreset.FullRect);
		AddChild(background);

		var centre = new CenterContainer();
		centre.SetAnchorsPreset(LayoutPreset.FullRect);
		AddChild(centre);

		var column = new VBoxContainer { CustomMinimumSize = new Vector2(660f, 0f) };
		column.AddThemeConstantOverride("separation", 10);
		centre.AddChild(column);

		var title = new Label { Text = "gdpyr" };
		title.AddThemeFontSizeOverride("font_size", 48);
		column.AddChild(title);

		column.AddChild(new Label { Text = "asymmetric 6v2 — six on the ground, two above it" });

		_browserColumn = new VBoxContainer();
		_browserColumn.AddThemeConstantOverride("separation", 10);
		column.AddChild(_browserColumn);

		_hostColumn = new VBoxContainer { Visible = false };
		_hostColumn.AddThemeConstantOverride("separation", 10);
		column.AddChild(_hostColumn);

		BuildBrowser(_browserColumn);
		BuildHostPanel(_hostColumn);

		_join.GrabFocus();
	}

	private void BuildBrowser(VBoxContainer column)
	{
		var listHeader = new HBoxContainer();
		column.AddChild(listHeader);

		listHeader.AddChild(new Label
		{
			Text = "servers on this network",
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
		});

		_refresh = new Button { Text = "Refresh" };
		_refresh.Pressed += () =>
		{
			_browser.Clear();
			_drawn = null;
			_refreshAge = RefreshIntervalSeconds;
		};
		listHeader.AddChild(_refresh);

		_list = new ItemList
		{
			CustomMinimumSize = new Vector2(660f, 200f),
			SelectMode = ItemList.SelectModeEnum.Single,
			AllowReselect = true,
		};
		_list.ItemActivated += _ => JoinSelected();
		column.AddChild(_list);

		var addressRow = new HBoxContainer();
		column.AddChild(addressRow);

		_address = new LineEdit
		{
			PlaceholderText = "192.168.1.20:7777 — or any address, for a server further away",
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
		};
		_address.TextSubmitted += _ => JoinTyped();
		addressRow.AddChild(_address);

		_joinByAddress = new Button { Text = "Connect" };
		_joinByAddress.Pressed += JoinTyped;
		addressRow.AddChild(_joinByAddress);

		var buttons = new HBoxContainer();
		column.AddChild(buttons);

		_join = NewButton(buttons, "Join selected");
		_join.Pressed += JoinSelected;

		_host = NewButton(buttons, "Host…");
		_host.Pressed += () => ShowHostPanel(true);

		_offline = NewButton(buttons, "Practice offline");
		_offline.Pressed += Offline;

		_watch = NewButton(buttons, "Watch the bots");
		_watch.Pressed += WatchBots;

		Button quit = NewButton(buttons, "Quit");
		quit.Pressed += () => GetTree().Quit();

		_status = new Label { Text = "searching…", AutowrapMode = TextServer.AutowrapMode.WordSmart };
		_status.CustomMinimumSize = new Vector2(660f, 48f);
		column.AddChild(_status);
	}

	/// <summary>
	/// The host panel (docs/LAN.md §3.4). It starts on the game mode's numbers, with
	/// the command line's <c>--bots</c> over them where it gave any, so a host who
	/// changes nothing gets the round a <c>--listen</c> would have.
	/// </summary>
	private void BuildHostPanel(VBoxContainer column)
	{
		GameModeDefinition mode = CombatManager.Instance?.GameMode ?? new GameModeDefinition();
		HostSettings defaults = HostSettings.Defaults(mode.RoundDurationMinutes, mode.GroundForceTickets,
			Bootstrap.Options.GroundBots ?? mode.BotGroundForce,
			Bootstrap.Options.StrategistBots ?? mode.BotStrategists);

		column.AddChild(new Label { Text = "host a game" });

		var grid = new GridContainer { Columns = 2 };
		grid.AddThemeConstantOverride("h_separation", 16);
		grid.AddThemeConstantOverride("v_separation", 8);
		column.AddChild(grid);

		_mapPicker = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		for (int i = 0; i < MapCatalog.Count; i++)
		{
			_mapPicker.AddItem(MapCatalog.All[i].Name, i);
		}
		_mapPicker.Selected = Math.Max(MapCatalog.IndexOf(defaults.MapId), 0);
		_mapPicker.ItemSelected += _ => DescribeSelectedMap();
		AddRow(grid, "Map", _mapPicker);

		_mapDescription = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
		AddRow(grid, string.Empty, _mapDescription);
		DescribeSelectedMap();

		_roundMinutes = NewSpinBox(HostSettings.MinRoundMinutes, HostSettings.MaxRoundMinutes,
			defaults.RoundMinutes, " min");
		AddRow(grid, "Round length", _roundMinutes);

		_groundTickets = NewSpinBox(HostSettings.MinGroundForceTickets, HostSettings.MaxGroundForceTickets,
			defaults.GroundForceTickets, string.Empty);
		AddRow(grid, "Ground force tickets", _groundTickets);

		_groundForceSize = NewSpinBox(HostSettings.MinTeamSize, HostSettings.MaxGroundForceSize,
			defaults.GroundForceSize, string.Empty);
		AddRow(grid, "Ground force team size", _groundForceSize);

		_strategistSize = NewSpinBox(HostSettings.MinTeamSize, HostSettings.MaxStrategistSize,
			defaults.StrategistSize, string.Empty);
		AddRow(grid, "Strategist team size", _strategistSize);

		column.AddChild(new Label
		{
			Text = "Computer players fill the seats nobody takes, and get up when a person wants one."
				+ " No more than the strategist team size may be strategists at once; anybody else plays on the ground.",
			AutowrapMode = TextServer.AutowrapMode.WordSmart,
			CustomMinimumSize = new Vector2(660f, 0f),
		});

		var buttons = new HBoxContainer();
		column.AddChild(buttons);

		_startHosting = NewButton(buttons, "Start");
		_startHosting.Pressed += StartHosting;

		Button back = NewButton(buttons, "Back");
		back.Pressed += () => ShowHostPanel(false);

		_hostStatus = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
		_hostStatus.CustomMinimumSize = new Vector2(660f, 48f);
		column.AddChild(_hostStatus);
	}

	private void DescribeSelectedMap()
	{
		int index = _mapPicker.Selected;
		_mapDescription.Text = index >= 0 && index < MapCatalog.Count ? MapCatalog.All[index].Description : string.Empty;
	}

	private static void AddRow(GridContainer grid, string label, Control control)
	{
		grid.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(220f, 0f) });
		control.SizeFlagsHorizontal = SizeFlags.ExpandFill;
		grid.AddChild(control);
	}

	private static SpinBox NewSpinBox(int min, int max, int value, string suffix)
	{
		var box = new SpinBox
		{
			MinValue = min,
			MaxValue = max,
			Step = 1,
			Rounded = true,
			Suffix = suffix,
		};
		box.Value = value;
		return box;
	}

	private static Button NewButton(Control parent, string text)
	{
		var button = new Button
		{
			Text = text,
			CustomMinimumSize = new Vector2(150f, 40f),
		};
		parent.AddChild(button);
		return button;
	}
}
