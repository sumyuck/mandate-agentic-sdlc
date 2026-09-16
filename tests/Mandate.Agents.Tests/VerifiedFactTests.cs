using System.Collections.Immutable;
using Mandate.Agents.Model;
using Mandate.Agents.Tests.Support;
using Mandate.Core.Execution;
using Mandate.Core.Llm;
using Mandate.Core.Workflow;
using Mandate.Llm.Pricing;
using Mandate.Llm.Prompts;
using Mandate.Workflows;

namespace Mandate.Agents.Tests;

/// <summary>
/// The lifecycle's own words: the testing gate "depends on the recorded result of an actual
/// test run, never on an agent's assertion that the code works". These tests hold the code
/// to that sentence.
/// </summary>
public sealed class VerifiedFactTests
{
    private static WorkflowGraph Graph => WorkflowYamlLoader.LoadGraph(
        Path.Combine(RepositoryRoot.Path, "workflows", "sdlc.v1.yaml"));

    private static PromptLibrary Prompts => PromptLibrary.Load(
        Path.Combine(RepositoryRoot.Path, "prompts"));

    private static ModelPriceBook Prices => ModelPriceBook.Load(
        Path.Combine(RepositoryRoot.Path, "config", "model-pricing.yaml"));

    private const string ImplementClaiming = """
        {
          "summary": "Wrote the endpoint.",
          "documents": [ { "kind": "source-patch", "path": "src/Links.cs",
                           "content": "namespace Service; public static class Links { }" } ],
          "facts": { "implementation.builds": "true",
                     "implementation.commit": "Add links endpoint",
                     "implementation.files-changed": "1" },
          "decisions": []
        }
        """;

    private static string TestClaiming(string coverage, string failures) => $$"""
        {
          "summary": "Wrote tests.",
          "documents": [
            { "kind": "test-suite", "path": "tests/Service.Tests/LinksTests.cs", "content": "// tests" },
            { "kind": "test-report", "path": null, "content": "Covered the happy path." }
          ],
          "facts": { "test.coverage": "{{coverage}}", "test.failures": "{{failures}}" },
          "decisions": []
        }
        """;

    private static async Task<StageResult> RunAsync(
        string nodeId, string agentId, string answer, IWorkspaceVerifier verifier)
    {
        WorkflowNode node = Graph.Node(Core.Identifiers.NodeId.Parse(nodeId));

        ModelStageAgent agent = new(
            agentId, Prompts.Get(agentId), new CannedLlmClient(answer), Prices,
            new TestClock(), verifier);

        return await agent.ExecuteAsync(Executions.For(node), CancellationToken.None);
    }

    [Fact]
    public async Task A_measured_build_failure_replaces_the_stages_claim_that_it_builds()
    {
        StageResult result = await RunAsync(
            "implement", "implementer", ImplementClaiming,
            FakeVerifier.Build(succeeded: false, summary: "error CS1002: ; expected"));

        result.Succeeded.ShouldBeFalse();
        result.Failure!.ShouldContain("reported 'implementation.builds' as true");
        result.Failure!.ShouldContain("CS1002");
    }

    [Fact]
    public async Task A_measured_build_success_is_recorded_as_the_fact()
    {
        StageResult result = await RunAsync(
            "implement", "implementer", ImplementClaiming,
            FakeVerifier.Build(succeeded: true, summary: "The tree compiles."));

        result.Succeeded.ShouldBeTrue(result.Failure);
        result.Facts.Single(fact => fact.Key == "implementation.builds").Value.ShouldBe("true");
    }

    [Fact]
    public async Task Measured_coverage_replaces_the_stages_estimate()
    {
        // Claimed 0.80, measured 0.7345. Within tolerance, so not an overclaim — but the
        // number that reaches the gate has to be the measured one either way.
        StageResult result = await RunAsync(
            "test", "test-engineer", TestClaiming("0.80", "0"),
            FakeVerifier.Test(passed: 9, failed: 0, coverage: 0.7345));

        result.Succeeded.ShouldBeTrue(result.Failure);
        result.Facts.Single(fact => fact.Key == "test.coverage").Value.ShouldBe("0.7345");
    }

    [Fact]
    public async Task A_wildly_optimistic_coverage_claim_fails_the_stage()
    {
        StageResult result = await RunAsync(
            "test", "test-engineer", TestClaiming("0.95", "0"),
            FakeVerifier.Test(passed: 2, failed: 0, coverage: 0.31));

        result.Succeeded.ShouldBeFalse();
        result.Failure!.ShouldContain("test.coverage");
        result.Failure!.ShouldContain("0.31");
    }

    [Fact]
    public async Task Claiming_no_failures_when_tests_failed_fails_the_stage()
    {
        StageResult result = await RunAsync(
            "test", "test-engineer", TestClaiming("0.80", "0"),
            FakeVerifier.Test(passed: 5, failed: 3, coverage: 0.80));

        result.Succeeded.ShouldBeFalse();
        result.Failure!.ShouldContain("3 test(s) failed");
    }

    [Fact]
    public async Task Failures_the_stage_honestly_predicted_are_recorded_not_punished()
    {
        // The stage said tests would fail and they did. That is a failing change, which the
        // gate will reject — but it is not a false report, and the two must not be conflated.
        StageResult result = await RunAsync(
            "test", "test-engineer", TestClaiming("0.80", "2"),
            FakeVerifier.Test(passed: 5, failed: 2, coverage: 0.82));

        result.Succeeded.ShouldBeTrue(result.Failure);
        result.Facts.Single(fact => fact.Key == "test.failures").Value.ShouldBe("2");
    }

    [Fact]
    public async Task A_verifier_that_could_not_run_fails_the_stage_rather_than_trusting_the_claim()
    {
        // The quiet failure this mechanism exists to prevent: verification switched on,
        // toolchain absent, and the gate passing on a number nobody measured.
        StageResult result = await RunAsync(
            "implement", "implementer", ImplementClaiming,
            FakeVerifier.NotRun("no .NET SDK on the PATH"));

        result.Succeeded.ShouldBeFalse();
        result.Failure!.ShouldContain("Verification did not run");
    }

    [Fact]
    public async Task A_verifier_that_ran_but_measured_no_coverage_fails_the_stage()
    {
        StageResult result = await RunAsync(
            "test", "test-engineer", TestClaiming("0.80", "0"),
            FakeVerifier.Test(passed: 4, failed: 0, coverage: null));

        result.Succeeded.ShouldBeFalse();
        result.Failure!.ShouldContain("no figure for test.coverage");
    }

    [Fact]
    public async Task With_no_verifier_the_claim_stands_and_that_is_the_unverified_mode()
    {
        StageResult result = await RunAsync(
            "test", "test-engineer", TestClaiming("0.90", "0"), IWorkspaceVerifier.Disabled);

        result.Succeeded.ShouldBeTrue(result.Failure);
        result.Facts.Single(fact => fact.Key == "test.coverage").Value.ShouldBe("0.90");
    }

    [Fact]
    public async Task A_stage_that_declares_no_verifiable_fact_is_never_verified()
    {
        // Intake produces a document and a boolean. Running a build for it would cost forty
        // seconds to measure nothing.
        FakeVerifier verifier = FakeVerifier.Build(succeeded: true, summary: "n/a");

        await RunAsync("intake", "intake", """
            {
              "summary": "Recorded.",
              "documents": [ { "kind": "request", "path": "docs/request.md", "content": "r" } ],
              "facts": { "intake.recorded": "true" },
              "decisions": []
            }
            """, verifier);

        verifier.Invocations.ShouldBe(0);
    }

    [Fact]
    public async Task The_implementation_stage_is_verified_by_building_not_by_testing()
    {
        FakeVerifier verifier = FakeVerifier.Build(succeeded: true, summary: "compiles");

        await RunAsync("implement", "implementer", ImplementClaiming, verifier);

        verifier.LastKind.ShouldBe(VerificationKind.Build);
    }

    [Fact]
    public async Task The_verifier_sees_the_files_the_stage_proposed()
    {
        // Verification happens before the engine commits, so the sandbox is the tree as it
        // *would* be. Checking the tree without the change would measure the wrong thing.
        FakeVerifier verifier = FakeVerifier.Build(succeeded: true, summary: "compiles");

        await RunAsync("implement", "implementer", ImplementClaiming, verifier);

        verifier.LastProposed.Select(file => file.RelativePath).ShouldContain("src/Links.cs");
    }

    private sealed class CannedLlmClient(string answer) : ILlmClient
    {
        public string Description => "canned";

        public Task<LlmResponse> CompleteAsync(
            LlmRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new LlmResponse(
                answer, request.Model, "end_turn", new LlmUsage(10, 10, 0, 0),
                LlmResponseSource.Stub));
    }

    private sealed class FakeVerifier(VerificationOutcome outcome) : IWorkspaceVerifier
    {
        public string Description => "fake";

        public bool IsAvailable => true;

        public int Invocations { get; private set; }

        public VerificationKind LastKind { get; private set; }

        public ImmutableArray<WorkspaceFile> LastProposed { get; private set; } = [];

        public Task<VerificationOutcome> VerifyAsync(
            IWorkspaceReader tree,
            IReadOnlyCollection<WorkspaceFile> proposed,
            VerificationKind kind,
            CancellationToken cancellationToken)
        {
            Invocations++;
            LastKind = kind;
            LastProposed = [.. proposed];
            return Task.FromResult(outcome);
        }

        public static FakeVerifier Build(bool succeeded, string summary) =>
            new(new VerificationOutcome(
                true, succeeded, summary, summary, null, null, null, 10));

        public static FakeVerifier Test(int passed, int failed, double? coverage) =>
            new(new VerificationOutcome(
                true, failed == 0, $"{passed} passed, {failed} failed", string.Empty,
                passed, failed, coverage, 20));

        public static FakeVerifier NotRun(string reason) =>
            new(VerificationOutcome.NotRun(reason));
    }
}
