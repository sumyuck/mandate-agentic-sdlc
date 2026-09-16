using System.Collections.Immutable;
using Mandate.Core.Execution;
using Mandate.Core.Identifiers;
using Mandate.Persistence.Workspaces;

namespace Mandate.Persistence.Tests;

/// <summary>
/// The workspace, against real git.
/// </summary>
/// <remarks>
/// Deliberately not mocked. The claim being made is that rollback genuinely restores the
/// tree, and that claim is only worth anything if it is tested against the thing that does
/// the restoring.
/// </remarks>
public sealed class GitRunWorkspaceTests : IDisposable
{
    private static readonly RunId Run = RunId.New(
        new DateTimeOffset(2026, 9, 16, 14, 25, 0, TimeSpan.Zero), "abc123");

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"mandate-ws-{Guid.NewGuid():N}");

    private static NodeId Node(string value) => NodeId.Parse(value);

    private static string TemplatePath => Path.Combine(
        RepositoryRoot.Path, "templates", "service");

    private Task<GitRunWorkspace> CreateAsync(string? template = null) =>
        GitRunWorkspace.CreateAsync(_root, Run, template, CancellationToken.None);

    private static Task<WorkspaceCommit?> WriteAsync(
        GitRunWorkspace workspace, string node, int attempt, params (string Path, string Body)[] files) =>
        workspace.CommitAsync(
            Node(node),
            attempt,
            [.. files.Select(file => new WorkspaceFile(file.Path, file.Body))],
            $"{node}: apply changes",
            CancellationToken.None);

    [Fact]
    public async Task A_new_workspace_is_a_clean_repository_with_a_seed_commit()
    {
        GitRunWorkspace workspace = await CreateAsync();

        WorkspaceStatus status = await workspace.StatusAsync(CancellationToken.None);

        status.IsClean.ShouldBeTrue();
        status.CommitCount.ShouldBe(1);
        status.HeadSha.Length.ShouldBe(40);
    }

    [Fact]
    public async Task The_template_is_copied_in_as_the_seed()
    {
        // The starting tree is its own commit, so it stays distinguishable from run output.
        GitRunWorkspace workspace = await CreateAsync(TemplatePath);

        File.Exists(Path.Combine(_root, "Program.cs")).ShouldBeTrue();
        File.Exists(Path.Combine(_root, "Service.csproj")).ShouldBeTrue();

        (await workspace.StatusAsync(CancellationToken.None)).CommitCount.ShouldBe(1);
    }

    [Fact]
    public async Task A_missing_template_is_reported_rather_than_silently_skipped()
    {
        await Should.ThrowAsync<DirectoryNotFoundException>(
            () => GitRunWorkspace.CreateAsync(
                _root, Run, Path.Combine(_root, "nope"), CancellationToken.None));
    }

    [Fact]
    public async Task Each_node_gets_its_own_commit()
    {
        GitRunWorkspace workspace = await CreateAsync(TemplatePath);

        await WriteAsync(workspace, "implement", 1, ("src/Shortener.cs", "// v1"));
        await WriteAsync(workspace, "test", 1, ("tests/ShortenerTests.cs", "// tests"));

        WorkspaceStatus status = await workspace.StatusAsync(CancellationToken.None);

        status.CommitCount.ShouldBe(3);
        status.IsClean.ShouldBeTrue();

        (await workspace.CommitsForAsync(Node("implement"), CancellationToken.None))
            .Length.ShouldBe(1);
    }

    [Fact]
    public async Task A_commit_reports_how_many_files_it_touched()
    {
        GitRunWorkspace workspace = await CreateAsync();

        WorkspaceCommit? commit = await WriteAsync(
            workspace, "implement", 1, ("a.cs", "// a"), ("b/c.cs", "// c"));

        commit.ShouldNotBeNull();
        commit.FilesChanged.ShouldBe(2);
        commit.NodeId.ShouldBe(Node("implement"));
        commit.Attempt.ShouldBe(1);
    }

    [Fact]
    public async Task A_stage_that_changes_nothing_gets_no_commit()
    {
        // An empty commit would make the history claim work happened that did not.
        GitRunWorkspace workspace = await CreateAsync();

        await WriteAsync(workspace, "implement", 1, ("a.cs", "// a"));
        WorkspaceCommit? second = await WriteAsync(workspace, "implement", 2, ("a.cs", "// a"));

        second.ShouldBeNull();
        (await workspace.StatusAsync(CancellationToken.None)).CommitCount.ShouldBe(2);
    }

    // ---- the rollback claim ----

    [Fact]
    public async Task Reverting_a_node_restores_the_tree_it_changed()
    {
        GitRunWorkspace workspace = await CreateAsync(TemplatePath);

        string before = (await workspace.StatusAsync(CancellationToken.None)).HeadSha;
        string originalProgram = await File.ReadAllTextAsync(
            Path.Combine(_root, "Program.cs"), CancellationToken.None);

        await WriteAsync(
            workspace,
            "implement",
            1,
            ("src/Shortener.cs", "// generated"),
            ("Program.cs", "// rewritten by the run"));

        File.Exists(Path.Combine(_root, "src/Shortener.cs")).ShouldBeTrue();

        int reverted = await workspace.RevertNodeAsync(Node("implement"), CancellationToken.None);

        reverted.ShouldBe(1);

        // The tree is back: the added file is gone and the edited file is as it was.
        File.Exists(Path.Combine(_root, "src/Shortener.cs")).ShouldBeFalse();
        (await File.ReadAllTextAsync(Path.Combine(_root, "Program.cs"), CancellationToken.None))
            .ShouldBe(originalProgram);

        WorkspaceStatus after = await workspace.StatusAsync(CancellationToken.None);

        after.IsClean.ShouldBeTrue();
        after.HeadSha.ShouldNotBe(before);
    }

    [Fact]
    public async Task Rollback_preserves_the_history_rather_than_erasing_it()
    {
        // A revert, not a reset: both the change and its reversal stay on the record. For a
        // system whose purpose is an auditable trail, tidying history away is the wrong move.
        GitRunWorkspace workspace = await CreateAsync(TemplatePath);

        await WriteAsync(workspace, "implement", 1, ("src/Shortener.cs", "// generated"));
        await workspace.RevertNodeAsync(Node("implement"), CancellationToken.None);

        WorkspaceStatus status = await workspace.StatusAsync(CancellationToken.None);

        // Seed, the change, and the reversal.
        status.CommitCount.ShouldBe(3);

        (await workspace.CommitsForAsync(Node("implement"), CancellationToken.None))
            .Length.ShouldBe(1, "the original commit is still in the history.");
    }

    [Fact]
    public async Task Reverting_a_node_with_several_commits_undoes_all_of_them()
    {
        GitRunWorkspace workspace = await CreateAsync();

        await WriteAsync(workspace, "implement", 1, ("a.cs", "// first"));
        await WriteAsync(workspace, "implement", 2, ("a.cs", "// second"), ("b.cs", "// b"));

        int reverted = await workspace.RevertNodeAsync(Node("implement"), CancellationToken.None);

        reverted.ShouldBe(2);
        File.Exists(Path.Combine(_root, "a.cs")).ShouldBeFalse();
        File.Exists(Path.Combine(_root, "b.cs")).ShouldBeFalse();
    }

    [Fact]
    public async Task Reverting_one_node_leaves_another_node_untouched()
    {
        GitRunWorkspace workspace = await CreateAsync();

        await WriteAsync(workspace, "implement", 1, ("src/impl.cs", "// impl"));
        await WriteAsync(workspace, "documentation", 1, ("README.md", "# docs"));

        await workspace.RevertNodeAsync(Node("implement"), CancellationToken.None);

        File.Exists(Path.Combine(_root, "src/impl.cs")).ShouldBeFalse();
        File.Exists(Path.Combine(_root, "README.md")).ShouldBeTrue();
    }

    [Fact]
    public async Task Reverting_a_node_that_wrote_nothing_is_a_no_op()
    {
        GitRunWorkspace workspace = await CreateAsync();

        (await workspace.RevertNodeAsync(Node("never-ran"), CancellationToken.None)).ShouldBe(0);
        (await workspace.StatusAsync(CancellationToken.None)).CommitCount.ShouldBe(1);
    }

    [Fact]
    public async Task Uncommitted_changes_can_be_discarded()
    {
        GitRunWorkspace workspace = await CreateAsync(TemplatePath);

        await File.WriteAllTextAsync(
            Path.Combine(_root, "stray.cs"), "// left behind", CancellationToken.None);

        (await workspace.StatusAsync(CancellationToken.None)).IsClean.ShouldBeFalse();

        await workspace.DiscardUncommittedAsync(CancellationToken.None);

        (await workspace.StatusAsync(CancellationToken.None)).IsClean.ShouldBeTrue();
        File.Exists(Path.Combine(_root, "stray.cs")).ShouldBeFalse();
    }

    // ---- the trust boundary ----

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("../escape.cs")]
    [InlineData("src/../../escape.cs")]
    [InlineData(".git/config")]
    [InlineData("src/.git/hooks/pre-commit")]
    [InlineData("C:/Windows/system32/x.dll")]
    [InlineData("")]
    public async Task A_path_that_would_escape_the_workspace_is_refused(string hostile)
    {
        // This validates output the engine did not author, so it is a trust boundary and not
        // a convenience check. A stage writing a git hook would be executing code on the host.
        GitRunWorkspace workspace = await CreateAsync();

        InvalidOperationException error = await Should.ThrowAsync<InvalidOperationException>(
            () => workspace.CommitAsync(
                Node("implement"),
                1,
                [new WorkspaceFile(hostile, "// hostile")],
                "attempt to escape",
                CancellationToken.None));

        error.Message.ShouldContain("outside its workspace");
    }

    [Theory]
    [InlineData("src/Shortener.cs")]
    [InlineData("a.cs")]
    [InlineData("deeply/nested/path/File.cs")]
    public void Ordinary_relative_paths_are_accepted(string path) =>
        WorkspaceFile.IsSafeRelativePath(path).ShouldBeTrue();

    [Fact]
    public async Task Commits_are_attributed_to_the_engine_not_to_whoever_is_configured()
    {
        // Otherwise the history would attribute agent output to a person.
        GitRunWorkspace workspace = await CreateAsync();

        await WriteAsync(workspace, "implement", 1, ("a.cs", "// a"));

        string author = await GitCommandProbe.RunAsync(
            _root, ["log", "-1", "--format=%an <%ae>"], CancellationToken.None);

        author.Trim().ShouldBe("mandate <mandate@localhost>");
    }

    [Fact]
    public async Task Node_provenance_is_recorded_on_every_commit()
    {
        GitRunWorkspace workspace = await CreateAsync();

        await WriteAsync(workspace, "implement", 3, ("a.cs", "// a"));

        ImmutableArray<WorkspaceCommit> commits =
            await workspace.CommitsForAsync(Node("implement"), CancellationToken.None);

        commits.ShouldHaveSingleItem();
        commits[0].Attempt.ShouldBe(3);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            try
            {
                SetWritable(_root);
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // A lock on a temp directory is not worth failing a test over.
            }
        }
    }

    private static void SetWritable(string root)
    {
        foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
    }
}
