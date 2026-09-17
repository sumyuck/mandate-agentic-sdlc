namespace Service.Tests;

/// <summary>
/// Tests for <see cref="LinkRepository.DeleteAsync(string)"/>, exercising the permanent
/// removal of links and their click statistics, the 404 behavior for nonexistent codes,
/// idempotency on repeated delete, and the critical invariant that a deleted code behaves
/// exactly as a never-created one for all subsequent operations.
/// </summary>
public sealed class LinkRepositoryDeleteTests
{
    private readonly LinkRepository _repository;

    public LinkRepositoryDeleteTests()
    {
        string connectionString = $"Data Source={Path.GetTempFileName()}";
        SchemaInitializer.Initialize(connectionString);
        _repository = new LinkRepository(connectionString);
    }

    [Fact]
    public async Task DeleteAsync_for_existing_code_returns_true()
    {
        await _repository.CreateAsync("https://example.com", "tobedeleted", null);

        bool result = await _repository.DeleteAsync("tobedeleted");

        Assert.True(result);
    }

    [Fact]
    public async Task DeleteAsync_for_existing_code_removes_the_link()
    {
        await _repository.CreateAsync("https://example.com/target", "removal-test", null);

        await _repository.DeleteAsync("removal-test");

        LinkStats? stats = await _repository.GetStatsAsync("removal-test");
        Assert.Null(stats);
    }

    [Fact]
    public async Task DeleteAsync_removes_click_statistics_along_with_link()
    {
        await _repository.CreateAsync("https://example.com", "withclicks", null);
        await _repository.RedirectAsync("withclicks");
        await _repository.RedirectAsync("withclicks");
        await _repository.RedirectAsync("withclicks");

        await _repository.DeleteAsync("withclicks");

        LinkStats? stats = await _repository.GetStatsAsync("withclicks");
        Assert.Null(stats);
    }

    [Fact]
    public async Task DeleteAsync_for_nonexistent_code_returns_false()
    {
        bool result = await _repository.DeleteAsync("neverexisted");

        Assert.False(result);
    }

    [Fact]
    public async Task DeleteAsync_called_twice_returns_false_the_second_time()
    {
        await _repository.CreateAsync("https://example.com", "deletedtwice", null);

        bool first = await _repository.DeleteAsync("deletedtwice");
        bool second = await _repository.DeleteAsync("deletedtwice");

        Assert.True(first);
        Assert.False(second);
    }

    [Fact]
    public async Task RedirectAsync_after_delete_returns_not_found()
    {
        await _repository.CreateAsync("https://example.com/target", "gonecode", null);
        await _repository.DeleteAsync("gonecode");

        RedirectResult result = await _repository.RedirectAsync("gonecode");

        Assert.Equal(RedirectKind.NotFound, result.Kind);
    }

    [Fact]
    public async Task GetStatsAsync_after_delete_returns_null()
    {
        await _repository.CreateAsync("https://example.com/page", "statsgone", null);
        await _repository.DeleteAsync("statsgone");

        LinkStats? stats = await _repository.GetStatsAsync("statsGone");

        Assert.Null(stats);
    }

    [Fact]
    public async Task Deleted_code_can_be_reused_as_new_alias()
    {
        string url1 = "https://example.com/first";
        string url2 = "https://example.com/second";
        const string reuseableCode = "reusable";

        await _repository.CreateAsync(url1, reuseableCode, null);
        await _repository.DeleteAsync(reuseableCode);

        RepositoryCreateResult result = await _repository.CreateAsync(url2, reuseableCode, null);

        Assert.True(result.Success);
        Assert.Equal(reuseableCode, result.Code);

        LinkStats? newStats = await _repository.GetStatsAsync(reuseableCode);
        Assert.NotNull(newStats);
        Assert.Equal(url2, newStats.Url);
    }

    [Fact]
    public async Task DeleteAsync_for_code_with_expiry_removes_entire_record()
    {
        DateTimeOffset expiry = new DateTimeOffset(2099, 12, 31, 23, 59, 59, TimeSpan.Zero);
        await _repository.CreateAsync("https://example.com", "expiring", expiry);

        bool deleted = await _repository.DeleteAsync("expiring");

        Assert.True(deleted);
        Assert.Null(await _repository.GetStatsAsync("expiring"));
    }
}