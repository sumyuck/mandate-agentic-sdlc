using System.Collections.Immutable;
using Mandate.Agents.Model;
using Mandate.Agents.Tests.Support;
using Mandate.Core.Artifacts;
using Mandate.Core.Context;
using Mandate.Core.Execution;
using Mandate.Core.Identifiers;
using Mandate.Core.Llm;
using Mandate.Core.Workflow;
using Mandate.Llm.Pricing;
using Mandate.Llm.Prompts;
using Mandate.Workflows;

namespace Mandate.Agents.Tests;

/// <summary>
/// The agent's job is to hold a model to the contract the workflow declares. These tests are
/// mostly about what it refuses, because a stage that accepts anything makes every gate
/// downstream of it decorative.
/// </summary>
public sealed class ModelStageAgentTests
{
    private static WorkflowGraph Graph => WorkflowYamlLoader.LoadGraph(
        Path.Combine(RepositoryRoot.Path, "workflows", "sdlc.v1.yaml"));

    private static PromptLibrary Prompts => PromptLibrary.Load(
        Path.Combine(RepositoryRoot.Path, "prompts"));

    private static ModelPriceBook Prices => ModelPriceBook.Load(
        Path.Combine(RepositoryRoot.Path, "config", "model-pricing.yaml"));

    private static StageExecution ExecutionFor(string nodeId) =>
        new(
            RunId.New(TestClock.DefaultStart, "abc123"),
            Graph.Node(NodeId.Parse(nodeId)),
            1,
            RunContext.Empty
                .Contribute(ContextFact.Create(
                    "run.request", "Build a URL shortener.", NodeId.Parse("run"),
                    Actor.Engine, TestClock.DefaultStart, []))
                .Contribute(ContextFact.Create(
                    "run.scenario", "greenfield", NodeId.Parse("run"),
                    Actor.Engine, TestClock.DefaultStart, []))
                .Contribute(ContextFact.Create(
                    "run.has-existing-code", "false", NodeId.Parse("run"),
                    Actor.Engine, TestClock.DefaultStart, [])),
            ImmutableArray<Artifact>.Empty,
            Actor.Agent("intake"),
            IWorkspaceReader.Empty);

    private static ModelStageAgent AgentFor(string agentId, Func<LlmRequest, string> responder) =>
        new(agentId, Prompts.Get(agentId), new ScriptedLlmClient(responder), Prices, new TestClock());

    private static async Task<StageResult> RunAsync(
        string nodeId, string agentId, string answer) =>
        await AgentFor(agentId, _ => answer).ExecuteAsync(
            ExecutionFor(nodeId), CancellationToken.None);

    private const string GoodIntake = """
        {
          "summary": "Recorded the request verbatim.",
          "documents": [ { "kind": "request", "path": "docs/request.md",
                           "content": "# Request\n\nBuild a URL shortener." } ],
          "facts": { "intake.recorded": "true" },
          "decisions": []
        }
        """;

    [Fact]
    public async Task A_contract_satisfying_answer_becomes_artifacts_facts_and_files()
    {
        StageResult result = await RunAsync("intake", "intake", GoodIntake);

        result.Succeeded.ShouldBeTrue(result.Failure);
        result.Artifacts.Single().Kind.ShouldBe(ArtifactKind.Request);
        result.Artifacts.Single().Name.ShouldBe("docs/request.md");
        result.Files.Single().RelativePath.ShouldBe("docs/request.md");
        result.Facts.Single().Key.ShouldBe("intake.recorded");

        // What the audit log records and what the tree contains are the same bytes.
        result.Files.Single().Content.ShouldBe("# Request\n\nBuild a URL shortener.");
    }

    [Fact]
    public async Task Every_call_is_reported_so_the_run_can_be_costed()
    {
        StageResult result = await RunAsync("intake", "intake", GoodIntake);

        ModelCall call = result.ModelCalls.Single();

        call.PromptId.ShouldBe("intake");
        call.PromptVersion.ShouldBe("v1");
        call.Source.ShouldBe(LlmResponseSource.Stub);
        call.CostNanoUsd.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_declared_output_the_model_did_not_produce_fails_the_stage()
    {
        // 'requirements' declares a requirement-spec and an ambiguity-report. Returning one
        // of them would otherwise reach the exit gate and fail there, several steps later
        // and with a worse message.
        StageResult result = await RunAsync("requirements", "requirements-analyst", """
            {
              "summary": "Analysed it.",
              "documents": [ { "kind": "requirement-spec", "path": "docs/requirements.md",
                               "content": "spec" } ],
              "facts": { "requirements.scope": "s", "requirements.ambiguity-score": "0.1",
                         "requirements.acceptance-criteria": "4" },
              "decisions": []
            }
            """);

        result.Succeeded.ShouldBeFalse();
        result.Failure!.ShouldContain("ambiguity-report");
    }

    [Fact]
    public async Task An_output_the_workflow_never_declared_fails_the_stage()
    {
        // The important direction. A missing output fails a gate anyway; an extra one is
        // never checked by anything, because gates are written against what a node declares.
        StageResult result = await RunAsync("intake", "intake", """
            {
              "summary": "Recorded, and designed it while I was there.",
              "documents": [
                { "kind": "request", "path": "docs/request.md", "content": "r" },
                { "kind": "design-doc", "path": "docs/design.md", "content": "d" }
              ],
              "facts": { "intake.recorded": "true" },
              "decisions": []
            }
            """);

        result.Succeeded.ShouldBeFalse();
        result.Failure!.ShouldContain("design-doc");
        result.Failure!.ShouldContain("does not declare");
    }

    [Fact]
    public async Task A_missing_declared_context_fact_fails_the_stage()
    {
        StageResult result = await RunAsync("intake", "intake", """
            {
              "summary": "Recorded.",
              "documents": [ { "kind": "request", "path": "docs/request.md", "content": "r" } ],
              "facts": {},
              "decisions": []
            }
            """);

        result.Succeeded.ShouldBeFalse();
        result.Failure!.ShouldContain("intake.recorded");
    }

    [Fact]
    public async Task An_undeclared_context_fact_fails_the_stage()
    {
        // A fact nothing validated could be branched on by a guard, routing the run down a
        // path no one reviewed.
        StageResult result = await RunAsync("intake", "intake", """
            {
              "summary": "Recorded.",
              "documents": [ { "kind": "request", "path": "docs/request.md", "content": "r" } ],
              "facts": { "intake.recorded": "true", "requirements.ambiguity-score": "0.0" },
              "decisions": []
            }
            """);

        result.Succeeded.ShouldBeFalse();
        result.Failure!.ShouldContain("requirements.ambiguity-score");
    }

    [Fact]
    public async Task A_path_outside_the_workspace_fails_the_stage()
    {
        StageResult result = await RunAsync("intake", "intake", """
            {
              "summary": "Recorded.",
              "documents": [ { "kind": "request", "path": "../../.ssh/authorized_keys",
                               "content": "r" } ],
              "facts": { "intake.recorded": "true" },
              "decisions": []
            }
            """);

        result.Succeeded.ShouldBeFalse();
        result.Failure!.ShouldContain("not a path this stage may write");
    }

    [Fact]
    public async Task A_decision_with_one_option_fails_the_stage()
    {
        // The domain refuses it, and the agent lets that refusal be the stage's failure
        // rather than swallowing it. A "decision" with nothing rejected is a statement.
        StageResult result = await RunAsync("intake", "intake", """
            {
              "summary": "Recorded.",
              "documents": [ { "kind": "request", "path": "docs/request.md", "content": "r" } ],
              "facts": { "intake.recorded": "true" },
              "decisions": [ { "id": "x", "question": "q", "chosen": "only",
                               "options": [ { "name": "only", "summary": "s" } ],
                               "rationale": "because", "confidence": 0.9 } ]
            }
            """);

        result.Succeeded.ShouldBeFalse();
    }

    [Fact]
    public async Task A_truncated_answer_is_a_failure_that_names_the_prompt_to_widen()
    {
        ModelStageAgent agent = new(
            "intake",
            Prompts.Get("intake"),
            new ScriptedLlmClient(_ => GoodIntake, stopReason: "max_tokens"),
            Prices,
            new TestClock());

        StageResult result = await agent.ExecuteAsync(
            ExecutionFor("intake"), CancellationToken.None);

        result.Succeeded.ShouldBeFalse();
        result.Failure!.ShouldContain("intake.v1");
        result.ModelCalls.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task A_failed_model_call_is_a_stage_failure_not_an_escaping_exception()
    {
        ModelStageAgent agent = new(
            "intake",
            Prompts.Get("intake"),
            new ScriptedLlmClient(_ => throw new LlmException("the provider is down")),
            Prices,
            new TestClock());

        StageResult result = await agent.ExecuteAsync(
            ExecutionFor("intake"), CancellationToken.None);

        result.Succeeded.ShouldBeFalse();
        result.Failure!.ShouldContain("the provider is down");
    }

    [Fact]
    public async Task The_prompt_never_carries_the_run_identifier()
    {
        // If it did, every run would ask a question no cassette has answered and replay
        // would be a permanent miss. The prompt library refuses run ids outright, so this
        // asserts the agent does not hand it one in the first place.
        LlmRequest? sent = null;

        ModelStageAgent agent = new(
            "intake",
            Prompts.Get("intake"),
            new ScriptedLlmClient(request =>
            {
                sent = request;
                return GoodIntake;
            }),
            Prices,
            new TestClock());

        StageExecution execution = ExecutionFor("intake");
        await agent.ExecuteAsync(execution, CancellationToken.None);

        sent.ShouldNotBeNull();
        string whole = sent.System + string.Join("\n", sent.Messages.Select(m => m.Text));
        whole.ShouldNotContain(execution.RunId.Value);
    }

    [Fact]
    public async Task The_same_question_produces_the_same_fingerprint_on_a_later_attempt()
    {
        // Retry must ask the same question. A prompt that embedded the attempt number would
        // make attempt two a different request with a different cassette key.
        List<LlmRequest> sent = [];
        ScriptedLlmClient client = new(request =>
        {
            sent.Add(request);
            return GoodIntake;
        });

        ModelStageAgent agent = new("intake", Prompts.Get("intake"), client, Prices, new TestClock());

        await agent.ExecuteAsync(ExecutionFor("intake") with { Attempt = 1 }, CancellationToken.None);
        await agent.ExecuteAsync(ExecutionFor("intake") with { Attempt = 2 }, CancellationToken.None);

        sent[0].Fingerprint.ShouldBe(sent[1].Fingerprint);
    }

    private sealed class ScriptedLlmClient(
        Func<LlmRequest, string> responder, string stopReason = "end_turn") : ILlmClient
    {
        public string Description => "scripted";

        public Task<LlmResponse> CompleteAsync(
            LlmRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new LlmResponse(
                responder(request),
                request.Model,
                stopReason,
                new LlmUsage(100, 50, 0, 0),
                LlmResponseSource.Stub));
    }
}
