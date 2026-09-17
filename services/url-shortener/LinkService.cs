using System.Globalization;
using System.Text.RegularExpressions;

namespace Service;

/// <summary>
/// Application logic: validates a create request, delegates to <see cref="LinkRepository"/> for
/// persistence, and maps repository results onto the outcomes the HTTP layer understands.
/// </summary>
public sealed partial class LinkService
{
    private readonly LinkRepository _repository;
    private readonly string _baseUrl;

    public LinkService(LinkRepository repository, string baseUrl)
    {
        _repository = repository;
        _baseUrl = baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";
    }

    public async Task<CreateOutcome> CreateAsync(CreateLinkRequest request)
    {
        if (!UrlValidator.TryValidate(request.Url, out _, out string? urlError))
        {
            return CreateOutcome.Invalid(urlError!);
        }

        if (request.Alias is not null && !AliasPattern().IsMatch(request.Alias))
        {
            return CreateOutcome.Invalid("alias must match ^[A-Za-z0-9]{1,32}$");
        }

        DateTimeOffset? expiresAt = null;
        if (request.ExpiresAt is not null)
        {
            bool hasUtcDesignator = request.ExpiresAt.EndsWith('Z') || request.ExpiresAt.EndsWith("+00:00", StringComparison.Ordinal);
            if (!hasUtcDesignator ||
                !DateTimeOffset.TryParse(
                    request.ExpiresAt,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out DateTimeOffset parsed))
            {
                return CreateOutcome.Invalid("expiresAt must be a valid ISO-8601 UTC timestamp");
            }

            expiresAt = parsed;
        }

        RepositoryCreateResult result = await _repository.CreateAsync(request.Url!, request.Alias, expiresAt);
        if (!result.Success)
        {
            return CreateOutcome.AliasTaken();
        }

        string shortUrl = _baseUrl + result.Code;
        return CreateOutcome.Created(result.Code!, shortUrl);
    }

    public Task<RedirectResult> RedirectAsync(string code) => _repository.RedirectAsync(code);

    public Task<LinkStats?> GetStatsAsync(string code) => _repository.GetStatsAsync(code);

    /// <summary>Permanently deletes the link and its click statistics. Returns true if a link existed.</summary>
    public Task<bool> DeleteAsync(string code) => _repository.DeleteAsync(code);

    [GeneratedRegex("^[A-Za-z0-9]{1,32}$")]
    private static partial Regex AliasPattern();
}