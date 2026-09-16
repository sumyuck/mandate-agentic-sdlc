using System.Collections.Immutable;
using Mandate.Core.Artifacts;
using Mandate.Core.Context;
using Mandate.Core.Execution;
using Mandate.Core.Identifiers;
using Mandate.Core.Workflow;

namespace Mandate.Agents.Tests.Support;

/// <summary>Builds a stage execution with the run-level facts the engine always seeds.</summary>
internal static class Executions
{
    public static StageExecution For(WorkflowNode node, IWorkspaceReader? workspace = null)
    {
        NodeId engine = NodeId.Parse("run");

        RunContext context = RunContext.Empty
            .Contribute(Fact("run.request", "Build a URL shortener.", engine))
            .Contribute(Fact("run.scenario", "greenfield", engine))
            .Contribute(Fact("run.has-existing-code", "false", engine));

        return new StageExecution(
            RunId.New(TestClock.DefaultStart, "abc123"),
            node,
            1,
            context,
            ImmutableArray<Artifact>.Empty,
            Actor.Agent(node.Agent),
            workspace ?? IWorkspaceReader.Empty);
    }

    private static ContextFact Fact(string key, string value, NodeId node) =>
        ContextFact.Create(key, value, node, Actor.Engine, TestClock.DefaultStart, []);
}
