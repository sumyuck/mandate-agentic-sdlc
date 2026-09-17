using System.Text;
using Mandate.Core.Artifacts;
using Mandate.Core.Identifiers;
using Mandate.Core.Tests.Support;

namespace Mandate.Core.Tests.Artifacts;

public sealed class ArtifactTests
{
    [Fact]
    public void Identity_is_the_digest_of_the_content()
    {
        Artifact artifact = ArtifactFactory.Create("spec.md", "shorten a url", "requirements");

        artifact.Hash.ShouldBe(Sha256Hash.OfUtf8("shorten a url"));
        artifact.SizeBytes.ShouldBe(13);
    }

    [Fact]
    public void Equal_content_produces_one_identity_even_from_different_nodes()
    {
        // Content addressing is what lets "did this input change?" be an exact comparison.
        Artifact fromRequirements = ArtifactFactory.Create("a.md", "same bytes", "requirements");
        Artifact fromArchitecture = ArtifactFactory.Create("b.md", "same bytes", "architecture");

        fromArchitecture.Hash.ShouldBe(fromRequirements.Hash);
    }

    [Fact]
    public void Provenance_is_canonical_regardless_of_the_order_inputs_were_listed()
    {
        Sha256Hash first = Sha256Hash.OfUtf8("first");
        Sha256Hash second = Sha256Hash.OfUtf8("second");

        Artifact oneOrder = ArtifactFactory.Create(
            "d.md", "derived", "architecture", ArtifactKind.DesignDoc, first, second);
        Artifact otherOrder = ArtifactFactory.Create(
            "d.md", "derived", "architecture", ArtifactKind.DesignDoc, second, first);

        otherOrder.DerivedFrom.ShouldBe(oneOrder.DerivedFrom);
    }

    [Fact]
    public void Duplicate_inputs_are_collapsed()
    {
        Sha256Hash input = Sha256Hash.OfUtf8("input");

        ArtifactFactory.Create("d.md", "derived", "architecture", ArtifactKind.DesignDoc, input, input)
            .DerivedFrom.Length.ShouldBe(1);
    }

    [Fact]
    public void An_artifact_with_no_inputs_is_a_root()
    {
        ArtifactFactory.Create("spec.md", "root", "requirements").IsRoot.ShouldBeTrue();
    }

    [Fact]
    public void An_artifact_cannot_be_derived_from_itself()
    {
        Sha256Hash self = Sha256Hash.OfUtf8("cycle");

        Should.Throw<ArgumentException>(() => Artifact.FromContent(
            ArtifactKind.DesignDoc, "d.md", "text/plain", Encoding.UTF8.GetBytes("cycle"),
            NodeId.Parse("architecture"), Actor.Agent("architect"), TestClock.DefaultStart,
            [self]));
    }

    [Fact]
    public void An_artifact_must_declare_its_kind()
    {
        ArgumentException error = Should.Throw<ArgumentException>(() => Artifact.FromContent(
            ArtifactKind.Unknown, "d.md", "text/plain", Encoding.UTF8.GetBytes("x"),
            NodeId.Parse("architecture"), Actor.Agent("architect"), TestClock.DefaultStart));

        error.Message.ShouldContain("gates are written against kinds");
    }

    [Fact]
    public void An_artifact_must_name_its_producer()
    {
        Should.Throw<ArgumentException>(() => Artifact.FromContent(
            ArtifactKind.DesignDoc, "d.md", "text/plain", Encoding.UTF8.GetBytes("x"),
            NodeId.Parse("architecture"), default, TestClock.DefaultStart));
    }
}
