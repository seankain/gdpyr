using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Gdpyr.Sim;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// The engine-free half of explosion effects (docs/EFFECTS.md): which kind a hit is
/// drawn as, each kind's numbers and curves, the ledger a client names a round's blast
/// by, the pool effects are reused from, and the round files that say which explosion
/// each round is.
/// </summary>
public class ExplosionTests
{
	// ---- which kind a hit is drawn as (§2.1) ----------------------------------------------

	[Theory]
	[InlineData(HitFlags.None)]
	[InlineData(HitFlags.Killed)]
	[InlineData(HitFlags.Landed)]
	[InlineData(HitFlags.Melee)]
	[InlineData(HitFlags.Melee | HitFlags.Killed)]
	public void AHitThatDidNotGoOff_IsNoExplosion_WhateverTheRound(HitFlags flags)
	{
		Assert.Equal(ExplosionKind.None, Explosions.ForHit(flags, ExplosionKind.Shell));
		Assert.Equal(ExplosionKind.None, Explosions.ForHit(flags, ExplosionKind.None));
	}

	[Theory]
	[InlineData(ExplosionKind.HandGrenade)]
	[InlineData(ExplosionKind.LauncherGrenade)]
	[InlineData(ExplosionKind.Shell)]
	[InlineData(ExplosionKind.Drone)]
	public void ARoundThatWentOff_IsDrawnAsItsOwnKind_KillOrNot(ExplosionKind round)
	{
		Assert.Equal(round, Explosions.ForHit(HitFlags.Exploded, round));
		Assert.Equal(round, Explosions.ForHit(HitFlags.Exploded | HitFlags.Killed, round));
	}

	[Fact]
	public void ARoundThisPeerCannotName_IsStillDrawn_AsTheUnidentifiedKind()
	{
		Assert.Equal(Explosions.Unidentified, Explosions.ForHit(HitFlags.Exploded, ExplosionKind.None));
		Assert.NotEqual(ExplosionKind.None, Explosions.Unidentified);
		Assert.NotEqual(ExplosionKind.Vehicle, Explosions.Unidentified);
	}

	[Fact]
	public void AFlagSetThatSaysLandedAndExploded_IsNotDrawnTwice()
	{
		// Never sent, but a malformed message must not put a blast where a grenade is still lying.
		Assert.Equal(ExplosionKind.None, Explosions.ForHit(HitFlags.Exploded | HitFlags.Landed, ExplosionKind.HandGrenade));
	}

	// ---- profiles (§3) ---------------------------------------------------------------------

	[Fact]
	public void EveryDrawnKind_HasAFireball_AFlash_AndAnEnd()
	{
		foreach (ExplosionKind kind in Explosions.Drawn)
		{
			ExplosionProfile profile = Explosions.Profile(kind);
			Assert.True(profile.FireballRadius > 0f, $"{kind} fireball");
			Assert.True(profile.FireballSeconds > 0f, $"{kind} fireball time");
			Assert.True(profile.FlashEnergy > 0f && profile.FlashRange > 0f, $"{kind} flash");
			Assert.True(profile.Lifetime > 0f && float.IsFinite(profile.Lifetime), $"{kind} lifetime");
		}
	}

	[Fact]
	public void NothingIsDrawnForNone_OrForAValueTheEnumDoesNotHave()
	{
		Assert.Equal(0f, Explosions.Profile(ExplosionKind.None).Lifetime);
		Assert.Equal(0f, Explosions.Profile((ExplosionKind)200).Lifetime);
		Assert.DoesNotContain(ExplosionKind.None, Explosions.Drawn);
	}

	[Fact]
	public void EveryKindIsInDrawn_OnceAndInOrder()
	{
		ExplosionKind[] all = Enum.GetValues<ExplosionKind>();
		Assert.Equal(all.Length - 1, Explosions.Drawn.Length);
		for (int i = 1; i < Explosions.Drawn.Length; i++)
		{
			Assert.True(Explosions.Drawn[i - 1] < Explosions.Drawn[i]);
		}
	}

	[Fact]
	public void EveryRoundShowsItsReach_AndAWreck_WhichDoesNoBlastDamage_DoesNot()
	{
		foreach (ExplosionKind kind in Explosions.Drawn)
		{
			ExplosionProfile profile = Explosions.Profile(kind);
			if (kind == ExplosionKind.Vehicle)
			{
				Assert.Equal(0f, profile.ReachSeconds);
			}
			else
			{
				Assert.True(profile.ReachSeconds > 0f, $"{kind} reach");
			}
		}
	}

	[Fact]
	public void OnlyAWreck_BurnsOn_UnderAColumnOfSmoke_ThatOutlastsAnyRounds()
	{
		ExplosionProfile wreck = Explosions.Profile(ExplosionKind.Vehicle);
		Assert.True(wreck.BurnSeconds > 0f);

		foreach (ExplosionKind kind in Explosions.Drawn)
		{
			if (kind == ExplosionKind.Vehicle)
			{
				continue;
			}

			ExplosionProfile round = Explosions.Profile(kind);
			Assert.Equal(0f, round.BurnSeconds);
			Assert.True(wreck.SmokeEmitSeconds + wreck.SmokeSeconds > round.SmokeEmitSeconds + round.SmokeSeconds,
				$"{kind} smoke");
			Assert.True(wreck.FireballRadius > round.FireballRadius, $"{kind} fireball");
		}
	}

	[Fact]
	public void TheFireball_GrowsWithTheRound_GrenadeThenLauncherThenShell()
	{
		float hand = Explosions.Profile(ExplosionKind.HandGrenade).FireballRadius;
		float launcher = Explosions.Profile(ExplosionKind.LauncherGrenade).FireballRadius;
		float shell = Explosions.Profile(ExplosionKind.Shell).FireballRadius;

		Assert.True(hand < launcher && launcher < shell);
	}

	[Fact]
	public void EveryProfile_FitsTheEffectItIsDrawnWith()
	{
		foreach (ExplosionKind kind in Explosions.Drawn)
		{
			ExplosionProfile profile = Explosions.Profile(kind);
			Assert.InRange(profile.SmokePuffs, 0, Explosions.MaxSmokePuffs);
			Assert.InRange(profile.DebrisPieces, 0, Explosions.MaxDebrisPieces);
		}
	}

	[Fact]
	public void Lifetime_IsTheLongestPart_SmokeCountedFromItsLastPuff()
	{
		var profile = new ExplosionProfile
		{
			FireballSeconds = 0.3f,
			SmokePuffs = 4,
			SmokeEmitSeconds = 2f,
			SmokeSeconds = 3f,
			ScorchSeconds = 4f,
		};
		Assert.Equal(5f, profile.Lifetime, 5);

		Assert.Equal(4f, (profile with { SmokePuffs = 0 }).Lifetime, 5);
		Assert.Equal(9f, (profile with { BurnSeconds = 9f }).Lifetime, 5);
	}

	// ---- curves ----------------------------------------------------------------------------

	[Fact]
	public void Fade_HoldsWhole_ThenFallsInAStraightLine_ToNothingAtTheEnd()
	{
		Assert.Equal(1f, Explosions.Fade(0f, 2f, 0.5f));
		Assert.Equal(1f, Explosions.Fade(1f, 2f, 0.5f));
		Assert.Equal(0.5f, Explosions.Fade(1.5f, 2f, 0.5f), 5);
		Assert.Equal(0f, Explosions.Fade(2f, 2f, 0.5f));
		Assert.Equal(0f, Explosions.Fade(5f, 2f, 0.5f));
		Assert.Equal(0f, Explosions.Fade(-0.1f, 2f, 0.5f));
		Assert.Equal(0f, Explosions.Fade(0f, 0f, 0.5f));
	}

	[Fact]
	public void Fade_NeverRises_AndStaysBetweenNothingAndWhole()
	{
		float last = 1f;
		for (float age = 0f; age < 3f; age += 0.01f)
		{
			float fade = Explosions.Fade(age, 2.5f, 0.3f);
			Assert.InRange(fade, 0f, 1f);
			Assert.True(fade <= last + 1e-6f, $"rose at {age}");
			last = fade;
		}
	}

	[Fact]
	public void TheFireball_StartsAtAThird_AndGrowsToFullSize_NeverShrinking()
	{
		Assert.Equal(0.35f, Explosions.FireballGrowth(0f, 0.3f), 5);
		Assert.Equal(1f, Explosions.FireballGrowth(0.3f, 0.3f), 5);
		Assert.Equal(1f, Explosions.FireballGrowth(9f, 0.3f), 5);

		float last = 0f;
		for (float age = 0f; age <= 0.3f; age += 0.005f)
		{
			float growth = Explosions.FireballGrowth(age, 0.3f);
			Assert.True(growth >= last);
			last = growth;
		}

		// Fast then slow: more than half grown a quarter of the way through.
		Assert.True(Explosions.FireballGrowth(0.075f, 0.3f) > 0.675f);
	}

	[Fact]
	public void TheFlash_IsBrightestAtOnce_AndGoneAtItsEnd()
	{
		Assert.Equal(1f, Explosions.Flash(0f, 0.2f));
		Assert.Equal(0.25f, Explosions.Flash(0.1f, 0.2f), 5);
		Assert.Equal(0f, Explosions.Flash(0.2f, 0.2f));
		Assert.Equal(0f, Explosions.Flash(0f, 0f));
	}

	[Fact]
	public void TheReachRing_SweepsFromNothing_ToTheWholeBlast()
	{
		Assert.Equal(0f, Explosions.Reach(0f, 0.45f));
		Assert.Equal(1f, Explosions.Reach(0.45f, 0.45f));
		Assert.Equal(1f, Explosions.Reach(3f, 0.45f));
		Assert.True(Explosions.Reach(0.225f, 0.45f) > 0.5f);
	}

	[Fact]
	public void Progress_IsClamped_AndAPartOfNoLengthIsAlreadyOver()
	{
		Assert.Equal(0f, Explosions.Progress(-1f, 2f));
		Assert.Equal(0.25f, Explosions.Progress(0.5f, 2f), 5);
		Assert.Equal(1f, Explosions.Progress(9f, 2f));
		Assert.Equal(1f, Explosions.Progress(0f, 0f));
	}

	[Fact]
	public void TheFireball_CoolsFromWhiteHot_ToADullRed()
	{
		Godot.Color hot = Explosions.FireballColor(0f);
		Godot.Color cold = Explosions.FireballColor(1f);

		Assert.True(hot.B > 0.5f && hot.G > 0.9f, "white-hot");
		Assert.True(cold.R > cold.G && cold.G > cold.B, "red");
		Assert.True(cold.R + cold.G + cold.B < hot.R + hot.G + hot.B);
		Assert.Equal(Explosions.FireballColor(1f), Explosions.FireballColor(5f));
	}

	[Fact]
	public void Puffs_AreLetOutEvenly_FirstAtOnce_LastAtTheEndOfEmitting()
	{
		Assert.Equal(0f, Explosions.PuffDelay(0, 5, 2f));
		Assert.Equal(0.5f, Explosions.PuffDelay(1, 5, 2f), 5);
		Assert.Equal(2f, Explosions.PuffDelay(4, 5, 2f), 5);
		Assert.Equal(2f, Explosions.PuffDelay(9, 5, 2f), 5);
		Assert.Equal(0f, Explosions.PuffDelay(0, 1, 2f));
		Assert.Equal(0f, Explosions.PuffDelay(3, 5, 0f));
	}

	[Theory]
	[InlineData(1.3f, 1f)]
	[InlineData(2.6f, 2f)]
	[InlineData(0.1f, Explosions.MinHullScale)]
	[InlineData(50f, Explosions.MaxHullScale)]
	[InlineData(0f, 1f)]
	[InlineData(-1f, 1f)]
	[InlineData(float.NaN, 1f)]
	public void AWreck_IsSizedByItsHull_TheTankAtOne(float radius, float scale)
	{
		Assert.Equal(scale, Explosions.HullScale(radius), 5);
	}

	// ---- names (§5) ------------------------------------------------------------------------

	[Fact]
	public void EveryKind_IsParsedBackFromItsName_AnyCase()
	{
		foreach (ExplosionKind kind in Explosions.Drawn)
		{
			string name = Explosions.NameOf(kind);
			Assert.Matches("^[a-z_]+$", name);
			Assert.True(Explosions.TryParse(name, out ExplosionKind parsed));
			Assert.Equal(kind, parsed);
			Assert.True(Explosions.TryParse($" {name.ToUpperInvariant()} ", out parsed));
			Assert.Equal(kind, parsed);
		}
	}

	[Theory]
	[InlineData("none")]
	[InlineData("")]
	[InlineData(null)]
	[InlineData("nuke")]
	[InlineData("3")]
	public void AnythingElse_IsNoKind(string name)
	{
		Assert.False(Explosions.TryParse(name, out ExplosionKind kind));
		Assert.Equal(ExplosionKind.None, kind);
	}

	// ---- the ledger a client names a blast by (§2.1) ---------------------------------------

	[Fact]
	public void ARoundNoted_IsFoundByItsId()
	{
		var rounds = new RecentRounds(8);
		rounds.Note(41, 12);
		rounds.Note(42, 4);

		Assert.True(rounds.TryGet(41, out byte first));
		Assert.Equal(12, first);
		Assert.True(rounds.TryGet(42, out byte second));
		Assert.Equal(4, second);
		Assert.False(rounds.TryGet(43, out _));
	}

	[Fact]
	public void IdZero_IsNobodys_AndIsNeverNotedOrFound()
	{
		var rounds = new RecentRounds(4);
		rounds.Note(0, 6);

		Assert.False(rounds.TryGet(0, out _));
	}

	[Fact]
	public void TheOldestRound_IsForgottenFirst()
	{
		var rounds = new RecentRounds(3);
		for (uint id = 1; id <= 4; id++)
		{
			rounds.Note(id, (byte)id);
		}

		Assert.False(rounds.TryGet(1, out _));
		for (uint id = 2; id <= 4; id++)
		{
			Assert.True(rounds.TryGet(id, out byte definition));
			Assert.Equal(id, definition);
		}
	}

	[Fact]
	public void AnIdNotedTwice_IsTheNewerNote()
	{
		var rounds = new RecentRounds(4);
		rounds.Note(7, 4);
		rounds.Note(7, 12);

		Assert.True(rounds.TryGet(7, out byte definition));
		Assert.Equal(12, definition);
	}

	// ---- the pool effects are reused from (§4) ---------------------------------------------

	[Fact]
	public void ThePool_MakesEffectsOnlyAsTheyAreNeeded_AndReusesAFreeOneFirst()
	{
		int made = 0;
		var pool = new EffectPool<object>(4, () => { made++; return new object(); });
		Assert.Equal(0, made);

		object first = pool.Acquire(out int a, out bool evicted);
		Assert.False(evicted);
		object second = pool.Acquire(out int b, out _);
		Assert.Equal(2, made);
		Assert.Equal(2, pool.ActiveCount);
		Assert.NotSame(first, second);

		Assert.True(pool.Release(a));
		object again = pool.Acquire(out int c, out evicted);
		Assert.False(evicted);
		Assert.Equal(a, c);
		Assert.Same(first, again);
		Assert.Equal(2, made);
		Assert.True(pool.IsActive(b));
	}

	[Fact]
	public void AFullPool_CutsTheOldestShort_AndCountsIt()
	{
		var pool = new EffectPool<object>(3, () => new object());
		pool.Acquire(out int oldest, out _);
		pool.Acquire(out int middle, out _);
		pool.Acquire(out int newest, out _);

		object reused = pool.Acquire(out int index, out bool evicted);
		Assert.True(evicted);
		Assert.Equal(oldest, index);
		Assert.Same(pool.At(oldest), reused);
		Assert.Equal(3, pool.Created);
		Assert.Equal(3, pool.ActiveCount);
		Assert.Equal(1, pool.Evictions);

		// The one just restarted is now the newest: the next to go is the middle one.
		pool.Acquire(out index, out evicted);
		Assert.True(evicted);
		Assert.Equal(middle, index);
		Assert.NotEqual(newest, index);
	}

	[Fact]
	public void Releasing_IsOnlyForWhatIsPlaying()
	{
		var pool = new EffectPool<object>(2, () => new object());
		pool.Acquire(out int index, out _);

		Assert.True(pool.Release(index));
		Assert.False(pool.Release(index));
		Assert.False(pool.Release(1));
		Assert.False(pool.Release(-1));
		Assert.Equal(0, pool.ActiveCount);
		Assert.Null(pool.At(1));
	}

	[Fact]
	public void ReleaseAll_FreesEverything_AndKeepsWhatWasMade()
	{
		int made = 0;
		var pool = new EffectPool<object>(3, () => { made++; return new object(); });
		pool.Acquire(out _, out _);
		pool.Acquire(out _, out _);

		pool.ReleaseAll();
		Assert.Equal(0, pool.ActiveCount);
		Assert.Equal(2, pool.Created);

		pool.Acquire(out _, out _);
		pool.Acquire(out _, out _);
		Assert.Equal(2, made);
	}

	[Fact]
	public void APoolOfNothing_IsAPoolOfOne()
	{
		var pool = new EffectPool<object>(0, () => new object());
		Assert.Equal(1, pool.Capacity);
		Assert.Throws<ArgumentNullException>(() => new EffectPool<object>(2, null));
	}

	// ---- the round files (§2.1) ------------------------------------------------------------

	[Theory]
	[InlineData("cannon_76", ExplosionKind.Shell)]
	[InlineData("mortar_81", ExplosionKind.Shell)]
	[InlineData("grenade_40mm", ExplosionKind.LauncherGrenade)]
	[InlineData("hand_grenade", ExplosionKind.HandGrenade)]
	[InlineData("fpv_drone", ExplosionKind.Drone)]
	public void EachExplosiveRound_NamesItsExplosion(string round, ExplosionKind kind)
	{
		Dictionary<string, string> file = TrainedUnitResourceTests.Resource($"Weapons/projectiles/{round}.tres");
		Assert.Equal(kind, (ExplosionKind)TrainedUnitResourceTests.Int(file, "Explosion"));
	}

	[Fact]
	public void ARoundNamesAnExplosion_ExactlyWhenItExplodes()
	{
		// ProjectileStats.IsExplosive: a radius and some damage. A round that goes off and
		// names nothing is drawn as Unidentified, and one that names an explosion it never
		// has would be drawn going off on every bullet hole.
		string directory = Path.GetDirectoryName(TrainedUnitResourceTests.Repo("Weapons/projectiles/rifle_556.tres"));
		int explosive = 0;
		foreach (string path in Directory.GetFiles(directory, "*.tres"))
		{
			string name = Path.GetFileName(path);
			Dictionary<string, string> file = TrainedUnitResourceTests.Resource($"Weapons/projectiles/{name}");
			bool explodes = Value(file, "ExplosionRadiusMeters") > 0f && Value(file, "ExplosionDamage") != 0f;
			int named = file.TryGetValue("Explosion", out string value) ? int.Parse(value, CultureInfo.InvariantCulture) : 0;

			Assert.True(explodes == (named != 0), $"{name}: explodes {explodes}, names {named}");
			if (named != 0)
			{
				Assert.Contains((ExplosionKind)named, Explosions.Drawn);
				Assert.NotEqual(ExplosionKind.Vehicle, (ExplosionKind)named);
				explosive++;
			}
		}

		Assert.Equal(5, explosive);
	}

	private static float Value(Dictionary<string, string> file, string key) =>
		file.TryGetValue(key, out string value) ? float.Parse(value, CultureInfo.InvariantCulture) : 0f;
}
