using Mandate.Core.Llm;
using Mandate.Llm.Cassettes;
using Mandate.Llm.Clients;
using Mandate.Llm.Tests.Support;

namespace Mandate.Llm.Tests;

/// <summary>
/// Cassettes are what make a run reproducible by someone with no key and no network, so
/// these tests care about two things: that a recording round-trips exactly, and that an
/// altered or absent one is refused rather than worked around.
/// </summary>
public sealed class CassetteTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "mandate-cassettes-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly TestClock _clock = new();

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task A_recording_round_trips_exactly()
    {
        FileCassetteStore store = new(_root);
        LlmRequest request = Requests.A();

        LlmResponse original = FakeLlmClient.Answer(
            "A URL shortener needs a create endpoint and a redirect endpoint.",
            inputTokens: 412,
            outputTokens: 96);

        await store.SaveAsync(Cassette.Of(request, original, _clock.UtcNow), CancellationToken.None);

        Cassette? found = await store.FindAsync(request, CancellationToken.None);

        found.ShouldNotBeNull();
        found.Response.Text.ShouldBe(original.Text);
        found.Response.Usage.ShouldBe(original.Usage);
        found.Response.Model.ShouldBe(original.Model);
        found.Response.StopReason.ShouldBe(original.StopReason);

        // Compared by fingerprint, which is the request's identity: record equality would
        // compare the message array by reference and pass for the wrong reason.
        found.Request.Fingerprint.ShouldBe(request.Fingerprint);
        found.Request.System.ShouldBe(request.System);
        found.Request.Messages.Single().Text.ShouldBe(request.Messages.Single().Text);
    }

    [Fact]
    public async Task A_replayed_answer_is_labelled_as_replayed_not_as_live()
    {
        FileCassetteStore store = new(_root);
        LlmRequest request = Requests.A();

        await store.SaveAsync(
            Cassette.Of(request, FakeLlmClient.Answer(), _clock.UtcNow),
            CancellationToken.None);

        LlmResponse replayed = await new ReplayLlmClient(store)
            .CompleteAsync(request, CancellationToken.None);

        replayed.Source.ShouldBe(LlmResponseSource.Replay);
    }

    [Fact]
    public async Task A_request_that_was_never_recorded_is_a_hard_failure()
    {
        CassetteMissException miss = await Should.ThrowAsync<CassetteMissException>(
            () => new ReplayLlmClient(new FileCassetteStore(_root))
                .CompleteAsync(Requests.A(), CancellationToken.None));

        miss.PromptIdentity.ShouldBe("requirements-analyst.v1");
        miss.Message.ShouldContain("re-record");
    }

    [Fact]
    public async Task A_changed_prompt_misses_rather_than_reusing_the_old_answer()
    {
        FileCassetteStore store = new(_root);

        await store.SaveAsync(
            Cassette.Of(Requests.A(), FakeLlmClient.Answer(), _clock.UtcNow),
            CancellationToken.None);

        // Same prompt id, different text: a different question, so the old answer is not
        // an answer to it. Silently reusing it is the failure mode this whole design exists
        // to prevent.
        await Should.ThrowAsync<CassetteMissException>(
            () => new ReplayLlmClient(store).CompleteAsync(
                Requests.A(system: "You analyse requirements, strictly."),
                CancellationToken.None));
    }

    [Fact]
    public async Task An_edited_recording_is_refused()
    {
        FileCassetteStore store = new(_root);
        LlmRequest request = Requests.A();

        await store.SaveAsync(
            Cassette.Of(request, FakeLlmClient.Answer(), _clock.UtcNow),
            CancellationToken.None);

        string path = store.PathFor(request);
        string text = await File.ReadAllTextAsync(path, CancellationToken.None);

        await File.WriteAllTextAsync(
            path,
            text.Replace("Shorten URLs.", "Shorten URLs and expire them.", StringComparison.Ordinal),
            CancellationToken.None);

        await Should.ThrowAsync<CassetteCorruptException>(
            () => store.FindAsync(request, CancellationToken.None));
    }

    [Fact]
    public void Recordings_are_filed_under_the_prompt_they_belong_to()
    {
        string path = new FileCassetteStore(_root).PathFor(Requests.A());

        Path.GetFileName(Path.GetDirectoryName(path)).ShouldBe("requirements-analyst.v1");
        Path.GetFileNameWithoutExtension(path).ShouldBe(Requests.A().Fingerprint.Hex);
    }

    [Fact]
    public async Task Recording_calls_the_provider_once_and_keeps_the_answer()
    {
        FakeLlmClient live = new();
        FileCassetteStore store = new(_root);
        RecordingLlmClient recorder = new(live, store, _clock);

        await recorder.CompleteAsync(Requests.A(), CancellationToken.None);

        live.Calls.ShouldBe(1);
        recorder.Recorded.ShouldBe(1);
        store.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Recording_an_exchange_it_already_has_does_not_spend_again()
    {
        FileCassetteStore store = new(_root);

        await store.SaveAsync(
            Cassette.Of(Requests.A(), FakeLlmClient.Answer(), _clock.UtcNow),
            CancellationToken.None);

        RecordingLlmClient recorder = new(new ExplodingLlmClient(), store, _clock);

        LlmResponse response = await recorder.CompleteAsync(
            Requests.A(), CancellationToken.None);

        response.Source.ShouldBe(LlmResponseSource.Replay);
        recorder.Reused.ShouldBe(1);
        recorder.Recorded.ShouldBe(0);
    }

    [Fact]
    public async Task Refreshing_deliberately_re_asks_a_recorded_exchange()
    {
        FileCassetteStore store = new(_root);

        await store.SaveAsync(
            Cassette.Of(Requests.A(), FakeLlmClient.Answer("stale"), _clock.UtcNow),
            CancellationToken.None);

        FakeLlmClient live = new(_ => FakeLlmClient.Answer("fresh"));
        RecordingLlmClient recorder = new(live, store, _clock, refresh: true);

        LlmResponse response = await recorder.CompleteAsync(
            Requests.A(), CancellationToken.None);

        response.Text.ShouldBe("fresh");
        live.Calls.ShouldBe(1);

        Cassette? stored = await store.FindAsync(
            Requests.A(), CancellationToken.None);

        stored!.Response.Text.ShouldBe("fresh");
    }

    [Fact]
    public async Task The_stub_answers_the_same_way_every_time_and_says_it_is_a_stub()
    {
        StubLlmClient stub = new();

        LlmResponse first = await stub.CompleteAsync(
            Requests.A(), CancellationToken.None);

        LlmResponse second = await stub.CompleteAsync(
            Requests.A(), CancellationToken.None);

        first.Text.ShouldBe(second.Text);
        first.Source.ShouldBe(LlmResponseSource.Stub);
        first.Text.ShouldContain("STUB RESPONSE");
    }
}
