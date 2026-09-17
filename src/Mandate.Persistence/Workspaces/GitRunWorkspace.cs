using System.Collections.Immutable;
using System.Globalization;
using Mandate.Core.Execution;
using Mandate.Core.Identifiers;

namespace Mandate.Persistence.Workspaces;

/// <summary>
/// A git-backed workspace: one commit per node, and rollback that really reverts.
/// </summary>
/// <remarks>
/// <para>
/// Each node's output is committed under a trailer naming the node and attempt, so every
/// generated line has provenance down to the stage that produced it, and a node's commits can
/// be found again without a separate index that could drift out of step with the history.
/// </para>
/// <para>
/// Compensation is <c>git revert</c>, not <c>git reset</c>. Reverting adds a commit that
/// undoes the change, leaving both the mistake and its correction in the history; resetting
/// would erase the evidence that the work ever happened. For a system whose purpose is an
/// auditable trail, destroying history to tidy up is the wrong instinct.
/// </para>
/// </remarks>
public sealed class GitRunWorkspace : IRunWorkspace, IDisposable
{
    // Stages run concurrently; a git repository has one index and one HEAD. Committing,
    // reverting and cleaning are therefore serialised here rather than left to race on
    // index.lock — the parallelism worth having is in the work the stages do, not in the
    // handful of milliseconds it takes to record the result.
    private readonly SemaphoreSlim _treeLock = new(1, 1);

    private const string NodeTrailer = "Mandate-Node";
    private const string AttemptTrailer = "Mandate-Attempt";

    // Separates fields in the log format. A vertical bar cannot occur in a commit SHA, a node
    // id (a lowercase slug) or an attempt number, so parsing stays unambiguous.
    private const char FieldSeparator = '|';

    // Marks the start of each commit's record, so the output can be split per commit rather
    // than per line - git emits a newline after every trailer value.
    private const string RecordMarker = "@@commit@@";

    private GitRunWorkspace(string root, RunId runId)
    {
        Root = root;
        RunId = runId;
    }

    /// <summary>The synthetic node the seed commit is attributed to.</summary>
    public const string SeedNode = "seed";

    /// <summary>Prefix marking a commit as a compensation for another node.</summary>
    public const string RevertNodePrefix = "revert-of-";

    /// <inheritdoc />
    public string Root { get; }

    /// <inheritdoc />
    public IWorkspaceReader Reader => field ??= new FileWorkspaceReader(Root);

    /// <summary>The run this workspace belongs to.</summary>
    public RunId RunId { get; }

    /// <summary>
    /// Creates a workspace for a run, seeded from a template.
    /// </summary>
    /// <param name="root">Directory to create the workspace in.</param>
    /// <param name="runId">The run the workspace belongs to.</param>
    /// <param name="templatePath">
    /// A directory to copy in as the starting tree, or <see langword="null"/> for an empty
    /// one. The seed becomes its own first commit, so it stays distinguishable from anything
    /// the run produced.
    /// </param>
    public static async Task<GitRunWorkspace> CreateAsync(
        string root, RunId runId, string? templatePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        Directory.CreateDirectory(root);

        await GitCommand.RunAsync(root, ["init", "--initial-branch", "main"], cancellationToken)
            .ConfigureAwait(false);

        if (templatePath is not null)
        {
            if (!Directory.Exists(templatePath))
            {
                throw new DirectoryNotFoundException(
                    $"Workspace template '{templatePath}' does not exist.");
            }

            CopyTree(templatePath, root);
        }

        await GitCommand.RunAsync(root, ["add", "-A"], cancellationToken).ConfigureAwait(false);

        // An initial commit even when the template is empty, so every later operation has a
        // parent and the run's first real commit is never the root commit.
        await GitCommand.RunAsync(
            root,
            [
                "commit", "--allow-empty", "-m",
                $"Seed workspace for {runId}\n\n{NodeTrailer}: {SeedNode}",
            ],
            cancellationToken).ConfigureAwait(false);

        return new GitRunWorkspace(root, runId);
    }

    /// <summary>Reopens an existing workspace.</summary>
    /// <exception cref="DirectoryNotFoundException">There is no repository at that path.</exception>
    public static GitRunWorkspace Open(string root, RunId runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        if (!Directory.Exists(Path.Combine(root, ".git")))
        {
            throw new DirectoryNotFoundException(
                $"There is no workspace repository at '{root}'.");
        }

        return new GitRunWorkspace(root, runId);
    }

    /// <inheritdoc />
    public async Task<WorkspaceCommit?> CommitAsync(
        NodeId nodeId,
        int attempt,
        IReadOnlyCollection<WorkspaceFile> files,
        string message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(files);

        await _treeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await CommitCoreAsync(nodeId, attempt, files, message, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _treeLock.Release();
        }
    }

    private async Task<WorkspaceCommit?> CommitCoreAsync(
        NodeId nodeId,
        int attempt,
        IReadOnlyCollection<WorkspaceFile> files,
        string message,
        CancellationToken cancellationToken)
    {
        foreach (WorkspaceFile file in files)
        {
            if (!WorkspaceFile.IsSafeRelativePath(file.RelativePath))
            {
                throw new InvalidOperationException(
                    $"Node '{nodeId}' proposed the path '{file.RelativePath}', which would write "
                    + "outside its workspace. Refusing: a stage may write into the run's tree "
                    + "and nowhere else.");
            }

            string destination = Path.Combine(
                Root, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            await File.WriteAllTextAsync(destination, file.Content, cancellationToken)
                .ConfigureAwait(false);
        }

        await GitCommand.RunAsync(Root, ["add", "-A"], cancellationToken).ConfigureAwait(false);

        string staged = await GitCommand
            .RunAsync(Root, ["diff", "--cached", "--name-only"], cancellationToken)
            .ConfigureAwait(false);

        int changed = staged
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Length;

        if (changed == 0)
        {
            // A stage that changed nothing gets no commit. An empty commit would make the
            // history claim work happened that did not.
            return null;
        }

        string body =
            $"{message}\n\n{NodeTrailer}: {nodeId}\n{AttemptTrailer}: "
            + attempt.ToString(CultureInfo.InvariantCulture);

        await GitCommand.RunAsync(Root, ["commit", "-m", body], cancellationToken)
            .ConfigureAwait(false);

        string sha = await HeadAsync(cancellationToken).ConfigureAwait(false);

        return new WorkspaceCommit(sha, nodeId, attempt, changed);
    }

    /// <inheritdoc />
    public async Task<int> RevertNodeAsync(NodeId nodeId, CancellationToken cancellationToken)
    {
        await _treeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await RevertNodeCoreAsync(nodeId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _treeLock.Release();
        }
    }

    /// <summary>
    /// Leaves the tree clean after a revert that could not be applied.
    /// </summary>
    /// <remarks>
    /// A conflicted revert leaves markers in the files and entries in the index. Left
    /// alone, the next stage's commit would carry them, and the run would go on building
    /// on a tree nobody authored.
    /// </remarks>
    private async Task AbandonRevertAsync(CancellationToken cancellationToken)
    {
        foreach (string[] arguments in new[]
        {
            new[] { "revert", "--abort" },
            ["reset", "--hard", "HEAD"],
            ["clean", "-fd"],
        })
        {
            try
            {
                await GitCommand.RunAsync(Root, arguments, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (GitCommandException)
            {
                // 'revert --abort' fails when there is no revert in progress, which is the
                // ordinary case for the reset and clean that follow it. The point is to end
                // with a clean tree, and the next command gets its turn either way.
            }
        }
    }

    private async Task<int> RevertNodeCoreAsync(NodeId nodeId, CancellationToken cancellationToken)
    {
        ImmutableArray<WorkspaceCommit> commits =
            await CommitsForAsync(nodeId, cancellationToken).ConfigureAwait(false);

        if (commits.IsEmpty)
        {
            return 0;
        }

        // Newest first: reverting an older commit before a newer one that builds on it would
        // conflict, and a rollback that needs conflict resolution is not a rollback.
        //
        // It can still conflict, because this node is not the only one that commits. A
        // sibling stage running in parallel may have touched the same file, and then a
        // clean revert of this node's work does not exist. That is a real outcome and not
        // an engine fault: the tree is returned to a clean state and the caller is told
        // how far the rollback got, rather than the run dying on an unhandled git error
        // with a half-applied revert in the index.
        int reverted = 0;

        foreach (WorkspaceCommit commit in commits.Reverse())
        {
            try
            {
                await GitCommand.RunAsync(
                    Root,
                    ["revert", "--no-edit", "--no-commit", commit.Sha],
                    cancellationToken).ConfigureAwait(false);
            }
            catch (GitCommandException exception)
            {
                await AbandonRevertAsync(cancellationToken).ConfigureAwait(false);

                throw new WorkspaceCompensationException(
                    $"'{nodeId}' could not be rolled back cleanly: reverting {commit.Sha[..8]} "
                    + $"conflicts with later work in the tree. {reverted} of {commits.Length} "
                    + "commit(s) were undone before it stopped, and the working tree has been "
                    + "returned to a clean state at the last good commit. A human must decide "
                    + $"what the tree should contain. {exception.Message}",
                    exception);
            }

            string shortSha = commit.Sha[..Math.Min(8, commit.Sha.Length)];

            await GitCommand.RunAsync(
                Root,
                [
                    "commit", "-m",
                    $"Revert {shortSha} from '{nodeId}'\n\n"
                    + "Compensating action. The change is undone, and both the change and its\n"
                    + "reversal remain in the history.\n\n"
                    + $"{NodeTrailer}: {RevertNodePrefix}{nodeId}",
                ],
                cancellationToken).ConfigureAwait(false);

            reverted++;
        }

        return reverted;
    }

    /// <inheritdoc />
    public async Task DiscardUncommittedAsync(CancellationToken cancellationToken)
    {
        await _treeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await GitCommand.RunAsync(Root, ["reset", "--hard", "HEAD"], cancellationToken)
                .ConfigureAwait(false);

            await GitCommand.RunAsync(Root, ["clean", "-fd"], cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _treeLock.Release();
        }
    }

    /// <summary>Releases the tree lock.</summary>
    public void Dispose() => _treeLock.Dispose();

    /// <inheritdoc />
    public async Task<WorkspaceStatus> StatusAsync(CancellationToken cancellationToken)
    {
        string head = await HeadAsync(cancellationToken).ConfigureAwait(false);

        string porcelain = await GitCommand
            .RunAsync(Root, ["status", "--porcelain"], cancellationToken)
            .ConfigureAwait(false);

        string count = await GitCommand
            .RunAsync(Root, ["rev-list", "--count", "HEAD"], cancellationToken)
            .ConfigureAwait(false);

        return new WorkspaceStatus(
            head,
            string.IsNullOrWhiteSpace(porcelain),
            int.Parse(count.Trim(), CultureInfo.InvariantCulture));
    }

    /// <inheritdoc />
    public async Task<ImmutableArray<WorkspaceCommit>> CommitsForAsync(
        NodeId nodeId, CancellationToken cancellationToken)
    {
        // Matched on the trailer rather than on message text, so a stage whose commit message
        // happens to mention another node cannot be mistaken for it.
        //
        // Each trailer value git emits carries its own trailing newline, so a commit's fields
        // do not arrive on one line. A record marker lets the output be split per commit and
        // the newlines inside a record discarded, which is stable regardless of how many
        // trailers a commit happens to have.
        string format =
            "--format=" + RecordMarker + "%H" + FieldSeparator
            + "%(trailers:key=" + NodeTrailer + ",valueonly)" + FieldSeparator
            + "%(trailers:key=" + AttemptTrailer + ",valueonly)";

        string log = await GitCommand
            .RunAsync(Root, ["log", "--reverse", format], cancellationToken)
            .ConfigureAwait(false);

        ImmutableArray<WorkspaceCommit>.Builder commits =
            ImmutableArray.CreateBuilder<WorkspaceCommit>();

        foreach (string record in log.Split(
                     RecordMarker, StringSplitOptions.RemoveEmptyEntries))
        {
            string flattened = string.Concat(
                record.Where(character => !char.IsControl(character) && character != '\r'));

            string[] fields = flattened.Split(FieldSeparator);

            if (fields.Length < 2
                || !string.Equals(fields[1].Trim(), nodeId.Value, StringComparison.Ordinal))
            {
                continue;
            }

            int attempt = fields.Length > 2
                          && int.TryParse(
                              fields[2].Trim(), CultureInfo.InvariantCulture, out int parsed)
                ? parsed
                : 0;

            commits.Add(new WorkspaceCommit(fields[0].Trim(), nodeId, attempt, 0));
        }

        return commits.ToImmutable();
    }

    private async Task<string> HeadAsync(CancellationToken cancellationToken) =>
        (await GitCommand.RunAsync(Root, ["rev-parse", "HEAD"], cancellationToken)
            .ConfigureAwait(false)).Trim();

    private static void CopyTree(string source, string destination)
    {
        foreach (string directory in Directory.GetDirectories(
                     source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, directory);

            if (IsGitInternal(relative))
            {
                continue;
            }

            Directory.CreateDirectory(Path.Combine(destination, relative));
        }

        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);

            if (IsGitInternal(relative))
            {
                continue;
            }

            string target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static bool IsGitInternal(string relativePath) =>
        relativePath.Split(Path.DirectorySeparatorChar, '/')
            .Any(segment => string.Equals(segment, ".git", StringComparison.OrdinalIgnoreCase));
}
