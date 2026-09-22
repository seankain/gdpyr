using System;
using System.Collections.Generic;
using System.Text;

namespace Gdpyr.Core;

/// <summary>What running a console command produced (docs/DEMOS.md §3).</summary>
public readonly struct ConsoleResult
{
	/// <summary>False when the command failed, was unknown, or was given bad arguments.</summary>
	public readonly bool Ok;

	/// <summary>What to print. Never null; often empty, for a command that just did the thing.</summary>
	public readonly IReadOnlyList<string> Lines;

	public ConsoleResult(bool ok, IReadOnlyList<string> lines)
	{
		Ok = ok;
		Lines = lines ?? Array.Empty<string>();
	}

	public static ConsoleResult Done() => new(true, Array.Empty<string>());

	public static ConsoleResult Say(params string[] lines) => new(true, lines);

	public static ConsoleResult Failed(string message) => new(false, new[] { message });
}

/// <summary>
/// One console command: a name, one line of usage, one line of help, and what it
/// does. Arguments arrive already split, without the command name.
/// </summary>
public sealed class ConsoleCommand
{
	public ConsoleCommand(string name, string usage, string help, Func<IReadOnlyList<string>, ConsoleResult> run)
	{
		Name = name;
		Usage = usage;
		Help = help;
		Run = run;
	}

	public string Name { get; }

	/// <summary>How to spell it, e.g. <c>record &lt;name&gt;</c>.</summary>
	public string Usage { get; }

	/// <summary>One line, for <c>help</c>.</summary>
	public string Help { get; }

	public Func<IReadOnlyList<string>, ConsoleResult> Run { get; }
}

/// <summary>
/// The console's command table (docs/DEMOS.md §3).
///
/// Engine-free on purpose: what <c>record game.demo</c> parses to, what an unknown
/// command says and what tab completes to are all answerable by <c>dotnet test</c>
/// rather than by standing a game up and typing into it — the same reasoning that
/// keeps <see cref="LaunchOptions"/> out of the engine. The half that needs a
/// window is <c>Scripts/Ui/GameConsole.cs</c>, which owns one of these and does
/// nothing but feed lines into it.
/// </summary>
public sealed class ConsoleCommandTable
{
	private readonly Dictionary<string, ConsoleCommand> _commands = new(StringComparer.OrdinalIgnoreCase);
	private readonly List<string> _names = new();

	/// <summary>Commands, in the order they were registered.</summary>
	public IReadOnlyList<string> Names => _names;

	public int Count => _names.Count;

	/// <summary>
	/// Adds a command. Registering the same name twice replaces the first, which is
	/// what makes a reload of the UI that owns this table idempotent.
	/// </summary>
	public void Register(string name, string usage, string help, Func<IReadOnlyList<string>, ConsoleResult> run)
	{
		if (string.IsNullOrWhiteSpace(name) || run == null)
		{
			return;
		}

		name = name.Trim();
		if (!_commands.ContainsKey(name))
		{
			_names.Add(name);
		}

		_commands[name] = new ConsoleCommand(name, usage ?? name, help ?? string.Empty, run);
	}

	public bool TryGet(string name, out ConsoleCommand command) =>
		_commands.TryGetValue(name ?? string.Empty, out command);

	/// <summary>
	/// Runs a whole typed line. A blank line is a no-op rather than an error: the
	/// return key is how a console is closed as often as it is how one is used.
	/// </summary>
	public ConsoleResult Execute(string line)
	{
		IReadOnlyList<string> tokens = Tokenize(line);
		if (tokens.Count == 0)
		{
			return ConsoleResult.Done();
		}

		if (!_commands.TryGetValue(tokens[0], out ConsoleCommand command))
		{
			string suggestion = Nearest(tokens[0]);
			return ConsoleResult.Failed($"unknown command '{tokens[0]}'"
				+ (suggestion != null ? $"; did you mean '{suggestion}'?" : ". Try 'help'."));
		}

		var args = new string[tokens.Count - 1];
		for (int i = 1; i < tokens.Count; i++)
		{
			args[i - 1] = tokens[i];
		}

		return command.Run(args);
	}

	/// <summary>One line per command, for <c>help</c> with no argument.</summary>
	public IReadOnlyList<string> Describe()
	{
		var lines = new List<string>(_names.Count);
		int width = 0;
		for (int i = 0; i < _names.Count; i++)
		{
			width = Math.Max(width, _commands[_names[i]].Usage.Length);
		}

		for (int i = 0; i < _names.Count; i++)
		{
			ConsoleCommand command = _commands[_names[i]];
			lines.Add($"  {command.Usage.PadRight(width)}  {command.Help}");
		}

		return lines;
	}

	/// <summary>
	/// The commands a half-typed name could become, for the tab key. The whole line
	/// is passed in rather than the word, because only the first word completes —
	/// completing an argument would mean knowing what the argument is.
	/// </summary>
	public IReadOnlyList<string> Complete(string prefix)
	{
		var matches = new List<string>();
		prefix = prefix?.TrimStart() ?? string.Empty;

		if (prefix.Length == 0 || prefix.IndexOf(' ') >= 0)
		{
			return matches;
		}

		for (int i = 0; i < _names.Count; i++)
		{
			if (_names[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
			{
				matches.Add(_names[i]);
			}
		}

		return matches;
	}

	/// <summary>
	/// Splits a typed line into a command and its arguments. Whitespace separates,
	/// double quotes group, and a backslash escapes the next character — enough for
	/// <c>record "last night's round.dem"</c> and deliberately not a shell.
	/// An unterminated quote runs to the end of the line rather than being an
	/// error: somebody who has not finished typing has not made a mistake.
	/// </summary>
	public static IReadOnlyList<string> Tokenize(string line)
	{
		var tokens = new List<string>();
		if (string.IsNullOrWhiteSpace(line))
		{
			return tokens;
		}

		var token = new StringBuilder();
		bool quoted = false;
		bool any = false;

		for (int i = 0; i < line.Length; i++)
		{
			char c = line[i];

			if (c == '\\' && i + 1 < line.Length)
			{
				token.Append(line[++i]);
				any = true;
				continue;
			}

			if (c == '"')
			{
				quoted = !quoted;
				// A quote on its own is an empty argument, which is a thing somebody
				// may well mean: `echo ""`.
				any = true;
				continue;
			}

			if (!quoted && char.IsWhiteSpace(c))
			{
				if (any)
				{
					tokens.Add(token.ToString());
					token.Clear();
					any = false;
				}
				continue;
			}

			token.Append(c);
			any = true;
		}

		if (any)
		{
			tokens.Add(token.ToString());
		}

		return tokens;
	}

	/// <summary>
	/// The registered command closest to what was typed, or null when nothing is
	/// close. A prefix match first, then one edit away — which covers the two
	/// things people actually do, which are stopping halfway and hitting the
	/// neighbouring key.
	/// </summary>
	private string Nearest(string typed)
	{
		for (int i = 0; i < _names.Count; i++)
		{
			if (_names[i].StartsWith(typed, StringComparison.OrdinalIgnoreCase))
			{
				return _names[i];
			}
		}

		for (int i = 0; i < _names.Count; i++)
		{
			if (WithinOneEdit(_names[i], typed))
			{
				return _names[i];
			}
		}

		return null;
	}

	private static bool WithinOneEdit(string a, string b)
	{
		if (Math.Abs(a.Length - b.Length) > 1)
		{
			return false;
		}

		// Walk both from the front, allow one disagreement, and walk on from
		// whichever side the lengths say the extra character is on.
		int i = 0, j = 0, edits = 0;
		while (i < a.Length && j < b.Length)
		{
			if (char.ToLowerInvariant(a[i]) == char.ToLowerInvariant(b[j]))
			{
				i++;
				j++;
				continue;
			}

			if (++edits > 1)
			{
				return false;
			}

			if (a.Length > b.Length) { i++; }
			else if (b.Length > a.Length) { j++; }
			else { i++; j++; }
		}

		return edits + (a.Length - i) + (b.Length - j) <= 1;
	}
}
