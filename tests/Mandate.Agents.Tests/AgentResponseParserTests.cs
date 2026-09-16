using Mandate.Agents.Model;

namespace Mandate.Agents.Tests;

/// <summary>
/// What a model actually returns, rather than what the prompt asked for. Every case here is
/// a shape a model really produces; the parser's job is to know which of them are usable.
/// </summary>
public sealed class AgentResponseParserTests
{
    private const string Minimal = """
        {
          "summary": "Recorded the request.",
          "documents": [ { "kind": "request", "path": "docs/request.md", "content": "hello" } ],
          "facts": { "intake.recorded": "true" },
          "decisions": []
        }
        """;

    [Fact]
    public void A_clean_object_parses()
    {
        AgentResponse answer = AgentResponseParser.Parse(Minimal);

        answer.Summary.ShouldBe("Recorded the request.");
        answer.Documents.Single().Kind.ShouldBe("request");
        answer.Documents.Single().Path.ShouldBe("docs/request.md");
        answer.Facts["intake.recorded"].ShouldBe("true");
    }

    [Fact]
    public void A_fenced_object_parses()
    {
        AgentResponseParser.Parse("```json\n" + Minimal + "\n```")
            .Documents.Single().Kind.ShouldBe("request");
    }

    [Fact]
    public void An_object_with_prose_around_it_parses()
    {
        AgentResponseParser.Parse(
            "Here is the result you asked for:\n\n" + Minimal + "\n\nLet me know if you need more.")
            .Summary.ShouldBe("Recorded the request.");
    }

    [Fact]
    public void Braces_inside_string_content_do_not_end_the_object()
    {
        // The case that breaks a naive scan. Every implementation stage returns C#, so an
        // answer whose content contains braces is the normal case, not the exotic one.
        const string withCode = """
            {
              "summary": "Wrote the endpoint.",
              "documents": [ { "kind": "source-patch", "path": "src/Api.cs",
                "content": "public static class Api { public static int Add(int a) { return a; } }" } ],
              "facts": { "implementation.builds": "true" },
              "decisions": []
            }
            """;

        AgentResponse answer = AgentResponseParser.Parse(withCode);

        answer.Documents.Single().Content.ShouldEndWith("} }");
        answer.Facts["implementation.builds"].ShouldBe("true");
    }

    [Fact]
    public void An_escaped_quote_inside_content_does_not_end_the_string()
    {
        const string withQuote = """
            {
              "summary": "s",
              "documents": [ { "kind": "request", "path": null,
                "content": "he said \"stop\" and then { left" } ],
              "facts": {},
              "decisions": []
            }
            """;

        AgentResponseParser.Parse(withQuote).Documents.Single().Content
            .ShouldBe("he said \"stop\" and then { left");
    }

    [Fact]
    public void A_null_path_means_a_record_about_the_run_rather_than_a_file()
    {
        const string report = """
            {
              "summary": "Reviewed.",
              "documents": [ { "kind": "review-report", "path": null, "content": "no findings" } ],
              "facts": {},
              "decisions": []
            }
            """;

        AgentResponseParser.Parse(report).Documents.Single().Path.ShouldBeNull();
    }

    [Fact]
    public void An_answer_with_no_json_at_all_is_refused_with_an_excerpt()
    {
        Should.Throw<AgentResponseException>(
            () => AgentResponseParser.Parse("I'm sorry, I can't help with that."))
            .Message.ShouldContain("I'm sorry");
    }

    [Fact]
    public void An_unclosed_object_is_refused_rather_than_guessed_at()
    {
        // What a truncated answer looks like. Completing it on the model's behalf would
        // invent content and commit it to a repository.
        Should.Throw<AgentResponseException>(() => AgentResponseParser.Parse(
            """{ "summary": "s", "documents": [ { "kind": "request", "content": "half"""));
    }

    [Fact]
    public void An_empty_document_is_refused()
    {
        Should.Throw<AgentResponseException>(() => AgentResponseParser.Parse("""
            {
              "summary": "s",
              "documents": [ { "kind": "request", "path": "docs/request.md", "content": "" } ],
              "facts": {},
              "decisions": []
            }
            """))
            .Message.ShouldContain("empty");
    }

    [Fact]
    public void A_document_that_does_not_say_what_kind_it_is_is_refused()
    {
        Should.Throw<AgentResponseException>(() => AgentResponseParser.Parse("""
            {
              "summary": "s",
              "documents": [ { "path": "docs/request.md", "content": "hello" } ],
              "facts": {},
              "decisions": []
            }
            """))
            .Message.ShouldContain("kind");
    }

    [Fact]
    public void Property_casing_is_not_something_a_stage_fails_over()
    {
        AgentResponseParser.Parse("""
            {
              "Summary": "Recorded.",
              "Documents": [ { "Kind": "request", "Path": null, "Content": "hello" } ],
              "Facts": { "intake.recorded": "true" },
              "Decisions": []
            }
            """)
            .Documents.Single().Kind.ShouldBe("request");
    }

    [Fact]
    public void Missing_optional_sections_default_rather_than_fail()
    {
        AgentResponse answer = AgentResponseParser.Parse("""
            { "documents": [ { "kind": "request", "content": "hello" } ] }
            """);

        answer.Summary.ShouldBe("(no summary)");
        answer.Facts.ShouldBeEmpty();
        answer.Decisions.ShouldBeEmpty();
    }

    [Fact]
    public void A_decision_is_carried_through_with_its_rejected_options()
    {
        AgentResponse answer = AgentResponseParser.Parse("""
            {
              "summary": "Designed it.",
              "documents": [ { "kind": "design-doc", "content": "d" } ],
              "facts": {},
              "decisions": [
                { "id": "storage", "question": "Where does state live?",
                  "options": [
                    { "name": "sqlite", "summary": "One file.", "rejectedBecause": null },
                    { "name": "postgres", "summary": "A server.", "rejectedBecause": "Too much for the scope." }
                  ],
                  "chosen": "sqlite", "rationale": "Proportionate.", "confidence": 0.8 }
              ]
            }
            """);

        AgentDecision decision = answer.Decisions.Single();

        decision.Options.Length.ShouldBe(2);
        decision.Options.Count(option => option.RejectedBecause is not null).ShouldBe(1);
        decision.Confidence.ShouldBe(0.8);
    }

    [Fact]
    public void Confidence_outside_the_range_is_clamped_rather_than_rejected()
    {
        AgentResponseParser.Parse("""
            {
              "documents": [ { "kind": "design-doc", "content": "d" } ],
              "decisions": [ { "id": "x", "question": "q", "chosen": "a",
                "options": [ { "name": "a", "summary": "s" } ], "confidence": 4.2 } ]
            }
            """)
            .Decisions.Single().Confidence.ShouldBe(1d);
    }
}
