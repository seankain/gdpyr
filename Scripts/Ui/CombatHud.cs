using System.Collections.Generic;
using Gdpyr.Core;
using Gdpyr.Fps;
using Gdpyr.Match;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Ui;

/// <summary>
/// Health, ammunition, tickets and the round clock, plus the hit marker, the next
/// spawn's kit on the death screen, and the loadout panel (docs/LOADOUT.md §7.2–7.4).
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
	private Label _prompt;
	private Panel _hitMarker;
	private LoadoutPanel _panel;

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

		// Three lines — health, vest and grenades; the load; the score — so it starts a
		// line higher than the ammunition corner's two.
		_status = NewLabel(root, HorizontalAlignment.Left, new Vector2(24f, -104f), anchorTop: 1f);
		_ammo = NewLabel(root, HorizontalAlignment.Right, new Vector2(-260f, -80f), anchorLeft: 1f, anchorTop: 1f);
		_round = NewLabel(root, HorizontalAlignment.Center, new Vector2(-160f, 16f), anchorLeft: 0.5f);
		// Wide enough for the death screen's line of the next spawn's kit (docs/LOADOUT.md §7.3).
		_notice = NewLabel(root, HorizontalAlignment.Center, new Vector2(-400f, 120f), anchorLeft: 0.5f);

		// Just under the reticle: what the key under the player's finger would do
		// with whatever they are standing next to (docs/IMPLEMENTATION_PLAN.md §M5).
		_prompt = NewLabel(root, HorizontalAlignment.Center, new Vector2(-260f, 48f), anchorLeft: 0.5f,
			anchorTop: 0.5f);
		// Two lines: the use key's, and at a locker what its resupply is doing.
		_prompt.Size = new Vector2(520f, 56f);

		_status.Size = new Vector2(240f, 80f);
		_ammo.Size = new Vector2(236f, 56f);
		_round.Size = new Vector2(320f, 48f);
		_notice.Size = new Vector2(800f, 72f);

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

		_panel = new LoadoutPanel { Name = "LoadoutPanel" };
		root.AddChild(_panel);
	}

	/// <summary>
	/// The local player's own tap at a weapon locker (docs/LOADOUT.md §7.2): resolved
	/// from its predicted frames on a client, and by the authority's own use step on a
	/// listen host.
	/// </summary>
	public void OpenLocker(PlayerCombat combat) => _panel?.Open(DraftMode.Locker, combat);

	/// <summary>The locker's answer to the kit the panel asked for (§6.2).</summary>
	public void AnswerKit(KitRefusal refusal) => _panel?.Answer(refusal);

	/// <summary>
	/// The death screen's <c>E</c> (docs/LOADOUT.md §7.3): the same panel, editing the
	/// next spawn's kit. A dead player's frames carry no buttons (InputFrame.LookOnly),
	/// so the key means nothing else while they are down; once the panel is up it is the
	/// panel's, and its own <c>E</c> closes it.
	/// </summary>
	public override void _UnhandledInput(InputEvent @event)
	{
		if (_panel == null || _panel.IsOpen || InputFocus.TextEntry || GetTree().Paused
			|| !@event.IsActionPressed("use") || @event.IsEcho())
		{
			return;
		}

		CombatManager combat = CombatManager.Instance;
		if (combat?.Local is not { IsDowned: true } local || combat.LocalAwaitingRole
			|| combat.Match.Phase == RoundPhase.Ended)
		{
			return;
		}

		_panel.Open(DraftMode.NextSpawn, local);
		GetViewport().SetInputAsHandled();
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

	/// <summary>
	/// Called once per simulation tick by the combat manager.
	///
	/// <paramref name="awaitingRole"/> is the role menu being up
	/// (<see cref="RoleSelect"/>). It occupies the middle of the screen, which is
	/// where the loadout panel lives, and a player who has not picked a side has
	/// nothing to pick a kit for yet.
	/// </summary>
	public void Refresh(PlayerCombat combat, MatchState match, uint tick, bool awaitingRole)
	{
		if (combat == null)
		{
			// A spectator has no character by design (docs/AI_DEBUG.md §2): the round
			// clock is still worth a corner, and the rest of the screen is the debugger's.
			bool spectating = Net.PlayerManager.Instance is { IsSpectating: true };
			_status.Text = spectating ? "spectating  ·  F3 to play" : "no character";
			_ammo.Text = string.Empty;
			_notice.Text = string.Empty;
			_prompt.Text = string.Empty;
			_panel.Refresh(default);
			if (spectating)
			{
				_round.Text = match.Phase == RoundPhase.Live
					? $"tickets {match.GroundTickets}   {Clock(match.SecondsRemaining(tick))}"
					: match.Phase.ToString().ToLowerInvariant();
			}

			return;
		}

		// A strategist's character is still in the roster — every peer has one — but
		// it is not on the field, and a health bar over an empty chair is noise. The
		// strategist's own HUD is StrategistHud (docs/IMPLEMENTATION_PLAN.md §M3).
		if (combat.Team == Team.Strategist)
		{
			// Only the round clock. Everything else about a strategist's game is on
			// StrategistHud, which occupies both of the corners this one uses.
			_status.Text = string.Empty;
			_ammo.Text = string.Empty;
			_round.Text = match.Phase == RoundPhase.Live
				? $"tickets {match.GroundTickets}   {Clock(match.SecondsRemaining(tick))}"
				: match.Phase.ToString().ToLowerInvariant();
			_notice.Text = string.Empty;
			_prompt.Text = string.Empty;
			_panel.Refresh(default);
			return;
		}

		// A gunner's own weapons are not in their hands while they are behind a gun
		// (docs/IMPLEMENTATION_PLAN.md §M5), so the corner shows the gun's belt.
		EmplacementManager emplacements = EmplacementManager.Instance;
		Emplacement gun = null;
		bool mounted = emplacements != null && emplacements.TryMounted(combat.PeerId, out gun);

		_status.Text = $"{StatusLine(combat.Health, combat.Vest, combat.Grenades.Count)}\n"
			+ $"{LoadLine(combat.LoadKg, combat.LoadScale)}\nK {combat.Kills}  D {combat.Deaths}";
		_ammo.Text = mounted
			? $"{WeaponCatalog.NameOf(gun.WeaponDefinitionId)}\n{gun.Weapon.Ammo} / {gun.Stats.MagazineSize}"
			: AmmoLine(combat.EquippedDefinitionId, combat.Equipped, combat.EquippedStats);

		_round.Text = match.Phase switch
		{
			RoundPhase.Live => $"tickets {match.GroundTickets}   {Clock(match.SecondsRemaining(tick))}",
			RoundPhase.Ended => $"round over — {match.Outcome}",
			_ => "warmup",
		};

		// The drones the side's drone nodes have built, or the one being flown
		// (docs/CONTROL_POINTS.md §4.4).
		// TODO(control points 3, docs/CONTROL_POINTS.md §6): check on a display that the second
		// line fits the round label's 48 px. Never looked at.
		if (CombatManager.Instance is { } manager && combat.IsAlive)
		{
			int flying = manager.PilotedSlot(combat.PeerId);
			string drone = DroneHud.Line(manager.Drones.Ready, manager.Drones.HeldNodes, manager.Drones.Progress,
				flying >= 0, manager.DroneSecondsLeft(flying, tick));
			if (drone.Length > 0)
			{
				_round.Text += $"\n{drone}";
			}
		}

		bool dead = combat.IsDowned;
		bool roundOver = match.Phase == RoundPhase.Ended;
		_panel.Refresh(new DraftSituation(combat.IsAlive, combat.Team == Team.GroundForce, awaitingRole, roundOver,
			combat.Character != null && emplacements != null
				? emplacements.LockerDistance(combat.Character.SimPosition)
				: float.PositiveInfinity));

		_notice.Text = !dead ? string.Empty
			: roundOver || awaitingRole ? "down — respawning"
			: $"down — respawning\n{NextSpawnLine(combat.PendingLoadout, editable: !_panel.IsOpen)}";

		// The panel takes the middle of the screen, where the prompt is; what the prompt
		// would say there is what the panel is for.
		_prompt.Text = dead || awaitingRole || _panel.IsOpen ? string.Empty : JoinLines(emplacements?.PromptFor(combat),
			ResupplyLine(combat, emplacements, tick));
	}

	/// <summary>
	/// The death screen's line (docs/LOADOUT.md §7.3): <c>next spawn: rifle · 5 mags · 3
	/// grenades · no vest · 9.3 kg · 100 %   [E] edit</c>.
	/// </summary>
	private static string NextSpawnLine(in LoadoutSelection kit, bool editable) =>
		$"next spawn: {WeaponCatalog.DescribeWithLoad(kit)}{(editable ? "   [E] edit" : string.Empty)}";

	/// <summary>
	/// Health, the vest's points and grenades (docs/LOADOUT.md §7.4): <c>HP 100   vest 60
	/// grenades 3</c>. No vest says nothing about one; a spent vest says 0, because it is
	/// still worn, still weighs what it weighs, and the locker can fill it.
	/// </summary>
	private static string StatusLine(int health, in BodyArmourState vest, int grenades) =>
		vest.Tier == BodyArmourTier.None
			? $"HP {health}   grenades {grenades}"
			: $"HP {health}   vest {vest.Points}   grenades {grenades}";

	/// <summary>
	/// What a weapon locker's resupply is doing for a player in its reach
	/// (docs/LOADOUT.md §7.4): <c>resupplying — 4/5 mags · 2/3 grenades · 40/60 vest</c>,
	/// counting full magazines, for whatever the kit holds that is not yet full; or
	/// <c>resupply waits: under fire</c> inside the three seconds after a hit. Nothing
	/// out of reach, and nothing once the kit is full. A client's "hit" is its record's
	/// health or vest falling, a one-way trip late (<see cref="PlayerCombat.LastDamagedTick"/>).
	/// </summary>
	private static string ResupplyLine(PlayerCombat combat, EmplacementManager lockers, uint tick)
	{
		if (lockers == null || combat.Character == null || !combat.IsAlive
			|| !LockerSupply.InReach(lockers.LockerDistance(combat.Character.SimPosition)))
		{
			return string.Empty;
		}

		LoadoutSelection kit = combat.Loadout;
		CarryStats primary = WeaponCatalog.CarryFor(kit.Large);
		KitState carried = combat.Carried;
		if (!LockerSupply.NeedsAnything(carried, kit, primary))
		{
			return string.Empty;
		}

		if (LockerSupply.IsLockedOut(tick, combat.LastDamagedTick))
		{
			return "resupply waits: under fire";
		}

		KitState full = LockerSupply.Ceiling(kit, primary);
		var parts = new List<string>(3);
		if (kit.Magazines > 0 && primary.PouchRounds > 0 && carried.Reserve >= 0)
		{
			parts.Add($"{carried.Reserve / primary.PouchRounds}/{kit.Magazines} mags");
		}

		if (full.Grenades > 0)
		{
			parts.Add($"{carried.Grenades}/{full.Grenades} grenades");
		}

		if (full.ArmourPoints > 0)
		{
			parts.Add($"{carried.ArmourPoints}/{full.ArmourPoints} vest");
		}

		return $"resupplying — {string.Join(" · ", parts)}";
	}

	private static string JoinLines(string first, string second) =>
		string.IsNullOrEmpty(first) ? second ?? string.Empty
		: string.IsNullOrEmpty(second) ? first
		: $"{first}\n{second}";

	/// <summary>
	/// What the kit weighs and the share of their speed it leaves the player
	/// (docs/LOADOUT.md §7.4): <c>load 15.3 kg · 88 %</c>. The kit as taken, not as
	/// spent (L-D5), so it moves at a spawn or a locker and nowhere else.
	/// </summary>
	private static string LoadLine(float kg, float scale) => $"load {kg:0.0} kg · {scale * 100f:0} %";

	/// <summary>
	/// The ammunition corner for a weapon in the hands (docs/LOADOUT.md §7.4): magazine
	/// and reserve — <c>rifle  30 / 150</c>, <c>pistol  12 / ∞</c>, <c>rifle  0 / 0  dry</c>
	/// once both are spent — the hammer's <c>--</c>, and <c>—</c> for an empty slot.
	/// </summary>
	private static string AmmoLine(byte id, in WeaponState state, in WeaponStats stats)
	{
		if (state.IsEmpty)
		{
			return "—";
		}

		string weapon = WeaponCatalog.NameOf(id);
		if (stats.HasUnlimitedAmmo)
		{
			return $"{weapon}\n--";
		}

		string reserve = state.HasUnlimitedReserve ? "∞" : state.Reserve.ToString();
		string after = state.IsReloading ? "  reloading"
			: state.Ammo <= 0 && state.Reserve == 0 ? "  dry"
			: string.Empty;
		return $"{weapon}\n{state.Ammo} / {reserve}{after}";
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
