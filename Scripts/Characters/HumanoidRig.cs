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
/// Quaternius, CC0), posed by an <see cref="AnimationTree"/> from its clips
/// (Scenes/humanoid.tscn).
///
/// Presentation only. The capsule a body collides with and is hit as is the owner's,
/// and unchanged; the mannequin is scaled to stand as tall as it (<see cref="Fit"/>), so
/// a round aimed at a head finds one. How it is posed is <see cref="BodyAnimator"/>'s,
/// from the owner's <see cref="IHumanoidPoseSource"/> — its parent — and from what
/// happens to it: <see cref="Shoot"/>, <see cref="Throw"/>, <see cref="Die"/>. A
/// dedicated server draws nothing and never keeps one.
///
/// The tree is layered (<see cref="HumanoidBlendTree"/>): the legs walk, run, crouch
/// and jump, and the chest, arms and head over them aim, recoil and throw, so a body
/// can do both at once; <see cref="TorsoAlign"/> keeps the aim on the body's line over
/// a stride that turns the hips. The scene's <c>AnimationPlayer</c> only holds the
/// clips, for the tree to read.
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

	/// <summary>The bone the arms layer starts at: it and everything off it are the chest, arms and head.</summary>
	private const string UpperBodyBone = "spine_01";

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

	/// <summary>The one graph every body plays, built from the first body's skeleton (<see cref="HumanoidBlendTree"/>).</summary>
	private static AnimationNodeBlendTree _blendTree;

	/// <summary>A material pair per colour, so every rifleman of one kind draws with the same two.</summary>
	private static readonly Dictionary<Color, (StandardMaterial3D Main, StandardMaterial3D Joints)> Paints = new();

	private AnimationPlayer _player;
	private AnimationTree _tree;
	private TorsoAlign _torso;
	private IHumanoidPoseSource _source;
	private BodyAnimator _animator;
	private readonly string[] _names = new string[Clips.Length];
	private BodyClip _legsShown;
	private bool _legsStarted;
	private int _legsSerial;
	private int _armsSerial;
	private GroundSpeedMeter _meter;
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
		_tree = GetNode<AnimationTree>("AnimationTree");
		if (!AdoptImportedLibrary() || FindSkeleton() is not { } skeleton)
		{
			_tree.Active = false;
			return;
		}

		ResolveClips();

		// A library, not a player: the tree plays the clips.
		_player.Active = false;

		Node model = GetNode("Model");
		string skeletonPath = model.GetPathTo(skeleton).ToString();
		_blendTree ??= HumanoidBlendTree.Build(clip => _names[(int)clip] ?? Clips[(int)clip].Name,
			UpperBodyTracks(skeleton, skeletonPath));
		_tree.TreeRoot = _blendTree;
		_tree.AnimPlayer = _tree.GetPathTo(_player);

		_tree.Set(HumanoidBlendTree.RecoilDeltaAmount, 1f);
		foreach (BodyClip clip in System.Enum.GetValues<BodyClip>())
		{
			_tree.Set(HumanoidBlendTree.Speed(clip), Clips[(int)clip].Rate);
		}

		int spine = skeleton.FindBone(UpperBodyBone);
		_torso = new TorsoAlign { Name = "TorsoAlign" };
		_torso.Configure(skeleton, spine, TorsoAlign.PoseRotation(AnimationOf(BodyClip.Aim), skeletonPath, skeleton,
			spine >= 0 ? skeleton.GetBoneParent(spine) : -1));
		skeleton.AddChild(_torso);

		_animator = new BodyAnimator(new BodyClipTimes(Seconds(BodyClip.JumpStart), Seconds(BodyClip.Land),
			Seconds(BodyClip.Shoot), Seconds(BodyClip.Throw), Seconds(BodyClip.Death)));
		_source = GetParent() as IHumanoidPoseSource;
		_meter = new GroundSpeedMeter(1f / Engine.PhysicsTicksPerSecond, GlobalPosition, Engine.GetPhysicsFrames());
		_tree.Active = true;
		Pose(default);
	}

	public override void _Process(double delta)
	{
		if (_animator == null)
		{
			return;
		}

		float dt = (float)delta;
		if (_animator.IsDead)
		{
			GlobalBasis = _deathBasis;
		}

		// A hidden body — a rider, a fogged player, the dead — poses for nobody.
		bool visible = IsVisibleInTree();
		if (_tree.Active != visible)
		{
			_tree.Active = visible;
		}

		// Measured by the ticks that have run, not by the frame: the body moves on the tick.
		ulong tick = Engine.GetPhysicsFrames();
		if (!visible)
		{
			_meter.Track(GlobalPosition, tick);
			return;
		}

		// A body with no owner to ask stands — and still falls when it is killed.
		BodyPose pose = _source?.ReadPose() ?? default;
		pose.GroundSpeed = _meter.Observe(GlobalPosition, tick);
		Pose(_animator.Step(pose, dt));
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

	/// <summary>A round fired: the arms up and the recoil over whatever the legs are doing, and the aim kept up for a moment after.</summary>
	public void Shoot() => _animator?.Shoot();

	/// <summary>A grenade thrown, by the arms, over whatever the legs are doing.</summary>
	public void Throw() => _animator?.Throw();

	public void Die()
	{
		if (_animator == null || _animator.IsDead)
		{
			return;
		}

		_animator.Die();
		_deathBasis = GlobalBasis;
		_tree.Set(HumanoidBlendTree.DeathSeekRequest, 0.0);
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
		_meter.Reset(GlobalPosition, Engine.GetPhysicsFrames());
		if (wasDead)
		{
			Basis = Basis.Identity.Scaled(Vector3.One * _scale);
			_tree.Set(HumanoidBlendTree.RecoilRequest, (int)AnimationNodeOneShot.OneShotRequest.Abort);
			_tree.Set(HumanoidBlendTree.ThrowRequest, (int)AnimationNodeOneShot.OneShotRequest.Abort);
			Pose(default);
		}
	}

	/// <summary>Sets the tree's parameters from <paramref name="mix"/>, and starts what began since the last frame.</summary>
	private void Pose(in BodyMix mix)
	{
		// The legs change clip when they are told to; a push-off or a landing begun
		// again while it shows is played again from the top.
		bool begunAgain = _animator.LegsSerial != _legsSerial && _animator.IsLegsActing
			&& mix.Legs == _animator.LegsAction && mix.Legs == _legsShown;
		_legsSerial = _animator.LegsSerial;
		if (!_legsStarted || mix.Legs != _legsShown || begunAgain)
		{
			_tree.Set(HumanoidBlendTree.LegsRequest, HumanoidBlendTree.LegsInput(mix.Legs));
			_legsShown = mix.Legs;
			_legsStarted = true;
		}

		Clip legs = Clips[(int)mix.Legs];
		_tree.Set(HumanoidBlendTree.Speed(mix.Legs), legs.Rate * BodyAnimator.StrideScale(_meter.Speed, legs.StrideSpeed));

		// A recoil or a throw that began, if it is still running: a body that fired while
		// hidden does not recoil when it is next seen.
		if (_animator.ArmsSerial != _armsSerial)
		{
			_armsSerial = _animator.ArmsSerial;
			if (_animator.IsArmsActing)
			{
				_tree.Set(_animator.ArmsAction == BodyClip.Throw ? HumanoidBlendTree.ThrowRequest
					: HumanoidBlendTree.RecoilRequest, (int)AnimationNodeOneShot.OneShotRequest.Fire);
			}
		}

		_tree.Set(HumanoidBlendTree.ArmsAmount, mix.Arms);
		_tree.Set(HumanoidBlendTree.AimPitchAmount, mix.AimBlend);
		_tree.Set(HumanoidBlendTree.DeathAmount, mix.Death);
		_torso.Weight = mix.Arms * (1f - mix.Death);
	}

	/// <summary>
	/// The imported scene brings its own player with the clips in it. Its library moves to
	/// this rig's player, which the scene points at the mannequin and the tree reads from,
	/// and the imported one goes: one player per body, and the one the rig was written against.
	/// </summary>
	private bool AdoptImportedLibrary()
	{
		Node model = GetNodeOrNull("Model");
		if (model?.FindChild("AnimationPlayer", false, false) is not AnimationPlayer imported)
		{
			GD.PushWarning($"[humanoid] {ScenePath}: no imported AnimationPlayer under Model; the body will not animate");
			return false;
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
		return true;
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
			string found = _player.HasAnimation(clip.Name) ? clip.Name
				: _player.HasAnimation(clip.Name + "_Loop") ? clip.Name + "_Loop"
				: null;
			if (found == null)
			{
				GD.PushWarning($"[humanoid] no clip named {clip.Name} for {(BodyClip)i}");
				continue;
			}

			_names[i] = found;

			// The import already loops what it named a loop; the aim poses are looped here.
			_player.GetAnimation(found).LoopMode = clip.Loop ? Animation.LoopModeEnum.Linear : Animation.LoopModeEnum.None;
		}
	}

	/// <summary>The clip, or null for one the library does not have.</summary>
	private Animation AnimationOf(BodyClip clip)
	{
		string name = _names[(int)clip];
		return name == null ? null : _player.GetAnimation(name);
	}

	/// <summary>How long a clip runs as played [s]; 0 for one the library does not have.</summary>
	private float Seconds(BodyClip clip) =>
		AnimationOf(clip) is { } animation ? (float)animation.Length / Clips[(int)clip].Rate : 0f;

	/// <summary>The tracks of the arms layer: <see cref="UpperBodyBone"/> and every bone off it, as the clips address them.</summary>
	private static List<NodePath> UpperBodyTracks(Skeleton3D skeleton, string skeletonPath)
	{
		var paths = new List<NodePath>();
		int root = skeleton.FindBone(UpperBodyBone);
		if (root < 0)
		{
			GD.PushWarning($"[humanoid] no bone named {UpperBodyBone}; the arms will not layer over the legs");
			return paths;
		}

		var open = new Stack<int>();
		open.Push(root);
		while (open.Count > 0)
		{
			int bone = open.Pop();
			paths.Add(new NodePath($"{skeletonPath}:{skeleton.GetBoneName(bone)}"));
			foreach (int child in skeleton.GetBoneChildren(bone))
			{
				open.Push(child);
			}
		}

		return paths;
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
