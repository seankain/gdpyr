using System.Collections.Generic;
using Gdpyr.Core;
using Gdpyr.Fps;
using Gdpyr.Match;
using Gdpyr.Net;
using Gdpyr.Rts;
using Gdpyr.Sim;
using Gdpyr.Sim.AiDebug;
using Godot;

namespace Gdpyr.Ui.Spectator;

/// <summary>
/// A spectator's seat (docs/AI_DEBUG.md §2.2, §4): the free camera, the AI debugger's
/// panel and world overlay, what is selected, and which of the debugger's views are
/// on. It turns that into the watch request the authority answers
/// (<see cref="PlayerManager.SendAiWatch"/>) and draws whatever frame came back.
///
/// <list type="table">
/// <item><term>left click</term><description>inspect the bot, unit or squad under the pointer; shift adds it</description></item>
/// <item><term>right drag</term><description>look; WASD, space and crouch fly; sprint is faster</description></item>
/// <item><term>F</term><description>follow what the panel is showing; the movement keys let go</description></item>
/// <item><term>Tab</term><description>the next ground player</description></item>
/// <item><term>Backspace</term><description>clear the selection</description></item>
/// <item><term>F4 – F9</term><description>panel, every agent's labels, the strategist's commander, graph or tree, world markers, fold the tree</description></item>
/// <item><term>F3</term><description>stop spectating (<see cref="TeamSelect"/>)</description></item>
/// </list>
///
/// Built by <see cref="PlayerManager"/> when this process starts spectating and freed
/// when it stops. Nothing here simulates: it reads characters and units where every
/// client draws them, and the frame the authority sent.
/// </summary>
public partial class SpectatorView : Node3D
{
	/// <summary>How near the pointer, in pixels, something has to be to be clicked.</summary>
	private const float PickRadiusPixels = 36f;

	/// <summary>A watch is re-sent this often even unchanged: cheap, and it survives anything that lost it.</summary>
	private const ulong WatchRefreshMsec = 3000;

	private readonly List<AiEntityRef> _selected = new();
	private readonly AiWatch _watch = new();
	private readonly AiWatch _sent = new();
	private ulong _sentMsec;

	private SpectatorCamera _camera;
	private AiDebugWorld _world;
	private AiDebugPanel _panel;

	private AiEntityRef _follow;
	private AiEntityRef _pendingPrint;
	private int _cycle = -1;

	public static SpectatorView Instance { get; private set; }

	public IReadOnlyList<AiEntityRef> Selected => _selected;

	public bool PanelVisible { get; private set; } = true;

	/// <summary>Labels and heading lines over every planning ground bot and unit.</summary>
	public bool LabelsAll { get; private set; }

	/// <summary>The computer strategists' commanders: their squads in the world, and a tab in the panel.</summary>
	public bool Strategist { get; private set; }

	/// <summary>The panel draws the domain as boxes rather than as a text tree.</summary>
	public bool Graph { get; private set; }

	public bool WorldMarkers { get; private set; } = true;

	/// <summary>Fold everything off the plan's path, in the tree and the graph. On to start with: the path is what is read first.</summary>
	public bool Collapse { get; private set; } = true;

	/// <summary>Which tab the panel shows: a selection's index, or <see cref="Selected"/>.Count for the strategist.</summary>
	public int Tab { get; set; }

	/// <summary>The squad the strategist tab is showing, or nothing for the commander's table alone.</summary>
	public AiEntityRef StrategistSquad { get; private set; }

	public override void _Ready()
	{
		Instance = this;

		_camera = new SpectatorCamera { Name = "SpectatorCamera" };
		AddChild(_camera);

		_world = new AiDebugWorld { Name = "AiDebugWorld" };
		AddChild(_world);

		_panel = new AiDebugPanel { Name = "AiDebugPanel" };
		AddChild(_panel);

		Input.MouseMode = Input.MouseModeEnum.Visible;
	}

	public override void _ExitTree()
	{
		if (Instance == this)
		{
			Instance = null;
		}
	}

	public override void _Process(double delta)
	{
		ManagePointer();

		AiDebugFrame frame = PlayerManager.Instance?.AiFrame;
		ResolveSquads(frame);
		Prune(frame);
		SendWatch();
		UpdateFollow(frame);

		_world.Render(frame, this);
		_panel.Render(frame);

		if (_pendingPrint.IsValid && frame?.Find(_pendingPrint) is { } inspect)
		{
			GameConsole.Print(AiDebugText.Describe(inspect, frame, HtnTextStyle.Plain));
			_pendingPrint = default;
		}
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (InputFocus.TextEntry)
		{
			return;
		}

		if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } click
			&& Input.MouseMode == Input.MouseModeEnum.Visible)
		{
			Pick(click.Position, click.ShiftPressed);
			GetViewport().SetInputAsHandled();
			return;
		}

		if (@event is not InputEventKey { Pressed: true, Echo: false } key)
		{
			return;
		}

		bool handled = true;
		switch (key.PhysicalKeycode)
		{
			case Key.F4:
				PanelVisible = !PanelVisible;
				break;
			case Key.F5:
				LabelsAll = !LabelsAll;
				break;
			case Key.F6:
				SetStrategist(!Strategist);
				break;
			case Key.F7:
				Graph = !Graph;
				break;
			case Key.F8:
				WorldMarkers = !WorldMarkers;
				break;
			case Key.F9:
				Collapse = !Collapse;
				break;
			case Key.F:
				FollowShown();
				break;
			case Key.Tab:
				CyclePlayers(key.ShiftPressed ? -1 : 1);
				break;
			case Key.Backspace:
			case Key.Delete:
				ClearSelection();
				break;
			default:
				handled = false;
				break;
		}

		if (handled)
		{
			GetViewport().SetInputAsHandled();
		}
	}

	// ---- what the panel and the console call ------------------------------

	public bool IsSelected(AiEntityRef target) => _selected.Contains(target);

	/// <summary>Inspects <paramref name="target"/>: alone, or beside what is already selected when <paramref name="add"/>.</summary>
	public void Select(AiEntityRef target, bool add = false)
	{
		if (!target.IsValid)
		{
			return;
		}

		if (!add)
		{
			_selected.Clear();
		}

		int index = _selected.IndexOf(target);
		if (index < 0)
		{
			if (_selected.Count >= AiWatch.MaxSelected)
			{
				_selected.RemoveAt(0);
			}

			_selected.Add(target);
			index = _selected.Count - 1;
		}
		else if (add)
		{
			// Shift-clicking something already selected lets go of it.
			_selected.RemoveAt(index);
			Tab = Mathf.Clamp(Tab, 0, _selected.Count);
			return;
		}

		Tab = index;
	}

	public void ClearSelection()
	{
		_selected.Clear();
		_follow = default;
		Tab = 0;
	}

	public void SetStrategist(bool on)
	{
		Strategist = on;
		if (on)
		{
			Tab = _selected.Count;
		}
		else
		{
			StrategistSquad = default;
			Tab = Mathf.Clamp(Tab, 0, Mathf.Max(_selected.Count - 1, 0));
		}
	}

	public void SetLabelsAll(bool on) => LabelsAll = on;

	public void SetGraph(bool on) => Graph = on;

	public void SetWorldMarkers(bool on) => WorldMarkers = on;

	public void SetCollapse(bool on) => Collapse = on;

	/// <summary>Shows one squad's plan in the strategist tab; nothing goes back to the table.</summary>
	public void ShowSquad(AiEntityRef squad)
	{
		StrategistSquad = squad.Kind == AiEntityKind.Squad ? squad : default;
		if (!Strategist)
		{
			SetStrategist(true);
		}

		Tab = _selected.Count;
	}

	/// <summary>Prints <paramref name="target"/>'s plan to the console as soon as a frame carries it.</summary>
	public void PrintWhenReady(AiEntityRef target)
	{
		Select(target, add: true);
		_pendingPrint = target;
	}

	/// <summary>The entity the panel's current tab is about, or nothing.</summary>
	public AiEntityRef Shown => Tab < _selected.Count && Tab >= 0 ? _selected[Tab]
		: Strategist ? StrategistSquad
		: default;

	// ---- per frame -----------------------------------------------------------

	/// <summary>A cursor, unless the right button is held to look; the console and the pause menu own it while they are up.</summary>
	private void ManagePointer()
	{
		if (InputFocus.TextEntry || GetTree().Paused)
		{
			return;
		}

		Input.MouseModeEnum wanted = _camera.Looking ? Input.MouseModeEnum.Captured : Input.MouseModeEnum.Visible;
		if (Input.MouseMode != wanted)
		{
			Input.MouseMode = wanted;
		}
	}

	/// <summary>"squad 2" from the console means the first computer strategist's: named once a frame says who that is.</summary>
	private void ResolveSquads(AiDebugFrame frame)
	{
		if (frame == null || frame.Commanders.Count == 0)
		{
			return;
		}

		int first = frame.Commanders[0].PeerId;
		for (int i = 0; i < _selected.Count; i++)
		{
			if (_selected[i].Kind == AiEntityKind.Squad && _selected[i].Id == AiTargets.FirstStrategist)
			{
				_selected[i] = AiEntityRef.Squad(first, _selected[i].Slot);
			}
		}

		if (_pendingPrint.Kind == AiEntityKind.Squad && _pendingPrint.Id == AiTargets.FirstStrategist)
		{
			_pendingPrint = AiEntityRef.Squad(first, _pendingPrint.Slot);
		}
	}

	/// <summary>Lets go of whatever has left the field.</summary>
	private void Prune(AiDebugFrame frame)
	{
		for (int i = _selected.Count - 1; i >= 0; i--)
		{
			AiEntityRef target = _selected[i];
			bool gone = target.Kind switch
			{
				AiEntityKind.Player => PlayerManager.Instance?.CharacterOf(target.Id) == null,
				AiEntityKind.Unit => UnitManager.Instance?.Find((ushort)target.Id) == null,

				// A squad is only known through a frame that carries its commander.
				AiEntityKind.Squad => frame?.Commander(target.Id) is { } commander && commander.Squad(target.Slot) == null,
				_ => true,
			};

			if (gone)
			{
				_selected.RemoveAt(i);
				if (Tab > i)
				{
					Tab--;
				}
			}
		}

		Tab = Mathf.Clamp(Tab, 0, Strategist ? _selected.Count : Mathf.Max(_selected.Count - 1, 0));
	}

	private void SendWatch()
	{
		_watch.Flags = (LabelsAll && WorldMarkers ? AiWatchFlags.Labels : AiWatchFlags.None)
			| (Strategist ? AiWatchFlags.Commander : AiWatchFlags.None);
		_watch.CommanderPeerId = 0;
		_watch.Selected.Clear();
		foreach (AiEntityRef target in _selected)
		{
			if (target.Kind != AiEntityKind.Squad || target.Id != AiTargets.FirstStrategist)
			{
				_watch.Selected.Add(target);
			}
		}

		ulong now = Time.GetTicksMsec();
		if (_watch.SameAs(_sent) && now - _sentMsec < WatchRefreshMsec)
		{
			return;
		}

		PlayerManager.Instance?.SendAiWatch(_watch);
		_sent.CopyFrom(_watch);
		_sentMsec = now;
	}

	private void UpdateFollow(AiDebugFrame frame)
	{
		if (!_follow.IsValid)
		{
			return;
		}

		// The movement keys let go of it; so does its leaving the field.
		if (!_camera.Following || !AiEntities.TryPosition(_follow, frame, out Vector3 at))
		{
			_follow = default;
			_camera.StopFollow();
			return;
		}

		_camera.MoveFollow(at);
	}

	private void FollowShown()
	{
		AiEntityRef target = Shown;
		if (!target.IsValid && _selected.Count > 0)
		{
			target = _selected[0];
		}

		if (_follow == target || !AiEntities.TryPosition(target, PlayerManager.Instance?.AiFrame, out Vector3 at))
		{
			_follow = default;
			_camera.StopFollow();
			return;
		}

		_follow = target;
		_camera.StartFollow(at);
	}

	private void CyclePlayers(int step)
	{
		CombatManager combat = CombatManager.Instance;
		if (combat == null || combat.PlayerCount == 0)
		{
			return;
		}

		for (int tries = 0; tries < combat.PlayerCount; tries++)
		{
			_cycle = ((_cycle + step) % combat.PlayerCount + combat.PlayerCount) % combat.PlayerCount;
			PlayerCombat player = combat.PlayerAt(_cycle);
			if (AiEntities.IsPickable(player))
			{
				AiEntityRef target = AiEntityRef.Player(player.PeerId);
				Select(target);
				if (AiEntities.TryPosition(target, null, out Vector3 at))
				{
					_camera.LookAtFromAbove(at);
				}

				return;
			}
		}
	}

	/// <summary>Whatever is drawn nearest the pointer: a ground player, a unit, or — with the strategist on — a squad's middle.</summary>
	private void Pick(Vector2 pointer, bool add)
	{
		AiEntityRef best = default;
		float bestDistance = PickRadiusPixels;

		void Consider(AiEntityRef target, Vector3 at)
		{
			if (_camera.IsPositionBehind(at))
			{
				return;
			}

			float distance = (_camera.UnprojectPosition(at) - pointer).Length();
			if (distance < bestDistance)
			{
				bestDistance = distance;
				best = target;
			}
		}

		if (CombatManager.Instance is { } combat)
		{
			for (int i = 0; i < combat.PlayerCount; i++)
			{
				PlayerCombat player = combat.PlayerAt(i);
				if (AiEntities.IsPickable(player))
				{
					Consider(AiEntityRef.Player(player.PeerId), player.Character.GlobalPosition + (Vector3.Up * 1f));
				}
			}
		}

		if (UnitManager.Instance is { } units)
		{
			for (int i = 0; i < units.SlotCount; i++)
			{
				if (units.UnitAt(i) is { IsAlive: true } unit)
				{
					Consider(AiEntityRef.Unit(unit.UnitId), unit.GlobalPosition + (Vector3.Up * 0.9f));
				}
			}
		}

		if (Strategist && PlayerManager.Instance?.AiFrame is { } frame)
		{
			foreach (AiCommander commander in frame.Commanders)
			{
				foreach (AiSquad squad in commander.Squads)
				{
					Consider(AiEntityRef.Squad(commander.PeerId, squad.Slot), squad.Centroid + Vector3.Up);
				}
			}
		}

		if (best.IsValid)
		{
			if (best.Kind == AiEntityKind.Squad && !add)
			{
				ShowSquad(best);
				return;
			}

			Select(best, add);
		}
		else if (!add)
		{
			ClearSelection();
		}
	}
}
