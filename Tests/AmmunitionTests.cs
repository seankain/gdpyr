using System;
using System.Collections.Generic;
using Gdpyr.Sim;
using Gdpyr.Ui;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// A strategist unit's ammunition (docs/NETCODE.md §10.7): the rule (<see cref="Ammunition"/>),
/// what the unit files give each unit, the strategist-only message that carries it
/// (<see cref="AmmoStateCodec"/>), the card's readout of it, and the colour of the health
/// bar drawn over every unit (<see cref="UnitBars"/>).
/// </summary>
public class AmmunitionTests
{
	// ---- the rule -------------------------------------------------------------

	[Theory]
	[InlineData(30, 30)]
	[InlineData(1, 1)]
	[InlineData(0, Ammunition.Unlimited)]
	[InlineData(-5, Ammunition.Unlimited)]
	[InlineData(int.MaxValue, Ammunition.MaxCarried)]
	public void AFullLoad_IsWhatTheDefinitionCarries_AndNothingNeverRunsOut(int maxRounds, int expected)
	{
		Assert.Equal(expected, Ammunition.Full(maxRounds));
	}

	[Fact]
	public void ATank_FiresThirtyRounds_ThenNothing_UntilItIsResupplied()
	{
		int rounds = Ammunition.Full(30);
		int fired = 0;
		for (int i = 0; i < 100 && Ammunition.CanFire(rounds); i++)
		{
			rounds = Ammunition.Spend(rounds);
			fired++;
		}

		Assert.Equal(30, fired);
		Assert.Equal(0, rounds);
		Assert.False(Ammunition.CanFire(rounds));

		// Out of reach nothing comes back; in reach, everything does at once.
		Assert.Equal(0, Ammunition.Rearm(rounds, 30, supplied: false));
		Assert.Equal(30, Ammunition.Rearm(rounds, 30, supplied: true));
	}

	[Fact]
	public void AUnitThatCarriesNothing_NeverRunsOut()
	{
		int rounds = Ammunition.Full(0);
		for (int i = 0; i < 1000; i++)
		{
			rounds = Ammunition.Spend(rounds);
		}

		Assert.Equal(Ammunition.Unlimited, rounds);
		Assert.True(Ammunition.CanFire(rounds));
		Assert.True(Ammunition.IsFull(rounds, 0));
		Assert.Equal(1f, Ammunition.Fraction(rounds, 0));
		Assert.False(Ammunition.IsBelow(rounds, 0, 0.25f));
		Assert.Equal(Ammunition.Unlimited, Ammunition.Rearm(rounds, 0, supplied: true));
	}

	[Fact]
	public void AnEmptyCount_StaysEmpty_WhenSpent()
	{
		Assert.Equal(0, Ammunition.Spend(0));
	}

	[Theory]
	[InlineData(30, 30, true)]
	[InlineData(29, 30, false)]
	[InlineData(0, 30, false)]
	[InlineData(Ammunition.Unlimited, 0, true)]
	public void IsFull_OnlyWithEverythingItCarries(int rounds, int maxRounds, bool expected)
	{
		Assert.Equal(expected, Ammunition.IsFull(rounds, maxRounds));
	}

	[Theory]
	[InlineData(30, 30, 1f)]
	[InlineData(15, 30, 0.5f)]
	[InlineData(0, 30, 0f)]
	[InlineData(40, 30, 1f)]
	public void Fraction_IsTheShareLeft(int rounds, int maxRounds, float expected)
	{
		Assert.Equal(expected, Ammunition.Fraction(rounds, maxRounds), 4);
	}

	[Theory]
	[InlineData(8, false)]
	[InlineData(7, true)]
	[InlineData(0, true)]
	public void ATank_IsLow_UnderAQuarterOfItsRounds(int rounds, bool expected)
	{
		Assert.Equal(expected, Ammunition.IsBelow(rounds, 30, 0.25f));
	}

	// ---- the unit files -------------------------------------------------------

	[Fact]
	public void TheTank_CarriesThirtyMainGunRounds()
	{
		Dictionary<string, string> tank = TrainedUnitResourceTests.Resource("Units/tank.tres");

		Assert.Equal(30, TrainedUnitResourceTests.Int(tank, "MaxRounds"));
		Assert.Equal("6", tank["WeaponId"]);
	}

	[Theory]
	[InlineData("infantry")]
	[InlineData("technical")]
	[InlineData("tank")]
	[InlineData("builder")]
	[InlineData("supply_truck")]
	[InlineData("sniper")]
	[InlineData("heavy_gunner")]
	public void EveryUnitWithAGun_CarriesAFiniteLoad_OfAtLeastAMagazine(string unit)
	{
		Dictionary<string, string> definition = TrainedUnitResourceTests.Resource($"Units/{unit}.tres");
		int rounds = TrainedUnitResourceTests.Int(definition, "MaxRounds");
		Assert.InRange(rounds, 1, Ammunition.MaxCarried);

		string weapon = TrainedUnitResourceTests.WeaponPath(TrainedUnitResourceTests.Int(definition, "WeaponId"))
			.Replace("res://", string.Empty, StringComparison.Ordinal);
		Dictionary<string, string> stats = TrainedUnitResourceTests.Resource(weapon);
		Assert.True(rounds >= TrainedUnitResourceTests.Int(stats, "MagazineSize"),
			$"{unit} carries {rounds} rounds, less than one magazine of {weapon}");
	}

	[Fact]
	public void TheDropship_HasNoGun_AndSoNothingToRunOutOf()
	{
		Dictionary<string, string> dropship = TrainedUnitResourceTests.Resource("Units/dropship.tres");

		Assert.False(dropship.ContainsKey("MaxRounds"));
		Assert.Equal("0.0", dropship["EngageRangeMeters"]);
	}

	// ---- the wire -------------------------------------------------------------

	[Fact]
	public void TheState_RoundTrips()
	{
		var records = new[]
		{
			new UnitAmmoRecord { UnitId = 7, Rounds = 0 },
			new UnitAmmoRecord { UnitId = 300, Rounds = 29 },
			new UnitAmmoRecord { UnitId = ushort.MaxValue, Rounds = ushort.MaxValue },
		};

		byte[] payload = AmmoStateCodec.Encode(records);
		Assert.Equal(AmmoStateCodec.PayloadBytes(3), payload.Length);

		var into = new UnitAmmoRecord[AmmoStateCodec.MaxRecords];
		Assert.True(AmmoStateCodec.TryDecode(payload, into, out int count));
		Assert.Equal(3, count);
		for (int i = 0; i < count; i++)
		{
			Assert.Equal(records[i].UnitId, into[i].UnitId);
			Assert.Equal(records[i].Rounds, into[i].Rounds);
		}
	}

	[Fact]
	public void EveryUnitFull_IsTwoBytes()
	{
		byte[] payload = AmmoStateCodec.Encode(ReadOnlySpan<UnitAmmoRecord>.Empty);

		Assert.Equal(AmmoStateCodec.HeaderBytes, payload.Length);
		Assert.True(AmmoStateCodec.TryDecode(payload, new UnitAmmoRecord[1], out int count));
		Assert.Equal(0, count);
	}

	[Fact]
	public void AFieldOfUnits_FitsTheMaximum()
	{
		var records = new UnitAmmoRecord[SimConfig.MaxUnits + 10];
		byte[] payload = AmmoStateCodec.Encode(records);

		Assert.Equal(AmmoStateCodec.MaxPayloadBytes, payload.Length);
	}

	[Fact]
	public void AMalformedPayload_IsRefused_AndNeverThrows()
	{
		var into = new UnitAmmoRecord[AmmoStateCodec.MaxRecords];
		byte[] two = AmmoStateCodec.Encode(new[] { new UnitAmmoRecord { UnitId = 1, Rounds = 2 }, default });

		Assert.False(AmmoStateCodec.TryDecode(null, into, out _));
		Assert.False(AmmoStateCodec.TryDecode(new byte[1], into, out _));
		Assert.False(AmmoStateCodec.TryDecode(two.AsSpan(0, two.Length - 1), into, out _));
		Assert.False(AmmoStateCodec.TryDecode(two, new UnitAmmoRecord[1], out _));

		// More records than there can be units.
		byte[] tooMany = new byte[AmmoStateCodec.PayloadBytes(AmmoStateCodec.MaxRecords + 1)];
		System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(tooMany, AmmoStateCodec.MaxRecords + 1);
		Assert.False(AmmoStateCodec.TryDecode(tooMany, new UnitAmmoRecord[AmmoStateCodec.MaxRecords + 1], out _));
	}

	[Fact]
	public void EncodingIntoTooSmallABuffer_WritesNothing()
	{
		var records = new[] { new UnitAmmoRecord { UnitId = 1, Rounds = 2 } };

		Assert.Equal(0, AmmoStateCodec.Encode(records, new byte[AmmoStateCodec.PayloadBytes(1) - 1]));
	}

	// ---- the card ------------------------------------------------------------

	[Fact]
	public void OneUnit_ReadsAsRoundsOfItsLoad()
	{
		Assert.Equal("ammo 24/30", StrategistCards.AmmoReadout(1, 24, 30, 0.8f, 0));
		Assert.Equal("ammo 0/30 · out", StrategistCards.AmmoReadout(1, 0, 30, 0f, 1));
	}

	[Fact]
	public void SeveralUnits_ReadAsTheMeanShareLeft_AndHowManyAreOut()
	{
		// A full rifleman and an empty tank: half, not 210 of 240.
		Assert.Equal("ammo 50% · 1 out", StrategistCards.AmmoReadout(2, 210, 240, 1f, 1));
		Assert.Equal("ammo 75%", StrategistCards.AmmoReadout(2, 300, 360, 1.5f, 0));
	}

	[Fact]
	public void NothingThatCanRunOut_ReadsAsNothing()
	{
		Assert.Equal(string.Empty, StrategistCards.AmmoReadout(0, 0, 0, 0f, 0));
	}

	// ---- the health bar ------------------------------------------------------

	[Fact]
	public void TheBar_IsGreenWhole_YellowAtTwoThirds_OrangeAtOneThird_RedAtNothing()
	{
		AssertColor(UnitBars.Green, UnitBars.HealthColor(1f));
		AssertColor(UnitBars.Yellow, UnitBars.HealthColor(2f / 3f));
		AssertColor(UnitBars.Orange, UnitBars.HealthColor(1f / 3f));
		AssertColor(UnitBars.Red, UnitBars.HealthColor(0f));
	}

	[Fact]
	public void TheBar_IsClamped()
	{
		AssertColor(UnitBars.Green, UnitBars.HealthColor(5f));
		AssertColor(UnitBars.Red, UnitBars.HealthColor(-1f));
		AssertColor(UnitBars.Red, UnitBars.HealthColor(float.NaN));
	}

	[Fact]
	public void TheBar_RunsFromGreenToRed_WithoutTurningBack()
	{
		// Hue falls from green's through yellow's and orange's to red's as health does.
		float previous = float.MaxValue;
		for (int i = 100; i >= 0; i--)
		{
			float hue = UnitBars.HealthColor(i / 100f).H;
			Assert.True(hue <= previous + 1e-4f, $"hue went back up at {i}%: {hue} after {previous}");
			previous = hue;
		}

		Assert.True(UnitBars.Green.H > UnitBars.Yellow.H);
		Assert.True(UnitBars.Yellow.H > UnitBars.Orange.H);
		Assert.True(UnitBars.Orange.H > UnitBars.Red.H);
	}

	[Fact]
	public void HalfHealth_IsBetweenYellowAndOrange()
	{
		Color half = UnitBars.HealthColor(0.5f);

		Assert.InRange(half.H, UnitBars.Orange.H, UnitBars.Yellow.H);
	}

	private static void AssertColor(Color expected, Color actual)
	{
		Assert.Equal(expected.R, actual.R, 4);
		Assert.Equal(expected.G, actual.G, 4);
		Assert.Equal(expected.B, actual.B, 4);
		Assert.Equal(expected.A, actual.A, 4);
	}
}
