using System;
using System.Collections.Generic;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Characters;

/// <summary>
/// The AnimationTree graph every humanoid body is posed by (<see cref="HumanoidRig"/>),
/// in the layers <see cref="BodyMix"/> describes:
/// <code>
///  Legs ──────────────────────────────── in ┐
///   Transition over the locomotion clips,   Arms ── in ┐
///   each through its own TimeScale          Blend2,    Throw ── in ┐
///                                           chest-up   OneShot,    Death ── output
///  AimPitch ── in ┐                         filter     chest-up    Blend2
///   Blend3:       Recoil ──────────── blend ┘          filter      │
///   down·level·up OneShot, add                         │           blend
///                 │                                    shot        DeathSeek ← Death
///                 shot: RecoilDelta = Shoot − Aim      ThrowSpeed ← Throw
/// </code>
/// The legs are the whole body underneath. The arms layer takes the chest, arms and
/// head — <c>spine_01</c> and everything off it — and is the aim, blended between low,
/// level and high by the pitch, with each round's recoil added on top as its difference
/// from the level aim, so a shot fired looking up stays looking up. A throw takes the
/// same bones over the aim; a death takes the lot.
///
/// One graph for every body: its nodes hold no state of their own (an AnimationTree
/// keeps its parameters), so it is built once, from the first body's skeleton, and
/// shared. Built in code rather than authored in the editor so the clip names come from
/// the rig's table and the filter from the skeleton, rather than being typed twice.
/// </summary>
public static class HumanoidBlendTree
{
	/// <summary>How long the legs cross-fade from one clip to the next [s].</summary>
	public const float LegsBlendSeconds = 0.15f;

	public const float RecoilFadeInSeconds = 0.03f;
	public const float RecoilFadeOutSeconds = 0.15f;
	public const float ThrowFadeInSeconds = 0.1f;
	public const float ThrowFadeOutSeconds = 0.25f;

	/// <summary>The legs' clips, in <see cref="BodyClip"/> order: the inputs of <c>Legs</c>, by name.</summary>
	public static readonly BodyClip[] LegsClips =
	{
		BodyClip.Idle, BodyClip.Walk, BodyClip.Run, BodyClip.Sprint, BodyClip.CrouchIdle, BodyClip.CrouchWalk,
		BodyClip.JumpStart, BodyClip.Airborne, BodyClip.Land,
	};

	public static readonly StringName LegsRequest = "parameters/Legs/transition_request";
	public static readonly StringName LegsState = "parameters/Legs/current_state";
	public static readonly StringName ArmsAmount = "parameters/Arms/blend_amount";
	public static readonly StringName AimPitchAmount = "parameters/AimPitch/blend_amount";
	public static readonly StringName RecoilRequest = "parameters/Recoil/request";
	public static readonly StringName RecoilActive = "parameters/Recoil/active";
	public static readonly StringName RecoilDeltaAmount = "parameters/RecoilDelta/sub_amount";
	public static readonly StringName ThrowRequest = "parameters/Throw/request";
	public static readonly StringName ThrowActive = "parameters/Throw/active";
	public static readonly StringName DeathAmount = "parameters/Death/blend_amount";
	public static readonly StringName DeathSeekRequest = "parameters/DeathSeek/seek_request";

	private static readonly StringName[] SpeedParameters = BuildSpeedParameters();

	/// <summary>The <c>scale</c> of the TimeScale a clip plays through: its rate, and a stride's match to the ground.</summary>
	public static StringName Speed(BodyClip clip) => SpeedParameters[(int)clip];

	/// <summary>The input of <c>Legs</c> that plays <paramref name="clip"/>.</summary>
	public static string LegsInput(BodyClip clip) => clip.ToString();

	/// <summary>
	/// The graph, playing each <see cref="BodyClip"/> as the animation <paramref name="clipName"/>
	/// names, with the arms and the throw layered over <paramref name="upperBody"/>: the
	/// track paths of the chest, arms and head, relative to the tree's root node.
	/// </summary>
	public static AnimationNodeBlendTree Build(Func<BodyClip, StringName> clipName, IReadOnlyCollection<NodePath> upperBody)
	{
		var tree = new AnimationNodeBlendTree();

		// The legs: one input per clip, each through a TimeScale the rig sets every frame.
		var legs = new AnimationNodeTransition { XfadeTime = LegsBlendSeconds, AllowTransitionToSelf = true };
		for (int i = 0; i < LegsClips.Length; i++)
		{
			legs.AddInput(LegsInput(LegsClips[i]));
		}

		tree.AddNode("Legs", legs, new Vector2(400, 0));
		for (int i = 0; i < LegsClips.Length; i++)
		{
			BodyClip clip = LegsClips[i];
			AddTimedClip(tree, clip, clipName(clip), new Vector2(0, (i * 120) - 480));
			tree.ConnectNode("Legs", i, $"{clip}Speed");
		}

		// The aim, between low, level and high.
		var aimPitch = new AnimationNodeBlend3();
		tree.AddNode("AimPitch", aimPitch, new Vector2(400, 600));
		AddClip(tree, BodyClip.AimDown, clipName(BodyClip.AimDown), new Vector2(0, 480));
		AddClip(tree, BodyClip.Aim, clipName(BodyClip.Aim), new Vector2(0, 600));
		AddClip(tree, BodyClip.AimUp, clipName(BodyClip.AimUp), new Vector2(0, 720));
		tree.ConnectNode("AimPitch", 0, $"{BodyClip.AimDown}Clip");
		tree.ConnectNode("AimPitch", 1, $"{BodyClip.Aim}Clip");
		tree.ConnectNode("AimPitch", 2, $"{BodyClip.AimUp}Clip");

		// The recoil, as what it changes from the level aim, added to the aim wherever it points.
		var delta = new AnimationNodeSub2();
		tree.AddNode("RecoilDelta", delta, new Vector2(400, 900));
		AddClip(tree, BodyClip.Shoot, clipName(BodyClip.Shoot), new Vector2(0, 860));
		tree.AddNode("RecoilRest", new AnimationNodeAnimation { Animation = clipName(BodyClip.Aim) }, new Vector2(0, 980));
		tree.ConnectNode("RecoilDelta", 0, $"{BodyClip.Shoot}Clip");
		tree.ConnectNode("RecoilDelta", 1, "RecoilRest");

		var recoil = new AnimationNodeOneShot
		{
			MixMode = AnimationNodeOneShot.MixModeEnum.Add,
			FadeInTime = RecoilFadeInSeconds,
			FadeOutTime = RecoilFadeOutSeconds,
		};
		tree.AddNode("Recoil", recoil, new Vector2(700, 700));
		tree.ConnectNode("Recoil", 0, "AimPitch");
		tree.ConnectNode("Recoil", 1, "RecoilDelta");

		// The arms over the legs.
		var arms = new AnimationNodeBlend2();
		Filter(arms, upperBody);
		tree.AddNode("Arms", arms, new Vector2(1000, 200));
		tree.ConnectNode("Arms", 0, "Legs");
		tree.ConnectNode("Arms", 1, "Recoil");

		// The throw over the arms.
		var @throw = new AnimationNodeOneShot { FadeInTime = ThrowFadeInSeconds, FadeOutTime = ThrowFadeOutSeconds };
		Filter(@throw, upperBody);
		tree.AddNode("Throw", @throw, new Vector2(1300, 200));
		AddTimedClip(tree, BodyClip.Throw, clipName(BodyClip.Throw), new Vector2(1000, 500));
		tree.ConnectNode("Throw", 0, "Arms");
		tree.ConnectNode("Throw", 1, $"{BodyClip.Throw}Speed");

		// The death over everything, from its first frame each time.
		var death = new AnimationNodeBlend2();
		tree.AddNode("Death", death, new Vector2(1600, 200));
		AddClip(tree, BodyClip.Death, clipName(BodyClip.Death), new Vector2(1000, 700));
		tree.AddNode("DeathSeek", new AnimationNodeTimeSeek(), new Vector2(1300, 700));
		tree.ConnectNode("DeathSeek", 0, $"{BodyClip.Death}Clip");
		tree.ConnectNode("Death", 0, "Throw");
		tree.ConnectNode("Death", 1, "DeathSeek");

		tree.ConnectNode("output", 0, "Death");
		tree.SetNodePosition("output", new Vector2(1900, 200));
		return tree;
	}

	private static void AddClip(AnimationNodeBlendTree tree, BodyClip clip, StringName animation, Vector2 position)
	{
		tree.AddNode($"{clip}Clip", new AnimationNodeAnimation { Animation = animation }, position);
	}

	/// <summary>A clip and the TimeScale (<see cref="Speed"/>) it plays through.</summary>
	private static void AddTimedClip(AnimationNodeBlendTree tree, BodyClip clip, StringName animation, Vector2 position)
	{
		AddClip(tree, clip, animation, position);
		tree.AddNode($"{clip}Speed", new AnimationNodeTimeScale(), position + new Vector2(200, 0));
		tree.ConnectNode($"{clip}Speed", 0, $"{clip}Clip");
	}

	private static void Filter(AnimationNode node, IReadOnlyCollection<NodePath> paths)
	{
		node.FilterEnabled = true;
		foreach (NodePath path in paths)
		{
			node.SetFilterPath(path, true);
		}
	}

	private static StringName[] BuildSpeedParameters()
	{
		var names = new StringName[Enum.GetValues<BodyClip>().Length];
		for (int i = 0; i < names.Length; i++)
		{
			names[i] = $"parameters/{(BodyClip)i}Speed/scale";
		}

		return names;
	}
}
