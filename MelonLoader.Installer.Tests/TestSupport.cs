using System.Net;
using System.Text;

namespace MelonLoader.Installer.Tests;

/// <summary>
/// Answers requests with whatever the test provides and records them.
/// </summary>
internal sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(respond(request));
    }

    public static HttpResponseMessage Json(string json, string? etag = null)
    {
        var resp = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        if (etag != null)
            resp.Headers.TryAddWithoutValidation("ETag", etag);
        return resp;
    }
}

/// <summary>
/// Gives a test its own cache folder and puts the global state back afterwards.
/// </summary>
internal sealed class IsolatedApi : IDisposable
{
    private readonly string oldCache = GitHubApi.CacheDirectory;
    private readonly HttpClient oldHttp = InstallerUtils.Http;

    public string CacheDir { get; } = Path.Combine(Path.GetTempPath(), "mli-tests-" + Guid.NewGuid().ToString("N"));

    public IsolatedApi(FakeHandler handler)
    {
        GitHubApi.CacheDirectory = CacheDir;
        var client = new HttpClient(handler);
        GitHubApi.Client = client;
        InstallerUtils.Http = client;
    }

    public void Dispose()
    {
        GitHubApi.CacheDirectory = oldCache;
        GitHubApi.Client = oldHttp;
        InstallerUtils.Http = oldHttp;
        try
        {
            Directory.Delete(CacheDir, true);
        }
        catch { }
    }
}

/// <summary>
/// The tests share static state (HTTP client, cache folder, version list), so they must not run in parallel.
/// </summary>
[CollectionDefinition("Static state", DisableParallelization = true)]
public class StaticStateCollection;

/// <summary>
/// Needs the real GitHub, so it only runs when MLI_LIVE_TESTS=1.
/// </summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("MLI_LIVE_TESTS") != "1")
            Skip = "Set MLI_LIVE_TESTS=1 to run tests against the real GitHub.";
    }
}
