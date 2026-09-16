using System.Collections.Immutable;
using Mandate.Core.Execution;
using Mandate.Persistence.Verification;
using Mandate.Persistence.Workspaces;

namespace Mandate.Persistence.Tests;

/// <summary>
/// These run the real .NET SDK against the real workspace template, which is the only way
/// to know the parsing works — a verifier tested against a canned TRX file proves that the
/// test author can write TRX, not that the toolchain produces it.
/// </summary>
/// <remarks>
/// They are slower than everything else in the suite, deliberately. The alternative is
/// discovering on the day of a demo that coverage has silently been null for a week.
/// </remarks>
[Trait("Category", "Toolchain")]
public sealed class DotnetWorkspaceVerifierTests
{
    private static string Template => Path.Combine(RepositoryRoot.Path, "templates", "service");

    private static IWorkspaceReader Tree => new FileWorkspaceReader(Template);

    private static DotnetWorkspaceVerifier Verifier =>
        new(timeout: TimeSpan.FromMinutes(5));

    [Fact]
    public async Task The_template_compiles()
    {
        VerificationOutcome outcome = await Verifier.VerifyAsync(
            Tree, [], VerificationKind.Build, CancellationToken.None);

        outcome.Executed.ShouldBeTrue(outcome.Summary);
        outcome.Succeeded.ShouldBeTrue(outcome.Output);
    }

    [Fact]
    public async Task The_templates_tests_run_and_coverage_is_collected()
    {
        // The seed test exists precisely so this is measurable. A tree whose test run finds
        // nothing cannot tell "the suite passed" apart from "the suite never ran", and a
        // coverage gate reading null fails for want of evidence rather than for cause.
        VerificationOutcome outcome = await Verifier.VerifyAsync(
            Tree, [], VerificationKind.Test, CancellationToken.None);

        outcome.Executed.ShouldBeTrue(outcome.Summary);
        outcome.Succeeded.ShouldBeTrue(outcome.Output);
        outcome.TestsPassed.ShouldNotBeNull();
        outcome.TestsPassed!.Value.ShouldBeGreaterThan(0);
        outcome.TestsFailed.ShouldBe(0);
        outcome.LineCoverage.ShouldNotBeNull(outcome.Output);
        outcome.LineCoverage!.Value.ShouldBeInRange(0d, 1d);
    }

    [Fact]
    public async Task A_proposed_file_that_does_not_compile_is_caught()
    {
        // The case the whole mechanism exists for: a stage proposing code that does not
        // build must not be able to report that the tree builds.
        ImmutableArray<WorkspaceFile> broken =
            [new WorkspaceFile("Broken.cs", "this is not C#")];

        VerificationOutcome outcome = await Verifier.VerifyAsync(
            Tree, broken, VerificationKind.Build, CancellationToken.None);

        outcome.Executed.ShouldBeTrue(outcome.Summary);
        outcome.Succeeded.ShouldBeFalse();
        outcome.Summary.ShouldContain("does not compile");
    }

    [Fact]
    public async Task A_proposed_test_that_fails_is_counted_as_a_failure()
    {
        ImmutableArray<WorkspaceFile> failing =
        [
            new WorkspaceFile(
                "tests/Service.Tests/FailingTests.cs",
                """
                namespace Service.Tests;

                public sealed class FailingTests
                {
                    [Fact]
                    public void This_one_is_meant_to_fail() => Assert.Equal(1, 2);
                }
                """),
        ];

        VerificationOutcome outcome = await Verifier.VerifyAsync(
            Tree, failing, VerificationKind.Test, CancellationToken.None);

        outcome.Executed.ShouldBeTrue(outcome.Summary);
        outcome.Succeeded.ShouldBeFalse();
        outcome.TestsFailed.ShouldBe(1);
    }

    [Fact]
    public async Task A_proposed_file_never_touches_the_real_template()
    {
        await Verifier.VerifyAsync(
            Tree,
            [new WorkspaceFile("Scratch.cs", "// should not survive")],
            VerificationKind.Build,
            CancellationToken.None);

        // Verification happens in a copy. If it did not, running the test suite would
        // slowly fill the repository's own template with other people's experiments.
        File.Exists(Path.Combine(Template, "Scratch.cs")).ShouldBeFalse();
        Directory.Exists(Path.Combine(Template, "obj")).ShouldBeFalse();
    }

    [Fact]
    public async Task An_empty_tree_reports_that_there_was_nothing_to_verify()
    {
        VerificationOutcome outcome = await Verifier.VerifyAsync(
            IWorkspaceReader.Empty, [], VerificationKind.Build, CancellationToken.None);

        outcome.Executed.ShouldBeFalse();
        outcome.Summary.ShouldContain("nothing to verify");
    }

    [Fact]
    public async Task A_missing_toolchain_reports_not_run_rather_than_a_failed_build()
    {
        // "The compiler said no" and "there was no compiler" call for different responses
        // from whoever reads the run, so they must not arrive as the same outcome.
        DotnetWorkspaceVerifier absent = new(dotnetPath: "dotnet-that-is-not-installed");

        VerificationOutcome outcome = await absent.VerifyAsync(
            Tree, [], VerificationKind.Build, CancellationToken.None);

        outcome.Executed.ShouldBeFalse();
        outcome.Succeeded.ShouldBeFalse();
    }

    [Fact]
    public void The_disabled_verifier_says_it_is_disabled()
    {
        IWorkspaceVerifier.Disabled.IsAvailable.ShouldBeFalse();
        IWorkspaceVerifier.Disabled.Description.ShouldContain("unverified");
    }
}
