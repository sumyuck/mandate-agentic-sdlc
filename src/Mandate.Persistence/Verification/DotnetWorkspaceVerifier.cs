using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Mandate.Core.Execution;

namespace Mandate.Persistence.Verification;

/// <summary>
/// Runs the real .NET SDK over a tree: <c>dotnet build</c>, then <c>dotnet test</c>.
/// </summary>
/// <remarks>
/// <para>
/// Counts come from the test run's own TRX report and coverage from the Cobertura file the
/// collector writes, not from scraping console text. Console output is formatted for people
/// and changes between SDK releases; a coverage number parsed out of it would drift silently
/// and nobody would notice until a gate started passing everything.
/// </para>
/// <para>
/// Results are written to a directory outside the tree. A verification run that left
/// <c>bin</c>, <c>obj</c> and a TRX file behind would dirty the run's workspace, and the
/// next stage's commit would carry build output as though an agent had authored it.
/// </para>
/// </remarks>
public sealed partial class DotnetWorkspaceVerifier : IWorkspaceVerifier
{
    /// <summary>How long a single toolchain invocation may take.</summary>
    public static TimeSpan DefaultTimeout { get; } = TimeSpan.FromMinutes(5);

    private readonly string _dotnet;
    private readonly TimeSpan _timeout;

    /// <summary>Creates a verifier.</summary>
    /// <param name="dotnetPath">The SDK to invoke. Defaults to whatever is on the PATH.</param>
    /// <param name="timeout">Ceiling on one invocation.</param>
    public DotnetWorkspaceVerifier(string dotnetPath = "dotnet", TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dotnetPath);

        _dotnet = dotnetPath;
        _timeout = timeout ?? DefaultTimeout;
    }

    /// <inheritdoc />
    public string Description => $"real toolchain ({_dotnet} build, {_dotnet} test)";

    /// <inheritdoc />
    public bool IsAvailable => true;

    /// <inheritdoc />
    public async Task<VerificationOutcome> VerifyAsync(
        IWorkspaceReader tree,
        IReadOnlyCollection<WorkspaceFile> proposed,
        VerificationKind kind,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(proposed);

        string sandbox = Path.Combine(
            Path.GetTempPath(), "mandate-sandbox-" + Guid.NewGuid().ToString("N")[..12]);

        try
        {
            int materialised = Materialise(tree, proposed, sandbox);

            if (materialised == 0)
            {
                return VerificationOutcome.NotRun(
                    "There was nothing to verify: the tree is empty and the stage proposed "
                    + "no files.");
            }

            VerificationOutcome outcome = kind switch
            {
                VerificationKind.Build => await BuildAsync(sandbox, cancellationToken)
                    .ConfigureAwait(false),
                VerificationKind.Test => await TestAsync(sandbox, cancellationToken)
                    .ConfigureAwait(false),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(kind), kind, "Not a verification kind."),
            };

            // The sandbox lives at a path with a fresh random id in it. That path appears in
            // every compiler diagnostic, and a retry that shows the stage its own errors
            // would then carry a value that changes on every run — the prompt would never
            // match a recording again. Stripped to workspace-relative, which is the form a
            // reader wants anyway.
            return Relativise(outcome, sandbox);
        }
        finally
        {
            TryDelete(sandbox);
        }
    }

    /// <summary>
    /// Writes the tree plus the proposed files into a scratch directory.
    /// </summary>
    /// <remarks>
    /// Built from what the stage could <em>see</em>, which is deliberate rather than a
    /// limitation of the reader. A stage cannot pass a build that depended on a file it was
    /// never shown, and a binary the reader withheld is not something a .NET build of
    /// source and configuration needs.
    /// </remarks>
    private static int Materialise(
        IWorkspaceReader tree, IReadOnlyCollection<WorkspaceFile> proposed, string sandbox)
    {
        Dictionary<string, string> files = new(StringComparer.Ordinal);

        foreach (string path in tree.Files)
        {
            if (tree.TryRead(path) is { } content)
            {
                files[path] = content;
            }
        }

        // The stage's proposal overlays the tree, because that is the state the engine is
        // about to commit — verifying the tree without it would check the wrong thing.
        foreach (WorkspaceFile file in proposed)
        {
            files[file.RelativePath] = file.Content;
        }

        foreach ((string path, string content) in files)
        {
            string target = Path.Combine(sandbox, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, content);
        }

        return files.Count;
    }

    private async Task<VerificationOutcome> BuildAsync(
        string directory, CancellationToken cancellationToken)
    {
        long startedTicks = Stopwatch.GetTimestamp();
        ImmutableArray<string> projects = ProjectsIn(directory);

        if (projects.IsEmpty)
        {
            return VerificationOutcome.NotRun("The tree contains no project to build.");
        }

        foreach (string project in projects)
        {
            ProcessOutcome build = await RunAsync(
                directory,
                ["build", project, "--nologo", "-v", "quiet", "-p:TreatWarningsAsErrors=false"],
                cancellationToken).ConfigureAwait(false);

            if (!build.Started)
            {
                return VerificationOutcome.NotRun($"Could not run '{_dotnet} build': {build.Output}");
            }

            if (build.ExitCode != 0)
            {
                return new VerificationOutcome(
                    Executed: true,
                    Succeeded: false,
                    Summary: $"The tree does not compile ({FirstError(build.Output)}).",
                    Output: build.Output,
                    TestsPassed: null,
                    TestsFailed: null,
                    LineCoverage: null,
                    DurationMilliseconds: Elapsed(startedTicks));
            }
        }

        return new VerificationOutcome(
            Executed: true,
            Succeeded: true,
            Summary: $"The tree compiles ({projects.Length} project(s)).",
            Output: string.Empty,
            TestsPassed: null,
            TestsFailed: null,
            LineCoverage: null,
            DurationMilliseconds: Elapsed(startedTicks));
    }

    private async Task<VerificationOutcome> TestAsync(
        string directory, CancellationToken cancellationToken)
    {
        long startedTicks = Stopwatch.GetTimestamp();

        string results = Path.Combine(
            Path.GetTempPath(), "mandate-verify-" + Guid.NewGuid().ToString("N")[..12]);

        Directory.CreateDirectory(results);

        try
        {
            ImmutableArray<string> projects = TestProjectsIn(directory);

            // No test project is a measured fact, not an excuse to skip the measurement.
            // Nothing failed because nothing ran, and coverage is zero because nothing was
            // covered — which is exactly what the coverage gate should then reject.
            if (projects.IsEmpty)
            {
                return new VerificationOutcome(
                    Executed: true,
                    Succeeded: false,
                    Summary: "The tree contains no test project, so nothing was tested.",
                    Output: string.Empty,
                    TestsPassed: 0,
                    TestsFailed: 0,
                    LineCoverage: 0d,
                    DurationMilliseconds: Elapsed(startedTicks));
            }

            StringBuilder combined = new();
            bool allExited = true;

            foreach (string project in projects)
            {
                ProcessOutcome test = await RunAsync(
                    directory,
                    [
                        "test",
                        project,
                        "--nologo",
                        "-v", "quiet",
                        "-p:TreatWarningsAsErrors=false",
                        "--logger", "trx",
                        "--collect:XPlat Code Coverage",
                        "--results-directory", results,
                    ],
                    cancellationToken).ConfigureAwait(false);

                if (!test.Started)
                {
                    return VerificationOutcome.NotRun(
                        $"Could not run '{_dotnet} test': {test.Output}");
                }

                combined.AppendLine(test.Output);
                allExited &= test.ExitCode == 0;
            }

            (int? passed, int? failed) = ReadCounts(results);
            double? coverage = ReadCoverage(results);

            // A non-zero exit with no counts means the run never reached the point of
            // executing tests — usually a compile error in the test project. Reported as
            // such rather than as "0 failed", which would read as a pass.
            if (passed is null && failed is null)
            {
                return new VerificationOutcome(
                    Executed: true,
                    Succeeded: false,
                    Summary: $"No tests ran ({FirstError(combined.ToString())}).",
                    Output: combined.ToString(),
                    TestsPassed: null,
                    TestsFailed: null,
                    LineCoverage: null,
                    DurationMilliseconds: Elapsed(startedTicks));
            }

            return new VerificationOutcome(
                Executed: true,
                Succeeded: allExited && (failed ?? 0) == 0,
                Summary: Describe(passed, failed, coverage),
                Output: combined.ToString(),
                TestsPassed: passed,
                TestsFailed: failed,
                LineCoverage: coverage,
                DurationMilliseconds: Elapsed(startedTicks));
        }
        finally
        {
            TryDelete(results);
        }
    }

    /// <summary>Rewrites absolute sandbox paths in the output as workspace-relative ones.</summary>
    private static VerificationOutcome Relativise(VerificationOutcome outcome, string sandbox)
    {
        string resolved = Path.GetFullPath(sandbox);

        // The /private variants go first. macOS reports /private/var where the path was
        // handed over as /var, so stripping the short form first leaves the "/private"
        // prefix stranded against the next path segment — the first attempt at this
        // produced "/privatetests/Service.Tests/..." and fed it straight into a retry's
        // prompt, telling the model its files lived somewhere they did not.
        string Strip(string text) => text
            .Replace("/private" + resolved + Path.DirectorySeparatorChar, string.Empty, StringComparison.Ordinal)
            .Replace("/private" + resolved, string.Empty, StringComparison.Ordinal)
            .Replace(resolved + Path.DirectorySeparatorChar, string.Empty, StringComparison.Ordinal)
            .Replace(resolved, string.Empty, StringComparison.Ordinal);

        return outcome with
        {
            Summary = Stabilise(Strip(outcome.Summary)),
            Output = Stabilise(Strip(outcome.Output)),
        };
    }

    /// <summary>
    /// Removes the parts of the toolchain's output that differ between identical runs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This output is fed back into a retry's prompt so the stage can fix what it broke,
    /// which means it is part of the prompt — and a prompt that changes between two
    /// identical runs can never be replayed from a recording. Timings change on every run.
    /// So does the results directory, which carries a fresh id.
    /// </para>
    /// <para>
    /// Found the hard way: the first offline replay of a successful run diverged the moment
    /// it reached a retried stage, because the retry asked a question whose wording included
    /// how many milliseconds the previous test run had taken.
    /// </para>
    /// </remarks>
    private static string Stabilise(string text)
    {
        string temp = Path.GetFullPath(Path.GetTempPath());

        string stabilised = text
            .Replace("/private" + temp, string.Empty, StringComparison.Ordinal)
            .Replace(temp, string.Empty, StringComparison.Ordinal);

        stabilised = Milliseconds().Replace(stabilised, "<ms>");
        stabilised = Duration().Replace(stabilised, "Duration: <duration>");
        stabilised = Elapsed().Replace(stabilised, "Time Elapsed <elapsed>");
        stabilised = SandboxId().Replace(stabilised, "<temp>");

        // The TRX file is named for the machine and the wall clock, the coverage
        // attachment for a fresh guid, and every xunit console line is stamped with how
        // long the run had been going. None of it tells a retry anything; all of it would
        // make the retry's prompt different on every run.
        stabilised = TrxName().Replace(stabilised, "<trx>");
        stabilised = Identifier().Replace(stabilised, "<id>");
        stabilised = ConsoleStamp().Replace(stabilised, "[xunit]");

        return stabilised;
    }

    [GeneratedRegex(@"\b\d+(\.\d+)?\s*ms\b", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Milliseconds();

    [GeneratedRegex(@"Duration:\s*[^\r\n,\]]+", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Duration();

    [GeneratedRegex(@"Time Elapsed\s*[\d:.]+", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Elapsed();

    [GeneratedRegex(@"mandate-(sandbox|verify)-[0-9a-f]+", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SandboxId();

    [GeneratedRegex(
        @"_[^/\\\s]+_\d{4}-\d{2}-\d{2}_\d{2}_\d{2}_\d{2}[^/\\\s]*\.trx",
        RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TrxName();

    [GeneratedRegex(
        @"\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b",
        RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Identifier();

    [GeneratedRegex(
        @"\[xUnit\.net \d{2}:\d{2}:\d{2}\.\d{2}\]",
        RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ConsoleStamp();

    private static long Elapsed(long startedTicks) =>
        (long)Stopwatch.GetElapsedTime(startedTicks).TotalMilliseconds;

    /// <summary>
    /// Every project in the tree.
    /// </summary>
    /// <remarks>
    /// Discovered rather than relying on a solution file, because <c>dotnet test</c> in a
    /// directory holding several projects and no solution does nothing at all and exits
    /// zero — a silent pass, which is the worst answer a verifier can give.
    /// </remarks>
    private static ImmutableArray<string> ProjectsIn(string directory) =>
    [
        .. Directory
            .EnumerateFiles(directory, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(directory, path))
            .Order(StringComparer.Ordinal),
    ];

    /// <summary>The projects that hold tests.</summary>
    private static ImmutableArray<string> TestProjectsIn(string directory) =>
    [
        .. ProjectsIn(directory).Where(IsTestProject),
    ];

    private static bool IsTestProject(string path)
    {
        try
        {
            string text = File.ReadAllText(path);

            return text.Contains("<IsTestProject>true", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsBuildOutput(string root, string path)
    {
        string relative = Path.GetRelativePath(root, path).Replace('\\', '/');

        return relative.StartsWith("bin/", StringComparison.Ordinal)
               || relative.StartsWith("obj/", StringComparison.Ordinal)
               || relative.Contains("/bin/", StringComparison.Ordinal)
               || relative.Contains("/obj/", StringComparison.Ordinal);
    }

    private static string Describe(int? passed, int? failed, double? coverage)
    {
        StringBuilder summary = new();

        summary.Append(CultureInfo.InvariantCulture, $"{passed ?? 0} passed");
        summary.Append(CultureInfo.InvariantCulture, $", {failed ?? 0} failed");

        summary.Append(coverage is { } rate
            ? string.Create(CultureInfo.InvariantCulture, $", {rate:P1} line coverage.")
            : ", coverage not collected.");

        return summary.ToString();
    }

    /// <summary>Reads the pass and fail counts out of the TRX report.</summary>
    private static (int? Passed, int? Failed) ReadCounts(string resultsDirectory)
    {
        int? passed = null;
        int? failed = null;

        // Summed across every report, because a tree may hold more than one test project
        // and reading only the first would report a fraction of the suite as the whole.
        foreach (string trx in Directory
            .EnumerateFiles(resultsDirectory, "*.trx", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal))
        {
            try
            {
                XElement? counters = XDocument.Load(trx)
                    .Descendants()
                    .FirstOrDefault(element =>
                        string.Equals(element.Name.LocalName, "Counters", StringComparison.Ordinal));

                if (counters is null)
                {
                    continue;
                }

                passed = (passed ?? 0) + (Attribute(counters, "passed") ?? 0);
                failed = (failed ?? 0) + (Attribute(counters, "failed") ?? 0);
            }
            catch (Exception exception) when (exception is IOException or System.Xml.XmlException)
            {
                // An unreadable report contributes nothing. If every report is unreadable
                // the counts stay null, which the caller treats as "no tests ran".
            }
        }

        return (passed, failed);
    }

    /// <summary>Reads overall line coverage out of the Cobertura report.</summary>
    /// <remarks>
    /// The rate on the root element, which is the whole run's. Per-package rates further
    /// down would answer a different question, and taking the first one found in the file
    /// would silently become a per-package figure the day a second package appears.
    /// </remarks>
    private static double? ReadCoverage(string resultsDirectory)
    {
        int covered = 0;
        int total = 0;
        bool found = false;

        // Summed from line counts rather than averaging the per-report rates: averaging
        // two rates gives a project with six lines the same weight as one with six hundred.
        foreach (string cobertura in Directory
            .EnumerateFiles(resultsDirectory, "coverage.cobertura.xml", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal))
        {
            try
            {
                XElement? root = XDocument.Load(cobertura).Root;

                if (root is null)
                {
                    continue;
                }

                int? lines = Attribute(root, "lines-valid");
                int? hit = Attribute(root, "lines-covered");

                if (lines is null || hit is null)
                {
                    continue;
                }

                covered += hit.Value;
                total += lines.Value;
                found = true;
            }
            catch (Exception exception) when (exception is IOException or System.Xml.XmlException)
            {
                // Same reasoning as the counters: an unreadable report contributes nothing.
            }
        }

        return !found ? null
            : total == 0 ? 0d
            : Math.Clamp((double)covered / total, 0d, 1d);
    }

    private static int? Attribute(XElement element, string name) =>
        element.Attribute(name) is { } attribute
        && int.TryParse(attribute.Value, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : null;

    /// <summary>The first line that looks like a compiler or test error, for the summary.</summary>
    private static string FirstError(string output)
    {
        string? line = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(candidate =>
                candidate.Contains(": error", StringComparison.OrdinalIgnoreCase));

        line ??= output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();

        line = (line ?? "no output").Trim();
        return line.Length <= 200 ? line : line[..197] + "...";
    }

    private static void TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // A results directory left in the temp folder is not worth failing a stage over.
        }
    }

    private readonly record struct ProcessOutcome(bool Started, int ExitCode, string Output);

    private async Task<ProcessOutcome> RunAsync(
        string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = _dotnet,
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

        // Keep the toolchain quiet and predictable. Telemetry and the first-run banner are
        // noise in captured evidence, and a colourised diagnostic is harder to read back.
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        startInfo.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        startInfo.Environment["TERM"] = "dumb";

        using Process process = new() { StartInfo = startInfo };

        StringBuilder output = new();

        process.OutputDataReceived += (_, args) => Append(output, args.Data);
        process.ErrorDataReceived += (_, args) => Append(output, args.Data);

        try
        {
            if (!process.Start())
            {
                return new ProcessOutcome(false, -1, $"'{_dotnet}' could not be started.");
            }
        }
        catch (Exception exception) when (
            exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No SDK on the PATH. The caller reports this as "not run" rather than as a
            // failed build: a missing toolchain is an operator problem, not a code problem.
            return new ProcessOutcome(false, -1, exception.Message);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using CancellationTokenSource timeout = new(_timeout);
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);

            return new ProcessOutcome(
                true,
                -1,
                output + $"\nThe command exceeded its {_timeout.TotalMinutes:0.##} minute limit.");
        }

        return new ProcessOutcome(true, process.ExitCode, output.ToString());
    }

    private static void Append(StringBuilder output, string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (output)
        {
            // Bounded: a build that fails in a loop can emit megabytes, and none of it after
            // the first screenful helps anyone.
            if (output.Length < 64 * 1024)
            {
                output.AppendLine(line);
            }
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or NotSupportedException
                or System.ComponentModel.Win32Exception)
        {
            // Already gone, which is the outcome we wanted.
        }
    }
}
