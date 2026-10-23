using System.Net;

namespace MelonLoader.Installer.Tests;

[Collection("Static state")]
public class GitHubApiTests
{
    private const string Url = "https://api.github.com/repos/test/test/releases";

    [Fact]
    public async Task RevalidatesWithETag_AndReusesTheCachedBodyOnNotModified()
    {
        var handler = new FakeHandler(req => req.Headers.IfNoneMatch.Count == 0
            ? FakeHandler.Json("""[{"tag_name":"v1.0.0"}]""", "\"abc\"")
            : new HttpResponseMessage(HttpStatusCode.NotModified));
        using var api = new IsolatedApi(handler);

        var first = await GitHubApi.GetJsonAsync(Url);
        var second = await GitHubApi.GetJsonAsync(Url);

        Assert.Equal("v1.0.0", (string?)first.Node![0]!["tag_name"]);
        Assert.Equal("v1.0.0", (string?)second.Node![0]!["tag_name"]);
        Assert.False(second.Stale);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("\"abc\"", handler.Requests[1].Headers.IfNoneMatch.ToString());
    }

    [Fact]
    public async Task ImmutableResponses_AreNotRequestedAgain()
    {
        var handler = new FakeHandler(_ => FakeHandler.Json("""{"artifacts":[]}""", "\"v1\""));
        using var api = new IsolatedApi(handler);

        await GitHubApi.GetJsonAsync(Url, true);
        var second = await GitHubApi.GetJsonAsync(Url, true);

        Assert.NotNull(second.Node);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task RateLimit_WithoutCache_ReportsAClearError()
    {
        var reset = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds();
        var handler = new FakeHandler(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.Forbidden);
            resp.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
            resp.Headers.TryAddWithoutValidation("X-RateLimit-Reset", reset.ToString());
            return resp;
        });
        using var api = new IsolatedApi(handler);

        var result = await GitHubApi.GetJsonAsync(Url);

        Assert.Null(result.Node);
        Assert.Contains("rate limit", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RateLimit_WithCache_FallsBackToTheCachedBody()
    {
        var calls = 0;
        var handler = new FakeHandler(_ => ++calls == 1
            ? FakeHandler.Json("""[{"tag_name":"v2.0.0"}]""", "\"e\"")
            : new HttpResponseMessage(HttpStatusCode.Forbidden));
        using var api = new IsolatedApi(handler);

        await GitHubApi.GetJsonAsync(Url);
        var result = await GitHubApi.GetJsonAsync(Url);

        Assert.Equal("v2.0.0", (string?)result.Node![0]!["tag_name"]);
        Assert.True(result.Stale);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task NetworkFailure_WithCache_FallsBackToTheCachedBody()
    {
        var calls = 0;
        var handler = new FakeHandler(_ => ++calls == 1
            ? FakeHandler.Json("""[1,2,3]""", "\"e\"")
            : throw new HttpRequestException("offline"));
        using var api = new IsolatedApi(handler);

        await GitHubApi.GetJsonAsync(Url);
        var result = await GitHubApi.GetJsonAsync(Url);

        Assert.Equal(3, result.Node!.AsArray().Count);
        Assert.True(result.Stale);
    }

    [Fact]
    public async Task NetworkFailure_WithoutCache_ReturnsAnError()
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException("offline"));
        using var api = new IsolatedApi(handler);

        var result = await GitHubApi.GetJsonAsync(Url);

        Assert.Null(result.Node);
        Assert.False(string.IsNullOrEmpty(result.Error));
    }

    [Fact]
    public async Task CorruptCacheFile_IsIgnored()
    {
        var handler = new FakeHandler(_ => FakeHandler.Json("""{"ok":true}""", "\"e\""));
        using var api = new IsolatedApi(handler);

        await GitHubApi.GetJsonAsync(Url);
        foreach (var file in Directory.GetFiles(api.CacheDir))
            File.WriteAllText(file, "garbage without a newline");

        var result = await GitHubApi.GetJsonAsync(Url);

        Assert.True((bool?)result.Node!["ok"]);
        Assert.Empty(handler.Requests[1].Headers.IfNoneMatch);
    }
}
