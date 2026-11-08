using System;

namespace Gdpyr.Sim;

/// <summary>
/// What firing a weapon sounds like (docs/AUDIO.md §1). Presentation only: nothing in
/// the simulation reads it and it never goes on the wire. A weapon says which one it
/// is in its <c>WeaponDefinition.Shot</c>; it picks the placeholder that is played
/// until the weapon is given a recording of its own (<c>WeaponDefinition.ShotSound</c>).
/// </summary>
public enum ShotKind : byte
{
	/// <summary>Nothing fired from a muzzle: the hammer, a thrown grenade, a drone, a ram.</summary>
	None = 0,

	Pistol = 1,

	Rifle = 2,

	/// <summary>The DMR, and the marksman's gun that fires its round.</summary>
	Dmr = 3,

	/// <summary>The heavy gunner's belt-fed gun, on the rifle's round.</summary>
	MachineGun = 4,

	/// <summary>The 12.7 mm guns: the technical's, the 4x4's, the deployed heavy gun and the barracks turret.</summary>
	HeavyMachineGun = 5,

	/// <summary>The launcher's 40 mm round leaving the tube: a thump rather than a crack.</summary>
	Launcher = 6,

	/// <summary>The tank's main gun.</summary>
	Cannon = 7,

	/// <summary>An 81 mm mortar: the barracks' and the ground force's carried one.</summary>
	Mortar = 8,
}

/// <summary>
/// What a round sounds like going past the listener (docs/AUDIO.md §2). A round says
/// which one it is in its <c>ProjectileDefinition.Flyby</c>.
/// </summary>
public enum FlybyKind : byte
{
	/// <summary>A round nobody hears go by: a thrown grenade, a drone's warhead.</summary>
	None = 0,

	/// <summary>Small arms — pistol, rifle, DMR: a sharp crack and a short whine.</summary>
	Bullet = 1,

	/// <summary>The 12.7 mm round: a heavier crack and a lower, longer whine.</summary>
	HeavyBullet = 2,

	/// <summary>A launcher grenade, a tank shell or a mortar round: a rush of air, no crack.</summary>
	Shell = 3,
}

/// <summary>
/// The numbers one placeholder sound is made and played with (docs/AUDIO.md §3).
/// Every part is optional — a gain of zero leaves it out. Durations are seconds from
/// the start of the sound; frequencies are hertz.
///
/// A shot is three parts: a <b>crack</b> (bright noise, gone in milliseconds), a
/// <b>boom</b> (a low tone whose pitch falls as it dies away) and a <b>tail</b> (duller
/// noise that lingers, a stand-in for the space round the shooter). A flyby is a
/// crack and a <b>whiz</b>: band-limited noise that swells and fades, with a tone
/// whose pitch falls as the round goes away.
/// </summary>
public readonly struct SoundVoice
{
	/// <summary>Length of the whole sound.</summary>
	public float Seconds { get; init; }

	public float CrackGain { get; init; }

	/// <summary>Time constant the crack dies away with.</summary>
	public float CrackSeconds { get; init; }

	/// <summary>Brightness of the crack: the corner of the low-pass its noise goes through.</summary>
	public float CrackCutoffHz { get; init; }

	public float BoomGain { get; init; }

	/// <summary>The boom's pitch once it has settled.</summary>
	public float BoomHz { get; init; }

	/// <summary>How far above <see cref="BoomHz"/> the boom starts, as a share of it: 0.5 starts it at 1.5×.</summary>
	public float BoomDrop { get; init; }

	public float BoomSeconds { get; init; }

	public float TailGain { get; init; }

	/// <summary>How long after the start the tail comes in: a crude first reflection.</summary>
	public float TailDelaySeconds { get; init; }

	public float TailSeconds { get; init; }

	public float TailCutoffHz { get; init; }

	public float WhizGain { get; init; }

	/// <summary>The band the whiz's noise is kept to.</summary>
	public float WhizLowHz { get; init; }

	public float WhizHighHz { get; init; }

	/// <summary>How long the whiz takes to swell to its loudest.</summary>
	public float WhizAttackSeconds { get; init; }

	/// <summary>Time constant it dies away with after that.</summary>
	public float WhizSeconds { get; init; }

	/// <summary>The whiz's tone at its loudest; it starts above and ends below by <see cref="ToneDrop"/>.</summary>
	public float ToneHz { get; init; }

	public float ToneGain { get; init; }

	/// <summary>Share of <see cref="ToneHz"/> the tone falls through, start to end: the Doppler shift of a round going past.</summary>
	public float ToneDrop { get; init; }

	/// <summary>
	/// The distance at which a 3D player plays it at its own volume
	/// (<c>AudioStreamPlayer3D.UnitSize</c>): what makes a cannon carry and a pistol not.
	/// </summary>
	public float UnitSizeMeters { get; init; }

	/// <summary>The distance past which it is not heard at all (<c>AudioStreamPlayer3D.MaxDistance</c>).</summary>
	public float MaxDistanceMeters { get; init; }

	/// <summary>Volume it is played at, before distance [dB].</summary>
	public float VolumeDb { get; init; }

	/// <summary>How far each play's pitch is moved at random either way, as a share: so a burst is not one sample repeated.</summary>
	public float PitchJitter { get; init; }

	/// <summary>True for nothing at all: <see cref="ShotKind.None"/>, <see cref="FlybyKind.None"/> or an unknown value.</summary>
	public bool IsSilent => Seconds <= 0f;
}

/// <summary>
/// The engine-free half of weapon audio (docs/AUDIO.md): the kinds, the numbers each
/// placeholder is made with, and the synthesis that makes it — samples in, samples
/// out, no engine — so what a placeholder sounds like is a question <c>dotnet test</c>
/// can ask about. <c>Scripts/Effects/WeaponAudio.cs</c> is the half that plays them.
/// </summary>
public static class WeaponSounds
{
	/// <summary>The rate placeholders are made at [Hz].</summary>
	public const int SampleRate = 44100;

	/// <summary>The loudest sample a placeholder is scaled to: about −1 dBFS, so nothing clips.</summary>
	public const float Peak = 0.89f;

	/// <summary>How long a placeholder takes to come in, so its first sample does not click [s].</summary>
	public const float AttackSeconds = 0.0003f;

	/// <summary>How long a placeholder takes to go out at its end, so its last sample does not click [s].</summary>
	public const float ReleaseSeconds = 0.008f;

	/// <summary>
	/// How much quieter the local player's own shot is than the same shot heard from
	/// <see cref="SoundVoice.UnitSizeMeters"/> away [dB]. It is played at the ear,
	/// unattenuated, and at the volume a 3D player gives a shot beside it, it buries
	/// everything else.
	/// </summary>
	public const float OwnShotDb = -6f;

	/// <summary>Every kind of shot there is something to play for, in the order the console lists them.</summary>
	public static readonly ShotKind[] Shots =
	{
		ShotKind.Pistol, ShotKind.Rifle, ShotKind.Dmr, ShotKind.MachineGun, ShotKind.HeavyMachineGun,
		ShotKind.Launcher, ShotKind.Cannon, ShotKind.Mortar,
	};

	/// <summary>Every kind of flyby there is something to play for.</summary>
	public static readonly FlybyKind[] Flybys = { FlybyKind.Bullet, FlybyKind.HeavyBullet, FlybyKind.Shell };

	// ---- shots --------------------------------------------------------------------------

	private static readonly SoundVoice PistolVoice = new()
	{
		Seconds = 0.45f,
		CrackGain = 1f, CrackSeconds = 0.012f, CrackCutoffHz = 6000f,
		BoomGain = 0.5f, BoomHz = 140f, BoomDrop = 0.6f, BoomSeconds = 0.05f,
		TailGain = 0.25f, TailDelaySeconds = 0.02f, TailSeconds = 0.12f, TailCutoffHz = 1800f,
		UnitSizeMeters = 12f, MaxDistanceMeters = 250f, VolumeDb = -4f, PitchJitter = 0.04f,
	};

	private static readonly SoundVoice RifleVoice = new()
	{
		Seconds = 0.7f,
		CrackGain = 1f, CrackSeconds = 0.015f, CrackCutoffHz = 7000f,
		BoomGain = 0.6f, BoomHz = 110f, BoomDrop = 0.7f, BoomSeconds = 0.07f,
		TailGain = 0.3f, TailDelaySeconds = 0.03f, TailSeconds = 0.2f, TailCutoffHz = 1500f,
		UnitSizeMeters = 18f, MaxDistanceMeters = 400f, VolumeDb = -2f, PitchJitter = 0.04f,
	};

	private static readonly SoundVoice DmrVoice = new()
	{
		Seconds = 1f,
		CrackGain = 1f, CrackSeconds = 0.02f, CrackCutoffHz = 6500f,
		BoomGain = 0.8f, BoomHz = 90f, BoomDrop = 0.8f, BoomSeconds = 0.1f,
		TailGain = 0.35f, TailDelaySeconds = 0.04f, TailSeconds = 0.35f, TailCutoffHz = 1200f,
		UnitSizeMeters = 24f, MaxDistanceMeters = 500f, VolumeDb = 0f, PitchJitter = 0.03f,
	};

	private static readonly SoundVoice MachineGunVoice = new()
	{
		Seconds = 0.6f,
		CrackGain = 1f, CrackSeconds = 0.016f, CrackCutoffHz = 5000f,
		BoomGain = 0.7f, BoomHz = 95f, BoomDrop = 0.7f, BoomSeconds = 0.08f,
		TailGain = 0.3f, TailDelaySeconds = 0.03f, TailSeconds = 0.18f, TailCutoffHz = 1300f,
		UnitSizeMeters = 20f, MaxDistanceMeters = 400f, VolumeDb = -2f, PitchJitter = 0.05f,
	};

	private static readonly SoundVoice HeavyMachineGunVoice = new()
	{
		Seconds = 0.9f,
		CrackGain = 1f, CrackSeconds = 0.02f, CrackCutoffHz = 4000f,
		BoomGain = 0.9f, BoomHz = 70f, BoomDrop = 0.8f, BoomSeconds = 0.12f,
		TailGain = 0.35f, TailDelaySeconds = 0.04f, TailSeconds = 0.3f, TailCutoffHz = 1000f,
		UnitSizeMeters = 28f, MaxDistanceMeters = 500f, VolumeDb = 0f, PitchJitter = 0.05f,
	};

	private static readonly SoundVoice LauncherVoice = new()
	{
		Seconds = 0.5f,
		CrackGain = 0.3f, CrackSeconds = 0.006f, CrackCutoffHz = 2500f,
		BoomGain = 1f, BoomHz = 60f, BoomDrop = 1f, BoomSeconds = 0.09f,
		TailGain = 0.15f, TailDelaySeconds = 0.02f, TailSeconds = 0.1f, TailCutoffHz = 800f,
		UnitSizeMeters = 10f, MaxDistanceMeters = 200f, VolumeDb = -4f, PitchJitter = 0.03f,
	};

	private static readonly SoundVoice CannonVoice = new()
	{
		Seconds = 2.2f,
		CrackGain = 1f, CrackSeconds = 0.03f, CrackCutoffHz = 3500f,
		BoomGain = 1f, BoomHz = 45f, BoomDrop = 1f, BoomSeconds = 0.35f,
		TailGain = 0.5f, TailDelaySeconds = 0.06f, TailSeconds = 0.9f, TailCutoffHz = 700f,
		UnitSizeMeters = 40f, MaxDistanceMeters = 900f, VolumeDb = 3f, PitchJitter = 0.02f,
	};

	private static readonly SoundVoice MortarVoice = new()
	{
		Seconds = 1.2f,
		CrackGain = 0.5f, CrackSeconds = 0.01f, CrackCutoffHz = 2000f,
		BoomGain = 1f, BoomHz = 55f, BoomDrop = 0.9f, BoomSeconds = 0.15f,
		TailGain = 0.3f, TailDelaySeconds = 0.05f, TailSeconds = 0.5f, TailCutoffHz = 600f,
		UnitSizeMeters = 30f, MaxDistanceMeters = 600f, VolumeDb = 0f, PitchJitter = 0.02f,
	};

	// ---- flybys -------------------------------------------------------------------------

	private static readonly SoundVoice BulletFlybyVoice = new()
	{
		Seconds = 0.35f,
		CrackGain = 1f, CrackSeconds = 0.004f, CrackCutoffHz = 9000f,
		WhizGain = 0.6f, WhizLowHz = 2000f, WhizHighHz = 6000f, WhizAttackSeconds = 0.015f, WhizSeconds = 0.08f,
		ToneHz = 1200f, ToneGain = 0.25f, ToneDrop = 0.35f,
		UnitSizeMeters = 3f, MaxDistanceMeters = 40f, VolumeDb = -3f, PitchJitter = 0.08f,
	};

	private static readonly SoundVoice HeavyBulletFlybyVoice = new()
	{
		Seconds = 0.5f,
		CrackGain = 1f, CrackSeconds = 0.006f, CrackCutoffHz = 7000f,
		WhizGain = 0.7f, WhizLowHz = 900f, WhizHighHz = 3500f, WhizAttackSeconds = 0.02f, WhizSeconds = 0.13f,
		ToneHz = 650f, ToneGain = 0.3f, ToneDrop = 0.35f,
		UnitSizeMeters = 4f, MaxDistanceMeters = 60f, VolumeDb = 0f, PitchJitter = 0.06f,
	};

	private static readonly SoundVoice ShellFlybyVoice = new()
	{
		Seconds = 0.8f,
		WhizGain = 1f, WhizLowHz = 250f, WhizHighHz = 1200f, WhizAttackSeconds = 0.06f, WhizSeconds = 0.22f,
		ToneHz = 180f, ToneGain = 0.35f, ToneDrop = 0.4f,
		UnitSizeMeters = 6f, MaxDistanceMeters = 80f, VolumeDb = 0f, PitchJitter = 0.05f,
	};

	/// <summary>The numbers a shot of <paramref name="kind"/> is made with; silence for <see cref="ShotKind.None"/> or an unknown value.</summary>
	public static SoundVoice Shot(ShotKind kind) => kind switch
	{
		ShotKind.Pistol => PistolVoice,
		ShotKind.Rifle => RifleVoice,
		ShotKind.Dmr => DmrVoice,
		ShotKind.MachineGun => MachineGunVoice,
		ShotKind.HeavyMachineGun => HeavyMachineGunVoice,
		ShotKind.Launcher => LauncherVoice,
		ShotKind.Cannon => CannonVoice,
		ShotKind.Mortar => MortarVoice,
		_ => default,
	};

	/// <summary>The numbers a flyby of <paramref name="kind"/> is made with; silence for <see cref="FlybyKind.None"/> or an unknown value.</summary>
	public static SoundVoice Flyby(FlybyKind kind) => kind switch
	{
		FlybyKind.Bullet => BulletFlybyVoice,
		FlybyKind.HeavyBullet => HeavyBulletFlybyVoice,
		FlybyKind.Shell => ShellFlybyVoice,
		_ => default,
	};

	/// <summary>A shot kind's name, as the console takes it; empty for <see cref="ShotKind.None"/>.</summary>
	public static string NameOf(ShotKind kind) => kind switch
	{
		ShotKind.Pistol => "pistol",
		ShotKind.Rifle => "rifle",
		ShotKind.Dmr => "dmr",
		ShotKind.MachineGun => "machine_gun",
		ShotKind.HeavyMachineGun => "heavy_machine_gun",
		ShotKind.Launcher => "launcher",
		ShotKind.Cannon => "cannon",
		ShotKind.Mortar => "mortar",
		_ => string.Empty,
	};

	/// <summary>A flyby kind's name, as the console takes it; empty for <see cref="FlybyKind.None"/>.</summary>
	public static string NameOf(FlybyKind kind) => kind switch
	{
		FlybyKind.Bullet => "bullet",
		FlybyKind.HeavyBullet => "heavy_bullet",
		FlybyKind.Shell => "shell",
		_ => string.Empty,
	};

	/// <summary>The shot kind <see cref="NameOf(ShotKind)"/> calls <paramref name="name"/>, ignoring case; false for anything else.</summary>
	public static bool TryParse(string name, out ShotKind kind)
	{
		foreach (ShotKind candidate in Shots)
		{
			if (string.Equals(NameOf(candidate), name?.Trim(), StringComparison.OrdinalIgnoreCase))
			{
				kind = candidate;
				return true;
			}
		}

		kind = ShotKind.None;
		return false;
	}

	/// <summary>The flyby kind <see cref="NameOf(FlybyKind)"/> calls <paramref name="name"/>, ignoring case; false for anything else.</summary>
	public static bool TryParse(string name, out FlybyKind kind)
	{
		foreach (FlybyKind candidate in Flybys)
		{
			if (string.Equals(NameOf(candidate), name?.Trim(), StringComparison.OrdinalIgnoreCase))
			{
				kind = candidate;
				return true;
			}
		}

		kind = FlybyKind.None;
		return false;
	}

	/// <summary>
	/// The pitch one play is made at: 1 moved by up to <see cref="SoundVoice.PitchJitter"/>
	/// either way, by <paramref name="unit"/> from 0 to 1 (0.5 is no change).
	/// </summary>
	public static float PitchScale(in SoundVoice voice, float unit)
	{
		float jitter = float.IsFinite(voice.PitchJitter) ? Math.Clamp(voice.PitchJitter, 0f, 0.5f) : 0f;
		float centred = float.IsFinite(unit) ? (Math.Clamp(unit, 0f, 1f) * 2f) - 1f : 0f;
		return 1f + (jitter * centred);
	}

	// ---- synthesis ----------------------------------------------------------------------

	/// <summary>
	/// The placeholder for <paramref name="voice"/>: mono samples from −1 to 1 at
	/// <paramref name="sampleRate"/>, scaled so the loudest is <see cref="Peak"/>, faded in
	/// over <see cref="AttackSeconds"/> and out over <see cref="ReleaseSeconds"/>. The same
	/// voice and <paramref name="seed"/> make the same samples. Empty for a silent voice.
	/// </summary>
	public static float[] Synthesize(in SoundVoice voice, int sampleRate = SampleRate, uint seed = 1u)
	{
		if (voice.IsSilent || !float.IsFinite(voice.Seconds) || sampleRate <= 0)
		{
			return Array.Empty<float>();
		}

		int count = Math.Max((int)MathF.Round(voice.Seconds * sampleRate), 1);
		var samples = new float[count];
		float dt = 1f / sampleRate;
		uint state = seed != 0 ? seed : 0x9E3779B9u;

		float crackA = OnePole(voice.CrackCutoffHz, sampleRate);
		float tailA = OnePole(voice.TailCutoffHz, sampleRate);
		float whizLowA = OnePole(voice.WhizLowHz, sampleRate);
		float whizHighA = OnePole(voice.WhizHighHz, sampleRate);

		float crack = 0f, tail = 0f, whizLow = 0f, whizHigh = 0f;
		float boomPhase = 0f, tonePhase = 0f;

		for (int i = 0; i < count; i++)
		{
			float t = i * dt;
			float progress = count > 1 ? (float)i / (count - 1) : 0f;
			float sample = 0f;

			// Crack: bright noise, gone almost at once.
			if (voice.CrackGain > 0f)
			{
				crack += crackA * (Noise(ref state) - crack);
				sample += voice.CrackGain * crack * Decay(t, voice.CrackSeconds);
			}

			// Boom: a low tone that starts sharp and falls to its pitch as it dies.
			if (voice.BoomGain > 0f && voice.BoomHz > 0f)
			{
				float hz = voice.BoomHz * (1f + (MathF.Max(voice.BoomDrop, 0f) * Decay(t, 0.02f)));
				boomPhase = Wrap(boomPhase + (hz * dt));
				sample += voice.BoomGain * MathF.Sin(boomPhase * MathF.Tau) * Decay(t, voice.BoomSeconds);
			}

			// Tail: duller noise that comes in a moment later and lingers.
			if (voice.TailGain > 0f && t >= voice.TailDelaySeconds)
			{
				tail += tailA * (Noise(ref state) - tail);
				sample += voice.TailGain * tail * Decay(t - voice.TailDelaySeconds, voice.TailSeconds);
			}

			// Whiz: band-limited noise that swells and fades, and a tone falling through it.
			if (voice.WhizGain > 0f || voice.ToneGain > 0f)
			{
				float envelope = Swell(t, voice.WhizAttackSeconds, voice.WhizSeconds);
				if (voice.WhizGain > 0f)
				{
					float noise = Noise(ref state);
					whizLow += whizLowA * (noise - whizLow);
					whizHigh += whizHighA * (noise - whizHigh);
					sample += voice.WhizGain * (whizHigh - whizLow) * envelope;
				}

				if (voice.ToneGain > 0f && voice.ToneHz > 0f)
				{
					float drop = Math.Clamp(voice.ToneDrop, 0f, 1.5f);
					float hz = voice.ToneHz * (1f + (drop * (0.5f - progress)));
					tonePhase = Wrap(tonePhase + (hz * dt));
					sample += voice.ToneGain * MathF.Sin(tonePhase * MathF.Tau) * envelope;
				}
			}

			samples[i] = sample;
		}

		// Faded first: a crack is loudest in its first few samples, and fading after
		// scaling would take the peak back down under Peak.
		Fade(samples, sampleRate);
		Normalize(samples, Peak);
		return samples;
	}

	/// <summary>
	/// Samples as signed 16-bit little-endian PCM, the layout an <c>AudioStreamWav</c> of
	/// format 16 bits holds. Anything outside −1 to 1 is clipped to it; a non-number is silence.
	/// </summary>
	public static byte[] ToPcm16(ReadOnlySpan<float> samples)
	{
		var bytes = new byte[samples.Length * 2];
		for (int i = 0; i < samples.Length; i++)
		{
			float s = float.IsFinite(samples[i]) ? Math.Clamp(samples[i], -1f, 1f) : 0f;
			short value = (short)MathF.Round(s * short.MaxValue);
			bytes[2 * i] = (byte)(value & 0xFF);
			bytes[(2 * i) + 1] = (byte)((value >> 8) & 0xFF);
		}

		return bytes;
	}

	/// <summary>e^(−t/τ) from t = 0; nothing before it, and nothing for a τ of zero.</summary>
	public static float Decay(float t, float seconds) =>
		t < 0f || seconds <= 0f ? 0f : MathF.Exp(-t / seconds);

	/// <summary>
	/// The whiz's envelope: up from 0 to 1 over <paramref name="attack"/> seconds along a
	/// quarter sine, then down with time constant <paramref name="decay"/>.
	/// </summary>
	public static float Swell(float t, float attack, float decay)
	{
		if (t < 0f)
		{
			return 0f;
		}

		if (attack > 0f && t < attack)
		{
			return MathF.Sin(0.5f * MathF.PI * t / attack);
		}

		return Decay(t - MathF.Max(attack, 0f), decay);
	}

	/// <summary>The coefficient of a one-pole low-pass with its corner at <paramref name="hz"/>.</summary>
	private static float OnePole(float hz, int sampleRate) =>
		hz > 0f ? 1f - MathF.Exp(-MathF.Tau * MathF.Min(hz, sampleRate * 0.45f) / sampleRate) : 0f;

	private static float Wrap(float phase) => phase - MathF.Floor(phase);

	/// <summary>Xorshift: white noise from −1 to 1, allocation-free and the same for the same seed.</summary>
	private static float Noise(ref uint state)
	{
		state ^= state << 13;
		state ^= state >> 17;
		state ^= state << 5;
		return ((state & 0xFFFFFF) / (float)0x800000) - 1f;
	}

	private static void Normalize(float[] samples, float peak)
	{
		float loudest = 0f;
		for (int i = 0; i < samples.Length; i++)
		{
			loudest = MathF.Max(loudest, MathF.Abs(samples[i]));
		}

		if (loudest <= 0f || !float.IsFinite(loudest))
		{
			Array.Clear(samples);
			return;
		}

		float scale = peak / loudest;
		for (int i = 0; i < samples.Length; i++)
		{
			samples[i] *= scale;
		}
	}

	private static void Fade(float[] samples, int sampleRate)
	{
		int attack = Math.Min((int)MathF.Ceiling(AttackSeconds * sampleRate), samples.Length);
		for (int i = 0; i < attack; i++)
		{
			samples[i] *= (float)i / attack;
		}

		int release = Math.Min((int)MathF.Ceiling(ReleaseSeconds * sampleRate), samples.Length);
		for (int i = 0; i < release; i++)
		{
			samples[samples.Length - 1 - i] *= (float)i / release;
		}
	}
}
