using System.Collections.Generic;
using Gdpyr.Core;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// The console's line parser and command table (docs/DEMOS.md §3). The half of the
/// console that has no window in it, which is deliberately the half that decides
/// anything.
/// </summary>
public class ConsoleCommandTests
{
	private static ConsoleCommandTable Table(out List<string> ran)
	{
		var log = new List<string>();
		ran = log;

		var table = new ConsoleCommandTable();
		table.Register("record", "record <name>", "start a demo", args =>
		{
			if (args.Count != 1)
			{
				return ConsoleResult.Failed("usage: record <name>");
			}

			log.Add(args[0]);
			return ConsoleResult.Say($"recording to {args[0]}");
		});
		table.Register("stoprecord", "stoprecord", "close it", _ =>
		{
			log.Add("stop");
			return ConsoleResult.Done();
		});
		table.Register("stopdemo", "stopdemo", "stop watching", _ => ConsoleResult.Done());
		return table;
	}

	// ---- tokenizing --------------------------------------------------------

	[Fact]
	public void ALine_SplitsOnWhitespace()
	{
		Assert.Equal(new[] { "record", "game.demo" }, ConsoleCommandTable.Tokenize("record game.demo"));
		Assert.Equal(new[] { "record", "game.demo" }, ConsoleCommandTable.Tokenize("  record   game.demo  "));
	}

	[Fact]
	public void QuotesGroup()
	{
		Assert.Equal(new[] { "record", "last night's round.dem" },
			ConsoleCommandTable.Tokenize("record \"last night's round.dem\""));
	}

	[Fact]
	public void ABackslashEscapesTheNextCharacter() =>
		Assert.Equal(new[] { "echo", "a\"b", "c d" }, ConsoleCommandTable.Tokenize("echo a\\\"b \"c d\""));

	[Fact]
	public void AnUnterminatedQuoteRunsToTheEndOfTheLine() =>
		Assert.Equal(new[] { "record", "half typed" }, ConsoleCommandTable.Tokenize("record \"half typed"));

	[Fact]
	public void AnEmptyQuotedArgumentIsAnArgument() =>
		Assert.Equal(new[] { "echo", "" }, ConsoleCommandTable.Tokenize("echo \"\""));

	[Fact]
	public void ABlankLineIsNoTokens()
	{
		Assert.Empty(ConsoleCommandTable.Tokenize(string.Empty));
		Assert.Empty(ConsoleCommandTable.Tokenize("   \t  "));
		Assert.Empty(ConsoleCommandTable.Tokenize(null));
	}

	// ---- running -----------------------------------------------------------

	[Fact]
	public void ACommand_GetsItsArgumentsWithoutItsName()
	{
		ConsoleCommandTable table = Table(out List<string> ran);

		ConsoleResult result = table.Execute("record game.demo");

		Assert.True(result.Ok);
		Assert.Equal(new[] { "game.demo" }, ran);
		Assert.Equal(new[] { "recording to game.demo" }, result.Lines);
	}

	[Fact]
	public void ACommand_IsCaseInsensitive()
	{
		ConsoleCommandTable table = Table(out List<string> ran);
		Assert.True(table.Execute("RECORD game.demo").Ok);
		Assert.Single(ran);
	}

	[Fact]
	public void ABlankLine_DoesNothingAndIsNotAnError()
	{
		ConsoleCommandTable table = Table(out List<string> ran);

		ConsoleResult result = table.Execute("   ");

		Assert.True(result.Ok);
		Assert.Empty(result.Lines);
		Assert.Empty(ran);
	}

	[Fact]
	public void AnUnknownCommand_SaysSoAndSuggestsTheNearestOne()
	{
		ConsoleCommandTable table = Table(out _);

		ConsoleResult result = table.Execute("recrd game.demo");

		Assert.False(result.Ok);
		Assert.Contains("unknown command 'recrd'", result.Lines[0]);
		Assert.Contains("record", result.Lines[0]);
	}

	[Fact]
	public void AnUnknownCommand_WithNothingCloseSaysToTryHelp()
	{
		ConsoleCommandTable table = Table(out _);

		ConsoleResult result = table.Execute("noclip");

		Assert.False(result.Ok);
		Assert.Contains("Try 'help'", result.Lines[0]);
	}

	[Fact]
	public void ACommand_ThatFailsComesBackNotOk()
	{
		ConsoleCommandTable table = Table(out _);

		ConsoleResult result = table.Execute("record one two three");

		Assert.False(result.Ok);
		Assert.Equal("usage: record <name>", result.Lines[0]);
	}

	[Fact]
	public void RegisteringTheSameNameTwice_ReplacesItRatherThanAddingIt()
	{
		var table = new ConsoleCommandTable();
		table.Register("echo", "echo", "first", _ => ConsoleResult.Say("first"));
		table.Register("echo", "echo", "second", _ => ConsoleResult.Say("second"));

		Assert.Equal(1, table.Count);
		Assert.Equal(new[] { "second" }, table.Execute("echo").Lines);
	}

	// ---- completing --------------------------------------------------------

	[Fact]
	public void Tab_CompletesAPrefix()
	{
		ConsoleCommandTable table = Table(out _);

		Assert.Equal(new[] { "record" }, table.Complete("rec"));
		Assert.Equal(new[] { "stoprecord", "stopdemo" }, table.Complete("stop"));
		Assert.Empty(table.Complete("zzz"));
	}

	[Fact]
	public void Tab_CompletesNothingOnceThereIsAnArgument()
	{
		// Only the command completes: completing an argument would mean knowing what
		// the argument is, and the table does not.
		ConsoleCommandTable table = Table(out _);
		Assert.Empty(table.Complete("record ga"));
		Assert.Empty(table.Complete(string.Empty));
	}

	// ---- help --------------------------------------------------------------

	[Fact]
	public void Describe_IsOneLinePerCommandInRegistrationOrder()
	{
		ConsoleCommandTable table = Table(out _);

		IReadOnlyList<string> lines = table.Describe();

		Assert.Equal(3, lines.Count);
		Assert.Contains("record <name>", lines[0]);
		Assert.Contains("start a demo", lines[0]);
		Assert.Contains("stoprecord", lines[1]);
	}

	[Fact]
	public void TryGet_FindsACommandByName()
	{
		ConsoleCommandTable table = Table(out _);

		Assert.True(table.TryGet("stoprecord", out ConsoleCommand command));
		Assert.Equal("stoprecord", command.Name);
		Assert.Equal("close it", command.Help);

		Assert.False(table.TryGet("noclip", out _));
	}
}
