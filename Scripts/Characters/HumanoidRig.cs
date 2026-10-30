using System.Collections.Generic;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Characters;

/// <summary>What a humanoid body's owner tells its rig, every frame the body is drawn.</summary>
public interface IHumanoidPoseSource
{
	/// <summary>The pose as the owner knows it; the rig measures <see cref="BodyPose.GroundSpeed"/> itself.</summary>
	BodyPose ReadPose();
}

/// <summary>
/// The body a ground-force player and a strategist's unit on foot are drawn as: the
/// Universal Animation Library's mannequin (ThirdParty/UniversalAnimationLibrary,
/// Quaternius, CC0) and an <see cref="AnimationPlayer"/> playing its clips
/// (Scenes/humanoid.tscn).
///
/// Presentation only. The capsule a body collides with and is hit as is the owner's,
/// and unchanged; the mannequin is scaled to stand as tall as it (<see cref="Fit"/>), so
/// a round aimed at a head finds one. Which clip shows is <see cref="BodyAnimator"/>'s,
/// from the owner's <see cref="IHumanoidPoseSource"/> — its parent — and from what
/// happens to it: <see cref="Shoot"/>, <see cref="Throw"/>, <see cref="Die"/>. A
/// dedicated server draws nothing and never keeps one.
///
/// The library's clips are a pistol's: what aims and fires here is a pistol stance
/// whatever is in the hands, and there is no throw among them, so the throw is the
/// one-handed overhand attack — the right hand goes back over the head and forward
/// and down, which is a throw's arc.
/// </summary>
public partial class HumanoidRig : Node3D
{
	public const string ScenePath = "res://Scenes/humanoid.tscn";

	/// <summary>The mannequin's height in its own metres: the top of UAL1_Standard.glb's mesh bounds.</summary>
	public const float ModelHeightMeters = 1.83f;

	/// <summary>The bone a chest-worn thing hangs off: the upper spine, about armpit height.</summary>
	private const string ChestBone = "spine_03";

	/// <summary>Faster than anything walks, sprints or drives [m/s]: a respawn or a correction, not a stride.</summary>
	private const float TeleportSpeed = 15f;

	/// <summary>How quickly the measured speed follows the body [s]: steadies a server's 60 Hz steps under a faster frame.</summary>
	private const float SpeedSmoothingSeconds = 0.1f;

	private const float BlendSeconds = 0.15f;

	/// <summary>
	/// One clip as the library names it — the import drops a trailing <c>_Loop</c> and
	/// loops what it dropped it from — how fast it plays, and the ground speed its stride
	/// was authored at [m/s] (from UAL1_Standard_RM.glb's root motion), 0 for one that does
	/// not travel.
	/// </summary>
	private readonly struct Clip
	{
		public readonly string Name;
		public readonly bool Loop;
		public readonly float Rate;
		public readonly float StrideSpeed;

		public Clip(string name, bool loop, float rate = 1f, float strideSpeed = 0f)
		{
			Name = name;
			Loop = loop;
			Rate = rate;
			StrideSpeed = strideSpeed;
		}
	}

	/// <summary>By <see cref="BodyClip"/>, in its order.</summary>
	private static readonly Clip[] Clips =
	{
		new("Idle", loop: true),
		new("Walk", loop: true, strideSpeed: 0.975f),
		new("Jog_Fwd", loop: true, strideSpeed: 5.357f),
		new("Sprint", loop: true, strideSpeed: 8.25f),
		new("Crouch_Idle", loop: true),
		new("Crouch_Fwd", loop: true, strideSpeed: 0.75f),
		new("Jump_Start", loop: false, rate: 1.5f),
		new("Jump", loop: true),
		new("Jump_Land", loop: false, rate: 1.5f),

		// Single-pose clips: looped, so the pose holds however long the aim does.
		new("Pistol_Aim_Neutral", loop: true),
		new("Pistol_Aim_Up", loop: true),
		new("Pistol_Aim_Down", loop: true),
		new("Pistol_Shoot", loop: false),
		new("Sword_Attack", loop: false, rate: 1.4f),
		new("Death01", loop: false),
	};

	private static PackedScene _scene;

	/// <summary>A material pair per colour, so every rifleman of one kind draws with the same two.</summary>
	private static readonly Dictionary<Color, (StandardMaterial3D Main, StandardMaterial3D Joints)> Paints = new();

	private AnimationPlayer _player;
	private IHumanoidPoseSource _source;
	private BodyAnimator _animator;
	private readonly StringName[] _names = new StringName[Clips.Length];
	private BodyClip _shown;
	private bool _showing;
	private int _shownSerial;
	private Vector3 _lastPosition;
	private float _speed;
	private BoneAttachment3D _chest;
	private float _scale = 1f;

	/// <summary>Where a dead body faces, in the world: a dead player keeps turning their view, and their corpse does not.</summary>
	private Basis _deathBasis;

	/// <summary>A fresh body, not yet in the tree.</summary>
	public static HumanoidRig Instantiate()
	{
		_scene ??= GD.Load<PackedScene>(ScenePath);
		return _scene.Instantiate<HumanoidRig>();
	}

	/// <summary>Dead, and still falling: a corpse its owner keeps drawing until this is false.</summary>
	public bool IsDying => _animator?.IsDying ?? false;

	public override void _Ready()
	{
		_player = GetNode<AnimationPlayer>("AnimationPlayer");
		AdoptImportedLibrary();
		ResolveClips();

		_animator = new BodyAnimator(new BodyClipTimes(Seconds(BodyClip.JumpStart), Seconds(BodyClip.Land),
			Seconds(BodyClip.Shoot), Seconds(BodyClip.Throw), Seconds(BodyClip.Death)));
		_source = GetParent() as IHumanoidPoseSource;
		_lastPosition = GlobalPosition;
		Show(BodyClip.Idle, 0f);
	}

	public override void _Process(double delta)
	{
		float dt = (float)delta;
		Vector3 position = GlobalPosition;
		Vector3 step = position - _lastPosition;
		step.Y = 0f;
		_lastPosition = position;

		if (_animator.IsDead)
		{
			GlobalBasis = _deathBasis;
		}

		// A hidden body — a rider, a fogged player, the dead — poses for nobody.
		bool visible = IsVisibleInTree();
		if (_player.Active != visible)
		{
			_player.Active = visible;
		}

		if (_source == null || !visible)
		{
			return;
		}

		float measured = dt > 0f ? step.Length() / dt : _speed;
		if (measured > TeleportSpeed)
		{
			measured = _speed;
		}

		_speed += (measured - _speed) * (1f - Mathf.Exp(-dt / SpeedSmoothingSeconds));

		BodyPose pose = _source.ReadPose();
		pose.GroundSpeed = _speed;
		Show(_animator.Step(pose, dt), BlendSeconds);
	}

	/// <summary>Stands the mannequin as tall as <paramref name="heightMeters"/>: its owner's standing capsule.</summary>
	public void Fit(float heightMeters)
	{
		_scale = heightMeters / ModelHeightMeters;
		Scale = Vector3.One * _scale;
	}

	/// <summary>Paints the body <paramref name="color"/> and its joints a darker shade of it.</summary>
	public void Tint(Color color)
	{
		if (!Paints.TryGetValue(color, out var paint))
		{
			paint = (Paint(color), Paint(color.Darkened(0.55f)));
			Paints[color] = paint;
		}

		foreach (MeshInstance3D mesh in Meshes())
		{
			for (int i = 0; i < mesh.GetSurfaceOverrideMaterialCount(); i++)
			{
				string name = mesh.Mesh.SurfaceGetMaterial(i)?.ResourceName ?? string.Empty;
				bool joints = name == "M_Joints" || (name.Length == 0 && i > 0);
				mesh.SetSurfaceOverrideMaterial(i, joints ? paint.Joints : paint.Main);
			}
		}
	}

	/// <summary>Whether the body casts a shadow, is drawn and casts one, or only casts one — a first-person body.</summary>
	public void SetShadowCasting(GeometryInstance3D.ShadowCastingSetting setting)
	{
		foreach (MeshInstance3D mesh in Meshes())
		{
			mesh.CastShadow = setting;
		}
	}

	/// <summary>A node that moves with the upper chest, for a vest; the rig itself when the skeleton cannot be found.</summary>
	public Node3D Chest
	{
		get
		{
			if (_chest != null)
			{
				return _chest;
			}

			if (FindSkeleton() is { } skeleton)
			{
				_chest = new BoneAttachment3D { Name = "Chest", BoneName = ChestBone };
				skeleton.AddChild(_chest);
				return _chest;
			}

			return this;
		}
	}

	/// <summary>A round fired: the recoil standing still, and the weapon kept up for a moment after.</summary>
	public void Shoot() => _animator?.Shoot();

	/// <summary>A grenade thrown.</summary>
	public void Throw() => _animator?.Throw();

	public void Die()
	{
		if (_animator == null || _animator.IsDead)
		{
			return;
		}

		_animator.Die();
		_deathBasis = GlobalBasis;
		Show(BodyClip.Death, 0.1f);
	}

	/// <summary>Back on its feet at once: a respawn is a teleport, and so is its pose.</summary>
	public void Revive()
	{
		if (_animator == null)
		{
			return;
		}

		bool wasDead = _animator.IsDead;
		_animator.Revive();
		_speed = 0f;
		_lastPosition = GlobalPosition;
		if (wasDead)
		{
			Basis = Basis.Identity.Scaled(Vector3.One * _scale);
			Show(BodyClip.Idle, 0f);
		}
	}

	/// <summary>
	/// The imported scene brings its own player with the clips in it. Its library moves to
	/// this rig's player, which the scene points at the mannequin, and the imported one goes:
	/// one player per body, and the one the rig was written against.
	/// </summary>
	private void AdoptImportedLibrary()
	{
		Node model = GetNodeOrNull("Model");
		if (model?.FindChild("AnimationPlayer", false, false) is not AnimationPlayer imported)
		{
			GD.PushWarning($"[humanoid] {ScenePath}: no imported AnimationPlayer under Model; the body will not animate");
			return;
		}

		foreach (StringName name in imported.GetAnimationLibraryList())
		{
			AnimationLibrary library = imported.GetAnimationLibrary(name);
			imported.RemoveAnimationLibrary(name);
			if (!_player.HasAnimationLibrary(name))
			{
				_player.AddAnimationLibrary(name, library);
			}
		}

		imported.Stop();
		model.RemoveChild(imported);
		imported.QueueFree();
	}

	private void ResolveClips()
	{
		if (Clips.Length != System.Enum.GetValues<BodyClip>().Length)
		{
			GD.PushError("[humanoid] the clip table and BodyClip have drifted apart");
		}

		for (int i = 0; i < Clips.Length; i++)
		{
			Clip clip = Clips[i];
			StringName name = _player.HasAnimation(clip.Name) ? clip.Name
				: _player.HasAnimation(clip.Name + "_Loop") ? clip.Name + "_Loop"
				: null;
			_names[i] = name;
			if (name == null)
			{
				GD.PushWarning($"[humanoid] no clip named {clip.Name} for {(BodyClip)i}");
				continue;
			}

			// The import already loops what it named a loop; the aim poses are looped here.
			_player.GetAnimation(name).LoopMode = clip.Loop ? Animation.LoopModeEnum.Linear : Animation.LoopModeEnum.None;
		}
	}

	/// <summary>How long a clip runs as played [s]; 0 for one the library does not have.</summary>
	private float Seconds(BodyClip clip)
	{
		StringName name = _names[(int)clip];
		return name == null ? 0f : (float)_player.GetAnimation(name).Length / Clips[(int)clip].Rate;
	}

	private void Show(BodyClip clip, float blendSeconds)
	{
		int serial = _animator.ActionSerial;
		bool begunAgain = serial != _shownSerial && clip == _animator.Action;
		_shownSerial = serial;

		Clip spec = Clips[(int)clip];
		_player.SpeedScale = spec.Rate * BodyAnimator.StrideScale(_speed, spec.StrideSpeed);

		if (_showing && clip == _shown && !begunAgain)
		{
			return;
		}

		bool restart = _showing && clip == _shown;
		_shown = clip;
		_showing = true;

		StringName name = _names[(int)clip];
		if (name == null)
		{
			return;
		}

		_player.Play(name, blendSeconds);
		if (restart)
		{
			_player.Seek(0, true);
		}
	}

	private IEnumerable<MeshInstance3D> Meshes()
	{
		foreach (Node node in FindChildren("*", nameof(MeshInstance3D), true, false))
		{
			if (node is MeshInstance3D mesh && mesh.Mesh != null)
			{
				yield return mesh;
			}
		}
	}

	private Skeleton3D FindSkeleton()
	{
		foreach (Node node in FindChildren("*", nameof(Skeleton3D), true, false))
		{
			if (node is Skeleton3D skeleton)
			{
				return skeleton;
			}
		}

		return null;
	}

	private static StandardMaterial3D Paint(Color color) => new()
	{
		AlbedoColor = color,
		Roughness = 0.5f,

		// As the asset's own materials are: double-sided.
		CullMode = BaseMaterial3D.CullModeEnum.Disabled,
	};
}
