using System;
using Gdpyr.Sim.Level;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// Undo and redo (docs/LEVEL_EDITOR.md §8.6): an edit keeps the rectangle it touched,
/// before and after, and the objects it changed at their indices; reverting it is its
/// inverse exactly; two hundred steps are kept.
/// </summary>
public class EditHistoryTests
{
	private static MapDocument Hills(int seed)
	{
		MapDocument document = LevelMaps.Empty();
		HeightField ground = LevelGround.Hills(seed);
		ground.Round(ground.Bounds);
		Array.Copy(ground.Heights, document.Terrain.Heights, ground.Heights.Length);
		return document;
	}

	/// <summary>A stroke as the editor will make one: dabs on the document, rounded at the end, kept as one step.</summary>
	private static MapEdit Stroke(MapDocument document, Random random)
	{
		HeightField before = document.Terrain.Clone();
		var brush = new BrushSettings
		{
			Kind = random.Next(2) == 0 ? BrushKind.Sculpt : BrushKind.GradeLocked,
			RadiusMeters = 4f + (float)(random.NextDouble() * 20d),
			Core = 0.3f,
			RateMetersPerSecond = 2f,
			GradeDegrees = 25f,
			Strength = 0.5f,
		};
		float x = (float)(random.NextDouble() * 160d) - 80f;
		float z = (float)(random.NextDouble() * 160d) - 80f;
		BrushStroke stroke = TerrainBrush.Begin(document.Terrain, brush, x, z, random.Next(2) == 0);
		SampleRect touched = SampleRect.Empty;
		for (int dab = 0; dab < 10; dab++)
		{
			touched = touched.Union(TerrainBrush.Dab(document.Terrain, stroke, x + dab, z, 1f / 30f));
		}

		document.Terrain.Round(touched);
		return MapEdit.Terrain(before, document.Terrain, touched);
	}

	[Fact]
	public void AStrokeOverA64mSquare_Is17KB()
	{
		var before = new HeightField();
		HeightField after = before.Clone();
		MapEdit edit = MapEdit.Terrain(before, after, new SampleRect(10, 10, 74, 74));
		Assert.Equal(16_900, edit.TerrainBytes);
		Assert.Equal(65 * 65, edit.Rect.Count);
	}

	[Fact]
	public void UndoingStrokes_RestoresEverySample_AndRedoingThemRestoresEachAfter()
	{
		var random = new Random(2);
		MapDocument document = Hills(2);
		var history = new EditHistory();
		var states = new System.Collections.Generic.List<short[]> { document.Terrain.ToSamples() };
		for (int i = 0; i < 12; i++)
		{
			history.Push(Stroke(document, random));
			states.Add(document.Terrain.ToSamples());
		}

		Assert.Equal(12, history.UndoCount);
		for (int i = 11; i >= 0; i--)
		{
			Assert.True(history.Undo(document));
			Assert.Equal(states[i], document.Terrain.ToSamples());
		}

		Assert.False(history.Undo(document));
		for (int i = 1; i <= 12; i++)
		{
			Assert.True(history.Redo(document));
			Assert.Equal(states[i], document.Terrain.ToSamples());
		}

		Assert.False(history.Redo(document));
	}

	[Fact]
	public void TwoHundredSteps_AreKept_AndTheOldestGoes()
	{
		MapDocument document = LevelMaps.Empty();
		var history = new EditHistory();
		for (int i = 0; i < 250; i++)
		{
			history.Do(document, new MapEdit().Insert(document.Objects.Count, new MapObject(PrefabSpecs.AmmoCan, (short)i, 0, 0, 0)));
		}

		Assert.Equal(EditHistory.MaxSteps, history.UndoCount);
		while (history.Undo(document))
		{
		}

		// The first fifty are past undoing.
		Assert.Equal(50, document.Objects.Count);
		Assert.Equal(49, document.Objects[^1].X);
		Assert.Equal(200, history.RedoCount);
	}

	[Fact]
	public void ANewStep_DropsWhatCouldHaveBeenRedone()
	{
		MapDocument document = LevelMaps.Empty();
		var history = new EditHistory();
		history.Do(document, new MapEdit().Insert(0, new MapObject(PrefabSpecs.AmmoCan, 1, 0, 0, 0)));
		history.Do(document, new MapEdit().Insert(1, new MapObject(PrefabSpecs.AmmoCan, 2, 0, 0, 0)));
		Assert.True(history.Undo(document));
		Assert.True(history.CanRedo);

		history.Do(document, new MapEdit().Insert(1, new MapObject(PrefabSpecs.AmmoCan, 3, 0, 0, 0)));
		Assert.False(history.CanRedo);
		Assert.False(history.Redo(document));
		Assert.Equal(new short[] { 1, 3 }, new[] { document.Objects[0].X, document.Objects[1].X });

		// An edit that changes nothing is not a step.
		history.Push(new MapEdit());
		Assert.Equal(2, history.UndoCount);
		history.Clear();
		Assert.False(history.CanUndo);
	}

	[Fact]
	public void AnObjectTakenOutOrReplaced_ComesBackAtItsIndex()
	{
		MapDocument document = LevelMaps.Empty();
		for (short i = 0; i < 5; i++)
		{
			document.Objects.Add(new MapObject(PrefabSpecs.CoverBlock, i, 0, 0, 0));
		}

		MapDocument start = document.Clone();
		var history = new EditHistory();
		history.Do(document, new MapEdit().Remove(2, document.Objects[2]));
		history.Do(document, new MapEdit().Replace(0, document.Objects[0], new MapObject(PrefabSpecs.CoverLowWall, 9, 9, 9, 9)));
		history.Do(document, new MapEdit().Remove(3, document.Objects[3]).Insert(0, new MapObject(PrefabSpecs.Bunker, 7, 0, 0, 0)));
		Assert.Equal(new short[] { 7, 9, 1, 3 }, document.Objects.ConvertAll(o => o.X));

		while (history.Undo(document))
		{
		}

		Assert.True(start.SameAs(document, out string difference), difference);
	}

	[Fact]
	public void AnEdit_ThatPlacesAndLevels_IsOneStep()
	{
		MapDocument document = Hills(5);
		MapDocument start = document.Clone();
		PlacementCheck check = PrefabPlacement.Check(document, new PlacementRequest(PrefabSpecs.CoverBlock, 10f, 10f, 0.3f));
		if (!check.IsOk)
		{
			check = PrefabPlacement.Check(document, new PlacementRequest(PrefabSpecs.CoverLowWall, 30f, -20f, 0.3f));
		}

		Assert.True(check.IsOk, check.ToString());
		Assert.False(check.Edit.Rect.IsEmpty);
		Assert.Single(check.Edit.Objects);

		var history = new EditHistory();
		history.Do(document, check.Edit);
		Assert.Equal(1, history.UndoCount);
		history.Undo(document);
		Assert.True(start.SameAs(document, out string difference), difference);
	}

	[Fact]
	public void TwoFieldsOfDifferentSizes_AreNotOneEdit()
	{
		Assert.Throws<ArgumentException>(() => MapEdit.Terrain(new HeightField(), new HeightField(9, 9), new SampleRect(0, 0, 3, 3)));
	}
}
