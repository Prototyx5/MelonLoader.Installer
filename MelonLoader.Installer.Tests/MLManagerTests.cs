using System.Net;

namespace MelonLoader.Installer.Tests;

[Collection("Static state")]
public class MLManagerTests
{
    private const string ReleasesJson = """
        [{
          "tag_name": "v0.7.3",
          "assets": [{
            "name": "MelonLoader.x64.zip",
            "browser_download_url": "https://example.test/MelonLoader.x64.zip",
            "digest": "sha256:AABB"
          }]
        }]
        """;

    private static FakeHandler Handler(Func<string, HttpResponseMessage> byUrl)
        => new(req => byUrl(req.RequestUri!.AbsoluteUri));

    [Fact]
    public async Task NightlyFailure_DoesNotHideStableVersions()
    {
        var handler = Handler(url => url.Contains("/actions/")
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : FakeHandler.Json(ReleasesJson));
        using var api = new IsolatedApi(handler);

        var error = await MLManager.Init();

        Assert.True(string.IsNullOrEmpty(error));
        var version = Assert.Single(MLManager.Versions);
        Assert.Equal("0.7.3", version.Version.ToString());
        Assert.False(string.IsNullOrEmpty(MLManager.NightlyError));
    }

    [Fact]
    public async Task NightlyParsingCrash_DoesNotHideStableVersions()
    {
        // A run without an "id" used to throw out of Init
        var handler = Handler(url => url.Contains("/actions/")
            ? FakeHandler.Json("""{"workflow_runs":[{"name":"1.0.0"}]}""")
            : FakeHandler.Json(ReleasesJson));
        using var api = new IsolatedApi(handler);

        var error = await MLManager.Init();

        Assert.True(string.IsNullOrEmpty(error));
        Assert.Single(MLManager.Versions);
        Assert.False(string.IsNullOrEmpty(MLManager.NightlyError));
    }

    [Fact]
    public async Task ReleasesFailure_IsStillReported()
    {
        var handler = Handler(url => url.Contains("/actions/")
            ? FakeHandler.Json("""{"workflow_runs":[]}""")
            : new HttpResponseMessage(HttpStatusCode.BadGateway));
        using var api = new IsolatedApi(handler);

        var error = await MLManager.Init();

        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public async Task ReleaseDigest_IsKeptForTheMatchingArchitecture()
    {
        var handler = Handler(url => url.Contains("/actions/")
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : FakeHandler.Json(ReleasesJson));
        using var api = new IsolatedApi(handler);

        await MLManager.Init();

        var version = Assert.Single(MLManager.Versions);
        Assert.Equal("sha256:AABB", version.GetDigest(Architecture.WindowsX64));
        Assert.Null(version.GetDigest(Architecture.LinuxX64));
    }
}
