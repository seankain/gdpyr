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
	private Button _editor;
	private Button _refresh;

	private VBoxContainer _browserColumn;
	private VBoxContainer _hostColumn;
	private OptionButton _mapPicker;
	private Label _mapDescription;

	/// <summary>What the picker's rows stand for: the catalog, then one's own maps that play (docs/LEVEL_EDITOR.md §9.1).</summary>
	private readonly List<MapInfo> _pickable = new();
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

	/// <summary>
	/// Whether `--edit` has been acted on. The editor comes back to this menu when it is left,
	/// and the command line that sent this process to it once must not send it again.
	/// </summary>
	private static bool _editTaken;

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
		// `--edit` is the menu's "Map editor" said on the command line (docs/LEVEL_EDITOR.md §8.1),
		// once: leaving the editor comes back here, to the menu.
		if (Bootstrap.Options.Edit && !_editTaken && !Bootstrap.IsDedicatedServer)
		{
			_editTaken = true;
			Gdpyr.MapEditor.MapEditorRoot.OpenOnEnter = Bootstrap.Options.EditName;
			Callable.From(EnterEditor).CallDeferred();
			return;
		}

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

			// A demo is opened here, as the console's `playdemo` opens one from the menu, so the map it
			// was recorded on is the one loaded (docs/LEVEL_EDITOR.md §9.6). One that cannot be is fatal,
			// as it was when it was opened in the map.
			if (Bootstrap.Options.PlayDemo is { } demo && !PlayerManager.BeginPlaybackOf(demo, out string demoError))
			{
				_leaving = true;
				GD.PrintErr($"gdpyr: --playdemo {demo}: {demoError}");
				GetTree().Quit(1);
				return;
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

				case MapAnswer.Fetching:
					// docs/LEVEL_EDITOR.md §9.3: `fetching 'ridge' 47 / 114`.
					_status.Text = FetchProgress(net);
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

		/// <summary>The server's map is a data map this process lacks, and it is arriving (docs/LEVEL_EDITOR.md §9.3).</summary>
		Fetching,

		/// <summary>The server's map is this process's, and is now <see cref="Session.Map"/>.</summary>
		Taken,

		/// <summary>The server named a map this build cannot play, never named one, or sent one that was refused.</summary>
		Refused,
	}

	/// <summary>
	/// Makes the map a connected server named the one this process loads
	/// (docs/LAN.md §3.4). A map this build does not have is refused rather than
	/// replaced with the default: a client on a different floor from its server's is
	/// a desync from the first tick, and "update the checkout" is something a person
	/// can do.
	///
	/// A data map is named by its hash (docs/LEVEL_EDITOR.md §9.3): one this process has — bundled,
	/// its own, or received before — is read and validated against this build and played; one it
	/// lacks is fetched from the server, hashed, read, validated and cached, then played. A map this
	/// build's validator refuses, or a fetch the transfer refuses, is refused, with why.
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

		if (!string.IsNullOrEmpty(net.ServerMapHash))
		{
			return TakeServerDataMap(net, out error);
		}

		if (net.ServerMapId.Length == 0)
		{
			error = "the host named no map.";
			return MapAnswer.Refused;
		}

		if (!MapCatalog.TryFind(net.ServerMapId, out MapInfo map) || map.IsData)
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

	/// <summary>The data-map half of <see cref="TakeServerMap"/>: have it, or fetch it (docs/LEVEL_EDITOR.md §9.3).</summary>
	private MapAnswer TakeServerDataMap(NetworkManager net, out string error)
	{
		error = null;
		string hash = net.ServerMapHash;
		string name = string.IsNullOrEmpty(net.ServerMapName) ? Gdpyr.Sim.Level.MapTransfer.Short(hash) : net.ServerMapName;
		MapTransferService transfer = net.MapTransfer;

		if (transfer.Fetch is { } fetch && fetch.Hash == hash)
		{
			switch (fetch.State)
			{
				case Gdpyr.Sim.Level.MapReceiveState.Receiving:
					return MapAnswer.Fetching;

				case Gdpyr.Sim.Level.MapReceiveState.Refused:
					error = $"could not fetch the host's map '{name}': {fetch.Error}";
					return MapAnswer.Refused;
			}

			// Hashed, read and validated as it arrived (§6.10): kept, then played from what arrived.
			if (!Gdpyr.Level.MapLibrary.TryCache(fetch.File, fetch.Document, out MapInfo fetched, out string unkept))
			{
				GD.PushWarning($"[maps] '{name}' is played but not kept: {unkept}");
			}
			else
			{
				GD.Print($"[maps] kept '{name}' as {fetched.Path}");
			}

			Session.SelectResolved(fetched, fetch.Document, fetch.File);
			return MapAnswer.Taken;
		}

		if (Gdpyr.Level.MapLibrary.TryFindHash(hash, out MapInfo map, out Gdpyr.Sim.Level.MapDocument document,
			out byte[] bytes, out Gdpyr.Level.MapLibrary.Source source, out string unplayable, out string note))
		{
			GD.Print($"[maps] the host's map '{name}' is in {SourceName(source)}: {map.Path}, sha-256 {hash}; nothing to fetch");
			Session.SelectResolved(map, document, bytes);
			return MapAnswer.Taken;
		}

		if (source != Gdpyr.Level.MapLibrary.Source.None)
		{
			error = $"the host is playing '{name}', and this build's copy of it does not play: {unplayable}";
			return MapAnswer.Refused;
		}

		if (note != null)
		{
			GD.PushWarning($"[maps] {note}; fetching it again");
		}

		if (!transfer.BeginFetch(hash, net.ServerMapLength, name, out string refused))
		{
			error = $"cannot fetch the host's map '{name}': {refused}";
			return MapAnswer.Refused;
		}

		return MapAnswer.Fetching;
	}

	private static string SourceName(Gdpyr.Level.MapLibrary.Source source) => source switch
	{
		Gdpyr.Level.MapLibrary.Source.Bundled => "this build",
		Gdpyr.Level.MapLibrary.Source.Library => "this process's own maps",
		_ => "the cache",
	};

	/// <summary>The status line while a map arrives.</summary>
	private static string FetchProgress(NetworkManager net) =>
		net.MapTransfer.Fetch is { } fetch
			? Gdpyr.Sim.Level.MapTransfer.Progress(net.MapTransfer.FetchName, fetch.Received, fetch.Chunks)
			: "fetching the host's map…";

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
			case MapAnswer.Fetching:
				return;

			case MapAnswer.Refused:
				// The link is dropped before the process goes, so the server hears it now and not
				// when it gives up on a silent peer (docs/LEVEL_EDITOR.md §9.3).
				_leaving = true;
				GD.PrintErr($"gdpyr: {error}");
				PlayerManager.Instance?.ForgetClientRoster();
				net.AbandonConnection();
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

		return new HostSettings(SelectedMap(), (int)_roundMinutes.Value, (int)_groundTickets.Value,
			(int)_groundForceSize.Value, (int)_strategistSize.Value);
	}

	private MapInfo SelectedMap()
	{
		int index = _mapPicker.Selected;
		return index >= 0 && index < _pickable.Count ? _pickable[index] : MapCatalog.Default;
	}

	/// <summary>
	/// The picker's rows (docs/LEVEL_EDITOR.md §9.1): the catalog, then every map of one's own that
	/// reads and validates (LE-D16), asked again each time the panel opens, since the editor may have
	/// saved one since. A client that lacks one of one's own fetches it from this host (§9.3).
	/// </summary>
	private void FillMapPicker(MapInfo selected)
	{
		_pickable.Clear();
		_pickable.AddRange(MapCatalog.All);
		_pickable.AddRange(Gdpyr.Level.MapLibrary.PlayableOwnMaps());

		_mapPicker.Clear();
		int chosen = 0;
		for (int i = 0; i < _pickable.Count; i++)
		{
			MapInfo map = _pickable[i];
			bool own = map.IsData && string.IsNullOrEmpty(map.Id);
			_mapPicker.AddItem(own ? $"{map.Name} — {System.IO.Path.GetFileNameWithoutExtension(map.Path)}, yours" : map.Name, i);
			if (map.Kind == selected.Kind && map.Id == selected.Id && map.Path == selected.Path)
			{
				chosen = i;
			}
		}

		_mapPicker.Selected = chosen;
		DescribeSelectedMap();
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
			FillMapPicker(SelectedMap());
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
	/// The map editor (docs/LEVEL_EDITOR.md §8.1): its list of maps, or the one `--edit` named.
	/// Nothing is played there; leaving it comes back to this menu.
	/// </summary>
	private void EnterEditor()
	{
		if (_leaving)
		{
			return;
		}

		_leaving = true;
		Error error = GetTree().ChangeSceneToFile(Gdpyr.MapEditor.MapEditorRoot.ScenePath);
		if (error != Error.Ok)
		{
			GD.PrintErr($"[menu] could not load {Gdpyr.MapEditor.MapEditorRoot.ScenePath}: {error}");
		}
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
		_editor.Disabled = busy;
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

		_editor = NewButton(buttons, "Map editor");
		_editor.Pressed += () =>
		{
			Gdpyr.MapEditor.MapEditorRoot.OpenOnEnter = null;
			EnterEditor();
		};

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
		_mapPicker.ItemSelected += _ => DescribeSelectedMap();
		AddRow(grid, "Map", _mapPicker);

		_mapDescription = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
		AddRow(grid, string.Empty, _mapDescription);
		FillMapPicker(defaults.Map);

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
		if (index < 0 || index >= _pickable.Count)
		{
			_mapDescription.Text = string.Empty;
			return;
		}

		MapInfo map = _pickable[index];
		_mapDescription.Text = map.IsData && string.IsNullOrEmpty(map.Id)
			? $"{map.Description}\none of your own maps: a player who has not got it fetches it from you as they join."
			: map.Description;
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
