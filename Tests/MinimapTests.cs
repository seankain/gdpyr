using System;
using System.IO;
using System.Text.RegularExpressions;
using Gdpyr.Sim.Level;
using Gdpyr.Ui;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// The strategist's map, the half without a window (Scripts/Ui/MinimapView.cs): the square of
/// the field it shows, where a point is on it and what a pixel on it is, the zoom input and the
/// wheel over it, following the camera, the outline of what the camera sees, and the colours of
/// the ground under it.
/// </summary>
public class MinimapTests
{
	private static readonly Vector2 Size = new(200f, 200f);

	private static void Near(Vector2 expected, Vector2 actual, float tolerance = 1e-3f)
	{
		Assert.True(expected.DistanceTo(actual) <= tolerance, $"expected {expected}, got {actual}");
	}

	// ---- the square it shows ----------------------------------------------------------------

	[Fact]
	public void ZoomedOut_TheMapIsTheWholeField_NorthUpAndEastRight()
	{
		var view = new MinimapView();

		Assert.Equal(MapLimits.FieldHalfMeters, view.FieldHalf);
		Assert.Equal(1f, view.Zoom);
		Near(Vector2.Zero, view.ToMap(new Vector2(-view.FieldHalf, -view.FieldHalf), Size));
		Near(Size, view.ToMap(new Vector2(view.FieldHalf, view.FieldHalf), Size));
		Near(Size * 0.5f, view.ToMap(Vector2.Zero, Size));

		// North is world −Z, the top of the map; east is +X, its right.
		Vector2 north = view.ToMap(new Vector2(0f, -50f), Size);
		Vector2 east = view.ToMap(new Vector2(50f, 0f), Size);
		Assert.True(north.Y < Size.Y * 0.5f && Math.Abs(north.X - (Size.X * 0.5f)) < 1e-3f);
		Assert.True(east.X > Size.X * 0.5f && Math.Abs(east.Y - (Size.Y * 0.5f)) < 1e-3f);
	}

	[Theory]
	[InlineData(0)]
	[InlineData(1)]
	[InlineData(2)]
	public void ToWorld_UndoesToMap_AtEveryZoom(int zoomIndex)
	{
		var view = new MinimapView();
		for (int i = 0; i < zoomIndex; i++)
		{
			view.ZoomBy(1, new Vector2(150f, 60f), Size);
		}

		Assert.Equal(zoomIndex, view.ZoomIndex);
		foreach (Vector2 world in new[] { new Vector2(10f, -20f), new Vector2(-37.5f, 81f), view.Center })
		{
			Near(world, view.ToWorld(view.ToMap(world, Size), Size));
		}

		Assert.Equal(Size.X / (2f * view.HalfSpan), view.PixelsPerMeter(Size), 4);
	}

	[Fact]
	public void ClampToField_HoldsAPointToTheField()
	{
		var view = new MinimapView();
		Near(new Vector2(view.FieldHalf, -view.FieldHalf), view.ClampToField(new Vector2(500f, -500f)));
		Near(new Vector2(3f, 4f), view.ClampToField(new Vector2(3f, 4f)));
	}

	// ---- zoom -------------------------------------------------------------------------------

	[Fact]
	public void ZoomingIn_KeepsThePointUnderThePointerWhereItWas()
	{
		var view = new MinimapView();
		var pointer = new Vector2(120f, 90f);
		Vector2 under = view.ToWorld(pointer, Size);

		Assert.True(view.ZoomBy(1, pointer, Size));
		Assert.Equal(2f, view.Zoom);
		Near(under, view.ToWorld(pointer, Size));

		Assert.True(view.ZoomBy(1, pointer, Size));
		Assert.Equal(4f, view.Zoom);
		Near(under, view.ToWorld(pointer, Size));
	}

	[Fact]
	public void ZoomingIn_AtACorner_NeverShowsPastTheField()
	{
		// The anchor is inside the square it was in, so the smaller square round it is too.
		var view = new MinimapView();
		Assert.True(view.ZoomBy(2, Vector2.Zero, Size));
		float limit = view.FieldHalf - view.HalfSpan;
		Near(new Vector2(-limit, -limit), view.Center);
		Near(new Vector2(-view.FieldHalf, -view.FieldHalf), view.ToWorld(Vector2.Zero, Size));
	}

	[Fact]
	public void ZoomingOut_NearTheEdge_HoldsTheSquareInsideTheField()
	{
		var view = new MinimapView();
		view.ZoomBy(2, Vector2.Zero, Size);

		// Out about the far corner: keeping the point under it would show past the field's
		// north-west corner, so the square is held against it instead.
		Assert.True(view.ZoomBy(-1, Size, Size));
		float limit = view.FieldHalf - view.HalfSpan;
		Near(new Vector2(-limit, -limit), view.Center);
		Near(new Vector2(-view.FieldHalf, -view.FieldHalf), view.ToWorld(Vector2.Zero, Size));
	}

	[Fact]
	public void ZoomStopsAtEachEnd_AndOutAllTheWay_IsTheWholeFieldAgain()
	{
		var view = new MinimapView();
		Assert.False(view.ZoomBy(-1, Size * 0.5f, Size));

		view.ZoomBy(5, new Vector2(170f, 30f), Size);
		Assert.Equal(MinimapView.Zooms.Length - 1, view.ZoomIndex);
		Assert.False(view.ZoomBy(1, Size * 0.5f, Size));

		view.ZoomBy(-5, new Vector2(10f, 190f), Size);
		Assert.Equal(0, view.ZoomIndex);
		Near(Vector2.Zero, view.Center);
	}

	[Fact]
	public void TheZoomInput_StepsIn_ThenBackOutToTheWholeField()
	{
		var view = new MinimapView();
		var pointer = new Vector2(80f, 110f);
		Vector2 under = view.ToWorld(pointer, Size);

		view.CycleZoom(pointer, Size);
		Assert.Equal(2f, view.Zoom);
		Near(under, view.ToWorld(pointer, Size));

		view.CycleZoom(pointer, Size);
		Assert.Equal(4f, view.Zoom);
		Near(under, view.ToWorld(pointer, Size));

		view.CycleZoom(pointer, Size);
		Assert.Equal(1f, view.Zoom);
		Near(Vector2.Zero, view.Center);
	}

	// ---- following the camera -----------------------------------------------------------------

	[Fact]
	public void Follow_DoesNothing_WhileThePointIsInView()
	{
		var view = new MinimapView();
		Assert.False(view.Follow(new Vector2(100f, -100f)));

		view.ZoomBy(2, Size * 0.5f, Size);
		Assert.False(view.Follow(new Vector2(20f, -20f)));
		Near(Vector2.Zero, view.Center);
	}

	[Fact]
	public void Follow_BringsAPointThatLeftTheView_BackInsideTheMargin()
	{
		var view = new MinimapView();
		view.ZoomBy(2, Size * 0.5f, Size);
		var camera = new Vector2(60f, 0f);
		Assert.False(view.Shows(camera));

		Assert.True(view.Follow(camera));
		Assert.True(view.Shows(camera));

		// The least move: on the margin's line on X, and nothing on Z.
		float inner = view.HalfSpan * (1f - (2f * MinimapView.FollowMargin));
		Assert.Equal(camera.X - inner, view.Center.X, 3);
		Assert.Equal(0f, view.Center.Y, 3);
	}

	[Fact]
	public void Follow_NeverShowsPastTheField()
	{
		var view = new MinimapView();
		view.ZoomBy(2, Size * 0.5f, Size);
		view.Follow(new Vector2(view.FieldHalf, view.FieldHalf));

		float limit = view.FieldHalf - view.HalfSpan;
		Near(new Vector2(limit, limit), view.Center);
		Assert.True(view.Shows(new Vector2(view.FieldHalf, view.FieldHalf)));
	}

	// ---- what the camera sees -----------------------------------------------------------------

	[Fact]
	public void GroundPoint_StraightDown_IsUnderTheCamera()
	{
		Near(new Vector2(12f, -7f), MinimapView.GroundPoint(new Vector3(12f, 40f, -7f), Vector3.Down, 0f, 240f));
	}

	[Fact]
	public void GroundPoint_Slanted_IsWhereTheRayMeetsTheAnchorsPlane()
	{
		// 45° down towards −Z from 30 m over a plane at y = 2: 30 m out.
		Vector3 direction = new Vector3(0f, -1f, -1f).Normalized();
		Near(new Vector2(5f, -30f), MinimapView.GroundPoint(new Vector3(5f, 32f, 0f), direction, 2f, 240f));
	}

	[Fact]
	public void GroundPoint_AboveTheHorizonOrTooFar_IsTheReachOutAlongTheHeading()
	{
		Near(new Vector2(240f, 0f), MinimapView.GroundPoint(new Vector3(0f, 30f, 0f), new Vector3(1f, 0.2f, 0f).Normalized(), 0f, 240f));

		// Meets the plane 3000 m out: held to the reach.
		Near(new Vector2(0f, -240f), MinimapView.GroundPoint(new Vector3(0f, 30f, 0f), new Vector3(0f, -0.01f, -1f).Normalized(), 0f, 240f));
	}

	// ---- the ground under it ----------------------------------------------------------------

	private static Color Pixel(byte[] bytes, int size, int row, int column)
	{
		int i = ((row * size) + column) * 4;
		return Color.Color8(bytes[i], bytes[i + 1], bytes[i + 2], bytes[i + 3]);
	}

	[Fact]
	public void FlatGround_IsOneColour()
	{
		const int N = 8;
		var heights = new float[N * N];
		byte[] bytes = MinimapTerrain.Shade(heights, N, 1.5f, float.NegativeInfinity);

		Assert.Equal(N * N * 4, bytes.Length);
		Color first = Pixel(bytes, N, 0, 0);
		for (int i = 0; i < N * N; i++)
		{
			Assert.Equal(first, Pixel(bytes, N, i / N, i % N));
		}

		Assert.Equal(1f, first.A);
		Assert.NotEqual(MinimapTerrain.Empty.ToRgba32(), first.ToRgba32());
	}

	[Fact]
	public void NothingHit_IsTheBackground_AndUnderTheWater_IsWater()
	{
		const int N = 4;
		var heights = new float[N * N];
		heights[0] = float.NaN;
		heights[5] = -3f;

		byte[] bytes = MinimapTerrain.Shade(heights, N, 1.5f, waterLevel: -1f);
		Assert.Equal(Color.Color8(13, 15, 20), Pixel(bytes, N, 0, 0));

		Color water = Pixel(bytes, N, 1, 1);
		Assert.True(water.B > water.R && water.B > water.G, $"{water} is not water");
		Assert.False(Pixel(bytes, N, 3, 3).B > Pixel(bytes, N, 3, 3).G, "dry ground is not water");
	}

	[Fact]
	public void ASlopeFacingTheLight_IsBrighterThanOneFacingAway_AndHighGroundLighterThanLow()
	{
		// A ridge along Z down the middle: its west side faces the north-west light.
		const int N = 9;
		var heights = new float[N * N];
		for (int row = 0; row < N; row++)
		{
			for (int column = 0; column < N; column++)
			{
				heights[(row * N) + column] = 4f - Math.Abs(column - 4);
			}
		}

		byte[] bytes = MinimapTerrain.Shade(heights, N, 1f, float.NegativeInfinity);
		Color west = Pixel(bytes, N, 4, 2);
		Color east = Pixel(bytes, N, 4, 6);
		Assert.True(west.Luminance > east.Luminance, $"west {west} is not brighter than east {east}");

		// A plateau over a plain: the top is lighter than the bottom, both flat.
		var plateau = new float[N * N];
		for (int i = 0; i < N * N; i++)
		{
			plateau[i] = i / N < 4 ? 20f : 0f;
		}

		byte[] stepped = MinimapTerrain.Shade(plateau, N, 1f, float.NegativeInfinity);
		Assert.True(Pixel(stepped, N, 0, 4).Luminance > Pixel(stepped, N, 8, 4).Luminance);
	}

	[Fact]
	public void Shade_RefusesHeightsOfTheWrongSize()
	{
		Assert.Throws<ArgumentException>(() => MinimapTerrain.Shade(new float[10], 4, 1f, 0f));
	}

	// ---- the input --------------------------------------------------------------------------

	[Fact]
	public void TheMapZoomInput_IsTheMiddleButton_AndTheKeyListSaysSo()
	{
		string project = File.ReadAllText(Repo("project.godot"));
		System.Text.RegularExpressions.Match action = Regex.Match(project, @"^rts_map_zoom=\{[^}]*?InputEventMouseButton[^}]*?""button_index"":(\d+)", RegexOptions.Multiline);
		Assert.True(action.Success, "rts_map_zoom is not on a mouse button in the input map");
		Assert.Equal(3, int.Parse(action.Groups[1].Value));

		Assert.Contains("MMB or wheel over it zoom", StrategistCards.KeyList);
	}

	private static string Repo(string relative)
	{
		for (var at = new DirectoryInfo(AppContext.BaseDirectory); at != null; at = at.Parent)
		{
			string candidate = Path.Combine(at.FullName, relative);
			if (File.Exists(candidate) && File.Exists(Path.Combine(at.FullName, "project.godot")))
			{
				return candidate;
			}
		}

		throw new InvalidOperationException($"no {relative} above {AppContext.BaseDirectory}");
	}
}
