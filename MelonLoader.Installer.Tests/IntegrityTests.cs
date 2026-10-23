using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace MelonLoader.Installer.Tests;

[Collection("Static state")]
public class IntegrityTests
{
    private static string Sha256(byte[] data) => "sha256:" + Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    [Fact]
    public void VerifyDigest_AcceptsMatchingData_AndRewinds()
    {
        var data = Encoding.UTF8.GetBytes("hello melon");
        using var stream = new MemoryStream(data);

        Assert.Null(InstallerUtils.VerifyDigest(stream, Sha256(data)));
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public void VerifyDigest_IgnoresHexCase()
    {
        var data = new byte[] { 1, 2, 3 };
        using var stream = new MemoryStream(data);
        var upper = "sha256:" + Convert.ToHexString(SHA256.HashData(data)).ToUpperInvariant();

        Assert.Null(InstallerUtils.VerifyDigest(stream, upper));
    }

    [Fact]
    public void VerifyDigest_RejectsTamperedData()
    {
        var data = Encoding.UTF8.GetBytes("hello melon");
        var expected = Sha256(data);
        data[0] ^= 1;
        using var stream = new MemoryStream(data);

        Assert.NotNull(InstallerUtils.VerifyDigest(stream, expected));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha512:abcd")]
    [InlineData("garbage")]
    public void VerifyDigest_SkipsWhenThereIsNothingUsableToCheck(string? digest)
    {
        using var stream = new MemoryStream([1, 2, 3]);

        Assert.Null(InstallerUtils.VerifyDigest(stream, digest));
    }

    [Fact]
    public void ApplyURLDownload_StoresTheDigestPerDownload()
    {
        var version = new MLVersion { Version = Semver.SemVersion.Parse("1.0.0") };
        version.ApplyURLDownload("melonloader.x64", "https://x/win.zip", "sha256:11");
        version.ApplyURLDownload("melonloader.linux.x64", "https://x/linux.zip", "sha256:22");
        version.ApplyURLDownload("melonloader.linux.arm64", "https://x/arm.zip");

        Assert.Equal("sha256:11", version.GetDigest(Architecture.WindowsX64));
        Assert.Equal("sha256:22", version.GetDigest(Architecture.LinuxX64));
        Assert.Null(version.GetDigest(Architecture.LinuxArm64));
        Assert.Null(version.GetDigest(Architecture.MacOSX64));
    }

    private static HttpResponseMessage Download(byte[] body, long? declaredLength = null)
    {
        var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        resp.Content.Headers.ContentLength = declaredLength ?? body.Length;
        return resp;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(16 * 1024 - 1)]
    [InlineData(16 * 1024)]
    [InlineData(16 * 1024 + 1)]
    [InlineData(100_000)]
    public async Task Download_WritesEveryByte(int size)
    {
        var body = new byte[size];
        new Random(size).NextBytes(body);
        using var api = new IsolatedApi(new FakeHandler(_ => Download(body)));
        using var destination = new MemoryStream();

        var error = await InstallerUtils.DownloadFileAsync("https://example.test/file", destination, null);

        Assert.Null(error);
        Assert.Equal(body, destination.ToArray());
        Assert.Null(InstallerUtils.VerifyDigest(destination, Sha256(body)));
    }

    [Fact]
    public async Task Download_ReportsATruncatedBody()
    {
        using var api = new IsolatedApi(new FakeHandler(_ => Download(new byte[50], 100)));
        using var destination = new MemoryStream();

        var error = await InstallerUtils.DownloadFileAsync("https://example.test/file", destination, null);

        Assert.NotNull(error);
    }

    [Fact]
    public async Task Download_ReportsProgressUpToOne()
    {
        var body = new byte[40_000];
        using var api = new IsolatedApi(new FakeHandler(_ => Download(body)));
        using var destination = new MemoryStream();
        var last = 0d;

        await InstallerUtils.DownloadFileAsync("https://example.test/file", destination, (p, _) => last = p);

        Assert.Equal(1d, last);
    }

    private static async Task AssertLiveDownloadMatchesDigest(Func<MLVersion, bool> pick, Architecture arch)
    {
        var oldCache = GitHubApi.CacheDirectory;
        var tempCache = Path.Combine(Path.GetTempPath(), "mli-live-" + Guid.NewGuid().ToString("N"));
        GitHubApi.CacheDirectory = tempCache;
        try
        {
            var error = await MLManager.Init();
            Assert.True(string.IsNullOrEmpty(error), error);

            var version = MLManager.Versions.First(v => pick(v) && v.GetDownload(arch) != null);
            var digest = version.GetDigest(arch);
            Assert.False(string.IsNullOrEmpty(digest), "GitHub didn't publish a digest for " + version);

            using var stream = new MemoryStream();
            Assert.Null(await InstallerUtils.DownloadFileAsync(version.GetDownload(arch)!, stream, null));

            Assert.Null(InstallerUtils.VerifyDigest(stream, digest));
        }
        finally
        {
            GitHubApi.CacheDirectory = oldCache;
            try
            {
                Directory.Delete(tempCache, true);
            }
            catch { }
        }
    }

    [LiveFact]
    public Task Live_ARealReleaseMatchesItsPublishedDigest()
        => AssertLiveDownloadMatchesDigest(v => !v.Version.IsPrerelease, Architecture.WindowsX64);

    [LiveFact]
    public Task Live_ARealNightlyMatchesItsPublishedDigest()
        => AssertLiveDownloadMatchesDigest(v => v.Version.IsPrerelease, Architecture.LinuxX64);
}
