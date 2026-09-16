using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mandate.Core.Serialization;

/// <summary>
/// The system's two JSON contracts: one for bytes that get hashed, one for bytes that get read.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Canonical"/> is the serializer used for anything that participates in the
/// audit hash chain or in content-addressed artifact identity. Its settings are locked:
/// changing indentation, casing, escaping or null handling would change every hash the
/// system has ever produced and would invalidate previously recorded run evidence.
/// </para>
/// <para>
/// Property order is the declaring order of the type, which the runtime keeps stable for a
/// given assembly. Reordering members of a persisted record is therefore a breaking change
/// to the audit chain and must go through a schema version bump, not a silent edit.
/// </para>
/// </remarks>
public static class MandateJson
{
    /// <summary>Deterministic, compact serializer for hashing and durable storage.</summary>
    public static JsonSerializerOptions Canonical { get; } = Build(indented: false);

    /// <summary>Human-facing serializer for CLI output, reports and diagnostics.</summary>
    public static JsonSerializerOptions Pretty { get; } = Build(indented: true);

    private static JsonSerializerOptions Build(bool indented)
    {
        JsonSerializerOptions options = new()
        {
            WriteIndented = indented,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = null,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            NumberHandling = JsonNumberHandling.Strict,
            // Escape conservatively rather than relying on relaxed encoding: hashed bytes
            // must not vary with the consuming context.
            Encoder = JavaScriptEncoder.Default,
        };

        options.Converters.Add(new JsonStringEnumConverter());

        // Freeze the contract. Populating the reflection resolver here (rather than leaving
        // it implicit) is what makes the freeze legal, and it means no component can mutate
        // these options later and silently change how audit bytes are produced.
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
