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
/// </summary>
public partial class MainMenu : Control
{
	/// <summary>How often a query goes out while the menu is up.</summary>
	private const float RefreshIntervalSeconds = 2.5f;

	/// <summary>How often the rows are rebuilt. Faster than this only moves the cursor about.</summary>
	private const float RedrawIntervalSeconds = 0.25f;

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
	private Button _refresh;

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
		// and there is no server to pick (docs/DEMOS.md §5.1).
		if (Bootstrap.IsDedicatedServer || Bootstrap.Options.Mode != LaunchMode.Offline
			|| Bootstrap.Options.PlayDemo != null)
		{
			// No peer, no packets: a replay is a file being read, and the transport
			// would otherwise sit waiting for a menu that is not going to be built.
			if (Bootstrap.Options.PlayDemo != null)
			{
				NetworkManager.Instance?.PlayOffline();
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
		if (_browser == null || _leaving)
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

		WatchConnection();
	}

	// ---- the two things the menu does --------------------------------------

	/// <summary>
	/// Waits out the handshake. ENet answers on its own schedule and the transport
	/// has no callback to hang a scene change off, so the menu asks every frame
	/// whether the answer has arrived (docs/NETCODE.md §2).
	/// </summary>
	private void WatchConnection()
	{
		NetworkManager net = NetworkManager.Instance;
		if (net == null || !net.IsClient || net.IsConnecting)
		{
			return;
		}

		if (net.Connected)
		{
			_status.Text = "connected";
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

	private void Join(string host, int port)
	{
		NetworkManager net = NetworkManager.Instance;
		if (net == null || _leaving)
		{
			return;
		}

		SetBusy(true);
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

	private void Host()
	{
		NetworkManager net = NetworkManager.Instance;
		int port = Bootstrap.Options.Port;

		if (net == null || _leaving)
		{
			return;
		}

		if (!net.HostListen(port))
		{
			_status.Text = $"could not listen on UDP {port}: something else is already on it."
				+ " Launch with `-- --listen <port>` to host on another one.";
			return;
		}

		EnterWorld();
	}

	private void Offline()
	{
		NetworkManager.Instance?.PlayOffline();
		EnterWorld();
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

		_host = NewButton(buttons, "Host");
		_host.Pressed += Host;

		_offline = NewButton(buttons, "Practice offline");
		_offline.Pressed += Offline;

		Button quit = NewButton(buttons, "Quit");
		quit.Pressed += () => GetTree().Quit();

		_status = new Label { Text = "searching…", AutowrapMode = TextServer.AutowrapMode.WordSmart };
		_status.CustomMinimumSize = new Vector2(660f, 48f);
		column.AddChild(_status);

		_join.GrabFocus();
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
