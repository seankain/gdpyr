using Gdpyr.Core;
using Gdpyr.Match;
using Godot;

namespace Gdpyr.Ui;

/// <summary>
/// The large-weapon picker: DMR, rifle or launcher, chosen with 1/2/3 while dead
/// or between rounds and applied on the next spawn
/// (docs/IMPLEMENTATION_PLAN.md §M2).
///
/// Keyboard only, and no mouse capture change, so that choosing a weapon cannot
/// fight with the pause menu or drop the player out of mouse look. The choice is a
/// request: the server sanitizes it and decides what actually spawns
/// (docs/NETCODE.md §1).
/// </summary>
public partial class LoadoutMenu : Control
{
	private readonly Label[] _entries = new Label[3];
	private Label _title;
	private byte _selected = WeaponCatalog.Rifle;

	public override void _Ready()
	{
		MouseFilter = MouseFilterEnum.Ignore;
		AnchorLeft = 0.5f;
		AnchorTop = 0.5f;
		Position = new Vector2(-180f, -60f);
		Size = new Vector2(360f, 160f);
		Visible = false;

		var box = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore, Size = new Vector2(360f, 160f) };
		AddChild(box);

		_title = NewLabel(box, "loadout — press 1/2/3");
		for (int i = 0; i < _entries.Length; i++)
		{
			_entries[i] = NewLabel(box, string.Empty);
		}
	}

	public override void _Input(InputEvent @event)
	{
		if (!Visible)
		{
			return;
		}

		// The same keys that switch weapon slots when alive. There is no conflict:
		// the simulation ignores a dead player's input entirely, so while this panel
		// is up the keys mean nothing else.
		if (@event.IsActionPressed("weapon_melee")) { Select(0); }
		else if (@event.IsActionPressed("weapon_sidearm")) { Select(1); }
		else if (@event.IsActionPressed("weapon_large")) { Select(2); }
	}

	public void Refresh(bool visible, byte pending)
	{
		Visible = visible;
		if (!visible)
		{
			return;
		}

		_selected = pending;
		for (int i = 0; i < _entries.Length; i++)
		{
			byte id = WeaponCatalog.LargeWeapons[i];
			bool chosen = id == _selected;
			_entries[i].Text = $"{(chosen ? ">" : " ")} {i + 1}. {WeaponCatalog.NameOf(id)}";
			_entries[i].SelfModulate = chosen ? Colors.White : new Color(0.7f, 0.7f, 0.7f);
		}

		_title.Text = "loadout — press 1/2/3";
	}

	private void Select(int index)
	{
		if (index < 0 || index >= WeaponCatalog.LargeWeapons.Length)
		{
			return;
		}

		_selected = WeaponCatalog.LargeWeapons[index];
		CombatManager.Instance?.RequestLoadout(_selected);
		Refresh(true, _selected);
	}

	private static Label NewLabel(Control parent, string text)
	{
		var label = new Label
		{
			Text = text,
			MouseFilter = MouseFilterEnum.Ignore,
		};
		label.AddThemeColorOverride("font_outline_color", Colors.Black);
		label.AddThemeConstantOverride("outline_size", 4);
		parent.AddChild(label);
		return label;
	}
}
