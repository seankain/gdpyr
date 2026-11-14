using System;
using System.IO;
using System.Linq;
using Gdpyr.Sim.Level;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// One's own maps on disk (docs/LEVEL_EDITOR.md §8.6, §9.2, LE4): a save is the next
/// revision, written beside the map and renamed over it with the last kept as <c>.bak</c>;
/// an autosave is offered back until it is saved or is the map as saved; the list holds
/// every map that reads, finished or not; a fork is a new lineage with its parent named.
/// </summary>
public sealed class MapStoreTests : IDisposable
{
	private readonly string _dir = Path.Combine(Path.GetTempPath(), "gdpyr-mapstore-" + Guid.NewGuid().ToString("N"));

	public void Dispose()
	{
		if (Directory.Exists(_dir))
		{
			Directory.Delete(_dir, true);
		}
	}

	[Fact]
	public void SaveThenOpen_IsTheSameDocument_AtTheNextRevision()
	{
		MapDocument document = LevelMaps.Generate(1);
		uint revision = document.Revision;
		Assert.True(MapStore.TrySave(document, _dir, "ridge", 1_800_000_000, out byte[] file, out string error), error);
		Assert.Equal(revision + 1, document.Revision);
		Assert.Equal(1_800_000_000, document.ModifiedUnixSeconds);
		Assert.Equal(file, File.ReadAllBytes(Path.Combine(_dir, "ridge.map")));

		Assert.True(MapStore.TryOpen(_dir, "ridge", out MapDocument read, out byte[] bytes, out error), error);
		Assert.True(document.SameAs(read, out string difference), difference);
		Assert.Equal(file, bytes);
		Assert.Equal(file, MapFile.Write(read));
	}

	[Fact]
	public void Saving_KeepsTheLastFileAsBak_AndLeavesNoTmp()
	{
		MapDocument document = LevelMaps.Generate(2);
		Assert.True(MapStore.TrySave(document, _dir, "ridge", 1, out byte[] first, out _));
		Assert.False(File.Exists(MapStore.BackupPathOf(_dir, "ridge")));

		document.Terrain[3, 3] += 1f;
		Assert.True(MapStore.TrySave(document, _dir, "ridge", 2, out byte[] second, out _));
		Assert.Equal(first, File.ReadAllBytes(MapStore.BackupPathOf(_dir, "ridge")));
		Assert.Equal(second, File.ReadAllBytes(MapStore.PathOf(_dir, "ridge")));
		Assert.False(File.Exists(MapStore.TempPathOf(_dir, "ridge")));
		Assert.Equal(new[] { "ridge.map", "ridge.map.bak" }, Directory.GetFiles(_dir).Select(Path.GetFileName).OrderBy(n => n));
	}

	[Fact]
	public void AnUnfinishedMap_IsSaved_AndOpened()
	{
		MapDocument blank = LevelMaps.Empty();
		Assert.False(MapValidator.Validate(blank).IsPlayable);
		Assert.True(MapStore.TrySave(blank, _dir, "draft", 5, out _, out string error), error);
		Assert.True(MapStore.TryOpen(_dir, "draft", out MapDocument read, out _, out error), error);
		Assert.True(blank.SameAs(read, out _));
	}

	[Fact]
	public void ASaveThatCannotBeWritten_LeavesTheDocumentAndTheFileAsTheyWere()
	{
		MapDocument document = LevelMaps.Generate(1);
		Assert.True(MapStore.TrySave(document, _dir, "ridge", 10, out byte[] saved, out _));
		uint revision = document.Revision;

		document.Name = new string('x', MapDocument.MaxNameBytes + 1);
		Assert.False(MapStore.TrySave(document, _dir, "ridge", 20, out byte[] none, out string error));
		Assert.Null(none);
		Assert.Contains("name", error);
		Assert.Equal(revision, document.Revision);
		Assert.Equal(10, document.ModifiedUnixSeconds);
		Assert.Equal(saved, File.ReadAllBytes(MapStore.PathOf(_dir, "ridge")));

		Assert.False(MapStore.TrySave(LevelMaps.Empty(), _dir, "Not A Name", 1, out _, out error));
		Assert.Contains("not allowed", error);
		Assert.False(MapStore.TrySave(LevelMaps.Empty(), _dir, "../escape", 1, out _, out _));
		Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_dir)!, "escape.map")));
	}

	[Fact]
	public void AnAutosave_IsOfferedBack_UntilItIsSaved()
	{
		MapDocument document = LevelMaps.Generate(3);
		Assert.True(MapStore.TrySave(document, _dir, "ridge", 1, out _, out _));
		Assert.False(MapStore.TryReadAutosave(_dir, "ridge", out _, out _));

		document.Terrain[50, 50] += 2f;
		uint revision = document.Revision;
		Assert.True(MapStore.TryAutosave(document, _dir, "ridge", out string error), error);
		Assert.Equal(revision, document.Revision);
		Assert.True(File.Exists(MapStore.AutosavePathOf(_dir, "ridge")));
		Assert.True(MapStore.TryReadAutosave(_dir, "ridge", out MapDocument offered, out error), error);
		Assert.True(document.SameAs(offered, out string difference), difference);
		Assert.True(MapStore.List(_dir).Single().HasAutosave);

		Assert.True(MapStore.TrySave(document, _dir, "ridge", 2, out _, out _));
		Assert.False(File.Exists(MapStore.AutosavePathOf(_dir, "ridge")));
		Assert.False(MapStore.TryReadAutosave(_dir, "ridge", out _, out _));
	}

	[Fact]
	public void AnAutosaveThatIsTheMapAsSaved_IsDroppedRatherThanOffered()
	{
		MapDocument document = LevelMaps.Generate(4);
		Assert.True(MapStore.TrySave(document, _dir, "ridge", 1, out _, out _));
		Assert.True(MapStore.TryAutosave(document, _dir, "ridge", out _));
		Assert.False(MapStore.TryReadAutosave(_dir, "ridge", out MapDocument offered, out string why));
		Assert.Null(offered);
		Assert.Contains("as saved", why);
		Assert.False(File.Exists(MapStore.AutosavePathOf(_dir, "ridge")));
	}

	[Fact]
	public void AnAutosaveOfAMapNeverSaved_IsOffered()
	{
		MapDocument document = LevelMaps.Empty();
		Assert.True(MapStore.TryAutosave(document, _dir, "fresh", out _));
		Assert.True(MapStore.TryReadAutosave(_dir, "fresh", out MapDocument offered, out _));
		Assert.True(document.SameAs(offered, out _));
		MapStore.DiscardAutosave(_dir, "fresh");
		Assert.False(File.Exists(MapStore.AutosavePathOf(_dir, "fresh")));
	}

	[Fact]
	public void TheList_HoldsEveryMapThatReads_AndOnlyLibraryNames()
	{
		Assert.Empty(MapStore.List(_dir));
		Assert.True(MapStore.TrySave(LevelMaps.Generate(1), _dir, "b-ridge", 1, out _, out _));
		Assert.True(MapStore.TrySave(LevelMaps.Empty(), _dir, "a_draft", 1, out _, out _));
		Assert.True(MapStore.TrySave(LevelMaps.Generate(1), _dir, "b-ridge", 2, out byte[] ridge, out _));
		Assert.True(MapStore.TryAutosave(LevelMaps.Empty(), _dir, "a_draft", out _));
		File.WriteAllBytes(Path.Combine(_dir, "broken.map"), new byte[] { 1, 2, 3 });
		File.WriteAllBytes(Path.Combine(_dir, "Upper.map"), ridge);
		File.WriteAllText(Path.Combine(_dir, "notes.txt"), "not a map");

		var listed = MapStore.List(_dir);
		Assert.Equal(new[] { "a_draft", "b-ridge", "broken" }, listed.Select(m => m.Name));

		StoredMap draft = listed[0];
		Assert.True(draft.Reads);
		Assert.True(draft.HasAutosave);
		Assert.False(draft.Report.IsPlayable);
		Assert.Contains("error", MapStore.Describe(draft.Report));

		StoredMap good = listed[1];
		Assert.Equal(MapFile.HashHex(ridge), good.Hash);
		Assert.True(good.Report.IsPlayable);
		Assert.Equal("no issues", MapStore.Describe(good.Report));
		Assert.False(good.HasAutosave);

		StoredMap broken = listed[2];
		Assert.False(broken.Reads);
		Assert.Contains("not a map", broken.Error);
		Assert.Equal("not read", MapStore.Describe(broken.Report));
	}

	[Fact]
	public void AFork_IsANewLineage_WithItsParentNamed()
	{
		MapDocument source = LevelMaps.Generate(2);
		source.Revision = 7;
		byte[] hash = MapFile.Hash(MapFile.Write(source));
		var id = new Guid("fedcba98765432100123456789abcdef");
		MapDocument copy = MapStore.Fork(source, hash, id, "Ridge (copy)", 1_900_000_000);

		Assert.Equal(id, copy.Id);
		Assert.Equal(source.Id, copy.ParentId);
		Assert.Equal(hash, copy.ParentHash);
		Assert.Equal(0u, copy.Revision);
		Assert.Equal(1_900_000_000, copy.CreatedUnixSeconds);
		Assert.Equal(1_900_000_000, copy.ModifiedUnixSeconds);
		Assert.Equal("Ridge (copy)", copy.Name);
		Assert.Equal(source.Author, copy.Author);
		Assert.Equal(source.Terrain.ToSamples(), copy.Terrain.ToSamples());
		Assert.Equal(source.Objects, copy.Objects);

		copy.Terrain[1, 1] = 9f;
		Assert.NotEqual(9f, source.Terrain[1, 1]);
		Assert.Equal(7u, source.Revision);

		MapDocument unnamed = MapStore.Fork(source, ReadOnlySpan<byte>.Empty, id, null, 1);
		Assert.Equal(source.Name, unnamed.Name);
		Assert.Equal(new byte[MapDocument.HashBytes], unnamed.ParentHash);
	}

	[Fact]
	public void SaveAs_MakesTheDocumentAForkOfTheFileItWasSavedAs()
	{
		MapDocument document = LevelMaps.Generate(1);
		Assert.True(MapStore.TrySave(document, _dir, "ridge", 10, out byte[] file, out _));
		Guid was = document.Id;
		var id = new Guid("00000000000000000000000000000042");
		MapStore.BecomeFork(document, MapFile.Hash(file), id, 20);
		Assert.Equal(id, document.Id);
		Assert.Equal(was, document.ParentId);
		Assert.Equal(MapFile.Hash(file), document.ParentHash);
		Assert.Equal(0u, document.Revision);
		Assert.Equal(20, document.CreatedUnixSeconds);

		Assert.True(MapStore.TrySave(document, _dir, "ridge-2", 30, out _, out _));
		Assert.True(MapStore.TryOpen(_dir, "ridge", out MapDocument first, out _, out _));
		Assert.True(MapStore.TryOpen(_dir, "ridge-2", out MapDocument second, out _, out _));
		Assert.NotEqual(first.Id, second.Id);
		Assert.Equal(first.Id, second.ParentId);
		Assert.Equal(1u, second.Revision);
	}

	[Fact]
	public void Exists_AsksTheNameAsAName()
	{
		Assert.False(MapStore.Exists(_dir, "ridge"));
		Assert.True(MapStore.TrySave(LevelMaps.Empty(), _dir, "ridge", 1, out _, out _));
		Assert.True(MapStore.Exists(_dir, "ridge"));
		Assert.True(MapStore.Exists(_dir, " ridge "));
		Assert.False(MapStore.Exists(_dir, "../ridge"));
		Assert.False(MapStore.Exists(_dir, null));
	}

	[Fact]
	public void Describe_CountsErrorsAndWarnings()
	{
		var report = MapValidator.Validate(LevelMaps.Empty());
		Assert.Matches(@"^\d+ errors?(, \d+ warnings?)?$", MapStore.Describe(report));
	}
}
