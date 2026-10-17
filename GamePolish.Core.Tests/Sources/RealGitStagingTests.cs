using GamePolish.Core.Manifest;
using GamePolish.Core.Paths;
using GamePolish.Core.Sources;
using GamePolish.Core.Storage;
using GamePolish.Core.Tests.Storage;

namespace GamePolish.Core.Tests.Sources;

/// <summary>
/// The one test that needs the internet: a real repository, cloned by the real <c>git</c> binary, through
/// the production code path.
/// </summary>
/// <remarks>
/// <para>
/// Skipped unless <c>GAMEPOLISH_NETWORK_TESTS</c> is <c>1</c>.
/// It exists because the offline suite can only establish two things: that the argv is the hardened one, and
/// that the orchestration behaves. That git <i>accepts</i> those flags, that a real clone yields a real
/// 40-character SHA, and that the folder really is named after the repository's <c>.toc</c> are facts only a
/// real clone settles - and a required CI check that goes red when GitHub hiccups is a worse trade than an
/// opt-in test. Run it before a release, and after any change to the hardening list.
/// </para>
/// <para>
/// The repository is one of the seed registry's own rows (<c>../game-polish-notes/docs/games/octowow.md</c>
/// section <b>Seed Registry</b>), so the test exercises a URL the product actually ships.
/// </para>
/// </remarks>
[Collection(StorageCollection.Name)]
public class RealGitStagingTests
{
    /// <summary>The environment variable that turns this test on.</summary>
    public const string NetworkTestsVariable = "GAMEPOLISH_NETWORK_TESTS";

    private const string PfUiUrl = "https://github.com/brues-code/pfUI";

    private readonly GamePolishPaths _paths;

    public RealGitStagingTests(StorageFixture fixture)
    {
        _paths = new GamePolishPaths(fixture.NewSubdirectory($"real-git-{Guid.NewGuid():N}"));
    }

    [Fact]
    public async Task ARealCloneStagesTheRepositoryWithItsHeadSha()
    {
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable(NetworkTestsVariable) == "1",
            $"Set {NetworkTestsVariable}=1 to clone a real repository. The offline suite covers the hardened argv and the staging orchestration.");

        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SourceAcquirer acquirer = new(_paths);

        SourceRequest request = new()
        {
            GameId = "octowow",
            SourceUrl = PfUiUrl,
            SourceType = ModSourceType.Github,
            DataRoot = _paths.Root,
            OperationId = "real-clone",
        };

        StagedSource staged = await acquirer.StageGitAsync(request, cancellationToken);

        try
        {
            Assert.Equal(40, staged.Version.Length);
            Assert.True(staged.Version.All(char.IsAsciiHexDigit), $"'{staged.Version}' is not a commit SHA.");
            Assert.Equal("sha", staged.VersionKind);
            Assert.True(staged.HasGitDirectory);

            // The folder is named after the repository's own .toc, and the addon root is what the seed
            // registry says it is: this is the assertion the fake launcher cannot make.
            Assert.Equal("pfUI", staged.FolderName);
            Assert.Contains(staged.Files, file => file.RelativePath.Equals("pfUI/pfUI.toc", StringComparison.OrdinalIgnoreCase));
            Assert.All(staged.Files, file => Assert.StartsWith("pfUI/", file.RelativePath, StringComparison.Ordinal));
            Assert.DoesNotContain(staged.Files, file => file.RelativePath.StartsWith("pfUI/.git/", StringComparison.Ordinal));

            // A real clone is a real tree: the repository's Lua and TOC files are there and non-empty.
            Assert.True(staged.Files.Count > 10, $"a real pfUI clone has more than 10 files, not {staged.Files.Count}.");
            Assert.All(staged.Files, file => Assert.True(file.SizeBytes > 0, $"{file.RelativePath} is empty."));
        }
        finally
        {
            acquirer.Cleanup(staged);
        }

        Assert.False(Directory.Exists(staged.StagingDirectory));
    }
}
