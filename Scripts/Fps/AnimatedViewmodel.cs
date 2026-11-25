using System.Collections.Generic;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Fps;

/// <summary>
/// A weapon's first-person viewmodel when it is a scene rather than a mesh
/// (<see cref="WeaponDefinition.Viewmodel"/>): arms and weapon in one skeleton, with the
/// clips <see cref="ViewmodelAnimator"/> chooses between (docs/VIEWMODELS.md).
///
/// The scene is authored from the eye, at its own field of view — the launcher's by
/// tools/blender/launcher.py — and drawn at that field of view whatever the camera's is,
/// through <see cref="BaseMaterial3D.FovOverride"/> on a copy of every material it has. The
/// same copies scale its depth toward the camera (<see cref="BaseMaterial3D.ZClipScale"/>), so
/// that a weapon held out ahead of the eye is not cut off by the wall the player is standing
/// against. Neither needs a second viewport, which is how this was done before Godot 4.5.
///
/// It casts no shadow: the body the camera looks out of casts one already
/// (<see cref="fps_controller"/>), and a shadow drawn from a field of view of its own would be
/// in the wrong place.
///
/// The animation player is driven by hand: each frame the clip is chosen and sought to, and
/// never played forward by the engine, because where it is comes from the simulation.
///
/// The sights are clips too: <c>aim</c>, the weapon at the eye, and <c>aim_fire</c>, its shot
/// from there. They are blended over the held clips by how far up the sights are
/// (<see cref="LocalAim"/>), bone by bone, so the shoulders stay where they are and only the
/// weapon and the hands come up — which moving the whole viewmodel, as a mesh is moved, cannot
/// do. A reload takes the sights down: it is a held clip.
/// </summary>
public sealed class AnimatedViewmodel
{
	/// <summary>How far the viewmodel's depth is scaled toward the camera.</summary>
	public const float ZClipScale = 0.4f;

	/// <summary>How long the sights take to come down for a reload, and back up after it [s].</summary>
	public const float AimBlendSeconds = 0.15f;

	private static readonly StringName[] ClipNames = { "idle", "fire", "reload" };
	private static readonly StringName AimName = "aim";
	private static readonly StringName AimFireName = "aim_fire";

	private readonly Node3D _root;
	private readonly AnimationPlayer _player;
	private readonly Node3D _muzzle;
	private readonly Skeleton3D _skeleton;
	private readonly float[] _lengths = new float[ClipNames.Length];
	private readonly bool[] _has = new bool[ClipNames.Length];

	private ViewmodelAnimator _animator;
	private double _idleSeconds;
	private StringName _shown;
	private StringName _sought;
	private float _aimWeight;
	private Vector3[] _positions = System.Array.Empty<Vector3>();
	private Quaternion[] _rotations = System.Array.Empty<Quaternion>();
	private Vector3[] _scales = System.Array.Empty<Vector3>();

	private AnimatedViewmodel(Node3D root, float fovDegrees)
	{
		_root = root;
		FovDegrees = fovDegrees;
		_player = Find<AnimationPlayer>(root);
		_muzzle = root.FindChild("Muzzle*", true, false) as Node3D;
		_skeleton = Find<Skeleton3D>(root);

		if (_player != null)
		{
			_player.CallbackModeProcess = AnimationMixer.AnimationCallbackModeProcess.Manual;
			for (int i = 0; i < ClipNames.Length; i++)
			{
				_has[i] = _player.HasAnimation(ClipNames[i]);
				_lengths[i] = _has[i] ? (float)_player.GetAnimation(ClipNames[i]).Length : 0f;
			}
		}

		Prepare(root, fovDegrees);
	}

	/// <summary>The scene's root, under the node that places and aims it.</summary>
	public Node3D Root => _root;

	/// <summary>The held clip being shown, by its name in the scene; null before the first frame is drawn.</summary>
	public StringName Clip => _shown;

	/// <summary>How much of the sights' clip is blended over it, from 0 to 1.</summary>
	public float AimWeight => _aimWeight;

	/// <summary>The scene's skeleton: arms and weapon in one.</summary>
	public Skeleton3D Skeleton => _skeleton;

	/// <summary>The scene's end of the barrel, as it is posed; null when it has none.</summary>
	public Node3D Muzzle => _muzzle;

	/// <summary>The vertical field of view it is drawn at [deg].</summary>
	public float FovDegrees { get; }

	/// <summary>A viewmodel from <paramref name="scene"/>; null when the scene is not a 3D one.</summary>
	public static AnimatedViewmodel Instantiate(PackedScene scene, float fovDegrees)
	{
		if (scene?.Instantiate() is not Node3D root)
		{
			return null;
		}

		root.Name = "Viewmodel";
		return new AnimatedViewmodel(root, fovDegrees);
	}

	public bool Visible
	{
		get => _root.Visible;
		set => _root.Visible = value;
	}

	/// <summary>
	/// Poses the viewmodel for the drawn tick <paramref name="now"/>, from the weapon's state, with
	/// the sights <paramref name="aim"/> of the way up. <paramref name="delta"/> runs the idle loop,
	/// which no state says the place of.
	/// </summary>
	public void Draw(in WeaponState state, in WeaponStats stats, double now, double delta, float aim)
	{
		if (_player == null)
		{
			return;
		}

		ViewmodelFrame frame = _animator.Step(state, stats, now, _lengths[(int)ViewmodelClip.Fire]);
		int clip = (int)frame.Clip;
		if (!_has[clip])
		{
			clip = (int)ViewmodelClip.Idle;
			if (!_has[clip])
			{
				return;
			}
		}

		double seconds;
		if (clip == (int)ViewmodelClip.Idle)
		{
			_idleSeconds = _lengths[clip] > 0f ? (_idleSeconds + delta) % _lengths[clip] : 0.0;
			seconds = _idleSeconds;
		}
		else
		{
			seconds = frame.Progress * _lengths[clip];
		}

		_shown = ClipNames[clip];
		Seek(_shown, seconds);

		// The sights: followed as they come up and go down, which the simulation already eases;
		// taken down over a moment for a reload, and put back after it.
		float target = frame.Clip == ViewmodelClip.Reload ? 0f : Mathf.Clamp(aim, 0f, 1f);
		_aimWeight = Mathf.MoveToward(_aimWeight, target, (float)delta / AimBlendSeconds);
		bool shot = frame.Clip == ViewmodelClip.Fire && _player.HasAnimation(AimFireName);
		if (_aimWeight <= 0.001f || _skeleton == null || !_player.HasAnimation(shot ? AimFireName : AimName))
		{
			return;
		}

		Capture();
		StringName sights = shot ? AimFireName : AimName;
		Seek(sights, shot ? frame.Progress * _player.GetAnimation(sights).Length : 0.0);
		Blend(_aimWeight);
	}

	/// <summary>Poses the skeleton as <paramref name="clip"/> has it at <paramref name="seconds"/>, from rest.</summary>
	private void Seek(StringName clip, double seconds)
	{
		// From rest every time: a clip need not key what it leaves alone (the import drops tracks
		// that never change), and nothing of a reload cut short may stay open under the next one.
		_skeleton?.ResetBonePoses();
		if (_sought != clip)
		{
			_player.Play(clip);
			_sought = clip;
		}

		_player.Seek(seconds, true);
	}

	/// <summary>Keeps the skeleton's pose, to blend the next one over.</summary>
	private void Capture()
	{
		int count = _skeleton.GetBoneCount();
		if (_positions.Length != count)
		{
			_positions = new Vector3[count];
			_rotations = new Quaternion[count];
			_scales = new Vector3[count];
		}

		for (int i = 0; i < count; i++)
		{
			_positions[i] = _skeleton.GetBonePosePosition(i);
			_rotations[i] = _skeleton.GetBonePoseRotation(i);
			_scales[i] = _skeleton.GetBonePoseScale(i);
		}
	}

	/// <summary>The kept pose toward the skeleton's present one, by <paramref name="weight"/>, bone by bone.</summary>
	private void Blend(float weight)
	{
		for (int i = 0; i < _positions.Length; i++)
		{
			_skeleton.SetBonePosePosition(i, _positions[i].Lerp(_skeleton.GetBonePosePosition(i), weight));
			_skeleton.SetBonePoseRotation(i, _rotations[i].Slerp(_skeleton.GetBonePoseRotation(i), weight));
			_skeleton.SetBonePoseScale(i, _scales[i].Lerp(_skeleton.GetBonePoseScale(i), weight));
		}
	}

	/// <summary>
	/// Where the end of the barrel is drawn, in world space: the scene's <c>Muzzle</c> (the first
	/// node whose name starts so), moved from
	/// where it is to where the camera would have to see it to put it where the viewmodel's own
	/// field of view does — on the same line out of the eye as it is drawn, at its true depth
	/// (docs/MUZZLE_EFFECTS.md §2.2). False with no muzzle in the scene.
	/// </summary>
	public bool TryGetMuzzle(Camera3D camera, out Vector3 muzzle)
	{
		muzzle = default;
		if (_muzzle == null || camera == null)
		{
			return false;
		}

		Transform3D eye = camera.GlobalTransform;
		Vector3 local = eye.AffineInverse() * _muzzle.GlobalPosition;
		float spread = ViewmodelOptics.Spread(camera.Fov, FovDegrees);
		muzzle = eye * new Vector3(local.X * spread, local.Y * spread, local.Z);
		return true;
	}

	/// <summary>Copies every material it draws with, to draw at its own field of view and depth, and turns its shadows off.</summary>
	private static void Prepare(Node root, float fovDegrees)
	{
		var copies = new Dictionary<Material, Material>();
		foreach (MeshInstance3D mesh in FindAll<MeshInstance3D>(root))
		{
			mesh.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
			if (mesh.Mesh == null)
			{
				continue;
			}

			for (int i = 0; i < mesh.Mesh.GetSurfaceCount(); i++)
			{
				Material source = mesh.GetSurfaceOverrideMaterial(i) ?? mesh.Mesh.SurfaceGetMaterial(i);
				if (source is not BaseMaterial3D material)
				{
					continue;
				}

				if (!copies.TryGetValue(material, out Material copy))
				{
					var drawn = (BaseMaterial3D)material.Duplicate();
					drawn.UseFovOverride = true;
					drawn.FovOverride = fovDegrees;
					drawn.UseZClipScale = true;
					drawn.ZClipScale = ZClipScale;
					copies[material] = copy = drawn;
				}

				mesh.SetSurfaceOverrideMaterial(i, copy);
			}
		}
	}

	private static T Find<T>(Node node) where T : Node
	{
		foreach (T found in FindAll<T>(node))
		{
			return found;
		}

		return null;
	}

	private static IEnumerable<T> FindAll<T>(Node node) where T : Node
	{
		if (node is T match)
		{
			yield return match;
		}

		foreach (Node child in node.GetChildren())
		{
			foreach (T found in FindAll<T>(child))
			{
				yield return found;
			}
		}
	}
}
