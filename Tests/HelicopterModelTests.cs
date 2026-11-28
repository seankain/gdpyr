using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Gdpyr.Sim;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// The helicopter's model, <c>Models/Vehicles/helicopter/helicopter.glb</c>, and its definition
/// agreeing (docs/GROUND_VEHICLES.md §17): read as bytes — a <c>.glb</c> is a JSON chunk and a
/// binary one, and the JSON says every node, where it is and how many triangles each mesh
/// draws — without the engine. The engine's own check of it as drawn is
/// <c>scripts/helicopter-model-checks.sh</c>.
/// </summary>
public class HelicopterModelTests
{
	private const string ModelPath = "Models/Vehicles/helicopter/helicopter.glb";

	/// <summary>
	/// The most triangles the model may draw: the budget of a vehicle in an online shooter,
	/// which a server's worth of players see at once. The 4x4 draws 18,498.
	/// </summary>
	private const int TriangleBudget = 12_000;

	private static readonly Lazy<JsonDocument> Gltf = new(() => Read(TrainedUnitResourceTests.Repo(ModelPath)));

	/// <summary>A <c>.glb</c>'s JSON chunk: after the 12-byte header, its length, its type and itself.</summary>
	private static JsonDocument Read(string path)
	{
		byte[] bytes = File.ReadAllBytes(path);
		Assert.Equal("glTF", Encoding.ASCII.GetString(bytes, 0, 4));
		int length = BitConverter.ToInt32(bytes, 12);
		Assert.Equal("JSON", Encoding.ASCII.GetString(bytes, 16, 4));
		return JsonDocument.Parse(new ReadOnlyMemory<byte>(bytes, 20, length));
	}

	private static JsonElement Nodes => Gltf.Value.RootElement.GetProperty("nodes");

	private static JsonElement Node(string name)
	{
		foreach (JsonElement node in Nodes.EnumerateArray())
		{
			if (node.TryGetProperty("name", out JsonElement n) && n.GetString() == name)
			{
				return node;
			}
		}

		throw new Xunit.Sdk.XunitException($"{ModelPath} has no node '{name}'");
	}

	private static string NameOf(int index) => Nodes[index].GetProperty("name").GetString();

	/// <summary>A node's translation, in glTF's axes — Godot's: +Y up, forward −Z.</summary>
	private static Vector3 Translation(JsonElement node)
	{
		if (!node.TryGetProperty("translation", out JsonElement t))
		{
			return Vector3.Zero;
		}

		return new Vector3(t[0].GetSingle(), t[1].GetSingle(), t[2].GetSingle());
	}

	private static List<string> Children(JsonElement node)
	{
		var names = new List<string>();
		if (node.TryGetProperty("children", out JsonElement children))
		{
			foreach (JsonElement child in children.EnumerateArray())
			{
				names.Add(NameOf(child.GetInt32()));
			}
		}

		return names;
	}

	private static bool Unturned(JsonElement node)
	{
		if (node.TryGetProperty("scale", out JsonElement s) && (s[0].GetSingle() != 1f || s[1].GetSingle() != 1f || s[2].GetSingle() != 1f))
		{
			return false;
		}

		return !node.TryGetProperty("rotation", out JsonElement r)
			|| (MathF.Abs(r[0].GetSingle()) < 1e-6f && MathF.Abs(r[1].GetSingle()) < 1e-6f && MathF.Abs(r[2].GetSingle()) < 1e-6f);
	}

	[Fact]
	public void TheDefinition_DrawsTheHelicopterWithItsModel()
	{
		string text = File.ReadAllText(TrainedUnitResourceTests.Repo(CrewVehicleFiles.HelicopterPath));
		System.Text.RegularExpressions.Match ext = Regex.Match(text, $"\\[ext_resource type=\"PackedScene\"[^\\]]* path=\"res://{Regex.Escape(ModelPath)}\" id=\"([^\"]+)\"\\]");
		Assert.True(ext.Success, $"{CrewVehicleFiles.HelicopterPath} loads no {ModelPath}");
		Assert.Equal($"ExtResource(\"{ext.Groups[1].Value}\")", CrewVehicleFiles.Resource(CrewVehicleFiles.HelicopterPath)["Model"]);
	}

	[Fact]
	public void TheModel_IsABodyAndTwoRotors_AtTheTop_TheRotorsUnturned()
	{
		var top = new List<string>();
		foreach (JsonElement index in Gltf.Value.RootElement.GetProperty("scenes")[0].GetProperty("nodes").EnumerateArray())
		{
			top.Add(NameOf(index.GetInt32()));
		}

		top.Sort(StringComparer.Ordinal);
		Assert.Equal(new[] { "Body", "Rotor", "TailRotor" }, top);
		Assert.Equal(Vector3.Zero, Translation(Node("Body")));
		Assert.True(Unturned(Node("Body")), "the body is turned or scaled");

		// CrewVehicle sets each rotor's turn whole each frame: one exported turned would lose it.
		Assert.True(Unturned(Node("Rotor")), "the main rotor is exported turned or scaled");
		Assert.True(Unturned(Node("TailRotor")), "the tail rotor is exported turned or scaled");
	}

	[Fact]
	public void EachSeatOfTheDefinition_RestsOnTheCushionTheModelDrawsForIt()
	{
		List<string> marked = Children(Node("Body"));
		marked.Sort(StringComparer.Ordinal);
		Assert.Equal(new[] { "LeftSeat", "PilotSeat", "RightSeat" }, marked);

		foreach (CrewSeat seat in CrewVehicleFiles.Seats(CrewVehicleFiles.HelicopterPath))
		{
			string name = seat.Role == SeatRole.Driver ? "PilotSeat" : seat.Capsule.Center.X < 0f ? "LeftSeat" : "RightSeat";
			Vector3 cushion = Translation(Node(name));
			Vector3 foot = seat.Capsule.Bottom + (Vector3.Down * seat.Capsule.Radius);
			Assert.True(cushion.DistanceTo(foot) < 0.01f, $"{name} at {cushion}; the {seat.Role}'s capsule rests at {foot}");
		}
	}

	[Fact]
	public void TheMainRotor_TurnsOverTheOriginAboveTheCabin_AndTheFanInTheFenestronsBox()
	{
		HullBox[] hull = CrewVehicleFiles.Hull(CrewVehicleFiles.HelicopterPath);
		Vector3 hub = Translation(Node("Rotor"));
		Vector3 fan = Translation(Node("TailRotor"));

		// The helicopter tilts about its origin: a hub over it stays over the middle of its lift.
		Assert.Equal(0f, hub.X, 4);
		Assert.Equal(0f, hub.Z, 4);
		int under = 0;
		foreach (HullBox box in hull)
		{
			if (MathF.Abs(hub.X - box.Center.X) <= box.HalfExtents.X && MathF.Abs(hub.Z - box.Center.Z) <= box.HalfExtents.Z)
			{
				under++;
				Assert.True(hub.Y > box.Center.Y + box.HalfExtents.Y, $"the hub at {hub.Y} m is below the top of the box at {box.Center}");
			}
		}

		Assert.Equal(1, under);

		HullBox fenestron = hull[^1];
		Vector3 d = (fan - fenestron.Center).Abs();
		Assert.True(d.X <= fenestron.HalfExtents.X && d.Y <= fenestron.HalfExtents.Y && d.Z <= fenestron.HalfExtents.Z,
			$"the fan at {fan} is outside the fenestron's box");
	}

	[Fact]
	public void TheModel_DrawsNoMoreThanAShootersVehicleBudget()
	{
		JsonElement root = Gltf.Value.RootElement;
		JsonElement accessors = root.GetProperty("accessors");
		int triangles = 0;
		foreach (JsonElement mesh in root.GetProperty("meshes").EnumerateArray())
		{
			foreach (JsonElement primitive in mesh.GetProperty("primitives").EnumerateArray())
			{
				// Mode 4, triangles, is glTF's default.
				Assert.Equal(4, primitive.TryGetProperty("mode", out JsonElement mode) ? mode.GetInt32() : 4);
				triangles += accessors[primitive.GetProperty("indices").GetInt32()].GetProperty("count").GetInt32() / 3;
			}
		}

		// One node a mesh: nothing is drawn twice.
		int meshNodes = 0;
		foreach (JsonElement node in Nodes.EnumerateArray())
		{
			meshNodes += node.TryGetProperty("mesh", out _) ? 1 : 0;
		}

		Assert.Equal(root.GetProperty("meshes").GetArrayLength(), meshNodes);
		Assert.InRange(triangles, 1, TriangleBudget);
	}
}
