using System.Text;
using Helmsman.Core.Artifacts;
using Helmsman.Core.Identifiers;

namespace Helmsman.Core.Tests.Support;

internal static class ArtifactFactory
{
    public static Artifact Create(
        string name,
        string content,
        string node,
        ArtifactKind kind = ArtifactKind.RequirementSpec,
        params Sha256Hash[] derivedFrom) =>
        Artifact.FromContent(
            kind,
            name,
            "text/plain",
            Encoding.UTF8.GetBytes(content),
            NodeId.Parse(node),
            Actor.Agent(node),
            TestClock.DefaultStart,
            derivedFrom);
}
