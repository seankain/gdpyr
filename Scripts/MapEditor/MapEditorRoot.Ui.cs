using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Gdpyr.Core;
using Gdpyr.Level;
using Gdpyr.Sim.Level;
using Godot;

namespace Gdpyr.MapEditor;

/// <summary>
/// The editor's screen (docs/LEVEL_EDITOR.md §8.2), built in code as the menu and the HUDs
/// are: a bar across the top — the map's name and whether it is saved, Validate, Test, Save,
/// Save as, Export, Exit — the tools down the left, the terrain tool's settings on the right,
/// and under the view the cursor, the readout, the validator's count and what was last said.
/// Before a map is open, the list of one's own maps and those to clone (§8.1). What each
/// widget says is <see cref="MapEditorControls"/>'.
/// </summary>
public partial class MapEditorRoot
{
	private const float RailWidth = 130f;
	private const float PanelWidth = 340f;
	private const float BarHeight = 40f;
	private const float StatusHeight = 30f;

	private CanvasLayer _layer;
	private Control _screen;
	private Button _title;
	private Label _cursorLabel;
	private Label _readoutLabel;
	private Button _issuesButton;
	private Label _messageLabel;
	private readonly Button[] _toolButtons = new Button[4];
	private readonly List<Button> _presetButtons = new();
	private Label _brushLabel;
	private VBoxContainer _brushFields;
	private readonly Dictionary<BrushField, HSlider> _brushSliders = new();
	private readonly Dictionary<BandNumber, HSlider> _bandSliders = new();
	private OptionButton _overlayPicker;
	private VBoxContainer _terrainPanel;
	private VBoxContainer _prefabPanel;
	private readonly Dictionary<ushort, Button> _paletteButtons = new();
	private CheckBox _snapCheck;
	private Label _ghostLabel;
	private VBoxContainer _selectionPanel;
	private Label _selectionLabel;
	private VBoxContainer _parameterFields;
	private PanelContainer _issuesPanel;
	private ItemList _issuesList;

	private Control _listPanel;
	private ItemList _maps;
	private Label _listStatus;
	private readonly List<(bool Own, string Path, string Name)> _rows = new();

	private ConfirmationDialog _namePrompt;
	private LineEdit _nameEdit;
	private Action<string> _named;
	private ConfirmationDialog _autosaveDialog;
	private ConfirmationDialog _exitDialog;
	private bool _exitToList;
	private ConfirmationDialog _settingsDialog;
	private LineEdit _settingsName;
	private LineEdit _settingsAuthor;
	private TextEdit _settingsDescription;
	private FileDialog _exportDialog;

	/// <summary>The list is up, or a dialog: keys and clicks are theirs.</summary>
	public bool Modal =>
		(_listPanel?.Visible ?? false) || (_namePrompt?.Visible ?? false) || (_autosaveDialog?.Visible ?? false)
		|| (_exitDialog?.Visible ?? false) || (_settingsDialog?.Visible ?? false) || (_exportDialog?.Visible ?? false);

	/// <summary>The issues the list shows, as the validator last found them.</summary>
	public IReadOnlyList<MapIssue> ListedIssues => Editing == null ? Array.Empty<MapIssue>() : ShownReport.Issues;

	/// <summary>The deep check's report while it is of the map as it is; else the validator's.</summary>
	public MapReport ShownReport => DeepIsCurrent ? DeepReport : Editing.Report;

	private void BuildUi()
	{
		_layer = new CanvasLayer { Name = "Ui", Layer = 5 };
		AddChild(_layer);
		_screen = new Control { Name = "Screen", MouseFilter = Control.MouseFilterEnum.Ignore };
		_screen.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		_layer.AddChild(_screen);

		BuildBar();
		BuildRail();
		BuildPanel();
		BuildStatus();
		BuildIssues();
		BuildList();
		BuildDialogs();
	}

	// ---- the bar --------------------------------------------------------------------------------

	private void BuildBar()
	{
		PanelContainer bar = Panel(Control.LayoutPreset.TopWide);
		bar.OffsetBottom = BarHeight;
		var row = new HBoxContainer();
		row.AddThemeConstantOverride("separation", 8);
		bar.AddChild(row);

		_title = new Button
		{
			Flat = true,
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
			Alignment = HorizontalAlignment.Left,
			TooltipText = "the map's name, author and description",
		};
		_title.Pressed += ShowSettings;
		row.AddChild(_title);

		BarButton(row, "Maps", "back to the list of maps", () => CloseMap());
		BarButton(row, "Validate", "what the validator finds (§6.9); a click on one flies to it", ShowIssues);
		BarButton(row, $"Test ({MapEditorControls.KeyOf(EditorAction.Test)})", "save, and play it in a second process",
			() => Test(out _));
		BarButton(row, $"Save ({MapEditorControls.KeyOf(EditorAction.Save)})", "save as one's own map", () => Save(out _));
		BarButton(row, "Save as…", "save under another name: a fork of this map", () =>
			AskName("Save as", FileName + "-2", name => SaveAs(name, out _)));
		BarButton(row, "Export…", "copy the saved file somewhere else", ShowExport);
		BarButton(row, "Exit", "back to the main menu", () => Exit());
	}

	private static void BarButton(Container row, string text, string tooltip, Action pressed)
	{
		var button = new Button { Text = text, TooltipText = tooltip, FocusMode = Control.FocusModeEnum.None };
		button.Pressed += pressed;
		row.AddChild(button);
	}

	// ---- the rail ---------------------------------------------------------------------------------

	private void BuildRail()
	{
		PanelContainer rail = Panel(Control.LayoutPreset.LeftWide);
		rail.OffsetTop = BarHeight;
		rail.OffsetBottom = -StatusHeight;
		rail.OffsetRight = RailWidth;
		var column = new VBoxContainer();
		rail.AddChild(column);
		foreach (EditorTool tool in Enum.GetValues<EditorTool>())
		{
			var button = new Button
			{
				Text = MapEditorControls.ToolLabel(tool),
				ToggleMode = true,
				Disabled = !MapEditorControls.IsBuilt(tool),
				TooltipText = MapEditorControls.IsBuilt(tool) ? string.Empty : MapEditorControls.NotBuilt(tool),
				FocusMode = Control.FocusModeEnum.None,
				Alignment = HorizontalAlignment.Left,
			};
			EditorTool chosen = tool;
			button.Pressed += () => SelectTool(chosen);
			column.AddChild(button);
			_toolButtons[(int)tool] = button;
		}
	}

	// ---- the terrain tool's panel ------------------------------------------------------------------

	private void BuildPanel()
	{
		PanelContainer panel = Panel(Control.LayoutPreset.RightWide);
		panel.OffsetTop = BarHeight;
		panel.OffsetBottom = -StatusHeight;
		panel.OffsetLeft = -PanelWidth;
		var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
		panel.AddChild(scroll);
		var column = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		column.AddThemeConstantOverride("separation", 6);
		scroll.AddChild(column);

		_terrainPanel = new VBoxContainer();
		_terrainPanel.AddThemeConstantOverride("separation", 6);
		column.AddChild(_terrainPanel);
		BuildTerrainPanel(_terrainPanel);

		_prefabPanel = new VBoxContainer { Visible = false };
		_prefabPanel.AddThemeConstantOverride("separation", 6);
		column.AddChild(_prefabPanel);
		BuildPrefabPanel(_prefabPanel);

		column.AddChild(new HSeparator());
		column.AddChild(Heading($"Overlay ({MapEditorControls.KeyOf(EditorAction.NextOverlay)})"));
		_overlayPicker = new OptionButton { FocusMode = Control.FocusModeEnum.None };
		foreach (TerrainOverlay overlay in Enum.GetValues<TerrainOverlay>())
		{
			_overlayPicker.AddItem(MapEditorControls.Name(overlay), (int)overlay);
		}

		_overlayPicker.ItemSelected += index => SetOverlay((TerrainOverlay)_overlayPicker.GetItemId((int)index));
		column.AddChild(_overlayPicker);

		column.AddChild(new HSeparator());
		column.AddChild(Heading("Keys"));
		column.AddChild(new Label
		{
			Text = "WASD fly, Space or E up, Q down, Shift faster; middle button looks\n"
				+ "1 terrain · 2 prefabs · O overlay · T straight down\n"
				+ "[ ] radius · - = rate or strength · , . core or grade\n"
				+ "R, Shift+R turn a prefab 15° · G metre grid · Del take up\n"
				+ "Esc drop a stroke, a ramp or a selection\n"
				+ "Ctrl+Z undo · Ctrl+Y redo · Ctrl+S save · F5 test",
			AutowrapMode = TextServer.AutowrapMode.WordSmart,
		});
	}

	private void BuildTerrainPanel(VBoxContainer column)
	{
		column.AddChild(Heading("Brushes: raise on the left button, lower on the right"));
		var presets = new GridContainer { Columns = 2 };
		column.AddChild(presets);
		for (int i = 0; i < TerrainBrush.Presets.Length; i++)
		{
			int index = i;
			var button = new Button
			{
				Text = TerrainBrush.Presets[i].Name,
				ToggleMode = true,
				SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
				FocusMode = Control.FocusModeEnum.None,
				TooltipText = MapEditorControls.Describe(TerrainBrush.Presets[i].Settings),
			};
			button.Pressed += () => SelectPreset(index);
			presets.AddChild(button);
			_presetButtons.Add(button);
		}

		_brushLabel = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
		column.AddChild(_brushLabel);
		_brushFields = new VBoxContainer();
		column.AddChild(_brushFields);

		column.AddChild(new HSeparator());
		column.AddChild(Heading("Water and bands"));
		foreach (BandNumber number in Enum.GetValues<BandNumber>())
		{
			BandNumber which = number;
			_bandSliders[number] = Slider(column, MapEditorControls.RangeOf(number), v => SetBand(which, v));
		}
	}

	/// <summary>The prefab tool's panel (§8.4): the palette by kind with counts, the grid, the ghost's answer, the selection.</summary>
	private void BuildPrefabPanel(VBoxContainer column)
	{
		column.AddChild(Heading("Prefabs: a click places one and its pad; a click on one selects it"));
		foreach (PrefabKind kind in MapEditorControls.PaletteKinds)
		{
			column.AddChild(new Label { Text = MapEditorControls.Name(kind) });

			// One a row: an entry's count against its limit is too long to share one (LE5's pictures).
			var grid = new GridContainer { Columns = 1 };
			column.AddChild(grid);
			foreach (PrefabSpec spec in MapEditorControls.Palette(kind))
			{
				ushort id = spec.Id;
				var button = new Button
				{
					ToggleMode = true,
					SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
					FocusMode = Control.FocusModeEnum.None,
					ClipText = true,
				};
				button.Pressed += () => SelectPrefab(id);
				grid.AddChild(button);
				_paletteButtons[id] = button;
			}
		}

		_snapCheck = new CheckBox { Text = $"Snap to the metre grid ({MapEditorControls.KeyOf(EditorAction.SnapToGrid)})", FocusMode = Control.FocusModeEnum.None };
		_snapCheck.Toggled += on =>
		{
			if (on != SnapToGrid)
			{
				ToggleSnap();
			}
		};
		column.AddChild(_snapCheck);
		_ghostLabel = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
		column.AddChild(_ghostLabel);

		_selectionPanel = new VBoxContainer { Visible = false };
		column.AddChild(_selectionPanel);
		_selectionLabel = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
		_selectionPanel.AddChild(_selectionLabel);
		var row = new HBoxContainer();
		_selectionPanel.AddChild(row);
		var left = new Button { Text = "Turn ↺ (R)", FocusMode = Control.FocusModeEnum.None };
		left.Pressed += () => TurnSelected(1);
		row.AddChild(left);
		var right = new Button { Text = "Turn ↻ (Shift+R)", FocusMode = Control.FocusModeEnum.None };
		right.Pressed += () => TurnSelected(-1);
		row.AddChild(right);
		var takeUp = new Button { Text = "Take up (Del)", FocusMode = Control.FocusModeEnum.None };
		takeUp.Pressed += () => TakeUpSelected();
		_selectionPanel.AddChild(takeUp);
		_parameterFields = new VBoxContainer();
		_selectionPanel.AddChild(_parameterFields);
	}

	/// <summary>The palette's counts and selection, the grid, and the selected object's panel.</summary>
	private void RefreshPrefabPanel()
	{
		if (_prefabPanel == null || Editing == null)
		{
			return;
		}

		foreach ((ushort id, Button button) in _paletteButtons)
		{
			button.Text = MapEditorControls.CountLabel(Editing.Document, PrefabSpecs.Get(id));
			button.SetPressedNoSignal(id == Prefab && Selected < 0);
		}

		_snapCheck.SetPressedNoSignal(SnapToGrid);
		_selectionPanel.Visible = Selected >= 0;
		foreach (Node child in _parameterFields.GetChildren())
		{
			_parameterFields.RemoveChild(child);
			child.QueueFree();
		}

		if (Selected < 0)
		{
			return;
		}

		MapObject placed = Editing.Document.Objects[Selected];
		PrefabSpec spec = PrefabSpecs.Get(placed.PrefabId);
		_selectionLabel.Text = $"selected: {spec.Name} {Selected}, at ({placed.Position.X:0.0}, {placed.Position.Z:0.0}),"
			+ $" turned {Mathf.RadToDeg(placed.YawRadians):0}°";
		foreach (PrefabParameter parameter in spec.Parameters)
		{
			byte key = parameter.Key;
			HSlider slider = Slider(_parameterFields, MapEditorControls.RangeOf(parameter), v => SetSelectedParameter(key, (int)v));
			Show(slider, spec.ValueOf(placed, key));
		}
	}

	/// <summary>The settings the brush shows, as sliders: built again when its kind changes, else their values set.</summary>
	private void RefreshBrush(bool rebuild)
	{
		if (Editing == null || _brushFields == null)
		{
			return;
		}

		BrushSettings brush = Editing.Brush;
		if (rebuild || _brushSliders.Count == 0)
		{
			foreach (Node child in _brushFields.GetChildren())
			{
				_brushFields.RemoveChild(child);
				child.QueueFree();
			}

			_brushSliders.Clear();
			foreach (BrushField field in MapEditorControls.FieldsOf(brush.Kind))
			{
				BrushField which = field;
				_brushSliders[field] = Slider(_brushFields, MapEditorControls.RangeOf(field, brush.Kind),
					v => SetBrush(MapEditorControls.Set(Editing.Brush, which, v)));
			}
		}

		foreach ((BrushField field, HSlider slider) in _brushSliders)
		{
			Show(slider, MapEditorControls.Get(brush, field));
		}

		int preset = MapEditorControls.PresetOf(brush);
		for (int i = 0; i < _presetButtons.Count; i++)
		{
			_presetButtons[i].SetPressedNoSignal(i == preset);
		}

		_brushLabel.Text = MapEditorControls.Describe(brush) + (preset < 0 ? " (changed)" : string.Empty);
	}

	private void RefreshOverlayPicker() => _overlayPicker?.Select(_overlayPicker.GetItemIndex((int)Overlay));

	// ---- under the view ----------------------------------------------------------------------------

	private void BuildStatus()
	{
		PanelContainer bar = Panel(Control.LayoutPreset.BottomWide);
		bar.OffsetTop = -StatusHeight;
		var row = new HBoxContainer();
		row.AddThemeConstantOverride("separation", 16);
		bar.AddChild(row);
		_cursorLabel = new Label { CustomMinimumSize = new Vector2(420f, 0f) };
		row.AddChild(_cursorLabel);
		_readoutLabel = new Label { CustomMinimumSize = new Vector2(250f, 0f) };
		row.AddChild(_readoutLabel);
		_issuesButton = new Button { Flat = true, FocusMode = Control.FocusModeEnum.None, TooltipText = "what the validator finds" };
		_issuesButton.Pressed += ShowIssues;
		row.AddChild(_issuesButton);
		_messageLabel = new Label
		{
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
			ClipText = true,
			TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
		};
		row.AddChild(_messageLabel);
	}

	/// <summary>The status line, the readout, the count of issues and the bar's title.</summary>
	private void RefreshStatus()
	{
		if (_cursorLabel == null)
		{
			return;
		}

		_messageLabel.Text = Message;
		if (Editing == null)
		{
			return;
		}

		_cursorLabel.Text = MapEditorControls.CursorLine(Editing.Document.Terrain, Editing.Document.Bands.Settings, Cursor);
		_readoutLabel.Text = Cursor != null ? MapEditorControls.Readout(_steepest) : string.Empty;
		_readoutLabel.Modulate = MapEditorControls.ColourOf(MapEditorControls.ClassOf(_steepest));
		_issuesButton.Text = MapEditorControls.Issues(ShownReport) + (DeepIsCurrent ? ", deep" : string.Empty);
		_title.Text = MapEditorControls.Title(Editing.Document.Name, FileName, Editing.Dirty, EverSaved);
	}

	/// <summary>Everything that shows the map: after it is opened, or edited.</summary>
	private void RefreshUi(bool rebuild)
	{
		if (Editing == null)
		{
			return;
		}

		for (int i = 0; i < _toolButtons.Length; i++)
		{
			_toolButtons[i]?.SetPressedNoSignal((int)Tool == i);
		}

		foreach ((BandNumber number, HSlider slider) in _bandSliders)
		{
			Show(slider, MapEditorControls.GetBand(Editing.Document.Bands, number));
		}

		_terrainPanel.Visible = Tool == EditorTool.Terrain;
		_prefabPanel.Visible = Tool == EditorTool.Prefabs;
		RefreshBrush(rebuild);
		RefreshPrefabPanel();
		RefreshOverlayPicker();
		if (_issuesPanel.Visible)
		{
			FillIssues();
		}

		RefreshStatus();
	}

	// ---- the validator's list ---------------------------------------------------------------------

	private void BuildIssues()
	{
		_issuesPanel = new PanelContainer { Visible = false };
		_issuesPanel.SetAnchorsPreset(Control.LayoutPreset.BottomLeft);
		_issuesPanel.OffsetLeft = RailWidth + 8f;
		_issuesPanel.OffsetTop = -StatusHeight - 260f;
		_issuesPanel.OffsetRight = RailWidth + 8f + 560f;
		_issuesPanel.OffsetBottom = -StatusHeight - 8f;
		_screen.AddChild(_issuesPanel);
		var column = new VBoxContainer();
		_issuesPanel.AddChild(column);
		var header = new HBoxContainer();
		column.AddChild(header);
		header.AddChild(new Label { Text = "Validate (§6.9): a click flies to it", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
		var deep = new Button
		{
			Text = "Deep",
			FocusMode = Control.FocusModeEnum.None,
			TooltipText = "bake the navigation mesh, and ask for a path from every barracks door and spawn line to every node",
		};
		deep.Pressed += Deep;
		header.AddChild(deep);
		var close = new Button { Text = "Close", FocusMode = Control.FocusModeEnum.None };
		close.Pressed += () => _issuesPanel.Visible = false;
		header.AddChild(close);
		_issuesList = new ItemList { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
		_issuesList.ItemSelected += index => FlyToIssue((int)index);
		column.AddChild(_issuesList);
	}

	/// <summary>Validate (§8.7): what §6.9 finds, listed.</summary>
	public void ShowIssues()
	{
		if (Editing == null)
		{
			return;
		}

		_issuesPanel.Visible = true;
		FillIssues();
		MapReport report = ShownReport;
		Say((DeepIsCurrent ? "validated, deep: " : "validated: ") + MapStore.Describe(report)
			+ (report.IsPlayable ? string.Empty : "; errors block hosting, not saving"));
	}

	private void FillIssues()
	{
		_issuesList.Clear();
		IReadOnlyList<MapIssue> issues = ShownReport.Issues;
		if (issues.Count == 0)
		{
			_issuesList.AddItem("no issues: it plays");
			return;
		}

		foreach (MapIssue issue in issues)
		{
			int row = _issuesList.AddItem(issue.ToString());
			_issuesList.SetItemCustomFgColor(row, issue.Severity == MapIssueSeverity.Error
				? new Color(1f, 0.45f, 0.4f) : new Color(1f, 0.85f, 0.4f));
		}
	}

	/// <summary>Flies the camera to an issue, as a click on it does.</summary>
	public void FlyToIssue(int index)
	{
		IReadOnlyList<MapIssue> issues = ListedIssues;
		if (index >= 0 && index < issues.Count)
		{
			_camera.LookAtFromAbove(issues[index].Where);
		}
	}

	// ---- the list of maps -------------------------------------------------------------------------

	private void BuildList()
	{
		_listPanel = new ColorRect { Color = new Color(0.06f, 0.07f, 0.09f), Visible = false };
		_listPanel.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		_screen.AddChild(_listPanel);
		var centre = new CenterContainer();
		centre.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		_listPanel.AddChild(centre);
		var column = new VBoxContainer { CustomMinimumSize = new Vector2(720f, 0f) };
		column.AddThemeConstantOverride("separation", 10);
		centre.AddChild(column);

		var title = new Label { Text = "Map editor" };
		title.AddThemeFontSizeOverride("font_size", 36);
		column.AddChild(title);
		column.AddChild(new Label
		{
			Text = "Your maps, in user://maps, and the maps to clone. A map the validator finds an error in is"
				+ " yours to finish: it is saved and opened, and not hosted.",
			AutowrapMode = TextServer.AutowrapMode.WordSmart,
		});

		_maps = new ItemList { CustomMinimumSize = new Vector2(720f, 320f) };
		_maps.ItemActivated += index => OpenRow((int)index);
		column.AddChild(_maps);

		var buttons = new HBoxContainer();
		column.AddChild(buttons);
		ListButton(buttons, "New…", () => AskName("A new map", "my-map", name => NewMap(name, out _)));
		ListButton(buttons, "Open", () => OpenRow(SelectedRow()));
		ListButton(buttons, "Clone as…", () =>
		{
			int row = SelectedRow();
			if (row >= 0)
			{
				string from = _rows[row].Path;
				AskName("Clone as", _rows[row].Name + "-copy", name => Clone(from, name, out _));
			}
		});
		ListButton(buttons, "Show folder", () =>
		{
			System.IO.Directory.CreateDirectory(Directory);
			OS.ShellOpen(Directory);
		});
		ListButton(buttons, "Back", () => Exit());

		_listStatus = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(720f, 40f) };
		column.AddChild(_listStatus);
	}

	private static void ListButton(Container row, string text, Action pressed)
	{
		var button = new Button { Text = text, CustomMinimumSize = new Vector2(130f, 36f) };
		button.Pressed += pressed;
		row.AddChild(button);
	}

	private int SelectedRow()
	{
		int[] selected = _maps.GetSelectedItems();
		return selected.Length > 0 && selected[0] < _rows.Count ? selected[0] : -1;
	}

	private void OpenRow(int row)
	{
		if (row < 0 || row >= _rows.Count)
		{
			return;
		}

		if (!_rows[row].Own)
		{
			_listStatus.Text = $"{_rows[row].Name} is not yours: clone it to edit it";
			return;
		}

		if (!Open(_rows[row].Name, out string error))
		{
			_listStatus.Text = error;
		}
	}

	/// <summary>The list (§8.1): one's own maps, then the bundled and the received ones to clone.</summary>
	public void ShowList()
	{
		_listPanel.Visible = true;
		_issuesPanel.Visible = false;
		_ringMesh?.ClearSurfaces();
		_maps.Clear();
		_rows.Clear();

		foreach (StoredMap map in MapStore.List(Directory))
		{
			_rows.Add((true, map.Path, map.Name));
			_maps.AddItem(map.Reads
				? $"{map.Name}   —   '{map.Document.Name}', revision {map.Document.Revision}   —   {MapStore.Describe(map.Report)}"
					+ (map.HasAutosave ? "   —   autosaved since" : string.Empty)
				: $"{map.Name}   —   {map.Error}");
		}

		foreach (MapInfo map in MapCatalog.All)
		{
			if (map.IsData)
			{
				_rows.Add((false, map.Path, map.Id));
				_maps.AddItem($"{map.Name}   —   bundled with the game: clone it to edit it");
			}
		}

		if (System.IO.Directory.Exists(MapLibrary.CacheRoot))
		{
			foreach (string path in System.IO.Directory.GetFiles(MapLibrary.CacheRoot, "*" + MapFile.Extension))
			{
				string stem = Path.GetFileNameWithoutExtension(path);
				string name = stem.Length >= MapNames.MinHashPrefix ? stem[..MapNames.MinHashPrefix] : stem;
				_rows.Add((false, path, "received-" + name));
				_maps.AddItem($"{name}…   —   received from a server: clone it to edit it");
			}
		}

		_listStatus.Text = Message;
	}

	private void HideList() => _listPanel.Visible = false;

	// ---- dialogs -----------------------------------------------------------------------------------

	private void BuildDialogs()
	{
		_namePrompt = new ConfirmationDialog { Title = "Name", MinSize = new Vector2I(420, 120) };
		var column = new VBoxContainer();
		column.AddChild(new Label { Text = "a-z, 0-9, _ and -, at most 48: the file is user://maps/<name>.map" });
		_nameEdit = new LineEdit();
		_nameEdit.TextSubmitted += _ => NameConfirmed();
		column.AddChild(_nameEdit);
		_namePrompt.AddChild(column);
		_namePrompt.Confirmed += NameConfirmed;
		_namePrompt.VisibilityChanged += () => TextEntry(_namePrompt.Visible);
		_layer.AddChild(_namePrompt);

		_autosaveDialog = new ConfirmationDialog
		{
			Title = "An autosave",
			DialogText = "This map was autosaved after it was last saved. Take the autosave back, or drop it?",
			OkButtonText = "Take it back",
			CancelButtonText = "Drop it",
		};
		_autosaveDialog.Confirmed += () => RestoreAutosave();
		_autosaveDialog.Canceled += DiscardAutosave;
		_layer.AddChild(_autosaveDialog);

		_exitDialog = new ConfirmationDialog
		{
			Title = "Unsaved changes",
			DialogText = "The map has changes that are not saved.",
			OkButtonText = "Save and leave",
			CancelButtonText = "Stay",
		};
		_exitDialog.AddButton("Leave without saving", true, "discard");
		_exitDialog.Confirmed += () =>
		{
			if (Save(out _))
			{
				Leave(false);
			}
		};
		_exitDialog.CustomAction += action =>
		{
			if (action == "discard")
			{
				_exitDialog.Hide();
				Leave(true);
			}
		};
		_layer.AddChild(_exitDialog);

		_settingsDialog = new ConfirmationDialog { Title = "The map", MinSize = new Vector2I(480, 300) };
		var settings = new VBoxContainer();
		settings.AddChild(new Label { Text = "Name" });
		_settingsName = new LineEdit();
		settings.AddChild(_settingsName);
		settings.AddChild(new Label { Text = "Author: what you type here, and nothing else (§9.2)" });
		_settingsAuthor = new LineEdit();
		settings.AddChild(_settingsAuthor);
		settings.AddChild(new Label { Text = "Description" });
		_settingsDescription = new TextEdit { CustomMinimumSize = new Vector2(440f, 90f) };
		settings.AddChild(_settingsDescription);
		_settingsDialog.AddChild(settings);
		_settingsDialog.Confirmed += () =>
			SetText(_settingsName.Text, _settingsAuthor.Text, _settingsDescription.Text, out _);
		_settingsDialog.VisibilityChanged += () => TextEntry(_settingsDialog.Visible);
		_layer.AddChild(_settingsDialog);

		_exportDialog = new FileDialog
		{
			Title = "Export",
			Access = FileDialog.AccessEnum.Filesystem,
			FileMode = FileDialog.FileModeEnum.SaveFile,
			Filters = new[] { "*.map ; gdpyr maps" },
			MinSize = new Vector2I(720, 480),
		};
		_exportDialog.FileSelected += path => Export(path, out _);
		_exportDialog.VisibilityChanged += () => TextEntry(_exportDialog.Visible);
		_layer.AddChild(_exportDialog);
	}

	private void AskName(string title, string initial, Action<string> then)
	{
		_named = then;
		_namePrompt.Title = title;
		_nameEdit.Text = initial ?? string.Empty;
		_namePrompt.PopupCentered();
		_nameEdit.GrabFocus();
		_nameEdit.SelectAll();
	}

	private void NameConfirmed()
	{
		_namePrompt.Hide();
		Action<string> then = _named;
		_named = null;
		then?.Invoke(_nameEdit.Text);
		if (_listPanel.Visible)
		{
			_listStatus.Text = Message;
		}
	}

	private void OfferAutosave()
	{
		if (AutosaveOffered != null && DisplayServer.GetName() != "headless")
		{
			_autosaveDialog.PopupCentered();
		}
	}

	private void ConfirmExit()
	{
		_exitToList = false;
		_exitDialog.PopupCentered();
	}

	private void ShowSettings()
	{
		if (Editing == null)
		{
			return;
		}

		_settingsName.Text = Editing.Document.Name;
		_settingsAuthor.Text = Editing.Document.Author;
		_settingsDescription.Text = Editing.Document.Description;
		_settingsDialog.PopupCentered();
	}

	private void ShowExport()
	{
		if (Editing == null)
		{
			return;
		}

		_exportDialog.CurrentFile = FileName + MapFile.Extension;
		_exportDialog.PopupCentered();
	}

	private void Leave(bool discard)
	{
		if (_exitToList)
		{
			CloseMap(true);
		}
		else
		{
			Exit(discard || Editing?.Dirty != true);
		}
	}

	/// <summary>Whether the mouse is over a panel rather than the view.</summary>
	private bool OverUi()
	{
		Control hovered = GetViewport().GuiGetHoveredControl();
		return hovered != null && hovered != _screen;
	}

	/// <summary>While a text field is up, the camera does not fly on the letters typed into it.</summary>
	private static void TextEntry(bool on)
	{
		if (on)
		{
			InputFocus.BeginTextEntry();
		}
		else
		{
			InputFocus.EndTextEntry();
		}
	}

	// ---- widgets ---------------------------------------------------------------------------------

	private PanelContainer Panel(Control.LayoutPreset preset)
	{
		var panel = new PanelContainer();
		panel.SetAnchorsPreset(preset);
		_screen.AddChild(panel);
		return panel;
	}

	private static Label Heading(string text) => new() { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart };

	/// <summary>A slider with its name and value. <paramref name="changed"/> hears a person move it, not <see cref="Show"/>.</summary>
	private static HSlider Slider(Container parent, SliderRange range, Action<float> changed)
	{
		var label = new Label { Text = range.Label };
		parent.AddChild(label);
		var row = new HBoxContainer();
		parent.AddChild(row);
		var slider = new HSlider
		{
			MinValue = range.Min,
			MaxValue = range.Max,
			Step = range.Step,
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
			FocusMode = Control.FocusModeEnum.None,
		};
		var value = new Label { CustomMinimumSize = new Vector2(70f, 0f), HorizontalAlignment = HorizontalAlignment.Right };
		slider.SetMeta("unit", range.Unit);
		slider.SetMeta("value", value);
		slider.ValueChanged += v =>
		{
			value.Text = Format((float)v, range.Unit);
			changed((float)v);
		};
		row.AddChild(slider);
		row.AddChild(value);
		return slider;
	}

	private static void Show(HSlider slider, float value)
	{
		slider.SetValueNoSignal(value);
		if (slider.GetMeta("value").As<Label>() is { } label)
		{
			label.Text = Format(value, slider.GetMeta("unit").AsString());
		}
	}

	private static string Format(float value, string unit) =>
		value.ToString("0.###", CultureInfo.InvariantCulture) + (string.IsNullOrEmpty(unit) ? string.Empty : unit == "°" ? unit : " " + unit);
}
