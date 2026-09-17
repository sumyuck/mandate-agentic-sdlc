using Mandate.Core.Artifacts;
using Mandate.Core.Identifiers;
using Mandate.Core.Tests.Support;

namespace Mandate.Core.Tests.Artifacts;

/// <summary>
/// Provenance answers two questions the brief asks for: how did this output come to exist
/// (lineage), and what stops being valid if this input changes (re-planning).
/// </summary>
public sealed class ArtifactProvenanceTests
{
    // request -> spec -> design -> patch -> test report
    private static readonly Artifact Request =
        ArtifactFactory.Create("request.txt", "shorten urls", "intake", ArtifactKind.Request);

    private static readonly Artifact Spec =
        ArtifactFactory.Create("spec.md", "spec v1", "requirements", ArtifactKind.RequirementSpec, Request.Hash);

    private static readonly Artifact Design =
        ArtifactFactory.Create("design.md", "design v1", "architecture", ArtifactKind.DesignDoc, Spec.Hash);

    private static readonly Artifact Patch =
        ArtifactFactory.Create("change.patch", "patch v1", "implement", ArtifactKind.SourcePatch, Design.Hash);

    private static readonly Artifact TestReport =
        ArtifactFactory.Create("tests.json", "all green", "test", ArtifactKind.TestReport, Patch.Hash);

    private static readonly Artifact Docs =
        ArtifactFactory.Create("README.md", "docs v1", "documentation", ArtifactKind.Documentation, Design.Hash, Patch.Hash);

    private static ArtifactProvenance Graph() =>
        ArtifactProvenance.Build([Request, Spec, Design, Patch, TestReport, Docs]);

    [Fact]
    public void An_output_traces_back_to_the_request_that_justified_it()
    {
        IEnumerable<string> lineage = Graph().Lineage(TestReport.Hash).Select(artifact => artifact.Name);

        lineage.ShouldBe(["request.txt", "spec.md", "design.md", "change.patch", "tests.json"]);
    }

    [Fact]
    public void Ancestors_are_transitive()
    {
        Graph().Ancestors(TestReport.Hash)
            .Select(artifact => artifact.Name)
            .ShouldBe(["change.patch", "design.md", "spec.md", "request.txt"], ignoreOrder: true);
    }

    [Fact]
    public void Changing_a_requirement_invalidates_everything_built_on_it()
    {
        Graph().Dependents(Spec.Hash)
            .Select(artifact => artifact.Name)
            .ShouldBe(["design.md", "change.patch", "tests.json", "README.md"], ignoreOrder: true);
    }

    [Fact]
    public void Invalidation_is_precise_rather_than_restarting_the_run()
    {
        // A change to the patch must not invalidate the design that preceded it.
        IEnumerable<string> affected = Graph().Dependents(Patch.Hash).Select(artifact => artifact.Name);

        affected.ShouldBe(["tests.json", "README.md"], ignoreOrder: true);
        affected.ShouldNotContain("design.md");
    }

    [Fact]
    public void Invalidation_collapses_onto_the_nodes_the_scheduler_must_re_plan()
    {
        Graph().NodesInvalidatedBy([Design.Hash])
            .Select(node => node.Value)
            .ShouldBe(["implement", "test", "documentation"], ignoreOrder: true);
    }

    [Fact]
    public void A_terminal_artifact_has_no_dependents()
    {
        Graph().Dependents(TestReport.Hash).ShouldBeEmpty();
    }

    [Fact]
    public void Roots_are_the_artifacts_produced_from_nothing()
    {
        Graph().Roots.Select(artifact => artifact.Name).ShouldBe(["request.txt"]);
    }

    [Fact]
    public void An_artifact_derived_from_two_inputs_is_reached_from_either()
    {
        ArtifactProvenance graph = Graph();

        graph.Dependents(Design.Hash).ShouldContain(artifact => artifact.Name == "README.md");
        graph.Dependents(Patch.Hash).ShouldContain(artifact => artifact.Name == "README.md");
    }

    [Fact]
    public void A_duplicated_artifact_does_not_produce_a_duplicate_node()
    {
        // Re-recording the same content is a no-op, not a conflict.
        ArtifactProvenance.Build([Request, Spec, Request, Spec]).Count.ShouldBe(2);
    }

    [Fact]
    public void Unknown_digests_yield_empty_results_rather_than_throwing()
    {
        ArtifactProvenance graph = Graph();
        Sha256Hash unknown = Sha256Hash.OfUtf8("never recorded");

        graph.Find(unknown).ShouldBeNull();
        graph.Ancestors(unknown).ShouldBeEmpty();
        graph.Dependents(unknown).ShouldBeEmpty();
        graph.Lineage(unknown).ShouldBeEmpty();
    }

    [Fact]
    public void An_empty_graph_is_usable()
    {
        ArtifactProvenance.Empty.Count.ShouldBe(0);
        ArtifactProvenance.Empty.Roots.ShouldBeEmpty();
    }
}
