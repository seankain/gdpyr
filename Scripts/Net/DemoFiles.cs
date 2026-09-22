using System;
using System.Collections.Generic;
using System.IO;
using Gdpyr.Sim.Demo;
using Godot;

// Godot has a FileAccess of its own and this file wants System.IO's: these are
// plain streams, because the reader and the writer take one and knowing nothing
// about Godot is the point of them.
using FileAccess = System.IO.FileAccess;

namespace Gdpyr.Net;

/// <summary>
/// Where demos live, and the only place this project turns a name somebody typed
/// into a path (docs/DEMOS.md §3.1).
///
/// <see cref="DemoFormat.Directory"/> is a Godot user path, which resolves to the
/// per-platform writable directory the engine picks — the one place a dedicated
/// server, an export and an editor run all agree they may write. Everything past
/// resolving it is plain <c>System.IO</c>, because the reader and the writer take
/// a <see cref="Stream"/> and knowing nothing about Godot is the point of them.
/// </summary>
public static class DemoFiles
{
	/// <summary>The demo directory as a real path on this machine.</summary>
	public static string Root => ProjectSettings.GlobalizePath(DemoFormat.Directory);

	/// <summary>
	/// The path a demo name resolves to, or null with a reason when the name is not
	/// one (<see cref="DemoFormat.TryParseName"/>).
	/// </summary>
	public static string Resolve(string name, out string fileName, out string error)
	{
		if (!DemoFormat.TryParseName(name, out fileName, out error))
		{
			return null;
		}

		return Path.Combine(Root, fileName);
	}

	/// <summary>Creates the demo directory if it is not there. False with a reason when it cannot.</summary>
	public static bool EnsureRoot(out string error)
	{
		error = null;
		try
		{
			Directory.CreateDirectory(Root);
			return true;
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException
			or ArgumentException)
		{
			error = e.Message;
			return false;
		}
	}

	/// <summary>
	/// What is in the demo directory, newest first. Never throws: a directory that
	/// is not there is an empty list, which is also what it means.
	/// </summary>
	public static IReadOnlyList<FileInfo> List()
	{
		var found = new List<FileInfo>();
		try
		{
			var root = new DirectoryInfo(Root);
			if (!root.Exists)
			{
				return found;
			}

			found.AddRange(root.GetFiles());
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			return found;
		}

		found.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
		return found;
	}

	/// <summary>Opens a demo for writing, creating the directory and truncating any file of that name.</summary>
	public static Stream TryCreate(string path, out string error)
	{
		error = null;
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Root);
			return new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException
			or ArgumentException)
		{
			error = e.Message;
			return null;
		}
	}

	/// <summary>
	/// Opens a demo for reading. Shares the file for writing so that a demo can be
	/// played back while it is still being recorded, which is the one thing a
	/// person testing this will try first.
	/// </summary>
	public static Stream TryOpen(string path, out string error)
	{
		error = null;
		try
		{
			return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException
			or ArgumentException)
		{
			error = e.Message;
			return null;
		}
	}
}
