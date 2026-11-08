using System;
using Gdpyr.Core;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Effects;

// TODO(audio, docs/AUDIO.md §5): listen on a display, as a client and on a listen host: every
// weapon fired by you and by someone else near and far, a burst from the LMG and the HMG, rounds
// fired past you from beyond 10 m, and a mortar coming down near you. Built headless and compiled,
// never heard.
/// <summary>
/// Every weapon sound this peer plays (docs/AUDIO.md): shots, and rounds going past
/// the listener. A pool of 3D players for the shots everyone else fires, placed at the
/// muzzle; a pool of flat players for the local player's own, played at the ear; and a
/// pool of 3D players for flybys, placed where the round came closest.
///
/// Made only by a process that draws — <c>CombatManager</c> builds it beside the
/// explosions, never on a dedicated server — so <see cref="Instance"/> is null there and
/// every caller's <c>?.</c> is the whole of "a server plays nothing". Purely cosmetic:
/// it reads nothing from the simulation it was not handed, and writes nothing back.
///
/// A weapon plays its <c>WeaponDefinition.ShotSound</c>, and a round its
/// <c>ProjectileDefinition.FlybySound</c>, when it has one; until then, the placeholder
/// <see cref="WeaponSounds.Synthesize"/> makes for its kind, once, on first need (§3, §4).
/// </summary>
public partial class WeaponAudio : Node3D
{
	/// <summary>
	/// The bus every weapon sound goes to when the project has one by this name, so a mixer
	/// can be put on weapons alone; <c>Master</c> until then.
	/// </summary>
	public const string BusName = "Weapons";

	/// <summary>
	/// How many shots by others can be heard at once; past it the oldest is cut short. The
	/// rifle at 600 rounds a minute keeps seven of its own going, so this is four or five
	/// automatic weapons firing together.
	/// </summary>
	private const int ShotCapacity = 32;

	/// <summary>How many of the local player's own shots can be heard at once.</summary>
	private const int OwnShotCapacity = 8;

	/// <summary>How many flybys can be heard at once.</summary>
	private const int FlybyCapacity = 16;

	/// <summary>
	/// How long past its stream's end a player is given before it is taken back whether or
	/// not it says it has finished [s]: an audio driver that mixes nothing — a headless run's —
	/// never says so.
	/// </summary>
	private const double GraceSeconds = 0.1;

	/// <summary>The same, for a stream that cannot say how long it is (a randomizer over several takes).</summary>
	private const double UnknownLengthSeconds = 3.0;

	public static WeaponAudio Instance { get; private set; }

	private readonly AudioStream[] _shotPlaceholders = new AudioStream[WeaponSounds.Shots.Length + 1];
	private readonly AudioStream[] _flybyPlaceholders = new AudioStream[WeaponSounds.Flybys.Length + 1];
	private readonly double[] _shotEnds = new double[ShotCapacity];
	private readonly double[] _ownShotEnds = new double[OwnShotCapacity];
	private readonly double[] _flybyEnds = new double[FlybyCapacity];
	private readonly Random _random = new();

	private EffectPool<AudioStreamPlayer3D> _shots;
	private EffectPool<AudioStreamPlayer> _ownShots;
	private EffectPool<AudioStreamPlayer3D> _flybys;
	private double _clock;
	private StringName _bus;

	/// <summary>Shots played so far — the local player's own and everyone else's — for the debug HUD's <c>sounds</c> row.</summary>
	public int ShotsPlayed { get; private set; }

	/// <summary>Flybys played so far.</summary>
	public int FlybysPlayed { get; private set; }

	/// <summary>Sounds playing now.</summary>
	public int Active => (_shots?.ActiveCount ?? 0) + (_ownShots?.ActiveCount ?? 0) + (_flybys?.ActiveCount ?? 0);

	/// <summary>Sounds cut short for a newer one: anything but 0 means a pool is small for the fight.</summary>
	public int Evictions => (_shots?.Evictions ?? 0) + (_ownShots?.Evictions ?? 0) + (_flybys?.Evictions ?? 0);

	public override void _EnterTree()
	{
		Instance = this;
	}

	public override void _ExitTree()
	{
		if (Instance == this)
		{
			Instance = null;
		}
	}

	public override void _Ready()
	{
		_bus = AudioServer.GetBusIndex(BusName) >= 0 ? BusName : "Master";
		_shots = new EffectPool<AudioStreamPlayer3D>(ShotCapacity, () => NewPlayer3D("Shot", _shots));
		_ownShots = new EffectPool<AudioStreamPlayer>(OwnShotCapacity, NewOwnPlayer);
		_flybys = new EffectPool<AudioStreamPlayer3D>(FlybyCapacity, () => NewPlayer3D("Flyby", _flybys));
	}

	/// <summary>
	/// A round leaving weapon <paramref name="weaponId"/>'s muzzle at <paramref name="origin"/>:
	/// at the ear when <paramref name="own"/> — the local player fired it — and in 3D there
	/// otherwise. Nothing for a weapon whose shot is none.
	/// </summary>
	public void PlayShot(byte weaponId, Vector3 origin, bool own) =>
		PlayShot(WeaponCatalog.ShotOf(weaponId), WeaponCatalog.Definition(weaponId)?.ShotSound, origin, own);

	/// <summary>A shot of <paramref name="kind"/>: <paramref name="recording"/> when there is one, else its placeholder.</summary>
	public void PlayShot(ShotKind kind, AudioStream recording, Vector3 origin, bool own)
	{
		SoundVoice voice = WeaponSounds.Shot(kind);
		if (voice.IsSilent || !IsInsideTree() || _shots == null)
		{
			return;
		}

		AudioStream stream = recording ?? ShotPlaceholder(kind);
		float pitch = WeaponSounds.PitchScale(voice, _random.NextSingle());
		ShotsPlayed++;

		if (own)
		{
			AudioStreamPlayer player = _ownShots.Acquire(out int index, out _);
			player.Stop();
			player.Stream = stream;
			player.VolumeDb = voice.VolumeDb + WeaponSounds.OwnShotDb;
			player.PitchScale = pitch;
			player.Play();
			_ownShotEnds[index] = EndOf(stream, pitch);
			return;
		}

		AudioStreamPlayer3D shot = _shots.Acquire(out int slot, out _);
		Play3D(shot, stream, voice, origin, pitch);
		_shotEnds[slot] = EndOf(stream, pitch);
	}

	/// <summary>
	/// Weapon <paramref name="weaponId"/>'s round going past the listener, played at
	/// <paramref name="point"/> — where it came closest. Nothing for a round whose flyby is none.
	/// </summary>
	public void PlayFlyby(byte weaponId, Vector3 point) =>
		PlayFlyby(WeaponCatalog.FlybyOf(weaponId), WeaponCatalog.Definition(weaponId)?.Projectile?.FlybySound, point);

	/// <summary>A flyby of <paramref name="kind"/>: <paramref name="recording"/> when there is one, else its placeholder.</summary>
	public void PlayFlyby(FlybyKind kind, AudioStream recording, Vector3 point)
	{
		SoundVoice voice = WeaponSounds.Flyby(kind);
		if (voice.IsSilent || !IsInsideTree() || _flybys == null)
		{
			return;
		}

		AudioStream stream = recording ?? FlybyPlaceholder(kind);
		float pitch = WeaponSounds.PitchScale(voice, _random.NextSingle());
		FlybysPlayed++;

		AudioStreamPlayer3D player = _flybys.Acquire(out int index, out _);
		Play3D(player, stream, voice, point, pitch);
		_flybyEnds[index] = EndOf(stream, pitch);
	}

	/// <summary>Stops every sound at once, keeping the players for next time.</summary>
	public void Clear()
	{
		StopAll(_shots);
		StopAll(_ownShots);
		StopAll(_flybys);
	}

	public override void _Process(double delta)
	{
		_clock += delta;
		Collect(_shots, _shotEnds);
		Collect(_ownShots, _ownShotEnds);
		Collect(_flybys, _flybyEnds);
	}

	// ---- playing ------------------------------------------------------------------------

	private static void Play3D(AudioStreamPlayer3D player, AudioStream stream, in SoundVoice voice, Vector3 at,
		float pitch)
	{
		player.Stop();
		player.Stream = stream;
		player.GlobalPosition = at;
		player.UnitSize = voice.UnitSizeMeters;
		player.MaxDistance = voice.MaxDistanceMeters;
		player.VolumeDb = voice.VolumeDb;
		player.PitchScale = pitch;
		player.Play();
	}

	/// <summary>When a stream started now at <paramref name="pitch"/> will have finished, on this node's clock.</summary>
	private double EndOf(AudioStream stream, float pitch)
	{
		double length = stream?.GetLength() ?? 0.0;
		double seconds = length > 0.0 ? length / Math.Max(pitch, 0.01f) : UnknownLengthSeconds;
		return _clock + seconds + GraceSeconds;
	}

	/// <summary>Takes back every player that has finished, or is past when it should have.</summary>
	private void Collect<T>(EffectPool<T> pool, double[] ends) where T : Node
	{
		if (pool == null || pool.ActiveCount == 0)
		{
			return;
		}

		for (int i = 0; i < pool.Created; i++)
		{
			if (!pool.IsActive(i))
			{
				continue;
			}

			T player = pool.At(i);
			bool playing = player switch
			{
				AudioStreamPlayer3D spatial => spatial.Playing,
				AudioStreamPlayer flat => flat.Playing,
				_ => false,
			};

			if (!playing || _clock >= ends[i])
			{
				Stop(player);
				pool.Release(i);
			}
		}
	}

	private static void StopAll<T>(EffectPool<T> pool) where T : Node
	{
		if (pool == null)
		{
			return;
		}

		for (int i = 0; i < pool.Created; i++)
		{
			Stop(pool.At(i));
		}

		pool.ReleaseAll();
	}

	private static void Stop(Node player)
	{
		switch (player)
		{
			case AudioStreamPlayer3D spatial:
				spatial.Stop();
				break;
			case AudioStreamPlayer flat:
				flat.Stop();
				break;
		}
	}

	// ---- placeholders -------------------------------------------------------------------

	private AudioStream ShotPlaceholder(ShotKind kind)
	{
		int slot = Math.Min((int)kind, _shotPlaceholders.Length - 1);
		return _shotPlaceholders[slot] ??= Placeholder(WeaponSounds.Shot(kind), 0x5107u + (uint)kind);
	}

	private AudioStream FlybyPlaceholder(FlybyKind kind)
	{
		int slot = Math.Min((int)kind, _flybyPlaceholders.Length - 1);
		return _flybyPlaceholders[slot] ??= Placeholder(WeaponSounds.Flyby(kind), 0xF1B7u + (uint)kind);
	}

	/// <summary>A voice's placeholder as a stream the engine can play: 16-bit mono PCM, made here rather than imported.</summary>
	private static AudioStreamWav Placeholder(in SoundVoice voice, uint seed) => new()
	{
		Format = AudioStreamWav.FormatEnum.Format16Bits,
		MixRate = WeaponSounds.SampleRate,
		Stereo = false,
		Data = WeaponSounds.ToPcm16(WeaponSounds.Synthesize(voice, WeaponSounds.SampleRate, seed)),
	};

	// ---- construction -------------------------------------------------------------------

	private AudioStreamPlayer3D NewPlayer3D<T>(string name, EffectPool<T> pool) where T : class
	{
		var player = new AudioStreamPlayer3D
		{
			Name = $"{name}{pool?.Created ?? 0}",
			Bus = _bus,
			DopplerTracking = AudioStreamPlayer3D.DopplerTrackingEnum.Disabled,
		};

		AddChild(player);
		return player;
	}

	private AudioStreamPlayer NewOwnPlayer()
	{
		var player = new AudioStreamPlayer
		{
			Name = $"OwnShot{_ownShots?.Created ?? 0}",
			Bus = _bus,
		};

		AddChild(player);
		return player;
	}
}
