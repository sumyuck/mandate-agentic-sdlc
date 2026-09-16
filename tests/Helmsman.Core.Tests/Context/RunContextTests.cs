using Helmsman.Core.Context;
using Helmsman.Core.Identifiers;
using Helmsman.Core.Tests.Support;

namespace Helmsman.Core.Tests.Context;

public sealed class RunContextTests
{
    private static ContextFact Fact(string key, string value, string node = "requirements", int atSecond = 0) =>
        ContextFact.Create(
            key, value, NodeId.Parse(node), Actor.Agent(node),
            TestClock.DefaultStart.AddSeconds(atSecond));

    [Fact]
    public void Contributing_a_fact_returns_a_new_context_and_leaves_the_original_untouched()
    {
        RunContext before = RunContext.Empty;
        RunContext after = before.Contribute(Fact("requirements.scope", "create and redirect"));

        before.Count.ShouldBe(0);
        after.Count.ShouldBe(1);
    }

    [Fact]
    public void Revising_a_key_appends_rather_than_overwrites()
    {
        // The context has to be able to answer "what changed, and when?" for a re-plan to be
        // explicable after the fact.
        RunContext context = RunContext.Empty
            .Contribute(Fact("requirements.scope", "create and redirect", atSecond: 0))
            .Contribute(Fact("requirements.scope", "create, redirect and expire", atSecond: 60));

        context.Count.ShouldBe(2);
        context.History("requirements.scope").Length.ShouldBe(2);
        context.Latest("requirements.scope")!.Value.ShouldBe("create, redirect and expire");
        context.WasRevised("requirements.scope").ShouldBeTrue();
    }

    [Fact]
    public void History_is_returned_oldest_first()
    {
        RunContext context = RunContext.Empty
            .Contribute(Fact("requirements.scope", "v1", atSecond: 0))
            .Contribute(Fact("requirements.scope", "v2", atSecond: 60));

        context.History("requirements.scope").Select(fact => fact.Value).ShouldBe(["v1", "v2"]);
    }

    [Fact]
    public void An_absent_key_reads_as_null_rather_than_throwing()
    {
        RunContext.Empty.Latest("requirements.scope").ShouldBeNull();
        RunContext.Empty.History("requirements.scope").ShouldBeEmpty();
        RunContext.Empty.WasRevised("requirements.scope").ShouldBeFalse();
    }

    [Fact]
    public void Keys_are_reported_in_a_stable_order()
    {
        RunContext context = RunContext.Empty
            .Contribute(Fact("test.coverage", "0.86", "test"))
            .Contribute(Fact("requirements.scope", "v1"));

        context.Keys.ShouldBe(["requirements.scope", "test.coverage"]);
    }

    [Fact]
    public void A_scoped_view_hides_facts_the_stage_is_not_entitled_to()
    {
        // Least privilege for context: a stage that cannot see unrelated facts cannot develop
        // a hidden dependency on them, and cannot leak them into a prompt.
        RunContext full = RunContext.Empty
            .Contribute(Fact("requirements.scope", "v1"))
            .Contribute(Fact("requirements.nfr.latency", "p99 < 50ms"))
            .Contribute(Fact("security.credentials", "should never reach an agent prompt", "security-scan"));

        RunContext scoped = full.ScopedTo(["requirements.*"]);

        scoped.Keys.ShouldBe(["requirements.nfr.latency", "requirements.scope"]);
        scoped.Latest("security.credentials").ShouldBeNull();
    }

    [Fact]
    public void An_exact_key_scope_grants_only_that_key()
    {
        RunContext scoped = RunContext.Empty
            .Contribute(Fact("requirements.scope", "v1"))
            .Contribute(Fact("requirements.nfr.latency", "p99 < 50ms"))
            .ScopedTo(["requirements.scope"]);

        scoped.Keys.ShouldBe(["requirements.scope"]);
    }

    [Fact]
    public void A_wildcard_scope_grants_everything()
    {
        RunContext full = RunContext.Empty
            .Contribute(Fact("requirements.scope", "v1"))
            .Contribute(Fact("test.coverage", "0.9", "test"));

        full.ScopedTo(["*"]).Count.ShouldBe(2);
    }

    [Fact]
    public void An_empty_scope_grants_nothing()
    {
        RunContext.Empty.Contribute(Fact("requirements.scope", "v1")).ScopedTo([]).Count.ShouldBe(0);
    }

    [Fact]
    public void A_namespace_prefix_does_not_leak_a_similarly_named_namespace()
    {
        RunContext scoped = RunContext.Empty
            .Contribute(Fact("requirements.scope", "v1"))
            .Contribute(Fact("requirements-draft.scope", "v0"))
            .ScopedTo(["requirements.*"]);

        scoped.Keys.ShouldBe(["requirements.scope"]);
    }

    [Theory]
    [InlineData("requirements.scope")]
    [InlineData("requirements.nfr.latency")]
    [InlineData("impact")]
    [InlineData("release-readiness.go-no-go")]
    public void Well_formed_keys_are_accepted(string key) => ContextFact.IsValidKey(key).ShouldBeTrue();

    [Theory]
    [InlineData("")]
    [InlineData("Requirements.Scope")]
    [InlineData("requirements..scope")]
    [InlineData(".scope")]
    [InlineData("requirements.")]
    [InlineData("requirements scope")]
    public void Malformed_keys_are_rejected(string key)
    {
        ContextFact.IsValidKey(key).ShouldBeFalse();
        Should.Throw<FormatException>(() => Fact(key, "v1"));
    }

    [Fact]
    public void A_fact_must_name_its_contributor() =>
        Should.Throw<ArgumentException>(() => ContextFact.Create(
            "requirements.scope", "v1", NodeId.Parse("requirements"), default, TestClock.DefaultStart));

    [Fact]
    public void Namespace_is_the_leading_segment()
    {
        Fact("requirements.nfr.latency", "v").Namespace.ShouldBe("requirements");
        Fact("impact", "v").Namespace.ShouldBe("impact");
    }
}
