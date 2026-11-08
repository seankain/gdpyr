using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Gdpyr.Sim;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// The engine-free half of weapon audio (docs/AUDIO.md): the placeholders' numbers and
/// the samples made from them, when a round is heard going past the listener, and the
/// weapon and round files that say which sound each one makes.
/// </summary>
public class WeaponSoundTests
{
	private const int Rate = WeaponSounds.SampleRate;

	public static IEnumerable<object[]> ShotKinds() => WeaponSounds.Shots.Select(kind => new object[] { kind });

	public static IEnumerable<object[]> FlybyKinds() => WeaponSounds.Flybys.Select(kind => new object[] { kind });

	private static IEnumerable<SoundVoice> EveryVoice() =>
		WeaponSounds.Shots.Select(WeaponSounds.Shot).Concat(WeaponSounds.Flybys.Select(WeaponSounds.Flyby));

	// ---- the placeholders (§3) ------------------------------------------------------------

	[Fact]
	public void EverySound_IsAsLongAsItsVoiceSays_AndPeaksJustUnderFullScale()
	{
		foreach (SoundVoice voice in EveryVoice())
		{
			float[] samples = WeaponSounds.Synthesize(voice);

			Assert.Equal((int)MathF.Round(voice.Seconds * Rate), samples.Length);
			Assert.All(samples, s => Assert.True(float.IsFinite(s)));
			float peak = samples.Max(MathF.Abs);
			Assert.InRange(peak, WeaponSounds.Peak - 0.01f, WeaponSounds.Peak + 1e-5f);
		}
	}

	[Fact]
	public void EverySound_StartsAndEndsOnSilence_SoNeitherEndClicks()
	{
		foreach (SoundVoice voice in EveryVoice())
		{
			float[] samples = WeaponSounds.Synthesize(voice);
			Assert.Equal(0f, samples[0]);
			Assert.Equal(0f, samples[^1]);
		}
	}

	[Fact]
	public void TheSameVoiceAndSeed_MakeTheSameSamples_AndAnotherSeedDoesNot()
	{
		SoundVoice rifle = WeaponSounds.Shot(ShotKind.Rifle);
		Assert.Equal(WeaponSounds.Synthesize(rifle, Rate, 7u), WeaponSounds.Synthesize(rifle, Rate, 7u));
		Assert.NotEqual(WeaponSounds.Synthesize(rifle, Rate, 7u), WeaponSounds.Synthesize(rifle, Rate, 8u));
	}

	[Fact]
	public void NoneAndUnknownKinds_AreSilent_AndMakeNoSamples()
	{
		Assert.True(WeaponSounds.Shot(ShotKind.None).IsSilent);
		Assert.True(WeaponSounds.Flyby(FlybyKind.None).IsSilent);
		Assert.True(WeaponSounds.Shot((ShotKind)200).IsSilent);
		Assert.True(WeaponSounds.Flyby((FlybyKind)200).IsSilent);
		Assert.Empty(WeaponSounds.Synthesize(WeaponSounds.Shot(ShotKind.None)));
		Assert.Empty(WeaponSounds.Synthesize(WeaponSounds.Shot(ShotKind.Rifle), sampleRate: 0));
	}

	[Theory]
	[MemberData(nameof(ShotKinds))]
	public void AShot_IsLoudestAtTheMuzzleBlast_AndDiesAway(ShotKind kind)
	{
		float[] samples = WeaponSounds.Synthesize(WeaponSounds.Shot(kind));
		int blast = (int)(0.03f * Rate);
		float start = Rms(samples.AsSpan(0, blast));
		float end = Rms(samples.AsSpan(samples.Length * 2 / 3));

		Assert.True(start > 4f * end, $"{kind}: first 30 ms {start}, last third {end}");
	}

	[Fact]
	public void ABullet_CracksAtOnce_AndAShell_SwellsAsItGoesBy()
	{
		float[] bullet = WeaponSounds.Synthesize(WeaponSounds.Flyby(FlybyKind.Bullet));
		float[] shell = WeaponSounds.Synthesize(WeaponSounds.Flyby(FlybyKind.Shell));

		Assert.True(IndexOfPeak(bullet) < 0.005f * Rate, $"bullet peaks at sample {IndexOfPeak(bullet)}");
		Assert.True(IndexOfPeak(shell) > 0.02f * Rate, $"shell peaks at sample {IndexOfPeak(shell)}");
	}

	[Fact]
	public void BiggerGuns_RingLongerAndCarryFurther()
	{
		SoundVoice pistol = WeaponSounds.Shot(ShotKind.Pistol);
		SoundVoice rifle = WeaponSounds.Shot(ShotKind.Rifle);
		SoundVoice dmr = WeaponSounds.Shot(ShotKind.Dmr);
		SoundVoice cannon = WeaponSounds.Shot(ShotKind.Cannon);

		Assert.True(pistol.Seconds < rifle.Seconds && rifle.Seconds < dmr.Seconds && dmr.Seconds < cannon.Seconds);
		Assert.True(pistol.MaxDistanceMeters < rifle.MaxDistanceMeters && rifle.MaxDistanceMeters < dmr.MaxDistanceMeters);
		Assert.True(WeaponSounds.Shot(ShotKind.MachineGun).BoomHz > WeaponSounds.Shot(ShotKind.HeavyMachineGun).BoomHz);

		foreach (ShotKind kind in WeaponSounds.Shots)
		{
			Assert.True(WeaponSounds.Shot(kind).MaxDistanceMeters <= cannon.MaxDistanceMeters, $"{kind} carries past the cannon");
		}
	}

	[Fact]
	public void EveryVoice_IsHeardFurtherThanItsOwnVolume_AndAFlybyNotAsFarAsAnyShot()
	{
		float nearestShot = WeaponSounds.Shots.Min(kind => WeaponSounds.Shot(kind).MaxDistanceMeters);
		foreach (SoundVoice voice in EveryVoice())
		{
			Assert.True(voice.UnitSizeMeters > 0f && voice.UnitSizeMeters < voice.MaxDistanceMeters);
			Assert.InRange(voice.PitchJitter, 0f, 0.1f);
		}

		foreach (FlybyKind kind in WeaponSounds.Flybys)
		{
			Assert.True(WeaponSounds.Flyby(kind).MaxDistanceMeters < nearestShot, $"{kind}");
			Assert.True(WeaponSounds.Flyby(kind).MaxDistanceMeters > Flyby.RadiusOf(kind), $"{kind}");
		}
	}

	[Fact]
	public void Pcm16_IsLittleEndian_Clipped_AndSilentForNonNumbers()
	{
		byte[] bytes = WeaponSounds.ToPcm16(new[] { 0f, 1f, -1f, 2f, float.NaN, 0.5f });

		Assert.Equal(12, bytes.Length);
		short At(int i) => (short)(bytes[2 * i] | (bytes[(2 * i) + 1] << 8));
		Assert.Equal(0, At(0));
		Assert.Equal(short.MaxValue, At(1));
		Assert.Equal(-short.MaxValue, At(2));
		Assert.Equal(short.MaxValue, At(3));
		Assert.Equal(0, At(4));
		Assert.Equal(16384, At(5));
	}

	[Fact]
	public void PitchScale_MovesByUpToTheJitter_EitherWay()
	{
		SoundVoice rifle = WeaponSounds.Shot(ShotKind.Rifle);
		Assert.Equal(1f - rifle.PitchJitter, WeaponSounds.PitchScale(rifle, 0f), 5);
		Assert.Equal(1f, WeaponSounds.PitchScale(rifle, 0.5f), 5);
		Assert.Equal(1f + rifle.PitchJitter, WeaponSounds.PitchScale(rifle, 1f), 5);
		Assert.Equal(1f, WeaponSounds.PitchScale(rifle, float.NaN), 5);
		Assert.Equal(1f, WeaponSounds.PitchScale(WeaponSounds.Shot(ShotKind.None), 1f), 5);
	}

	[Fact]
	public void Swell_RisesToOneOverTheAttack_ThenDecays()
	{
		Assert.Equal(0f, WeaponSounds.Swell(-0.1f, 0.05f, 0.1f));
		Assert.Equal(0f, WeaponSounds.Swell(0f, 0.05f, 0.1f), 5);
		Assert.Equal(1f, WeaponSounds.Swell(0.05f, 0.05f, 0.1f), 5);
		Assert.True(WeaponSounds.Swell(0.2f, 0.05f, 0.1f) < WeaponSounds.Swell(0.1f, 0.05f, 0.1f));
		Assert.Equal(0f, WeaponSounds.Decay(1f, 0f));
	}

	[Fact]
	public void EveryKind_IsNamed_AndParsedBackFromItsName()
	{
		foreach (ShotKind kind in WeaponSounds.Shots)
		{
			Assert.True(WeaponSounds.TryParse(WeaponSounds.NameOf(kind).ToUpperInvariant(), out ShotKind parsed));
			Assert.Equal(kind, parsed);
		}

		foreach (FlybyKind kind in WeaponSounds.Flybys)
		{
			Assert.True(WeaponSounds.TryParse($" {WeaponSounds.NameOf(kind)} ", out FlybyKind parsed));
			Assert.Equal(kind, parsed);
		}

		Assert.False(WeaponSounds.TryParse("none", out ShotKind _));
		Assert.False(WeaponSounds.TryParse((string)null, out FlybyKind _));
		Assert.Equal(Enum.GetValues<ShotKind>().Length - 1, WeaponSounds.Shots.Length);
		Assert.Equal(Enum.GetValues<FlybyKind>().Length - 1, WeaponSounds.Flybys.Length);
	}

	// ---- rounds going past (§2.2) ---------------------------------------------------------

	[Theory]
	[InlineData(5f)]
	[InlineData(6f)]
	[InlineData(6.7f)]
	public void ARoundGoingPast_IsHeardOnce_WhereItCameClosest(float chordMeters)
	{
		// Across the ear 2 m to its side, in chords of a tick's length — the first of these
		// meets the point of closest approach exactly on a chord's end.
		var origin = new Vector3(-100f, 0f, 2f);
		int heard = 0;
		Vector3 where = default;
		for (float x = -100f; x < 100f; x += chordMeters)
		{
			if (Flyby.Passes(FlybyKind.Bullet, new Vector3(x, 0f, 2f), new Vector3(x + chordMeters, 0f, 2f), origin,
				Vector3.Zero, out Vector3 point))
			{
				heard++;
				where = point;
			}
		}

		Assert.Equal(1, heard);
		Assert.True(where.DistanceTo(new Vector3(0f, 0f, 2f)) < 1e-3f, $"{where}");
	}

	[Fact]
	public void ARoundTooFarAway_IsNotHeard()
	{
		float reach = Flyby.RadiusOf(FlybyKind.Bullet);
		var origin = new Vector3(-100f, 0f, reach + 0.5f);
		Assert.False(Flyby.Passes(FlybyKind.Bullet, new Vector3(-3f, 0f, reach + 0.5f), new Vector3(3f, 0f, reach + 0.5f),
			origin, Vector3.Zero, out _));
		Assert.True(Flyby.Passes(FlybyKind.Shell, new Vector3(-3f, 0f, reach + 0.5f), new Vector3(3f, 0f, reach + 0.5f),
			origin, Vector3.Zero, out _));
	}

	[Fact]
	public void ARoundGoingAway_OrStillComing_IsNotHeardOnThisChord()
	{
		var origin = new Vector3(-100f, 0f, 1f);

		// Already past the ear when the chord began.
		Assert.False(Flyby.Passes(FlybyKind.Bullet, new Vector3(1f, 0f, 1f), new Vector3(7f, 0f, 1f), origin,
			Vector3.Zero, out _));

		// Still on its way in when the chord ended: the next chord's.
		Assert.False(Flyby.Passes(FlybyKind.Bullet, new Vector3(-7f, 0f, 1f), new Vector3(-1f, 0f, 1f), origin,
			Vector3.Zero, out _));
	}

	[Fact]
	public void ARoundFromAMuzzleBesideTheEar_IsTheShot_NotAFlyby()
	{
		// A teammate a few metres behind firing past a shoulder.
		var near = new Vector3(-5f, 0f, 1f);
		Assert.False(Flyby.Passes(FlybyKind.Bullet, near, new Vector3(1f, 0f, 1f), near, Vector3.Zero, out _));

		// The same line from across the field.
		var far = new Vector3(-60f, 0f, 1f);
		Assert.True(Flyby.Passes(FlybyKind.Bullet, new Vector3(-5f, 0f, 1f), new Vector3(1f, 0f, 1f), far,
			Vector3.Zero, out _));
	}

	[Fact]
	public void ARoundLyingStill_OrOfNoKind_IsNeverHeard()
	{
		var at = new Vector3(0f, 0f, 1f);
		var origin = new Vector3(-50f, 0f, 1f);
		Assert.False(Flyby.Passes(FlybyKind.Shell, at, at, origin, Vector3.Zero, out _));
		Assert.False(Flyby.Passes(FlybyKind.None, new Vector3(-3f, 0f, 1f), new Vector3(3f, 0f, 1f), origin,
			Vector3.Zero, out _));
		Assert.False(Flyby.Passes(new Vector3(-3f, 0f, 1f), new Vector3(3f, 0f, 1f), origin, Vector3.Zero,
			float.NaN, 0f, out _));
	}

	[Fact]
	public void HeavierRounds_AreHeardFurtherOff()
	{
		Assert.True(Flyby.RadiusOf(FlybyKind.Bullet) < Flyby.RadiusOf(FlybyKind.HeavyBullet));
		Assert.True(Flyby.RadiusOf(FlybyKind.HeavyBullet) < Flyby.RadiusOf(FlybyKind.Shell));
		Assert.Equal(0f, Flyby.RadiusOf(FlybyKind.None));

		foreach (FlybyKind kind in WeaponSounds.Flybys)
		{
			Assert.True(Flyby.MinTravelOf(kind) > Flyby.RadiusOf(kind), $"{kind}");
		}
	}

	// ---- the weapon and round files (§1, §2) ---------------------------------------------

	[Theory]
	[InlineData(0, ShotKind.None)]            // hammer
	[InlineData(1, ShotKind.Pistol)]
	[InlineData(2, ShotKind.Dmr)]
	[InlineData(3, ShotKind.Rifle)]
	[InlineData(4, ShotKind.Launcher)]
	[InlineData(5, ShotKind.HeavyMachineGun)] // the technical's gun
	[InlineData(6, ShotKind.Cannon)]
	[InlineData(7, ShotKind.HeavyMachineGun)] // the deployed heavy gun
	[InlineData(8, ShotKind.HeavyMachineGun)] // the barracks turret
	[InlineData(9, ShotKind.Mortar)]
	[InlineData(10, ShotKind.Dmr)]            // the marksman
	[InlineData(11, ShotKind.MachineGun)]
	[InlineData(12, ShotKind.None)]           // hand grenade
	[InlineData(13, ShotKind.None)]           // FPV drone
	[InlineData(14, ShotKind.HeavyMachineGun)] // the 4x4's gun
	[InlineData(15, ShotKind.None)]           // ram
	[InlineData(16, ShotKind.Mortar)]         // the carried mortar
	public void EachWeapon_NamesItsShot(int id, ShotKind kind)
	{
		Assert.Equal(kind, (ShotKind)IntOrZero(WeaponFile(id), "Shot"));
	}

	[Theory]
	[InlineData("pistol_9mm", FlybyKind.Bullet)]
	[InlineData("rifle_556", FlybyKind.Bullet)]
	[InlineData("dmr_762", FlybyKind.Bullet)]
	[InlineData("hmg_127", FlybyKind.HeavyBullet)]
	[InlineData("grenade_40mm", FlybyKind.Shell)]
	[InlineData("cannon_76", FlybyKind.Shell)]
	[InlineData("mortar_81", FlybyKind.Shell)]
	[InlineData("mortar_81_launcher", FlybyKind.Shell)]
	[InlineData("hand_grenade", FlybyKind.None)]
	[InlineData("fpv_drone", FlybyKind.None)]
	public void EachRound_NamesItsFlyby(string round, FlybyKind kind)
	{
		Dictionary<string, string> file = TrainedUnitResourceTests.Resource($"Weapons/projectiles/{round}.tres");
		Assert.Equal(kind, (FlybyKind)IntOrZero(file, "Flyby"));
	}

	[Fact]
	public void EveryRoundFile_HasHadItsFlybyDecided()
	{
		// A new round file fails here until the theory above says what it sounds like going by.
		string directory = Path.GetDirectoryName(TrainedUnitResourceTests.Repo("Weapons/projectiles/rifle_556.tres"));
		string[] decided =
		{
			"pistol_9mm", "rifle_556", "dmr_762", "hmg_127", "grenade_40mm", "cannon_76", "mortar_81",
			"mortar_81_launcher", "hand_grenade", "fpv_drone",
		};

		string[] files = Directory.GetFiles(directory, "*.tres").Select(Path.GetFileNameWithoutExtension).ToArray();
		Assert.Equal(decided.OrderBy(n => n), files.OrderBy(n => n));
	}

	[Fact]
	public void AWeaponIsHeardFiring_ExactlyWhenItsRoundIsHeardGoingBy()
	{
		// What leaves a muzzle cracks past an ear; what is thrown, flown or swung does neither.
		int weapons = 0;
		for (int id = 0; ; id++)
		{
			string path;
			try
			{
				path = TrainedUnitResourceTests.WeaponPath(id);
			}
			catch (ArgumentOutOfRangeException)
			{
				break;
			}

			Dictionary<string, string> weapon = WeaponFile(id);
			FlybyKind flyby = RoundOf(path) is { } round
				? (FlybyKind)IntOrZero(TrainedUnitResourceTests.Resource(round), "Flyby")
				: FlybyKind.None;
			ShotKind shot = (ShotKind)IntOrZero(weapon, "Shot");

			Assert.True((shot != ShotKind.None) == (flyby != FlybyKind.None), $"{path}: shot {shot}, flyby {flyby}");
			weapons++;
		}

		Assert.Equal(17, weapons);
	}

	// ---- helpers --------------------------------------------------------------------------

	private static readonly Regex ExtResourcePath = new("path=\"res://([^\"]+)\"", RegexOptions.Compiled);
	private static readonly Regex ExtResourceId = new(" id=\"([^\"]+)\"", RegexOptions.Compiled);
	private static readonly Regex ExtResourceRef = new("ExtResource\\(\"([^\"]+)\"\\)", RegexOptions.Compiled);

	private static Dictionary<string, string> WeaponFile(int id) =>
		TrainedUnitResourceTests.Resource(TrainedUnitResourceTests.WeaponPath(id)["res://".Length..]);

	/// <summary>The repo path of the round a weapon file names as its <c>Projectile</c>; null for none.</summary>
	private static string RoundOf(string weaponResPath)
	{
		string relative = weaponResPath["res://".Length..];
		if (!TrainedUnitResourceTests.Resource(relative).TryGetValue("Projectile", out string value))
		{
			return null;
		}

		string reference = ExtResourceRef.Match(value).Groups[1].Value;
		foreach (string line in File.ReadAllLines(TrainedUnitResourceTests.Repo(relative)))
		{
			if (line.StartsWith("[ext_resource", StringComparison.Ordinal)
				&& ExtResourceId.Match(line).Groups[1].Value == reference)
			{
				return ExtResourcePath.Match(line).Groups[1].Value;
			}
		}

		return null;
	}

	private static int IntOrZero(Dictionary<string, string> file, string key) =>
		file.TryGetValue(key, out string value) ? int.Parse(value, CultureInfo.InvariantCulture) : 0;

	private static float Rms(ReadOnlySpan<float> samples)
	{
		double sum = 0.0;
		foreach (float s in samples)
		{
			sum += s * s;
		}

		return samples.Length > 0 ? (float)Math.Sqrt(sum / samples.Length) : 0f;
	}

	private static int IndexOfPeak(float[] samples)
	{
		int best = 0;
		for (int i = 1; i < samples.Length; i++)
		{
			if (MathF.Abs(samples[i]) > MathF.Abs(samples[best]))
			{
				best = i;
			}
		}

		return best;
	}
}
