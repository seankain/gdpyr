using System.Text;
using Gdpyr.Net;
using Gdpyr.Sim;
using Gdpyr.Sim.AiDebug;
using Godot;

namespace Gdpyr.Ui.Spectator;

/// <summary>
/// The AI debugger's panel (docs/AI_DEBUG.md §4): a tab per inspected agent and one
/// for the computer strategist, each showing who it is, its plan on one line, its
/// facts, its domain as a text tree or a graph with the plan marked on it, and its
/// planner's recent history. The switches along the top are the F-keys'.
///
/// It only draws: what is selected and which views are on is
/// <see cref="SpectatorView"/>'s, and the text comes from the same engine-free
/// renderers the console prints with (<see cref="HtnText"/>, <see cref="AiDebugText"/>,
/// <see cref="SquadText"/>). It rebuilds when a new frame arrives or something it
/// shows changes, not every render frame.
/// </summary>
public partial class AiDebugPanel : CanvasLayer
{
	private const float Width = 640f;
	private const int TextSize = 13;

	private static readonly Font Mono = new SystemFont
	{
		FontNames = new[] { "DejaVu Sans Mono", "Consolas", "Menlo", "Liberation Mono", "Courier New", "monospace" },
	};

	private static readonly Font MonoBold = new SystemFont
	{
		FontNames = new[] { "DejaVu Sans Mono", "Consolas", "Menlo", "Liberation Mono", "Courier New", "monospace" },
		FontWeight = 700,
	};

	private SpectatorView _view;

	private Control _root;
	private Label _hidden;
	private RichTextLabel _status;
	private CheckButton _labels;
	private CheckButton _strategist;
	private CheckButton _graphMode;
	private CheckButton _markers;
	private CheckButton _fold;
	private TabBar _tabs;
	private RichTextLabel _header;
	private RichTextLabel _plan;
	private RichTextLabel _facts;
	private RichTextLabel _squads;
	private HtnGraphView _graph;
	private RichTextLabel _tree;
	private RichTextLabel _history;

	private string _tabsKey;
	private string _renderKey;
	private bool _syncing;

	public override void _Ready()
	{
		Layer = 2;
		_view = GetParent<SpectatorView>();
		Build();
	}

	/// <summary>Called by the view once a render frame.</summary>
	public void Render(AiDebugFrame frame)
	{
		_root.Visible = _view.PanelVisible;
		_hidden.Visible = !_view.PanelVisible;
		if (!_view.PanelVisible)
		{
			return;
		}

		SyncToolbar();
		SyncTabs();

		string key = $"{frame?.Tick}|{_view.Tab}|{_view.Shown}|{_view.Graph}|{_view.Collapse}|{_view.Strategist}"
			+ $"|{_view.Selected.Count}|{PlayerManager.Instance?.SpectatorAiDebug}|{AgeBucket()}";
		if (key == _renderKey)
		{
			return;
		}

		_renderKey = key;
		_status.Text = Status(frame);

		bool strategistTab = _view.Strategist && _view.Tab >= _view.Selected.Count;
		if (strategistTab)
		{
			RenderStrategist(frame);
		}
		else if (_view.Tab < _view.Selected.Count)
		{
			RenderInspect(frame, _view.Selected[_view.Tab]);
		}
		else
		{
			RenderEmpty();
		}
	}

	// ---- content -----------------------------------------------------------

	private void RenderEmpty()
	{
		_squads.Visible = false;
		Put(_header, "[b]nothing selected[/b]\nclick a bot or a unit to see its plan; shift-click to add one;"
			+ " F6 for the computer strategist's commander, F5 for every agent's task at once");
		Put(_plan, null);
		Put(_facts, null);
		ShowTree(null, null);
		Put(_history, null);
	}

	private void RenderInspect(AiDebugFrame frame, AiEntityRef target)
	{
		_squads.Visible = false;
		AiInspect inspect = frame?.Find(target);
		if (inspect == null)
		{
			Put(_header, $"[b]{HtnText.Escape(AiEntities.NameOf(target))}[/b]\n"
				+ (frame == null ? "waiting for the server…" : "waiting for its plan…"));
			Put(_plan, null);
			Put(_facts, null);
			ShowTree(null, null);
			Put(_history, null);
			return;
		}

		Put(_header, AiDebugText.Header(inspect, frame, HtnTextStyle.Rich));
		ShowPlan(frame, inspect.Trace, AiDebugText.MapFor(inspect, frame, out string problem), problem);
	}

	private void RenderStrategist(AiDebugFrame frame)
	{
		_squads.Visible = true;
		if (frame == null || frame.Commanders.Count == 0)
		{
			Put(_squads, frame == null
				? "waiting for the server…"
				: frame.BotAi == BotAi.Legacy
					? "the computer strategist runs --bot-ai legacy: it has no commander to show"
					: "no computer strategist is in a seat (a person is strategist, or the bots are off)");
			Put(_header, null);
			Put(_plan, null);
			Put(_facts, null);
			ShowTree(null, null);
			Put(_history, null);
			return;
		}

		var table = new StringBuilder();
		foreach (AiCommander commander in frame.Commanders)
		{
			if (table.Length > 0)
			{
				table.Append("\n\n");
			}

			table.Append(SquadText.Table(commander, HtnTextStyle.Rich, link: true));
		}

		Put(_squads, table.ToString());

		AiEntityRef shown = _view.StrategistSquad;
		AiSquad squad = shown.IsValid ? frame.Commander(shown.Id)?.Squad(shown.Slot) : null;
		if (squad == null)
		{
			Put(_header, "[color=#8a8f98]click a squad's row, or its ring in the world, to see its plan[/color]");
			Put(_plan, null);
			Put(_facts, null);
			ShowTree(null, null);
			Put(_history, null);
			return;
		}

		string color = AiGoals.Color(AiGoals.Family(HtnDomainKind.Squad, squad.Goal));
		string flags = SquadText.Flags(squad);
		Put(_header, $"[b]{HtnText.Escape(shown.ToString())}[/b] · {SquadText.RoleName(squad)} · "
			+ $"{(Gdpyr.Sim.Htn.CommandTask)squad.Task} → {HtnText.Escape(SquadText.Target(squad))}\n"
			+ $"goal [color=#{color}]{AiGoals.Name(HtnDomainKind.Squad, squad.Goal)}[/color]"
			+ $" · {squad.Members.Count} units · strength {SquadText.Strength(squad)}"
			+ $" · {(Gdpyr.Sim.Htn.SquadPhase)squad.Phase}" + (flags.Length > 0 ? $" · {flags}" : string.Empty));

		HtnDomainMap map = frame.MapFor(HtnDomainKind.Squad);
		ShowPlan(frame, squad.Trace, map, map == null
			? "this build numbers the squad domain differently from the server's: run the same commit on both"
			: null);
	}

	private void ShowPlan(AiDebugFrame frame, HtnTrace trace, HtnDomainMap map, string problem)
	{
		if (map == null || trace == null || !trace.HasPlan)
		{
			Put(_plan, problem == null ? null : $"[color=#ff6b6b]{HtnText.Escape(problem)}[/color]");
			Put(_facts, null);
			ShowTree(null, null);
			Put(_history, null);
			return;
		}

		uint tick = frame.Tick;
		Put(_plan, HtnText.Plan(map, trace, HtnTextStyle.Rich, tick));
		Put(_facts, "[color=#8a8f98]facts[/color]\n" + HtnText.Facts(HtnSchema.For(map.Kind), trace, HtnTextStyle.Rich));
		ShowTree(map, trace);
		Put(_history, "[color=#8a8f98]history, newest first[/color]\n"
			+ HtnText.History(map, trace, HtnTextStyle.Rich, tick));
	}

	private void ShowTree(HtnDomainMap map, HtnTrace trace)
	{
		if (map == null)
		{
			_graph.Visible = false;
			_graph.Display(null, null, false);
			Put(_tree, null);
			return;
		}

		if (_view.Graph)
		{
			_graph.Visible = true;
			_graph.Display(map, trace, _view.Collapse);
			Put(_tree, HtnText.Legend(HtnTextStyle.Rich) + "   [color=#8a8f98]hover a box for its conditions[/color]");
			return;
		}

		_graph.Visible = false;
		var options = new HtnTreeOptions { Conditions = true, Collapse = _view.Collapse, Tick = PlayerManager.Instance?.AiFrame?.Tick ?? 0 };
		Put(_tree, HtnText.Legend(HtnTextStyle.Rich) + "\n" + HtnText.Tree(map, trace, HtnTextStyle.Rich, options));
	}

	private static void Put(RichTextLabel label, string text)
	{
		label.Visible = !string.IsNullOrEmpty(text);
		if (label.Visible && label.Text != text)
		{
			label.Text = text;
		}
	}

	private string Status(AiDebugFrame frame)
	{
		PlayerManager players = PlayerManager.Instance;
		var text = new StringBuilder("[b]AI debugger[/b]  ");

		if (players is { SpectatorAiDebug: false })
		{
			text.Append("[color=#ff6b6b]this server does not send its bots' plans: start it with --ai-debug[/color]");
			return text.ToString();
		}

		if (frame == null)
		{
			text.Append(_view.Selected.Count == 0 && !_view.Strategist && !_view.LabelsAll
				? "[color=#8a8f98]select something[/color]"
				: "[color=#8a8f98]waiting for the first frame…[/color]");
			return text.ToString();
		}

		text.Append($"[color=#8a8f98]bots {BotAiNames.Name(frame.BotAi)} · tick {frame.Tick}");
		float age = (Time.GetTicksMsec() - players.AiFrameReceivedMsec) / 1000f;
		if (age > 1f)
		{
			text.Append($"[/color] · [color=#ff6b6b]frame {age:0}s old");
		}

		text.Append("[/color]");
		if (frame.BotAi == BotAi.Legacy)
		{
			text.Append("\n[color=#ffd34d]--bot-ai legacy: no HTN runs, so there are no plans to show[/color]");
		}

		return text.ToString();
	}

	private static int AgeBucket()
	{
		PlayerManager players = PlayerManager.Instance;
		return players == null || players.AiFrameReceivedMsec == 0
			? 0
			: (int)((Time.GetTicksMsec() - players.AiFrameReceivedMsec) / 1000);
	}

	// ---- toolbar and tabs -----------------------------------------------------

	private void SyncToolbar()
	{
		_syncing = true;
		_labels.SetPressedNoSignal(_view.LabelsAll);
		_strategist.SetPressedNoSignal(_view.Strategist);
		_graphMode.SetPressedNoSignal(_view.Graph);
		_markers.SetPressedNoSignal(_view.WorldMarkers);
		_fold.SetPressedNoSignal(_view.Collapse);
		_syncing = false;
	}

	private void SyncTabs()
	{
		var key = new StringBuilder();
		foreach (AiEntityRef target in _view.Selected)
		{
			key.Append(target).Append(';');
		}

		key.Append(_view.Strategist ? "S" : "-");
		string tabsKey = key.ToString();

		if (tabsKey != _tabsKey)
		{
			_tabsKey = tabsKey;
			_syncing = true;
			while (_tabs.TabCount > 0)
			{
				_tabs.RemoveTab(_tabs.TabCount - 1);
			}

			foreach (AiEntityRef target in _view.Selected)
			{
				_tabs.AddTab(AiEntities.NameOf(target));
			}

			if (_view.Strategist)
			{
				_tabs.AddTab("strategist");
			}

			_tabs.Visible = _tabs.TabCount > 0;
			_syncing = false;
		}

		if (_tabs.TabCount > 0 && _tabs.CurrentTab != Mathf.Clamp(_view.Tab, 0, _tabs.TabCount - 1))
		{
			_syncing = true;
			_tabs.CurrentTab = Mathf.Clamp(_view.Tab, 0, _tabs.TabCount - 1);
			_syncing = false;
		}
	}

	private void Build()
	{
		_root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
		_root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		AddChild(_root);

		_hidden = new Label { Text = "F4: AI panel", Visible = false, Position = new Vector2(12f, 12f) };
		_hidden.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, 0.6f));
		AddChild(_hidden);

		var panel = new PanelContainer
		{
			AnchorLeft = 1f,
			AnchorRight = 1f,
			AnchorTop = 0f,
			AnchorBottom = 1f,
			OffsetLeft = -Width - 12f,
			OffsetRight = -12f,
			OffsetTop = 12f,
			OffsetBottom = -12f,
		};
		panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
		{
			BgColor = new Color(0.05f, 0.06f, 0.07f, 0.84f),
			CornerRadiusTopLeft = 6,
			CornerRadiusTopRight = 6,
			CornerRadiusBottomLeft = 6,
			CornerRadiusBottomRight = 6,
			ContentMarginLeft = 10,
			ContentMarginRight = 10,
			ContentMarginTop = 8,
			ContentMarginBottom = 8,
		});
		_root.AddChild(panel);

		var column = new VBoxContainer();
		column.AddThemeConstantOverride("separation", 6);
		panel.AddChild(column);

		_status = NewText(column, mono: false);

		var toolbar = new HFlowContainer();
		column.AddChild(toolbar);
		_labels = Toggle(toolbar, "every agent  F5", on => _view.SetLabelsAll(on));
		_strategist = Toggle(toolbar, "strategist  F6", on => _view.SetStrategist(on));
		_graphMode = Toggle(toolbar, "graph  F7", on => _view.SetGraph(on));
		_markers = Toggle(toolbar, "world  F8", on => _view.SetWorldMarkers(on));
		_fold = Toggle(toolbar, "fold  F9", on => _view.SetCollapse(on));
		var clear = new Button { Text = "clear  ⌫", FocusMode = Control.FocusModeEnum.None };
		clear.Pressed += () => _view.ClearSelection();
		toolbar.AddChild(clear);

		_tabs = new TabBar { ClipTabs = true, FocusMode = Control.FocusModeEnum.None, Visible = false };
		_tabs.TabChanged += index =>
		{
			if (!_syncing)
			{
				_view.Tab = (int)index;
			}
		};
		column.AddChild(_tabs);

		var scroll = new ScrollContainer
		{
			SizeFlagsVertical = Control.SizeFlags.ExpandFill,
			HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
		};
		column.AddChild(scroll);

		var content = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		content.AddThemeConstantOverride("separation", 10);
		scroll.AddChild(content);

		// The commander's table above whichever squad of it is being shown.
		_squads = NewText(Sideways(content), mono: true, wrap: false);
		_squads.MetaClicked += meta => OnSquadLink(meta.AsString());
		_header = NewText(content, mono: false);
		_plan = NewText(content, mono: false);
		_facts = NewText(content, mono: true);

		// The graph and the tree are as wide as the domain is deep, and a tree whose
		// lines wrap is no longer a tree: they scroll sideways instead.
		Control wide = Sideways(content);
		var trees = new VBoxContainer();
		wide.AddChild(trees);
		_graph = new HtnGraphView { Visible = false };
		trees.AddChild(_graph);
		_tree = NewText(trees, mono: true, wrap: false);
		_history = NewText(content, mono: true);

		var help = NewText(column, mono: false);
		help.Text = "[color=#8a8f98]click inspect · shift-click add · right-drag look · WASD space crouch fly"
			+ " · F follow · Tab next player · ⌫ clear · F3 play · F4 hide[/color]";
		help.AddThemeFontSizeOverride("normal_font_size", 11);
	}

	private void OnSquadLink(string meta)
	{
		// squad:<strategist peer>:<slot>, from SquadText.Table.
		string[] parts = meta.Split(':');
		if (parts.Length == 3 && parts[0] == "squad" && int.TryParse(parts[1], out int peer)
			&& int.TryParse(parts[2], out int slot))
		{
			_view.ShowSquad(AiEntityRef.Squad(peer, slot));
		}
	}

	private CheckButton Toggle(Control parent, string text, System.Action<bool> changed)
	{
		var button = new CheckButton { Text = text, FocusMode = Control.FocusModeEnum.None };
		button.Toggled += on =>
		{
			if (!_syncing)
			{
				changed(on);
			}
		};
		parent.AddChild(button);
		return button;
	}

	/// <summary>A strip that scrolls sideways and is as tall as what is in it.</summary>
	private static Control Sideways(Control parent)
	{
		var scroll = new ScrollContainer
		{
			HorizontalScrollMode = ScrollContainer.ScrollMode.Auto,
			VerticalScrollMode = ScrollContainer.ScrollMode.Disabled,
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
		};
		parent.AddChild(scroll);
		return scroll;
	}

	private static RichTextLabel NewText(Control parent, bool mono, bool wrap = true)
	{
		var label = new RichTextLabel
		{
			BbcodeEnabled = true,
			FitContent = true,
			ScrollActive = false,
			SelectionEnabled = true,
			AutowrapMode = wrap ? TextServer.AutowrapMode.WordSmart : TextServer.AutowrapMode.Off,
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
			MouseFilter = Control.MouseFilterEnum.Pass,
		};

		label.AddThemeFontSizeOverride("normal_font_size", TextSize);
		label.AddThemeFontSizeOverride("bold_font_size", TextSize);
		if (mono)
		{
			label.AddThemeFontOverride("normal_font", Mono);
			label.AddThemeFontOverride("bold_font", MonoBold);
		}

		parent.AddChild(label);
		return label;
	}
}
