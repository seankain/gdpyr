using Gdpyr.Core;
using Gdpyr.Fps;
using Gdpyr.Match;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Ui;

/// <summary>
/// Health, ammunition, tickets and the round clock, plus the hit marker and the
/// loadout picker.
///
/// Built in code rather than as a scene on purpose: it belongs to the process, not
/// to the character — a death does not rebuild the character node, but nothing
/// about this HUD should depend on that either — and a greybox HUD that costs a
/// scene file costs a merge conflict every time a milestone touches it.
/// </summary>
public partial class CombatHud : CanvasLayer
{
	private const float HitMarkerSeconds = 0.25f;

	private Label _status;
	private Label _ammo;
	private Label _round;
	private Label _notice;
	private Panel _hitMarker;
	private LoadoutMenu _loadout;

	private float _hitMarkerAge = float.MaxValue;
	private bool _hitMarkerWasKill;

	public override void _Ready()
	{
		// The HUD keeps drawing while the pause menu is up; it is the only thing that
		// says why a paused player is dead.
		ProcessMode = ProcessModeEnum.Always;
		Layer = 2;

		var root = new Control
		{
			MouseFilter = Control.MouseFilterEnum.Ignore,
			AnchorRight = 1f,
			AnchorBottom = 1f,
		};
		AddChild(root);

		_status = NewLabel(root, HorizontalAlignment.Left, new Vector2(24f, -80f), anchorTop: 1f);
		_ammo = NewLabel(root, HorizontalAlignment.Right, new Vector2(-260f, -80f), anchorLeft: 1f, anchorTop: 1f);
		_round = NewLabel(root, HorizontalAlignment.Center, new Vector2(-160f, 16f), anchorLeft: 0.5f);
		_notice = NewLabel(root, HorizontalAlignment.Center, new Vector2(-260f, 120f), anchorLeft: 0.5f);

		_ammo.Size = new Vector2(236f, 56f);
		_round.Size = new Vector2(320f, 48f);
		_notice.Size = new Vector2(520f, 72f);

		_hitMarker = new Panel
		{
			Visible = false,
			MouseFilter = Control.MouseFilterEnum.Ignore,
			AnchorLeft = 0.5f,
			AnchorTop = 0.5f,
			Position = new Vector2(-7f, -7f),
			Size = new Vector2(14f, 14f),
		};
		root.AddChild(_hitMarker);

		_loadout = new LoadoutMenu { Name = "LoadoutMenu" };
		root.AddChild(_loadout);
	}

	public override void _Process(double delta)
	{
		if (_hitMarkerAge >= HitMarkerSeconds)
		{
			return;
		}

		_hitMarkerAge += (float)delta;
		if (_hitMarkerAge >= HitMarkerSeconds)
		{
			_hitMarker.Visible = false;
		}
	}

	/// <summary>Called once per simulation tick by the combat manager.</summary>
	public void Refresh(PlayerCombat combat, MatchState match, uint tick)
	{
		if (combat == null)
		{
			_status.Text = "no character";
			return;
		}

		WeaponStats stats = combat.EquippedStats;
		string weapon = WeaponCatalog.NameOf(combat.EquippedDefinitionId);

		_status.Text = $"HP {combat.Health}\nK {combat.Kills}  D {combat.Deaths}";
		_ammo.Text = stats.HasUnlimitedAmmo
			? $"{weapon}\n--"
			: $"{weapon}\n{combat.Equipped.Ammo} / {stats.MagazineSize}"
				+ (combat.Equipped.IsReloading ? "  reloading" : string.Empty);

		_round.Text = match.Phase switch
		{
			RoundPhase.Live => $"tickets {match.GroundTickets}   {Clock(match.SecondsRemaining(tick))}",
			RoundPhase.Ended => $"round over — {match.Outcome}",
			_ => "warmup",
		};

		bool dead = !combat.IsAlive;
		_notice.Text = dead ? "down — respawning" : string.Empty;
		_loadout.Refresh(dead || match.Phase == RoundPhase.Ended, combat.PendingLoadout.Large);
	}

	public void FlashHitMarker(bool killed)
	{
		_hitMarkerAge = 0f;
		_hitMarkerWasKill = killed;
		_hitMarker.Visible = true;
		_hitMarker.SelfModulate = killed ? Colors.Red : Colors.White;
	}

	/// <summary>True when the last hit marker was for a kill. Read by tests and the debug panel.</summary>
	public bool LastHitWasKill => _hitMarkerWasKill;

	private static string Clock(float seconds)
	{
		int whole = Mathf.Max(0, Mathf.RoundToInt(seconds));
		return $"{whole / 60:00}:{whole % 60:00}";
	}

	private static Label NewLabel(Control parent, HorizontalAlignment alignment, Vector2 position,
		float anchorLeft = 0f, float anchorTop = 0f)
	{
		var label = new Label
		{
			HorizontalAlignment = alignment,
			MouseFilter = Control.MouseFilterEnum.Ignore,
			AnchorLeft = anchorLeft,
			AnchorTop = anchorTop,
			Position = position,
			Size = new Vector2(240f, 56f),
		};

		// Greybox legibility: the map is a bright grey box and the HUD has no panel
		// behind it.
		label.AddThemeColorOverride("font_outline_color", Colors.Black);
		label.AddThemeConstantOverride("outline_size", 4);
		parent.AddChild(label);
		return label;
	}
}
