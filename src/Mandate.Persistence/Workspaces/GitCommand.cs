using System.Diagnostics;
using System.Text;

namespace Mandate.Persistence.Workspaces;

/// <summary>Raised when a git command fails.</summary>
public sealed class GitCommandException : Exception
{
    /// <summary>Creates the exception for a failed invocation.</summary>
    public GitCommandException(string arguments, int exitCode, string error)
        : base($"git {arguments} failed with exit code {exitCode}: {error.Trim()}")
    {
        Arguments = arguments;
        ExitCode = exitCode;
    }

    /// <summary>Creates the exception with no detail. Present to satisfy the exception pattern.</summary>
    public GitCommandException()
        : base("A git command failed.") => Arguments = string.Empty;

    /// <summary>Creates the exception with a message.</summary>
    public GitCommandException(string message)
        : base(message) => Arguments = string.Empty;

    /// <summary>Creates the exception with a message and an inner cause.</summary>
    public GitCommandException(string message, Exception innerException)
        : base(message, innerException) => Arguments = string.Empty;

    /// <summary>The arguments that were passed to git.</summary>
    public string Arguments { get; }

    /// <summary>The exit code git returned.</summary>
    public int ExitCode { get; }
}

/// <summary>
/// Runs git as a child process.
/// </summary>
/// <remarks>
/// <para>
/// The git command line rather than a managed library: no native dependency to ship, the
/// resulting repository is an ordinary one a reviewer can open with their own tools, and the
/// operations used here — init, add, commit, revert, log — are exactly the ones anyone
/// checking the work already knows how to read.
/// </para>
/// <para>
/// Arguments are passed as a list, never interpolated into a shell string, so a file path or
/// commit message containing shell metacharacters cannot become a command.
/// </para>
/// </remarks>
internal static class GitCommand
{
    public static async Task<string> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // The run's commits are the engine's, not the developer's: a workspace commit must
        // not pick up whoever happens to be configured on the machine, or the audit trail
        // would attribute agent output to a person.
        startInfo.Environment["GIT_AUTHOR_NAME"] = "mandate";
        startInfo.Environment["GIT_AUTHOR_EMAIL"] = "mandate@localhost";
        startInfo.Environment["GIT_COMMITTER_NAME"] = "mandate";
        startInfo.Environment["GIT_COMMITTER_EMAIL"] = "mandate@localhost";
        startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";

        using Process process = new() { StartInfo = startInfo };

        StringBuilder output = new();
        StringBuilder error = new();

        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                output.AppendLine(args.Data);
            }
        };

        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                error.AppendLine(args.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            // Some git failures report on stdout rather than stderr, and an exception whose
            // message is just an exit code tells whoever hits it nothing.
            string detail = error.Length > 0 ? error.ToString() : output.ToString();

            throw new GitCommandException(
                string.Join(' ', arguments), process.ExitCode, detail);
        }

        return output.ToString();
    }

    /// <summary>True when git is available on this machine.</summary>
    public static async Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RunAsync(Path.GetTempPath(), ["--version"], cancellationToken)
                .ConfigureAwait(false);

            return true;
        }
        catch (Exception exception) when (exception is GitCommandException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}

/// <summary>A node's work could not be undone cleanly.</summary>
/// <remarks>
/// Distinct from a git command simply failing. This one says something specific and
/// actionable: the rollback ran, it could not complete, the tree is clean, and a human has
/// to decide what it should contain. The engine turns it into a failed compensation rather
/// than letting it end the run — a rollback that needs a person is a governance outcome,
/// not a crash.
/// </remarks>
public sealed class WorkspaceCompensationException : Exception
{
    /// <summary>Creates the exception.</summary>
    public WorkspaceCompensationException()
        : base("The node's work could not be undone cleanly.")
    {
    }

    /// <summary>Creates the exception.</summary>
    public WorkspaceCompensationException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public WorkspaceCompensationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
