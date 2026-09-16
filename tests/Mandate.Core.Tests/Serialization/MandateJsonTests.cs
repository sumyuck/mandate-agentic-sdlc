using System.Text.Json;
using Mandate.Core.Serialization;

namespace Mandate.Core.Tests.Serialization;

/// <summary>
/// The audit hash chain is only trustworthy if serialization is byte-stable.
/// These tests are the guard on that property.
/// </summary>
public sealed class MandateJsonTests
{
    private sealed record Sample(string Name, int Order, string? Note, SampleKind Kind);

    private enum SampleKind
    {
        Unknown = 0,
        Gate = 1,
    }

    [Fact]
    public void Canonical_serialization_is_byte_stable_across_calls()
    {
        Sample sample = new("requirements", 1, null, SampleKind.Gate);

        string first = JsonSerializer.Serialize(sample, MandateJson.Canonical);
        string second = JsonSerializer.Serialize(sample, MandateJson.Canonical);

        second.ShouldBe(first);
    }

    [Fact]
    public void Canonical_serialization_is_compact_and_camel_cased()
    {
        string json = JsonSerializer.Serialize(
            new Sample("requirements", 1, null, SampleKind.Gate),
            MandateJson.Canonical);

        json.ShouldNotContain("\n");
        json.ShouldContain("\"name\":\"requirements\"");
    }

    [Fact]
    public void Canonical_serialization_keeps_nulls_so_absent_and_null_stay_distinguishable()
    {
        // A dropped null would make two materially different records hash identically.
        string json = JsonSerializer.Serialize(
            new Sample("requirements", 1, null, SampleKind.Gate),
            MandateJson.Canonical);

        json.ShouldContain("\"note\":null");
    }

    [Fact]
    public void Enums_persist_as_names_so_reordering_a_state_cannot_silently_rewrite_history()
    {
        string json = JsonSerializer.Serialize(
            new Sample("requirements", 1, null, SampleKind.Gate),
            MandateJson.Canonical);

        json.ShouldContain("\"kind\":\"Gate\"");
        json.ShouldNotContain("\"kind\":1");
    }

    [Fact]
    public void Shared_options_are_read_only_so_no_component_can_mutate_the_hash_contract()
    {
        MandateJson.Canonical.IsReadOnly.ShouldBeTrue();
        MandateJson.Pretty.IsReadOnly.ShouldBeTrue();
    }
}
