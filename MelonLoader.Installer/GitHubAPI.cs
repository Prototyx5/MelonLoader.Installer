using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace MelonLoader.Installer;

internal class GitHubResponse
{
    internal JsonNode? Node;
    internal string? Error;

    /// <summary>
    /// True when the data came from the local cache because GitHub could not be reached or refused the request.
    /// </summary>
    internal bool Stale;
}

public static class GitHubApi
{
    private const string RepoOwner = "LavaGang";
    private const string RepoName = "MelonLoader";
    private static readonly string ApiUrl = $"https://api.github.com/repos/{RepoOwner}/{RepoName}";

    private static HttpClient? client;

    // Overridable so the request/caching logic can be tested without hitting the network.
    internal static HttpClient Client
    {
        get => client ?? InstallerUtils.Http;
        set => client = value;
    }

    internal static string CacheDirectory { get; set; } = Path.Combine(PathManager.CacheDir, "api-cache");

    internal static async Task<GitHubResponse> GetReleases()
    {
        var url =
            $"{ApiUrl}/releases";

        return await GetJsonAsync(url);
    }

    internal static async Task<GitHubResponse> GetWorkflowRuns()
    {
        var url =
            $"{ApiUrl}/actions/workflows/5411546/runs" +
            $"?branch=alpha-development" +
            $"&event=push" +
            $"&status=success" +
            $"&page=1" +
            $"&per_page=5";

        var resp = await GetJsonAsync(url);
        if (resp.Node != null)
            resp.Node = resp.Node["workflow_runs"];
        return resp;
    }

    internal static async Task<GitHubResponse> GetWorkflowRunArtifacts(string runId)
    {
        var url =
            $"{ApiUrl}/actions/runs/{runId}/artifacts";

        // The artifacts of a finished run never change, so a cached copy is used without asking GitHub again
        var resp = await GetJsonAsync(url, true);
        if (resp.Node != null)
            resp.Node = resp.Node["artifacts"];
        return resp;
    }

    internal static Task<GitHubResponse> GetLatestInstallerRelease()
        => GetJsonAsync(PathManager.InstallerLatestReleaseApi);

    /// <summary>
    /// Fetches a JSON document from the GitHub API.
    /// Responses are cached on disk and revalidated with their ETag: a "304 Not Modified" answer does not count
    /// against the unauthenticated rate limit (60 requests per hour per IP). If GitHub can't be reached or
    /// refuses the request (rate limit, outage), the last known copy is used instead of failing.
    /// </summary>
    /// <param name="url">The API URL.</param>
    /// <param name="immutable">If true and a cached copy exists, no request is made at all.</param>
    internal static async Task<GitHubResponse> GetJsonAsync(string url, bool immutable = false)
    {
        var cached = LoadCache(url);
        if (cached != null && immutable)
            return new() { Node = cached.Value.Node };

        HttpResponseMessage resp;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (cached != null && !string.IsNullOrEmpty(cached.Value.ETag))
                request.Headers.TryAddWithoutValidation("If-None-Match", cached.Value.ETag);

            resp = await Client.SendAsync(request);
        }
        catch (Exception ex)
        {
            if (cached != null)
                return new() { Node = cached.Value.Node, Stale = true };
            return new() { Error = ex.ToString() };
        }

        using (resp)
        {
            if (resp.StatusCode == HttpStatusCode.NotModified && cached != null)
                return new() { Node = cached.Value.Node };

            if (!resp.IsSuccessStatusCode)
            {
                if (cached != null)
                    return new() { Node = cached.Value.Node, Stale = true };
                return new() { Error = DescribeFailure(resp) };
            }

            JsonNode? node;
            string body;
            try
            {
                body = await resp.Content.ReadAsStringAsync();
                node = JsonNode.Parse(body);
            }
            catch (Exception ex)
            {
                if (cached != null)
                    return new() { Node = cached.Value.Node, Stale = true };
                return new() { Error = ex.ToString() };
            }

            if (node == null)
                return new() { Error = "GitHub returned an empty response." };

            var etag = resp.Headers.ETag?.ToString();
            if (!string.IsNullOrEmpty(etag))
                SaveCache(url, etag, body);

            return new() { Node = node };
        }
    }

    private static string DescribeFailure(HttpResponseMessage resp)
    {
        if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
            && resp.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining)
            && remaining.FirstOrDefault() == "0")
        {
            var message = "GitHub's API rate limit has been reached.";
            if (resp.Headers.TryGetValues("X-RateLimit-Reset", out var reset)
                && long.TryParse(reset.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var resetUnix))
            {
                var resetTime = DateTimeOffset.FromUnixTimeSeconds(resetUnix).ToLocalTime();
                message += $" Try again after {resetTime:t}.";
            }
            return message;
        }

        return $"Status Code: {(int)resp.StatusCode}\n{Enum.GetName(resp.StatusCode)}\n{resp.ReasonPhrase}";
    }

    #region Cache

    // File layout: first line is the ETag, the rest is the raw JSON body.
    private static string GetCachePath(string url)
        => Path.Combine(CacheDirectory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))) + ".json");

    private static (JsonNode Node, string ETag)? LoadCache(string url)
    {
        try
        {
            var path = GetCachePath(url);
            if (!File.Exists(path))
                return null;

            var text = File.ReadAllText(path);
            var split = text.IndexOf('\n');
            if (split < 0)
                return null;

            var node = JsonNode.Parse(text[(split + 1)..]);
            if (node == null)
                return null;

            return (node, text[..split].TrimEnd('\r'));
        }
        catch
        {
            return null;
        }
    }

    private static void SaveCache(string url, string etag, string body)
    {
        try
        {
            Directory.CreateDirectory(CacheDirectory);

            var path = GetCachePath(url);
            var tempPath = path + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(tempPath, etag + "\n" + body);
            File.Move(tempPath, path, true);
        }
        catch
        {
            // The cache is only an optimization
        }
    }

    #endregion
}
