using Mandate.Core.Llm;
using Mandate.Llm.Tests.Support;

namespace Mandate.Llm.Tests;

/// <summary>
/// The request's fingerprint is the cassette key, so these tests pin the thing that decides
/// whether a recorded run can be replayed at all.
/// </summary>
public sealed class LlmRequestTests
{
    [Fact]
    public void Fingerprint_is_the_same_for_two_identical_requests()
    {
        Requests.A().Fingerprint.ShouldBe(Requests.A().Fingerprint);
    }

    [Fact]
    public void Fingerprint_is_stable_across_process_state()
    {
        // Pinned to a literal rather than only compared to itself. If a change to the
        // canonical serializer, to property order, or to the record's shape alters this
        // value, every cassette ever recorded stops matching — and this test is the only
        // thing that would say so before a demo did.
        Requests.A().Fingerprint.Hex.ShouldBe(
            "23e3be3d059eb983c8006d7dc88ceede34caf118fa7a3ddb57e3c5fb047f0e42");
    }

    [Theory]
    [InlineData("promptId")]
    [InlineData("version")]
    [InlineData("model")]
    [InlineData("system")]
    [InlineData("user")]
    [InlineData("maxOutputTokens")]
    public void Fingerprint_changes_when_any_part_of_the_question_changes(string field)
    {
        LlmRequest changed = field switch
        {
            "promptId" => Requests.A(promptId: "architect"),
            "version" => Requests.A(version: "v2"),
            "model" => Requests.A(model: "claude-sonnet-5"),
            "system" => Requests.A(system: "You do something else."),
            "user" => Requests.A(user: "Shorten URLs, with expiry."),
            _ => Requests.A(maxOutputTokens: 2000),
        };

        changed.Fingerprint.ShouldNotBe(Requests.A().Fingerprint);
    }

    [Fact]
    public void A_conversation_must_open_with_a_user_turn()
    {
        Should.Throw<ArgumentException>(() => LlmRequest.Create(
            "p", "v1", "m", "s", [LlmMessage.Assistant("I went first.")], 10));
    }

    [Fact]
    public void A_request_needs_at_least_one_message()
    {
        Should.Throw<ArgumentException>(() => LlmRequest.Create("p", "v1", "m", "s", [], 10));
    }

    [Fact]
    public void An_output_ceiling_must_be_positive()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => LlmRequest.Create(
            "p", "v1", "m", "s", [LlmMessage.User("hello")], 0));
    }

    [Fact]
    public void A_truncated_answer_is_recognised_as_truncated()
    {
        FakeLlmClient.Answer(stopReason: "max_tokens").WasTruncated.ShouldBeTrue();
        FakeLlmClient.Answer(stopReason: "end_turn").WasTruncated.ShouldBeFalse();
    }

    [Fact]
    public void Usage_adds_each_rate_separately()
    {
        LlmUsage total = new LlmUsage(10, 20, 30, 40) + new LlmUsage(1, 2, 3, 4);

        total.InputTokens.ShouldBe(11);
        total.OutputTokens.ShouldBe(22);
        total.CacheReadTokens.ShouldBe(33);
        total.CacheWriteTokens.ShouldBe(44);
        total.TotalTokens.ShouldBe(110);
    }
}
