namespace Service;

/// <summary>Request body for POST /api/v1/links.</summary>
public sealed record CreateLinkRequest(string? Url, string? Alias, string? ExpiresAt);

/// <summary>Response body for a successful link creation.</summary>
public sealed record CreateLinkResponse(string Code, string ShortUrl);

/// <summary>Response body returned for link statistics.</summary>
public sealed record LinkStats(string Code, string Url, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, long ClickCount);

/// <summary>Standard error envelope used across all endpoints.</summary>
public sealed record ErrorResponse(string Error, string Message);

/// <summary>Discriminates the outcome of a link-creation attempt.</summary>
public enum CreateOutcomeKind
{
    Created,
    Invalid,
    AliasTaken,
}

/// <summary>Result of attempting to create a link, at the service layer.</summary>
public sealed class CreateOutcome
{
    public CreateOutcomeKind Kind { get; }
    public string? Code { get; }
    public string? ShortUrl { get; }
    public string? Error { get; }

    private CreateOutcome(CreateOutcomeKind kind, string? code, string? shortUrl, string? error)
    {
        Kind = kind;
        Code = code;
        ShortUrl = shortUrl;
        Error = error;
    }

    public static CreateOutcome Created(string code, string shortUrl) => new(CreateOutcomeKind.Created, code, shortUrl, null);
    public static CreateOutcome Invalid(string error) => new(CreateOutcomeKind.Invalid, null, null, error);
    public static CreateOutcome AliasTaken() => new(CreateOutcomeKind.AliasTaken, null, null, null);
}

/// <summary>Discriminates the outcome of a redirect attempt.</summary>
public enum RedirectKind
{
    Redirect,
    NotFound,
    Expired,
}

/// <summary>Result of attempting to redirect a short code, at the repository/service layer.</summary>
public sealed class RedirectResult
{
    public RedirectKind Kind { get; }
    public string? Url { get; }

    private RedirectResult(RedirectKind kind, string? url)
    {
        Kind = kind;
        Url = url;
    }

    public static RedirectResult Redirect(string url) => new(RedirectKind.Redirect, url);
    public static RedirectResult NotFound() => new(RedirectKind.NotFound, null);
    public static RedirectResult Expired() => new(RedirectKind.Expired, null);
}

/// <summary>Result of the repository's create operation, before the service layer maps it to a <see cref="CreateOutcome"/>.</summary>
public sealed record RepositoryCreateResult(string? Code, bool Success);