using Microsoft.AspNetCore.Http.Json;
using Service;
using System.Text.Json;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
});

string connectionString = builder.Configuration.GetConnectionString("Sqlite") ?? "Data Source=links.db";
string baseUrl = builder.Configuration["BaseUrl"] ?? "http://localhost:5000/";

SchemaInitializer.Initialize(connectionString);

builder.Services.AddSingleton(new LinkRepository(connectionString));
builder.Services.AddSingleton(sp => new LinkService(sp.GetRequiredService<LinkRepository>(), baseUrl));

WebApplication app = builder.Build();

app.MapGet("/health/live", () => Results.Ok(HealthStatus.Live));

app.MapPost("/api/v1/links", async (CreateLinkRequest request, LinkService service) =>
{
    CreateOutcome outcome = await service.CreateAsync(request);
    return outcome.Kind switch
    {
        CreateOutcomeKind.Invalid => Results.Json(new ErrorResponse("invalid_request", outcome.Error!), statusCode: StatusCodes.Status400BadRequest),
        CreateOutcomeKind.AliasTaken => Results.Json(new ErrorResponse("alias_taken", "The requested alias is already taken."), statusCode: StatusCodes.Status409Conflict),
        CreateOutcomeKind.Created => Results.Json(new CreateLinkResponse(outcome.Code!, outcome.ShortUrl!), statusCode: StatusCodes.Status201Created),
        _ => Results.Problem(),
    };
});

app.MapGet("/api/v1/links/{code}/stats", async (string code, LinkService service) =>
{
    LinkStats? stats = await service.GetStatsAsync(code);
    if (stats is null)
    {
        return Results.Json(new ErrorResponse("not_found", "No link exists for this code."), statusCode: StatusCodes.Status404NotFound);
    }

    return Results.Json(stats, statusCode: StatusCodes.Status200OK);
});

app.MapGet("/{code}", async (string code, LinkService service) =>
{
    RedirectResult result = await service.RedirectAsync(code);
    return result.Kind switch
    {
        RedirectKind.Redirect => Results.Redirect(result.Url!, permanent: false),
        RedirectKind.NotFound => Results.Json(new ErrorResponse("not_found", "No link exists for this code."), statusCode: StatusCodes.Status404NotFound),
        RedirectKind.Expired => Results.Json(new ErrorResponse("expired", "The link has expired."), statusCode: StatusCodes.Status410Gone),
        _ => Results.Problem(),
    };
});

app.Run();

/// <summary>What the liveness endpoint reports.</summary>
public sealed record HealthStatus(string Status)
{
    /// <summary>The service is up.</summary>
    public static HealthStatus Live { get; } = new("live");
}