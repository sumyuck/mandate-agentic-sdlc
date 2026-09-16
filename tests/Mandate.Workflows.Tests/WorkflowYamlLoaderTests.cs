using Mandate.Core.Artifacts;
using Mandate.Core.Workflow;

namespace Mandate.Workflows.Tests;

public sealed class WorkflowYamlLoaderTests
{
    private const string Minimal = """
        name: sdlc
        version: v1
        nodes:
          - id: only
            stage: requirements
            agent: requirements-analyst
            autonomy: act-in-sandbox
        """;

    [Fact]
    public void A_minimal_workflow_loads()
    {
        WorkflowDefinition definition = WorkflowYamlLoader.Load(Minimal);

        definition.Identity.ShouldBe("sdlc@v1");
        definition.Nodes.Length.ShouldBe(1);
        definition.Nodes[0].Stage.ShouldBe(SdlcStage.Requirements);
        definition.Nodes[0].Autonomy.ShouldBe(AutonomyLevel.ActInSandbox);
    }

    [Fact]
    public void Hyphenated_yaml_values_map_onto_the_domain_enums()
    {
        WorkflowDefinition definition = WorkflowYamlLoader.Load("""
            name: sdlc
            version: v1
            nodes:
              - id: analyse
                stage: impact-analysis
                agent: codebase-analyst
                autonomy: act-and-auto-accept-low-risk
                produces:
                  - architecture-decision-record
            """);

        definition.Nodes[0].Stage.ShouldBe(SdlcStage.ImpactAnalysis);
        definition.Nodes[0].Autonomy.ShouldBe(AutonomyLevel.ActAndAutoAcceptLowRisk);
        definition.Nodes[0].Produces.ShouldBe([ArtifactKind.ArchitectureDecisionRecord]);
    }

    [Fact]
    public void Omitted_optional_settings_take_documented_defaults()
    {
        WorkflowNode node = WorkflowYamlLoader.Load(Minimal).Nodes[0];

        node.Join.ShouldBe(JoinPolicy.All);
        node.Timeout.ShouldBe(TimeSpan.FromMinutes(10));
        node.Retry.ShouldBe(RetryPolicy.None);
        node.Approvals.ShouldBeEmpty();
        node.Compensation.ShouldBeNull();
    }

    [Fact]
    public void A_node_inherits_the_workflow_default_model_and_may_override_it()
    {
        WorkflowDefinition definition = WorkflowYamlLoader.Load("""
            name: sdlc
            version: v1
            default-model: claude-sonnet-5
            nodes:
              - id: judge
                stage: architecture
                agent: architect
                autonomy: act-in-sandbox
              - id: grind
                stage: testing
                agent: test-engineer
                autonomy: act-and-auto-accept-low-risk
                model: claude-haiku-4-5
            """);

        definition.Nodes[0].Model.ShouldBe("claude-sonnet-5");
        definition.Nodes[1].Model.ShouldBe("claude-haiku-4-5");
    }

    [Fact]
    public void Segregation_of_duties_defaults_to_enforced()
    {
        // The weaker control has to be requested explicitly, never arrived at by omission.
        WorkflowDefinition definition = WorkflowYamlLoader.Load("""
            name: sdlc
            version: v1
            nodes:
              - id: release
                stage: release-readiness
                agent: release-manager
                autonomy: propose-only
                approvals:
                  - role: release-approver
                    reason: Release is not revertible.
            """);

        definition.Nodes[0].Approvals[0].SegregationOfDuties.ShouldBeTrue();
    }

    [Fact]
    public void Retry_settings_are_read_including_the_fallback()
    {
        WorkflowDefinition definition = WorkflowYamlLoader.Load("""
            name: sdlc
            version: v1
            nodes:
              - id: implement
                stage: implementation
                agent: implementer
                autonomy: act-in-sandbox
                compensation: revert-node-commit
                retry:
                  max-attempts: 3
                  initial-backoff: 5s
                  backoff-multiplier: 2
                  max-backoff: 1m
                  jitter-ratio: 0.2
                  on-exhaustion: compensate
            """);

        RetryPolicy retry = definition.Nodes[0].Retry;

        retry.MaxAttempts.ShouldBe(3);
        retry.InitialBackoff.ShouldBe(TimeSpan.FromSeconds(5));
        retry.MaxBackoff.ShouldBe(TimeSpan.FromMinutes(1));
        retry.OnExhaustion.ShouldBe(FallbackStrategy.Compensate);
    }

    [Fact]
    public void Edges_default_to_forward_and_carry_guards()
    {
        WorkflowDefinition definition = WorkflowYamlLoader.Load("""
            name: sdlc
            version: v1
            nodes:
              - id: a
                stage: requirements
                agent: x
                autonomy: act-in-sandbox
                produces-context:
                  - requirements.scope
              - id: b
                stage: implementation
                agent: y
                autonomy: act-in-sandbox
            edges:
              - from: a
                to: b
                guard: run.scenario == 'brownfield'
              - from: b
                to: a
                kind: loop-back
            """);

        definition.Edges[0].Kind.ShouldBe(EdgeKind.Forward);
        definition.Edges[0].Guard.ShouldBe("run.scenario == 'brownfield'");
        definition.Edges[1].Kind.ShouldBe(EdgeKind.LoopBack);
        definition.Edges[1].Guard.ShouldBeNull();
    }

    // ---- strictness ----

    [Fact]
    public void An_unknown_key_is_rejected_rather_than_ignored()
    {
        // A retry policy silently dropped because the key was misspelled would be a
        // governance failure that no test could catch.
        WorkflowFormatException error = Should.Throw<WorkflowFormatException>(
            () => WorkflowYamlLoader.Load("""
                name: sdlc
                version: v1
                nodes:
                  - id: only
                    stage: requirements
                    agent: x
                    autonomy: act-in-sandbox
                    maxattempts: 3
                """));

        error.Message.ShouldContain("maxattempts");
    }

    [Fact]
    public void A_duplicate_key_is_rejected()
    {
        Should.Throw<WorkflowFormatException>(() => WorkflowYamlLoader.Load("""
            name: sdlc
            version: v1
            version: v2
            nodes:
              - id: only
                stage: requirements
                agent: x
                autonomy: act-in-sandbox
            """));
    }

    [Fact]
    public void An_unrecognised_enum_value_lists_what_is_accepted()
    {
        WorkflowFormatException error = Should.Throw<WorkflowFormatException>(
            () => WorkflowYamlLoader.Load("""
                name: sdlc
                version: v1
                nodes:
                  - id: only
                    stage: requirements
                    agent: x
                    autonomy: fully-trusted
                """));

        error.Message.ShouldContain("fully-trusted");
        error.Message.ShouldContain("propose-only");
        error.Message.ShouldContain("act-in-sandbox");
    }

    [Fact]
    public void The_unknown_enum_member_cannot_be_selected_by_name()
    {
        // 'Unknown' exists so a missing value fails validation; it must not be settable.
        Should.Throw<WorkflowFormatException>(() => WorkflowYamlLoader.Load("""
            name: sdlc
            version: v1
            nodes:
              - id: only
                stage: unknown
                agent: x
                autonomy: act-in-sandbox
            """));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("version")]
    public void Identity_fields_are_required(string omitted)
    {
        string yaml = Minimal.Replace($"{omitted}: ", "ignored-", StringComparison.Ordinal);

        Should.Throw<WorkflowFormatException>(() => WorkflowYamlLoader.Load(yaml));
    }

    [Fact]
    public void A_workflow_with_no_nodes_is_rejected() =>
        Should.Throw<WorkflowFormatException>(() => WorkflowYamlLoader.Load("""
            name: sdlc
            version: v1
            """))
            .Message.ShouldContain("declares no 'nodes'");

    [Fact]
    public void An_empty_document_is_rejected() =>
        Should.Throw<WorkflowFormatException>(() => WorkflowYamlLoader.Load("  "));

    [Fact]
    public void A_malformed_duration_names_the_field_and_the_accepted_form()
    {
        WorkflowFormatException error = Should.Throw<WorkflowFormatException>(
            () => WorkflowYamlLoader.Load("""
                name: sdlc
                version: v1
                nodes:
                  - id: only
                    stage: requirements
                    agent: x
                    autonomy: act-in-sandbox
                    timeout: 10 minutes
                """));

        error.Message.ShouldContain("timeout");
        error.Message.ShouldContain("'5m'");
    }

    [Fact]
    public void A_malformed_node_id_is_rejected()
    {
        Should.Throw<WorkflowFormatException>(() => WorkflowYamlLoader.Load("""
            name: sdlc
            version: v1
            nodes:
              - id: Release Readiness
                stage: release-readiness
                agent: x
                autonomy: propose-only
            """))
            .Message.ShouldContain("lowercase slug");
    }

    [Fact]
    public void A_malformed_context_key_is_rejected()
    {
        Should.Throw<WorkflowFormatException>(() => WorkflowYamlLoader.Load("""
            name: sdlc
            version: v1
            nodes:
              - id: only
                stage: requirements
                agent: x
                autonomy: act-in-sandbox
                produces-context:
                  - Requirements.Scope
            """))
            .Message.ShouldContain("not a valid context key");
    }

    [Fact]
    public void Malformed_yaml_reports_the_line()
    {
        WorkflowFormatException error = Should.Throw<WorkflowFormatException>(
            () => WorkflowYamlLoader.Load("name: sdlc\nversion: [unclosed\n", "broken.yaml"));

        error.SourceName.ShouldBe("broken.yaml");
        error.Line.ShouldNotBeNull();
    }

    [Fact]
    public void A_missing_file_is_reported_as_such()
    {
        Should.Throw<WorkflowFormatException>(
                () => WorkflowYamlLoader.LoadFile("does/not/exist.yaml"))
            .Message.ShouldContain("does not exist");
    }

    [Fact]
    public void Loading_does_not_validate_executability()
    {
        // The two failures need different fixes — a format error is a typo, a validation
        // error is a design problem — so they are reported separately.
        WorkflowDefinition loaded = WorkflowYamlLoader.Load("""
            name: sdlc
            version: v1
            nodes:
              - id: a
                stage: requirements
                agent: x
                autonomy: act-in-sandbox
              - id: b
                stage: implementation
                agent: y
                autonomy: act-in-sandbox
            edges:
              - from: a
                to: b
              - from: b
                to: a
            """);

        loaded.Nodes.Length.ShouldBe(2);
        Should.Throw<WorkflowValidationException>(() => WorkflowGraph.Build(loaded));
    }
}
