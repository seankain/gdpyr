using Godot;

namespace Gdpyr.Characters;

/// <summary>
/// Keeps an aiming body's chest facing where the aim stance faces it, whatever the
/// hips under it are doing (<see cref="HumanoidRig"/>).
///
/// An AnimationTree layers bones in their parents' space: the arms layer hands
/// <c>spine_01</c> the aim's rotation relative to the hips, and the hips are the
/// legs'. The library's walk, jog and sprint carry their hips square to the path and
/// the crouches pitch them forward, where the pistol stance stands bladed about 13°
/// and upright — so an aim layered over a jog points 16° to the side and 10° low, over
/// a sprint 18° and 27°, over a crouch 8° and 25° (UAL1_Standard.glb, shoulder to hand,
/// against the stance's own 3° and −1°). This turns <c>spine_01</c> by the difference
/// between the hips it has and the stance's, so the chest, and the arms and the aim off
/// it, sit as the stance has them in the skeleton's space: the legs' stride stays theirs,
/// the aim stays on the line the body is looking along.
///
/// Runs after the AnimationTree, as every <see cref="SkeletonModifier3D"/> does, on
/// the pose it left; the skeleton puts the animated pose back before the next one.
/// </summary>
public partial class TorsoAlign : SkeletonModifier3D
{
	private int _spine = -1;
	private int _hips = -1;
	private Quaternion _stance = Quaternion.Identity;

	/// <summary>How much of the turn is applied [0, 1]: as much as the arms are given to the aim.</summary>
	public float Weight { get; set; }

	/// <summary>
	/// Turns <paramref name="spine"/> so that its parent — the hips — reads as
	/// <paramref name="stanceHips"/>, the hips' rotation in the skeleton's space in the aim stance.
	/// </summary>
	public void Configure(Skeleton3D skeleton, int spine, Quaternion stanceHips)
	{
		_spine = spine;
		_hips = spine >= 0 ? skeleton.GetBoneParent(spine) : -1;
		_stance = stanceHips.Normalized();
	}

	public override void _ProcessModificationWithDelta(double delta)
	{
		if (_spine < 0 || _hips < 0 || Weight <= 0f || GetSkeleton() is not { } skeleton)
		{
			return;
		}

		Quaternion hips = skeleton.GetBoneGlobalPose(_hips).Basis.GetRotationQuaternion();
		Quaternion local = skeleton.GetBonePoseRotation(_spine);
		Quaternion aligned = (hips.Inverse() * _stance * local).Normalized();
		skeleton.SetBonePoseRotation(_spine, Weight >= 1f ? aligned : local.Slerp(aligned, Weight));
	}

	/// <summary>
	/// The rotation in the skeleton's space of <paramref name="bone"/> as
	/// <paramref name="clip"/> poses it at its first frame: each bone from the skeleton
	/// up composed from the clip's track, or from the bone's rest where the clip has none —
	/// as the mixer itself would pose it. <paramref name="skeletonPath"/> is the skeleton's
	/// path in the clip's tracks.
	/// </summary>
	public static Quaternion PoseRotation(Animation clip, string skeletonPath, Skeleton3D skeleton, int bone)
	{
		Quaternion rotation = Quaternion.Identity;
		for (int b = bone; b >= 0; b = skeleton.GetBoneParent(b))
		{
			Quaternion local = skeleton.GetBoneRest(b).Basis.GetRotationQuaternion();
			int track = clip?.FindTrack(new NodePath($"{skeletonPath}:{skeleton.GetBoneName(b)}"),
				Animation.TrackType.Rotation3D) ?? -1;
			if (track >= 0)
			{
				local = clip.RotationTrackInterpolate(track, 0);
			}

			rotation = local * rotation;
		}

		return rotation;
	}
}
