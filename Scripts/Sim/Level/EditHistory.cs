using System;
using System.Collections.Generic;

namespace Gdpyr.Sim.Level;

/// <summary>One change to a document's objects: an object put in, taken out, or replaced, at an index.</summary>
public readonly struct ObjectChange
{
	public readonly int Index;

	/// <summary>What was there; null for an object put in.</summary>
	public readonly MapObject Before;

	/// <summary>What is there after; null for an object taken out.</summary>
	public readonly MapObject After;

	public ObjectChange(int index, MapObject before, MapObject after)
	{
		Index = index;
		Before = before;
		After = after;
	}
}

/// <summary>
/// One step to undo (docs/LEVEL_EDITOR.md §8.6): the rectangle of ground it changed,
/// before and after, as stored samples, and the objects it put in, took out or
/// replaced. A stroke keeps its rectangle; a placed object keeps its record and its
/// pad's rectangle. 2 bytes a sample each way, so a stroke over a 64 m square is 17 KB.
///
/// Applying it is the edit; reverting it is its inverse, exactly: the samples are put
/// back as they were stored, and the objects at the indices they were at.
/// </summary>
public sealed class MapEdit
{
	private readonly List<ObjectChange> _objects = new();
	private short[] _before = Array.Empty<short>();
	private short[] _after = Array.Empty<short>();

	/// <summary>The ground it changed; empty for an edit of objects alone.</summary>
	public SampleRect Rect { get; private set; } = SampleRect.Empty;

	public IReadOnlyList<ObjectChange> Objects => _objects;

	public bool IsEmpty => Rect.IsEmpty && _objects.Count == 0;

	/// <summary>What it holds of the ground: two samples of two bytes for each in its rectangle.</summary>
	public int TerrainBytes => (_before.Length + _after.Length) * sizeof(short);

	/// <summary>
	/// An edit of the ground: the rectangle as it is in <paramref name="before"/> and in
	/// <paramref name="after"/>, as stored samples. The two fields are the same size;
	/// the rectangle is held to it.
	/// </summary>
	public static MapEdit Terrain(HeightField before, HeightField after, in SampleRect rect)
	{
		if (before.Width != after.Width || before.Depth != after.Depth)
		{
			throw new ArgumentException("the two fields are not the same size", nameof(after));
		}

		SampleRect inside = rect.Intersect(before.Bounds);
		return new MapEdit
		{
			Rect = inside,
			_before = Capture(before, inside),
			_after = Capture(after, inside),
		};
	}

	/// <summary>A rectangle of a field, as stored samples, row by row.</summary>
	public static short[] Capture(HeightField field, in SampleRect rect)
	{
		SampleRect inside = rect.Intersect(field.Bounds);
		var samples = new short[inside.Count];
		int k = 0;
		for (int z = inside.MinZ; z <= inside.MaxZ; z++)
		{
			for (int x = inside.MinX; x <= inside.MaxX; x++)
			{
				samples[k++] = HeightField.ToSample(field[x, z]);
			}
		}

		return samples;
	}

	/// <summary>Puts <paramref name="placed"/> in at <paramref name="index"/>. Returns this edit.</summary>
	public MapEdit Insert(int index, MapObject placed)
	{
		_objects.Add(new ObjectChange(index, null, placed ?? throw new ArgumentNullException(nameof(placed))));
		return this;
	}

	/// <summary>Takes the object at <paramref name="index"/> out, which is <paramref name="placed"/>. Returns this edit.</summary>
	public MapEdit Remove(int index, MapObject placed)
	{
		_objects.Add(new ObjectChange(index, placed ?? throw new ArgumentNullException(nameof(placed)), null));
		return this;
	}

	/// <summary>Replaces the object at <paramref name="index"/>. Returns this edit.</summary>
	public MapEdit Replace(int index, MapObject before, MapObject after)
	{
		_objects.Add(new ObjectChange(index, before ?? throw new ArgumentNullException(nameof(before)),
			after ?? throw new ArgumentNullException(nameof(after))));
		return this;
	}

	/// <summary>Makes the edit: the ground after, then the objects in the order they were given.</summary>
	public void Apply(MapDocument document)
	{
		Put(document.Terrain, _after);
		foreach (ObjectChange change in _objects)
		{
			Change(document.Objects, change.Index, change.Before, change.After);
		}
	}

	/// <summary>Undoes it: the objects in reverse, then the ground before.</summary>
	public void Revert(MapDocument document)
	{
		for (int i = _objects.Count - 1; i >= 0; i--)
		{
			ObjectChange change = _objects[i];
			Change(document.Objects, change.Index, change.After, change.Before);
		}

		Put(document.Terrain, _before);
	}

	private static void Change(List<MapObject> objects, int index, MapObject from, MapObject to)
	{
		if (from == null)
		{
			objects.Insert(index, to);
		}
		else if (to == null)
		{
			objects.RemoveAt(index);
		}
		else
		{
			objects[index] = to;
		}
	}

	private void Put(HeightField field, short[] samples)
	{
		int k = 0;
		for (int z = Rect.MinZ; z <= Rect.MaxZ; z++)
		{
			for (int x = Rect.MinX; x <= Rect.MaxX; x++)
			{
				field[x, z] = HeightField.ToMeters(samples[k++]);
			}
		}
	}
}

/// <summary>
/// Undo and redo (docs/LEVEL_EDITOR.md §8.6): the last two hundred steps. A new step
/// drops what could have been redone. The history is the session's: it is not saved.
/// </summary>
public sealed class EditHistory
{
	/// <summary>Steps kept; past it the oldest goes.</summary>
	public const int MaxSteps = 200;

	private readonly LinkedList<MapEdit> _done = new();
	private readonly Stack<MapEdit> _undone = new();

	public int UndoCount => _done.Count;

	public int RedoCount => _undone.Count;

	public bool CanUndo => _done.Count > 0;

	public bool CanRedo => _undone.Count > 0;

	/// <summary>The step <see cref="Undo"/> would revert, or null: what the editor re-draws after it (LE4).</summary>
	public MapEdit NextUndo => _done.Last?.Value;

	/// <summary>The step <see cref="Redo"/> would make again, or null.</summary>
	public MapEdit NextRedo => _undone.Count > 0 ? _undone.Peek() : null;

	/// <summary>Applies an edit and keeps it as the newest step.</summary>
	public void Do(MapDocument document, MapEdit edit)
	{
		edit.Apply(document);
		Push(edit);
	}

	/// <summary>Keeps an edit already applied — a stroke, which changed the ground as it went — as the newest step.</summary>
	public void Push(MapEdit edit)
	{
		if (edit == null || edit.IsEmpty)
		{
			return;
		}

		_done.AddLast(edit);
		if (_done.Count > MaxSteps)
		{
			_done.RemoveFirst();
		}

		_undone.Clear();
	}

	/// <summary>Reverts the newest step. False when there is none.</summary>
	public bool Undo(MapDocument document)
	{
		if (_done.Last is not { } last)
		{
			return false;
		}

		_done.RemoveLast();
		last.Value.Revert(document);
		_undone.Push(last.Value);
		return true;
	}

	/// <summary>Makes the step last undone again. False when there is none.</summary>
	public bool Redo(MapDocument document)
	{
		if (_undone.Count == 0)
		{
			return false;
		}

		MapEdit edit = _undone.Pop();
		edit.Apply(document);
		_done.AddLast(edit);
		return true;
	}

	public void Clear()
	{
		_done.Clear();
		_undone.Clear();
	}
}
