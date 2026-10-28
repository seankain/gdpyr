using System;
using Gdpyr.Sim;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// The loadout panel's model (docs/LOADOUT.md §7.2, §7.3, L-D12): every row, every
/// limit, every key, every answer from the locker and every reason the panel closes,
/// without a window.
/// </summary>
public class LoadoutDraftTests
{
	// The catalog's ids and §4.1's numbers, as LoadoutTests has them; the panel's
	// primaries in its own order, none first and the default's rifle next.
	private const byte Hammer = 0;
	private const byte Pistol = 1;
	private const byte Dmr = 2;
	private const byte Rifle = 3;
	private const byte Launcher = 4;
	private static readonly byte[] LargeWeapons = { Dmr, Rifle, Launcher };
	private static readonly byte[] Primaries = { Loadout.NoPrimary, Rifle, Dmr, Launcher };

	private static readonly CarryStats[] Carry =
	{
		default,
		default,
		new(4.4f, 10, 0.6f),
		new(3.6f, 30, 0.5f),
		new(6.0f, 2, 0.9f),
	};

	private static readonly int[] Magazines = { 0, 12, 10, 30, 1 };
	private static readonly string[] Names = { "geopick", "pistol", "dmr", "rifle", "launcher" };

	private static LoadoutSelection Kit(byte large, int magazines, int grenades,
		BodyArmourTier armour = BodyArmourTier.None) => new()
	{
		Melee = Hammer,
		Sidearm = Pistol,
		Large = large,
		Magazines = (byte)magazines,
		Grenades = (byte)grenades,
		Armour = armour,
	};

	private static readonly LoadoutSelection Default = Kit(Rifle, Loadout.DefaultMagazines, Loadout.DefaultGrenades);

	private static DraftCatalog Catalog() => new(Primaries, LargeWeapons, Carry, Magazines, Names, Default,
		Loadout.WeightKg(Default, Carry), walkSpeed: 5f, sprintSpeed: 8f, referenceRound: "rifle",
		referenceDamage: 20f);

	private static LoadoutDraft Opened(LoadoutSelection kit, DraftMode mode = DraftMode.Locker)
	{
		var draft = new LoadoutDraft(Catalog());
		draft.Open(mode, kit);
		return draft;
	}

	private static DraftSituation At(float distance = 0.5f, bool alive = true, bool ground = true,
		bool awaitingRole = false, bool roundEnded = false) =>
		new(alive, ground, awaitingRole, roundEnded, distance);

	// ---- keys -----------------------------------------------------------------

	[Theory]
	[InlineData(Key.Key1, DraftAction.Primary1)]
	[InlineData(Key.Key2, DraftAction.Primary2)]
	[InlineData(Key.Key3, DraftAction.Primary3)]
	[InlineData(Key.Key4, DraftAction.Primary4)]
	[InlineData(Key.Key5, DraftAction.FewerMagazines)]
	[InlineData(Key.Key6, DraftAction.MoreMagazines)]
	[InlineData(Key.Key7, DraftAction.FewerGrenades)]
	[InlineData(Key.Key8, DraftAction.MoreGrenades)]
	[InlineData(Key.Key9, DraftAction.LighterVest)]
	[InlineData(Key.Key0, DraftAction.HeavierVest)]
	[InlineData(Key.Enter, DraftAction.Take)]
	[InlineData(Key.KpEnter, DraftAction.Take)]
	[InlineData(Key.Backspace, DraftAction.Revert)]
	[InlineData(Key.D, DraftAction.Default)]
	[InlineData(Key.E, DraftAction.Close)]
	[InlineData(Key.Escape, DraftAction.Close)]
	[InlineData(Key.W, DraftAction.None)]
	[InlineData(Key.Space, DraftAction.None)]
	[InlineData(Key.F1, DraftAction.None)]
	public void EachKeyMeansOneThing(Key key, DraftAction action) => Assert.Equal(action, LoadoutDraft.ActionOf(key));

	// ---- opening --------------------------------------------------------------

	[Fact]
	public void ItOpensOnTheKitItIsGivenAndThatKitIsWhatRevertGoesBackTo()
	{
		LoadoutSelection carried = Kit(Dmr, 4, 1, BodyArmourTier.Light);
		LoadoutDraft draft = Opened(carried);

		Assert.True(draft.IsOpen);
		Assert.Equal(DraftMode.Locker, draft.Mode);
		Assert.Equal(carried, draft.Kit);
		Assert.Equal(carried, draft.Original);
		Assert.False(draft.Awaiting);
		Assert.False(draft.IsEnabled(DraftAction.Revert));

		draft.Apply(DraftAction.HeavierVest);
		Assert.True(draft.IsEnabled(DraftAction.Revert));
		Assert.Equal(DraftOutcome.Edited, draft.Apply(DraftAction.Revert));
		Assert.Equal(carried, draft.Kit);
	}

	[Fact]
	public void AKitTheRulesWouldTrimIsTrimmedOnOpening()
	{
		// Nine magazines beside three grenades do not fit; grenades are kept first (§4.1).
		LoadoutDraft draft = Opened(Kit(Rifle, 9, 3));
		Assert.Equal(Kit(Rifle, 6, 3), draft.Kit);

		// An unknown primary is the rifle, and the hammer and the pistol are always carried.
		LoadoutSelection hostile = Kit(200, 2, 7, (BodyArmourTier)9);
		hostile.Melee = 99;
		draft = Opened(hostile);
		Assert.Equal(Kit(Rifle, 2, 3), draft.Kit);
	}

	[Fact]
	public void NothingIsEnabledOnAClosedPanel()
	{
		var draft = new LoadoutDraft(Catalog());
		Assert.False(draft.IsOpen);
		foreach (DraftAction action in Enum.GetValues<DraftAction>())
		{
			Assert.False(draft.IsEnabled(action), action.ToString());
			Assert.Equal(DraftOutcome.None, draft.Apply(action));
		}
	}

	// ---- the rows -------------------------------------------------------------

	[Theory]
	[InlineData(DraftAction.Primary1, Loadout.NoPrimary)]
	[InlineData(DraftAction.Primary2, Rifle)]
	[InlineData(DraftAction.Primary3, Dmr)]
	[InlineData(DraftAction.Primary4, Launcher)]
	public void ThePrimaryKeysAreNoneRifleDmrLauncher(DraftAction key, byte primary)
	{
		LoadoutDraft draft = Opened(Kit(Launcher, 2, 0));
		draft.Apply(key);
		Assert.Equal(primary, draft.Kit.Large);
	}

	[Fact]
	public void NoPrimaryCarriesNoMagazinesAndFreesTheirPouches()
	{
		LoadoutDraft draft = Opened(Default);
		Assert.Equal(DraftOutcome.Edited, draft.Apply(DraftAction.Primary1));
		Assert.Equal(Kit(Loadout.NoPrimary, 0, 3), draft.Kit);
		Assert.False(draft.IsEnabled(DraftAction.MoreMagazines));
		Assert.False(draft.IsEnabled(DraftAction.FewerMagazines));

		// Back to a primary, the magazines start from none: only the counts mean anything.
		draft.Apply(DraftAction.Primary2);
		Assert.Equal(Kit(Rifle, 0, 3), draft.Kit);
		Assert.True(draft.IsEnabled(DraftAction.MoreMagazines));
	}

	[Fact]
	public void ChoosingThePrimaryAlreadyChosenDoesNothing()
	{
		LoadoutDraft draft = Opened(Default);
		Assert.False(draft.IsEnabled(DraftAction.Primary2));
		Assert.Equal(DraftOutcome.None, draft.Apply(DraftAction.Primary2));
		Assert.Equal(DraftOutcome.None, draft.ChoosePrimary(1));
		Assert.Equal(DraftOutcome.Edited, draft.ChoosePrimary(2));
		Assert.Equal(Dmr, draft.Kit.Large);
		Assert.Equal(DraftOutcome.None, draft.ChoosePrimary(4));
		Assert.Equal(DraftOutcome.None, draft.ChoosePrimary(-1));
	}

	[Fact]
	public void MagazinesStopAtNinePouchesAndAtNone()
	{
		LoadoutDraft draft = Opened(Kit(Rifle, 0, 0));
		Assert.False(draft.IsEnabled(DraftAction.FewerMagazines));
		Assert.Equal(DraftOutcome.None, draft.Apply(DraftAction.FewerMagazines));

		for (int i = 1; i <= Loadout.PouchSlots; i++)
		{
			Assert.Equal(DraftOutcome.Edited, draft.Apply(DraftAction.MoreMagazines));
			Assert.Equal(i, draft.Kit.Magazines);
		}

		Assert.False(draft.IsEnabled(DraftAction.MoreMagazines));
		Assert.Equal(DraftOutcome.None, draft.Apply(DraftAction.MoreMagazines));
		Assert.Equal(9, draft.Kit.Magazines);

		// The pouches are one budget: no grenade fits beside nine magazines.
		Assert.False(draft.IsEnabled(DraftAction.MoreGrenades));
		draft.Apply(DraftAction.FewerMagazines);
		Assert.True(draft.IsEnabled(DraftAction.MoreGrenades));
	}

	[Fact]
	public void GrenadesStopAtThreeAtNinePouchesAndAtNone()
	{
		LoadoutDraft draft = Opened(Kit(Rifle, 0, 0));
		Assert.False(draft.IsEnabled(DraftAction.FewerGrenades));

		for (int i = 1; i <= SimConfig.MaxGrenades; i++)
		{
			Assert.Equal(DraftOutcome.Edited, draft.Apply(DraftAction.MoreGrenades));
		}

		Assert.Equal(3, draft.Kit.Grenades);
		Assert.False(draft.IsEnabled(DraftAction.MoreGrenades));
		Assert.Equal(DraftOutcome.None, draft.Apply(DraftAction.MoreGrenades));

		draft = Opened(Kit(Rifle, 8, 1));
		Assert.False(draft.IsEnabled(DraftAction.MoreGrenades));
		Assert.False(draft.IsEnabled(DraftAction.MoreMagazines));
		Assert.True(draft.IsEnabled(DraftAction.FewerGrenades));
		draft.Apply(DraftAction.FewerGrenades);
		Assert.Equal(Kit(Rifle, 8, 0), draft.Kit);
	}

	[Fact]
	public void TheVestStepsFromNoneToHeavyAndClicksStraightToATier()
	{
		LoadoutDraft draft = Opened(Default);
		Assert.False(draft.IsEnabled(DraftAction.LighterVest));

		BodyArmourTier[] steps = { BodyArmourTier.Light, BodyArmourTier.Medium, BodyArmourTier.Heavy };
		foreach (BodyArmourTier tier in steps)
		{
			Assert.Equal(DraftOutcome.Edited, draft.Apply(DraftAction.HeavierVest));
			Assert.Equal(tier, draft.Kit.Armour);
		}

		Assert.False(draft.IsEnabled(DraftAction.HeavierVest));
		Assert.Equal(DraftOutcome.None, draft.Apply(DraftAction.HeavierVest));
		draft.Apply(DraftAction.LighterVest);
		Assert.Equal(BodyArmourTier.Medium, draft.Kit.Armour);

		Assert.Equal(DraftOutcome.Edited, draft.ChooseVest(BodyArmourTier.None));
		Assert.Equal(BodyArmourTier.None, draft.Kit.Armour);
		Assert.Equal(DraftOutcome.None, draft.ChooseVest(BodyArmourTier.None));
		Assert.Equal(DraftOutcome.None, draft.ChooseVest((BodyArmourTier)4));
		Assert.Equal(DraftOutcome.Edited, draft.ChooseVest(BodyArmourTier.Heavy));
		Assert.Equal(BodyArmourTier.Heavy, draft.Kit.Armour);
	}

	[Fact]
	public void DefaultIsTheDefaultKitAndIsDisabledInIt()
	{
		LoadoutDraft draft = Opened(Kit(Launcher, 9, 0, BodyArmourTier.Heavy));
		Assert.True(draft.IsEnabled(DraftAction.Default));
		Assert.Equal(DraftOutcome.Edited, draft.Apply(DraftAction.Default));
		Assert.Equal(Default, draft.Kit);
		Assert.False(draft.IsEnabled(DraftAction.Default));
		Assert.Equal(DraftOutcome.None, draft.Apply(DraftAction.Default));
	}

	[Fact]
	public void NoSequenceOfKeysLeavesAKitTheValidatorWouldRefuse()
	{
		// The panel asks for what it holds; a kit the locker would refuse as invalid is
		// one the panel must never hold (docs/LOADOUT.md §5.3).
		var random = new Random(6);
		DraftAction[] edits =
		{
			DraftAction.Primary1, DraftAction.Primary2, DraftAction.Primary3, DraftAction.Primary4,
			DraftAction.FewerMagazines, DraftAction.MoreMagazines, DraftAction.FewerGrenades,
			DraftAction.MoreGrenades, DraftAction.LighterVest, DraftAction.HeavierVest, DraftAction.Revert,
			DraftAction.Default,
		};

		LoadoutDraft draft = Opened(Kit(Dmr, 3, 2, BodyArmourTier.Light));
		for (int i = 0; i < 5000; i++)
		{
			DraftAction action = edits[random.Next(edits.Length)];
			bool enabled = draft.IsEnabled(action);
			LoadoutSelection before = draft.Kit;
			DraftOutcome outcome = draft.Apply(action);

			Assert.Equal(enabled ? DraftOutcome.Edited : DraftOutcome.None, outcome);
			Assert.Equal(enabled, before != draft.Kit);
			Assert.True(Loadout.IsValid(draft.Kit, LargeWeapons), $"{action} after {i}");
			Assert.Equal(Hammer, draft.Kit.Melee);
			Assert.Equal(Pistol, draft.Kit.Sidearm);
			Assert.InRange(draft.Kit.Magazines + draft.Kit.Grenades, 0, Loadout.PouchSlots);
		}
	}

	// ---- taking it ------------------------------------------------------------

	[Fact]
	public void AtALockerEnterAsksAndWaitsAndTakenCloses()
	{
		LoadoutDraft draft = Opened(Default);
		draft.Apply(DraftAction.Primary4);

		Assert.Equal(DraftOutcome.AskLocker, draft.Apply(DraftAction.Take));
		Assert.True(draft.Awaiting);
		Assert.True(draft.IsOpen);
		Assert.Equal("asking the locker…", draft.StatusLine);

		// A second Enter while the answer is on its way sends nothing.
		Assert.False(draft.IsEnabled(DraftAction.Take));
		Assert.Equal(DraftOutcome.None, draft.Apply(DraftAction.Take));

		Assert.Equal(DraftOutcome.Close, draft.Answer(KitRefusal.None));
		Assert.False(draft.IsOpen);
		Assert.False(draft.Awaiting);
		Assert.Equal(Launcher, draft.Kit.Large);
	}

	[Theory]
	[InlineData(KitRefusal.Dead, "dead")]
	[InlineData(KitRefusal.NotAtLocker, "not_at_locker")]
	[InlineData(KitRefusal.Mounted, "mounted")]
	[InlineData(KitRefusal.Carrying, "carrying")]
	[InlineData(KitRefusal.Invalid, "invalid")]
	[InlineData(KitRefusal.TooSoon, "too_soon")]
	public void ARefusalGoesInTheFooterAndLeavesThePanelOpenToTryAgain(KitRefusal refusal, string code)
	{
		LoadoutDraft draft = Opened(Default);
		draft.Apply(DraftAction.HeavierVest);
		LoadoutSelection asked = draft.Kit;
		draft.Apply(DraftAction.Take);

		Assert.Equal(DraftOutcome.Edited, draft.Answer(refusal));
		Assert.True(draft.IsOpen);
		Assert.False(draft.Awaiting);
		Assert.Equal(refusal, draft.LastRefusal);
		Assert.Equal(asked, draft.Kit);
		Assert.StartsWith($"refused ({code}): ", draft.StatusLine);
		Assert.NotEqual(string.Empty, LoadoutDraft.Explain(refusal));
		Assert.True(draft.IsEnabled(DraftAction.Take));

		// Taking it again clears the footer for the new answer; so does an edit.
		Assert.Equal(DraftOutcome.AskLocker, draft.Apply(DraftAction.Take));
		Assert.Equal(KitRefusal.None, draft.LastRefusal);
		draft.Answer(refusal);
		draft.Apply(DraftAction.LighterVest);
		Assert.Equal(KitRefusal.None, draft.LastRefusal);
		Assert.Equal(string.Empty, draft.StatusLine);
	}

	[Fact]
	public void AnAnswerToNothingIsIgnored()
	{
		LoadoutDraft draft = Opened(Default);
		Assert.Equal(DraftOutcome.None, draft.Answer(KitRefusal.None));
		Assert.True(draft.IsOpen);

		// Asked, closed with E and opened again before the answer came: the answer is the
		// old panel's, and the new one has asked for nothing.
		draft.Apply(DraftAction.Take);
		draft.Apply(DraftAction.Close);
		draft.Open(DraftMode.Locker, Default);
		Assert.Equal(DraftOutcome.None, draft.Answer(KitRefusal.TooSoon));
		Assert.Equal(KitRefusal.None, draft.LastRefusal);
		Assert.True(draft.IsOpen);
	}

	[Fact]
	public void OnTheDeathScreenEnterKeepsTheKitForTheNextSpawnAndCloses()
	{
		LoadoutDraft draft = Opened(Default, DraftMode.NextSpawn);
		draft.Apply(DraftAction.Primary3);

		Assert.Equal(DraftOutcome.SetNextSpawn, draft.Apply(DraftAction.Take));
		Assert.False(draft.IsOpen);
		Assert.False(draft.Awaiting);
		Assert.Equal(Kit(Dmr, 5, 3), draft.Kit);
		Assert.Equal(DraftOutcome.None, draft.Answer(KitRefusal.None));
	}

	[Fact]
	public void CloseClosesWithoutTakingAnything()
	{
		LoadoutDraft draft = Opened(Default);
		draft.Apply(DraftAction.Primary4);
		Assert.Equal(DraftOutcome.Close, draft.Apply(DraftAction.Close));
		Assert.False(draft.IsOpen);
		Assert.False(draft.Awaiting);
	}

	// ---- when it closes itself (L-D12) ----------------------------------------

	[Fact]
	public void AtALockerItStaysWhileAliveOnTheGroundInReachAndTheRoundIsOn()
	{
		LoadoutDraft draft = Opened(Default);
		Assert.True(draft.StillWanted(At()));
		Assert.True(draft.StillWanted(At(distance: 0f)));
		Assert.True(draft.StillWanted(At(distance: 0.99f)));
		Assert.True(draft.StillWanted(At(distance: 1f)));

		Assert.False(draft.StillWanted(At(distance: 1.01f)));
		Assert.False(draft.StillWanted(At(distance: float.PositiveInfinity)));
		Assert.False(draft.StillWanted(At(alive: false)));
		Assert.False(draft.StillWanted(At(ground: false)));
		Assert.False(draft.StillWanted(At(awaitingRole: true)));
		Assert.False(draft.StillWanted(At(roundEnded: true)));
	}

	[Fact]
	public void OnTheDeathScreenItStaysUntilTheSpawn()
	{
		LoadoutDraft draft = Opened(Default, DraftMode.NextSpawn);

		// Dead, anywhere: the death screen is not at the locker.
		Assert.True(draft.StillWanted(At(alive: false, distance: float.PositiveInfinity)));

		Assert.False(draft.StillWanted(At(alive: true)));
		Assert.False(draft.StillWanted(At(alive: false, ground: false)));
		Assert.False(draft.StillWanted(At(alive: false, awaitingRole: true)));

		// The round-over scoreboard takes the middle of the screen.
		Assert.False(draft.StillWanted(At(alive: false, roundEnded: true)));
	}

	[Fact]
	public void AClosedPanelIsNotWanted()
	{
		LoadoutDraft draft = Opened(Default);
		draft.Close();
		Assert.False(draft.StillWanted(At()));
	}

	// ---- read-outs (§7.2) -----------------------------------------------------

	[Theory]
	// §4.1's kit table, as the panel prints it.
	[InlineData(Loadout.NoPrimary, 0, 0, BodyArmourTier.None, "LOAD 2.0 kg   SPEED 112 %   walk 5.6 · sprint 9.0 m/s")]
	[InlineData(Rifle, 5, 3, BodyArmourTier.None, "LOAD 9.3 kg   SPEED 100 %   walk 5.0 · sprint 8.0 m/s")]
	[InlineData(Rifle, 5, 3, BodyArmourTier.Medium, "LOAD 15.3 kg   SPEED 88 %   walk 4.4 · sprint 7.0 m/s")]
	[InlineData(Dmr, 5, 3, BodyArmourTier.Medium, "LOAD 16.6 kg   SPEED 85 %   walk 4.3 · sprint 6.8 m/s")]
	[InlineData(Launcher, 9, 0, BodyArmourTier.Heavy, "LOAD 26.1 kg   SPEED 70 %   walk 3.5 · sprint 5.6 m/s")]
	public void TheLoadLineIsTheKitTable(byte large, int magazines, int grenades, BodyArmourTier armour, string line)
	{
		LoadoutDraft draft = Opened(Kit(large, magazines, grenades, armour));
		Assert.Equal(line, draft.LoadLine);
	}

	[Theory]
	// §4.3's rifle row: 5, 7, 8 and 10 hits.
	[InlineData(BodyArmourTier.None, "VEST none   rifle hits to kill 5")]
	[InlineData(BodyArmourTier.Light, "VEST 35 pts, absorbs 35 %   rifle hits to kill 7 (5 without)")]
	[InlineData(BodyArmourTier.Medium, "VEST 60 pts, absorbs 50 %   rifle hits to kill 8 (5 without)")]
	[InlineData(BodyArmourTier.Heavy, "VEST 100 pts, absorbs 65 %   rifle hits to kill 10 (5 without)")]
	public void TheVestLineSaysWhatItStands(BodyArmourTier tier, string line)
	{
		LoadoutDraft draft = Opened(Kit(Rifle, 5, 3, tier));
		Assert.Equal(line, draft.VestLine);
	}

	[Theory]
	[InlineData(Rifle, 5, 3, "AMMO 30 + 150 rifle · 3 grenades")]
	[InlineData(Rifle, 9, 0, "AMMO 30 + 270 rifle · 0 grenades")]
	[InlineData(Dmr, 2, 1, "AMMO 10 + 20 dmr · 1 grenade")]
	[InlineData(Launcher, 3, 0, "AMMO 1 + 6 launcher · 0 grenades")]
	[InlineData(Loadout.NoPrimary, 0, 3, "AMMO pistol only · 3 grenades")]
	public void TheAmmoLineIsAMagazineAndThePouches(byte large, int magazines, int grenades, string line)
	{
		LoadoutDraft draft = Opened(Kit(large, magazines, grenades));
		Assert.Equal(line, draft.AmmoLine);
	}

	[Theory]
	[InlineData(Rifle, 5, 3, "▣▣▣▣▣◆◆◆□")]
	[InlineData(Rifle, 9, 0, "▣▣▣▣▣▣▣▣▣")]
	[InlineData(Loadout.NoPrimary, 0, 1, "◆□□□□□□□□")]
	[InlineData(Rifle, 0, 0, "□□□□□□□□□")]
	public void ThePouchesAreDrawnMagazinesFirst(byte large, int magazines, int grenades, string cells)
	{
		Assert.Equal(cells, Opened(Kit(large, magazines, grenades)).PouchCells);
	}

	[Fact]
	public void TheRadioLabelsNameTheirKeyAndWeight()
	{
		LoadoutDraft draft = Opened(Default);
		Assert.Equal("1 none", draft.PrimaryLabel(0));
		Assert.Equal("2 rifle 3.6 kg", draft.PrimaryLabel(1));
		Assert.Equal("3 dmr 4.4 kg", draft.PrimaryLabel(2));
		Assert.Equal("4 launcher 6.0 kg", draft.PrimaryLabel(3));
		Assert.Equal(string.Empty, draft.PrimaryLabel(4));

		Assert.Equal("none", LoadoutDraft.VestLabel(BodyArmourTier.None));
		Assert.Equal("light 3 kg", LoadoutDraft.VestLabel(BodyArmourTier.Light));
		Assert.Equal("medium 6 kg", LoadoutDraft.VestLabel(BodyArmourTier.Medium));
		Assert.Equal("heavy 10 kg", LoadoutDraft.VestLabel(BodyArmourTier.Heavy));
	}

	[Fact]
	public void TheTitleAndTheKeysSayWhichPanelItIs()
	{
		LoadoutDraft draft = Opened(Default);
		Assert.Equal("WEAPON LOCKER", draft.Title);
		Assert.Contains("[Enter] take this kit", draft.KeysLine);
		Assert.Contains("[Backspace] what I carry", draft.KeysLine);

		draft.Open(DraftMode.NextSpawn, Default);
		Assert.Equal("NEXT SPAWN", draft.Title);
		Assert.Contains("[Enter] keep for the next spawn", draft.KeysLine);
	}

	[Fact]
	public void TheReadOutsFollowTheKitAsItIsEdited()
	{
		LoadoutDraft draft = Opened(Default);
		Assert.Equal(1f, draft.SpeedScale);
		Assert.Equal(150, draft.Reserve);

		draft.Apply(DraftAction.HeavierVest);
		draft.Apply(DraftAction.HeavierVest);
		Assert.Equal(15.3f, draft.WeightKg, 3);
		Assert.Equal(8, draft.HitsToKill);
		Assert.Equal(5, draft.HitsToKillBare);

		draft.Apply(DraftAction.FewerMagazines);
		Assert.Equal(120, draft.Reserve);
		Assert.True(draft.SpeedScale > 0.88f);
	}
}
