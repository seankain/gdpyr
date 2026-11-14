using System;
using System.Collections.Generic;
using Gdpyr.Sim.Level;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// The map editor's commands (docs/LEVEL_EDITOR.md §8.3, §8.6, LE4): a stroke dabs at a
/// fixed rate, changes the samples under it and no others, ends as whole steps with every
/// pad kept, and is one step to undo; undoing restores every sample; a ramp is one step and
/// a ramp refused changes nothing.
/// </summary>
public class MapEditSessionTests
{
	private static readonly BrushKind[] Strokable = { BrushKind.Sculpt, BrushKind.GradeLocked, BrushKind.Smooth, BrushKind.Level };

	private static BrushSettings RandomBrush(Random random, BrushKind kind) => new BrushSettings
	{
		Kind = kind,
		RadiusMeters = 2f + (float)(random.NextDouble() * 22d),
		Core = (float)(random.NextDouble() * 0.8d),
		RateMetersPerSecond = 0.5f + (float)(random.NextDouble() * 4d),
		GradeDegrees = 10f + (float)(random.NextDouble() * 40d),
		Strength = 0.2f + (float)(random.NextDouble() * 0.8d),
	}.Clamped();

	/// <summary>
	/// A stroke along a path: pressed at its start, held at each point for a few frames of
	/// random length, let go. Returns the points it dabbed at.
	/// </summary>
	private static List<Vector2> Stroke(MapEditSession session, Random random, float x, float z, bool lower, int points,
		out EditOutcome outcome)
	{
		var dabbed = new List<Vector2> { new(x, z) };
		Assert.True(session.BeginStroke(x, z, lower));
		float heading = (float)(random.NextDouble() * Math.PI * 2d);
		for (int i = 0; i < points; i++)
		{
			x += MathF.Cos(heading) * 1.5f;
			z += MathF.Sin(heading) * 1.5f;
			heading += (float)(random.NextDouble() - 0.5d);
			int dabsBefore = session.StrokeDabs;
			session.StrokeTo(x, z, (float)(random.NextDouble() * 0.12d));
			if (session.StrokeDabs > dabsBefore)
			{
				dabbed.Add(new Vector2(x, z));
			}
		}

		outcome = session.EndStroke();
		return dabbed;
	}

	private static bool WithinReach(HeightField field, int x, int z, List<Vector2> dabbed, float radius)
	{
		foreach (Vector2 at in dabbed)
		{
			float dx = field.WorldX(x) - at.X;
			float dz = field.WorldZ(z) - at.Y;
			if ((dx * dx) + (dz * dz) <= (radius * radius) + 1e-3f)
			{
				return true;
			}
		}

		return false;
	}

	[Theory]
	[InlineData(1)]
	[InlineData(2)]
	[InlineData(3)]
	[InlineData(4)]
	public void AStroke_ChangesTheSamplesUnderIt_AndNoOthers_AndKeepsEveryPad(int seed)
	{
		var session = new MapEditSession(LevelMaps.Generate(seed));
		Assert.True(session.Report.IsPlayable);
		var random = new Random(seed * 7919);
		int strokesThatChanged = 0;
		for (int n = 0; n < 24; n++)
		{
			BrushKind kind = Strokable[n % Strokable.Length];
			session.Brush = RandomBrush(random, kind);
			short[] before = session.Document.Terrain.ToSamples();

			// Half the strokes start on an object, so that pads are under them.
			float x, z;
			if (n % 2 == 0)
			{
				MapObject on = session.Document.Objects[random.Next(session.Document.Objects.Count)];
				x = on.Position.X;
				z = on.Position.Z;
			}
			else
			{
				x = (float)(random.NextDouble() * 200d) - 100f;
				z = (float)(random.NextDouble() * 200d) - 100f;
			}

			List<Vector2> dabbed = Stroke(session, random, x, z, random.Next(2) == 0, 6, out EditOutcome outcome);
			HeightField field = session.Document.Terrain;
			short[] after = field.ToSamples();
			SampleRect changed = SampleRect.Empty;
			for (int i = 0; i < after.Length; i++)
			{
				int sx = i % field.Width;
				int sz = i / field.Width;
				Assert.Equal(HeightField.ToMeters(after[i]), field.Heights[i]);
				if (after[i] != before[i])
				{
					changed = changed.With(sx, sz);
					Assert.True(WithinReach(field, sx, sz, dabbed, session.Brush.RadiusMeters),
						$"seed {seed}, stroke {n} ({kind}): sample ({sx}, {sz}) moved, out of the brush's reach");
				}
			}

			Assert.Equal(changed, outcome.Changed);
			Assert.Equal(!changed.IsEmpty, outcome.Step);
			strokesThatChanged += outcome.Step ? 1 : 0;

			MapReport report = MapValidator.Validate(session.Document);
			Assert.False(report.Has(MapIssueCode.OffItsPad), $"seed {seed}, stroke {n}: an object is off its pad");
			Assert.True(report.IsPlayable, $"seed {seed}, stroke {n}: {string.Join("; ", report.Issues)}");
		}

		Assert.True(strokesThatChanged >= 20, $"only {strokesThatChanged} of 24 strokes changed anything");
		Assert.Equal(strokesThatChanged, session.History.UndoCount);
	}

	[Fact]
	public void AStrokeOnAPad_LeavesThePadAsItWas()
	{
		MapDocument document = LevelMaps.Generate(2);
		MapObject placed = document.Objects.Find(o => o.PrefabId == PrefabSpecs.ResourceNode);
		Assert.True(PrefabSpecs.TryGetPadArea(placed, out PadArea area));
		Assert.True(area.IsDisc);
		HeightField field = document.Terrain;
		var pad = new List<int>();
		for (int i = 0; i < field.Heights.Length; i++)
		{
			float dx = field.WorldX(i % field.Width) - area.X;
			float dz = field.WorldZ(i / field.Width) - area.Z;
			if (MathF.Sqrt((dx * dx) + (dz * dz)) <= area.Radius + TerrainConform.PadMarginMeters)
			{
				pad.Add(i);
			}
		}

		float level = PrefabPlacement.PadLevel(placed);
		var session = new MapEditSession(document)
		{
			Brush = TerrainBrush.Presets[1].Settings,
		};
		Assert.True(session.BeginStroke(area.X, area.Z, false));
		session.StrokeTo(area.X, area.Z, 2f);
		Assert.True(field.Sample(area.X, area.Z) > level + 0.1f, "while the stroke is down the ground under the pad rises");
		EditOutcome outcome = session.EndStroke();

		foreach (int i in pad)
		{
			Assert.Equal(level, field.Heights[i]);
		}

		Assert.True(outcome.Step, "the ground round the pad was raised, and stays");
		Assert.False(MapValidator.Validate(document).Has(MapIssueCode.OffItsPad));
	}

	[Fact]
	public void UndoingStrokes_RestoresEverySample_AndRedoingThemRestoresEachAfter()
	{
		var session = new MapEditSession(LevelMaps.Generate(3));
		var random = new Random(11);
		var states = new List<short[]> { session.Document.Terrain.ToSamples() };
		for (int n = 0; n < 12; n++)
		{
			session.Brush = RandomBrush(random, Strokable[n % Strokable.Length]);
			Stroke(session, random, (float)(random.NextDouble() * 160d) - 80f, (float)(random.NextDouble() * 160d) - 80f,
				n % 3 == 0, 5, out EditOutcome outcome);
			if (outcome.Step)
			{
				states.Add(session.Document.Terrain.ToSamples());
			}
		}

		Assert.Equal(states.Count - 1, session.History.UndoCount);
		for (int i = states.Count - 1; i > 0; i--)
		{
			Assert.True(session.Undo(out EditOutcome undone));
			Assert.True(undone.Step);
			Assert.Equal(states[i - 1], session.Document.Terrain.ToSamples());
		}

		Assert.False(session.Undo(out _));
		for (int i = 1; i < states.Count; i++)
		{
			Assert.True(session.Redo(out _));
			Assert.Equal(states[i], session.Document.Terrain.ToSamples());
		}

		Assert.False(session.Redo(out _));
	}

	[Fact]
	public void UndoAndRedo_SayWhichSamplesToDrawAgain()
	{
		var session = new MapEditSession(LevelMaps.Empty());
		session.BeginStroke(-60f, 50f, false);
		session.StrokeTo(-60f, 50f, 0.2f);
		EditOutcome first = session.EndStroke();
		session.BeginStroke(10f, -20f, false);
		session.StrokeTo(10f, -20f, 0.2f);
		EditOutcome made = session.EndStroke();
		Assert.True(first.Step && made.Step);
		Assert.NotEqual(first.Changed, made.Changed);
		Assert.True(session.Undo(out EditOutcome undone));
		Assert.Equal(made.Changed, undone.Area);
		Assert.Equal(made.Changed, undone.Changed);
		Assert.False(undone.ObjectsChanged);
		Assert.True(session.Redo(out EditOutcome redone));
		Assert.Equal(made.Changed, redone.Area);
	}

	[Fact]
	public void AHeldBrush_DabsThirtyTimesASecond_WhateverTheFrames()
	{
		short[] ground = null;
		foreach (int frames in new[] { 1, 7, 60, 144 })
		{
			var session = new MapEditSession(LevelMaps.Empty());
			session.Brush = TerrainBrush.Presets[0].Settings;
			Assert.True(session.BeginStroke(0f, 0f, false));
			Assert.Equal(1, session.StrokeDabs);
			for (int f = 0; f < frames; f++)
			{
				session.StrokeTo(0f, 0f, 1f / frames);
			}

			// The click's dab, and thirty for the second: at most eight a frame for the one frame of a second.
			Assert.Equal(frames == 1 ? 1 + MapEditSession.MaxDabsAtOnce : 31, session.StrokeDabs);
			session.EndStroke();
			if (frames == 7)
			{
				ground = session.Document.Terrain.ToSamples();
			}
			else if (frames > 7)
			{
				Assert.Equal(ground, session.Document.Terrain.ToSamples());
			}
		}
	}

	[Fact]
	public void ASculptDab_IsGivenItsShareOfASecond()
	{
		var session = new MapEditSession(LevelMaps.Empty());
		BrushSettings brush = TerrainBrush.Presets[1].Settings;
		session.Brush = brush;
		session.BeginStroke(0f, 0f, false);
		for (int frame = 0; frame < 30; frame++)
		{
			session.StrokeTo(0f, 0f, 1f / 30f);
		}

		Assert.Equal(31 * brush.RateMetersPerSecond / MapEditSession.DabsPerSecond, session.Document.Terrain[120, 120], 3);
	}

	[Fact]
	public void EndingAStroke_LeavesWholeSteps_AndCancellingOnePutsTheGroundBack()
	{
		var session = new MapEditSession(LevelMaps.Generate(1));
		short[] before = session.Document.Terrain.ToSamples();
		float[] floats = (float[])session.Document.Terrain.Heights.Clone();
		session.Brush = TerrainBrush.Presets[0].Settings;
		session.BeginStroke(-30f, 40f, true);
		session.StrokeTo(-25f, 40f, 0.5f);
		Assert.NotEqual(before, session.Document.Terrain.ToSamples());
		SampleRect redraw = session.CancelStroke();
		Assert.False(redraw.IsEmpty);
		Assert.False(session.Stroking);
		Assert.Equal(floats, session.Document.Terrain.Heights);
		Assert.Equal(0, session.History.UndoCount);
		Assert.False(session.Dirty);

		session.BeginStroke(-30f, 40f, true);
		session.StrokeTo(-25f, 40f, 0.5f);
		session.EndStroke();
		foreach (float h in session.Document.Terrain.Heights)
		{
			Assert.Equal(HeightField.ToMeters(HeightField.ToSample(h)), h);
		}
	}

	[Fact]
	public void AStrokeThatChangesNothing_IsNotAStep()
	{
		var session = new MapEditSession(LevelMaps.Empty());
		session.Brush = TerrainBrush.Presets[8].Settings;
		Assert.Equal(BrushKind.Smooth, session.Brush.Kind);
		session.BeginStroke(0f, 0f, false);
		session.StrokeTo(0f, 0f, 1f);
		EditOutcome outcome = session.EndStroke();
		Assert.False(outcome.Step);
		Assert.True(outcome.Changed.IsEmpty);
		Assert.Equal(0, session.History.UndoCount);
		Assert.False(session.Dirty);
		Assert.Equal(0, session.Changes);
	}

	[Fact]
	public void AStrokeWhoseDabsRoundBack_IsNotAStep_AndIsDrawnAgain()
	{
		// One step up at the middle; a light smooth moves it and its neighbours less than half a step.
		var session = new MapEditSession(LevelMaps.Empty());
		HeightField field = session.Document.Terrain;
		field[120, 120] = MapLimits.HeightStepMeters;
		float[] before = (float[])field.Heights.Clone();
		BrushSettings light = TerrainBrush.Presets[8].Settings;
		light.Strength = 0.05f;
		light.RadiusMeters = 3f;
		session.Brush = light;
		Assert.True(session.BeginStroke(0f, 0f, false));
		Assert.False(session.StrokeTouched.IsEmpty);
		EditOutcome outcome = session.EndStroke();
		Assert.False(outcome.Area.IsEmpty);
		Assert.True(outcome.Changed.IsEmpty);
		Assert.False(outcome.Step);
		Assert.Equal(before, field.Heights);
		Assert.Equal(0, session.History.UndoCount);
		Assert.False(session.Dirty);
	}

	[Fact]
	public void AStroke_IsRefusedForARamp_OrWhileOneIsUnderWay_AndUndoWaitsForIt()
	{
		var session = new MapEditSession(LevelMaps.Empty());
		session.Brush = TerrainBrush.Presets[10].Settings;
		Assert.Equal(BrushKind.Ramp, session.Brush.Kind);
		Assert.False(session.BeginStroke(0f, 0f, false));

		session.Brush = TerrainBrush.Presets[0].Settings;
		Assert.False(session.BeginStroke(float.NaN, 0f, false));
		Assert.True(session.BeginStroke(0f, 0f, false));
		Assert.False(session.BeginStroke(5f, 5f, false));
		session.StrokeTo(0f, 0f, 0.5f);
		session.EndStroke();

		session.BeginStroke(0f, 0f, false);
		Assert.False(session.Undo(out _));
		Assert.False(session.Redo(out _));
		session.EndStroke();
		Assert.True(session.Undo(out _));
	}

	[Fact]
	public void AStroke_KeepsTheBrushItBeganWith()
	{
		var session = new MapEditSession(LevelMaps.Empty());
		session.Brush = TerrainBrush.Presets[0].Settings;
		session.BeginStroke(0f, 0f, false);
		session.Brush = TerrainBrush.Presets[7].Settings;
		session.StrokeTo(0f, 0f, 1f);
		session.EndStroke();

		var plain = new MapEditSession(LevelMaps.Empty()) { Brush = TerrainBrush.Presets[0].Settings };
		plain.BeginStroke(0f, 0f, false);
		plain.StrokeTo(0f, 0f, 1f);
		plain.EndStroke();
		Assert.Equal(plain.Document.Terrain.ToSamples(), session.Document.Terrain.ToSamples());
	}

	[Fact]
	public void TheBrush_IsHeldToItsRanges()
	{
		var session = new MapEditSession(LevelMaps.Empty());
		Assert.Equal(TerrainBrush.Presets[MapEditSession.DefaultPreset].Settings.RadiusMeters, session.Brush.RadiusMeters);
		BrushSettings wild = session.Brush;
		wild.RadiusMeters = 500f;
		wild.RateMetersPerSecond = -3f;
		session.Brush = wild;
		Assert.Equal(BrushSettings.MaxRadiusMeters, session.Brush.RadiusMeters);
		Assert.Equal(BrushSettings.MinRateMetersPerSecond, session.Brush.RateMetersPerSecond);
	}

	[Fact]
	public void ARamp_IsAsWideAsItsRing_AndOneStep_AndARefusedOneChangesNothing()
	{
		var session = new MapEditSession(LevelMaps.Empty());
		BrushSettings ramp = TerrainBrush.Presets[10].Settings;
		ramp.RadiusMeters = 3f;
		session.Brush = ramp;

		// A mound to ramp up onto.
		HeightField field = session.Document.Terrain;
		for (int z = 100; z <= 140; z++)
		{
			for (int x = 140; x <= 180; x++)
			{
				field[x, z] = 4f;
			}
		}

		short[] before = field.ToSamples();
		Assert.Equal(RampResult.Ok, session.Ramp(-10f, 0f, 30f, 0f, out EditOutcome outcome));
		Assert.True(outcome.Step);
		Assert.Equal(1, session.History.UndoCount);

		// Across the strip at its middle: 3 m either side is on the line; the skirt is past it.
		float middle = field.Sample(0f, 0f);
		Assert.Equal(field.Sample(0f, 3f), middle, 2);
		Assert.Equal(field.Sample(0f, -3f), middle, 2);
		Assert.True(middle > 0.5f && middle < 3.5f, $"the ramp's middle is {middle} m");
		foreach (float h in field.Heights)
		{
			Assert.Equal(HeightField.ToMeters(HeightField.ToSample(h)), h);
		}

		Assert.True(session.Undo(out _));
		Assert.Equal(before, field.ToSamples());

		Assert.Equal(RampResult.TooSteep, session.Ramp(16f, 0f, 20f, 0f, out EditOutcome refused));
		Assert.False(refused.Step);
		Assert.Equal(RampResult.TooSmall, session.Ramp(5f, 5f, 5f, 5f, out _));
		Assert.Equal(before, field.ToSamples());
		Assert.Equal(0, session.History.UndoCount);
	}

	[Fact]
	public void TheBands_AreSetOnlyToNumbersAMapCanMean_AndAreNotAStep()
	{
		var session = new MapEditSession(LevelMaps.Empty());
		MapBands bands = session.Document.Bands;
		Assert.False(session.SetBands(bands));
		Assert.False(session.Dirty);

		MapBands outOfOrder = bands;
		outOfOrder.SandTop = (ushort)(bands.MudTop + 1);
		Assert.False(session.SetBands(outOfOrder));
		Assert.Equal(bands, session.Document.Bands);

		MapBands deeper = bands;
		deeper.WaterLevel = (short)(bands.WaterLevel - 256);
		Assert.True(session.SetBands(deeper));
		Assert.Equal(deeper, session.Document.Bands);
		Assert.True(session.Dirty);
		Assert.Equal(0, session.History.UndoCount);
	}

	[Fact]
	public void TheText_IsWhatAFileHolds()
	{
		var session = new MapEditSession(LevelMaps.Empty());
		Assert.True(session.TrySetText("  Ridge  ", "someone", "two\r\nlines", out _));
		Assert.Equal("Ridge", session.Document.Name);
		Assert.Equal("two\nlines", session.Document.Description);
		Assert.True(session.Dirty);

		Assert.False(session.TrySetText(" ", "a", "b", out string empty));
		Assert.Contains("needs a name", empty);
		Assert.False(session.TrySetText(new string('x', MapDocument.MaxNameBytes + 1), "", "", out string longName));
		Assert.Contains("at most 48", longName);
		Assert.False(session.TrySetText("ok", new string('é', 17), "", out string longAuthor));
		Assert.Contains("author", longAuthor);
		Assert.False(session.TrySetText("ok", "tab\there", "", out string control));
		Assert.Contains("control", control);
		Assert.False(session.TrySetText("ok", "", "bell\a", out _));
		Assert.Equal("Ridge", session.Document.Name);
		Assert.True(MapFile.TryWrite(session.Document, out _, out _));
	}

	[Fact]
	public void TheReport_IsAskedAgainOnlyAfterAChange()
	{
		var session = new MapEditSession(LevelMaps.Generate(1));
		MapReport first = session.Report;
		Assert.Same(first, session.Report);
		session.BeginStroke(0f, 0f, false);
		session.StrokeTo(0f, 0f, 0.5f);
		session.EndStroke();
		Assert.NotSame(first, session.Report);
	}

	[Fact]
	public void Saving_MarksTheDocumentClean_UntilTheNextChange()
	{
		var session = new MapEditSession(LevelMaps.Empty());
		session.BeginStroke(0f, 0f, false);
		session.StrokeTo(0f, 0f, 0.5f);
		session.EndStroke();
		Assert.True(session.Dirty);
		Assert.Equal(1, session.Changes);
		session.MarkSaved();
		Assert.False(session.Dirty);
		session.Undo(out _);
		Assert.True(session.Dirty);
		Assert.Equal(2, session.Changes);
	}
}
