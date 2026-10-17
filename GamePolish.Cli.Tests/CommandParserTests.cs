using GamePolish.Cli.CommandLine;

namespace GamePolish.Cli.Tests;

/// <summary>
/// Covers the command parser (.todo/213): every usage error is one exception and one exit code, and the
/// commands the acceptance script types actually parse.
/// </summary>
/// <remarks>
/// The parser is tested rather than the commands: a command body is argument reading plus one Core call, while
/// the parser is where five different mistakes (unknown command, unknown option, missing required option,
/// duplicated option, wrong number of positionals) have to end up as the same <c>2</c>. A shell script that
/// cannot tell a typo from a failed install is the failure this file exists to prevent.
/// </remarks>
public class CommandParserTests
{
    [Fact]
    public void HelpAloneEndsTheLineWithoutACommand()
    {
        ParsedCommandLine parsed = Parse("--help");

        Assert.True(parsed.Global.Help);
        Assert.Null(parsed.Command);
    }

    [Fact]
    public void GlobalOptionsAreReadBeforeTheCommand()
    {
        ParsedCommandLine parsed = Parse("--data-root", @"C:\tmp\gp", "--json", "--log-level", "debug", "list", "--game", "octowow");

        Assert.Equal(@"C:\tmp\gp", parsed.Global.DataRoot);
        Assert.True(parsed.Global.Json);
        Assert.Equal("debug", parsed.Global.LogLevel);
        Assert.True(parsed.Global.Debug);
        Assert.NotNull(parsed.Command);
    }

    [Fact]
    public void AOneWordCommandReadsItsPositionalAndOption()
    {
        ParsedCommand parsed = Parse("add-game", "octowow", "--path", @"D:\OctoWow").Command!;

        Assert.Equal(["add-game"], parsed.Spec.Tokens);
        Assert.Equal("octowow", parsed.Positional(0));
        Assert.Equal(@"D:\OctoWow", parsed.Required("path"));
    }

    [Fact]
    public void ATwoWordCommandResolvesToTheRightShape()
    {
        ParsedCommand parsed = Parse("backup", "list", "--game", "octowow").Command!;

        Assert.Equal(["backup", "list"], parsed.Spec.Tokens);
        Assert.Equal("octowow", parsed.Required("game"));
    }

    [Fact]
    public void AFlagIsBooleanAndTakesNoValue()
    {
        ParsedCommand parsed = Parse("install", "--game", "octowow", "--source", "https://github.com/x/y", "--overwrite").Command!;

        Assert.True(parsed.Flag("overwrite"));
        Assert.Equal("https://github.com/x/y", parsed.Required("source"));
    }

    [Theory]
    [InlineData("frobnicate")]
    [InlineData("list")]
    [InlineData("list", "--nonsense", "x", "--game", "octowow")]
    [InlineData("list", "--game")]
    [InlineData("list", "--game", "a", "--game", "b")]
    [InlineData("uninstall", "--game", "g", "--mod", "m", "extra")]
    [InlineData("install", "--game", "g", "--source", "https://github.com/x/y", "--zip", "a.zip")]
    public void EveryKindOfUsageMistakeIsOneUsageException(params string[] arguments)
    {
        Assert.Throws<CliUsageException>(() => CommandParser.Parse(arguments));
    }

    [Fact]
    public void TheExitCodeContractIsTheDocumentedTable()
    {
        Assert.Equal(ExitCodes.Success, ExitCodes.For(Succeeded()));
        Assert.Equal(ExitCodes.Failure, ExitCodes.For(Failed()));
        Assert.Equal(ExitCodes.Cancelled, ExitCodes.For(Cancelled()));
        Assert.Equal(ExitCodes.Usage, ExitCodes.For(new CliUsageException("unknown command")));
        Assert.Equal(
            ExitCodes.Environment,
            ExitCodes.For(new CliEnvironmentException(GamePolish.Core.Operations.OperationErrorCode.GitNotFound, "no git")));
        Assert.Equal(ExitCodes.Cancelled, ExitCodes.For(new OperationCanceledException()));

        // Anything else is an operation that failed, not a usage error and not the environment's fault.
        Assert.Equal(ExitCodes.Failure, ExitCodes.For(new InvalidOperationException("boom")));
    }

    [Fact]
    public void ACommandMayPinItsOwnExitCodeAndOtherwiseDerivesIt()
    {
        // doctor is the one command whose failed report means "this machine is not ready" (3), not "the
        // operation failed" (1).
        Assert.Equal(ExitCodes.Environment, new CommandOutcome { Result = Failed(), ExitCode = ExitCodes.Environment }.EffectiveExitCode);
        Assert.Equal(ExitCodes.Failure, CommandOutcome.FromResult(Failed()).EffectiveExitCode);
        Assert.Equal(ExitCodes.Success, CommandOutcome.FromResult(Succeeded()).EffectiveExitCode);
    }

    private static ParsedCommandLine Parse(params string[] arguments) => CommandParser.Parse(arguments);

    private static GamePolish.Core.Operations.OperationResult Succeeded() =>
        GamePolish.Core.Operations.OperationResult.Success("done.");

    private static GamePolish.Core.Operations.OperationResult Failed() =>
        GamePolish.Core.Operations.OperationResult.Failure(
            GamePolish.Core.Operations.OperationErrorCode.GitFailed,
            "The repository could not be read.");

    private static GamePolish.Core.Operations.OperationResult Cancelled() =>
        GamePolish.Core.Operations.OperationResult.Cancelled("Install cancelled. Nothing was changed.");
}
