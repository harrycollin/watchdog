using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using KioskWatchdog.Core.Configuration;

namespace KioskWatchdog.Core.Updates;

public sealed class GitHubUpdateClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _repository;

    public GitHubUpdateClient(string? repository = null, HttpClient? http = null)
    {
        _repository = string.IsNullOrWhiteSpace(repository)
            ? UpdatesConfig.DefaultGitHubRepository
            : repository.Trim().Trim('/');

        if (http is null)
        {
            _http = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("KioskWatchdog");
            _http.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            _ownsHttp = true;
        }
        else
        {
            _http = http;
            _ownsHttp = false;
        }
    }

    public string Repository => _repository;

    public async Task<UpdateCheckResult> CheckForUpdateAsync(
        Version currentVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentVersion);

        var latest = await GetLatestReleaseAsync(cancellationToken).ConfigureAwait(false);
        return new UpdateCheckResult
        {
            CurrentVersion = UpdateVersion.Normalize(currentVersion),
            LatestVersion = latest.Version,
            TagName = latest.TagName,
            SetupFileName = latest.SetupFileName,
            DownloadUrl = latest.DownloadUrl
        };
    }

    public async Task<GitHubReleaseInfo> GetLatestReleaseAsync(
        CancellationToken cancellationToken = default)
    {
        var url = $"https://api.github.com/repos/{_repository}/releases/latest";
        using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"GitHub releases request failed ({(int)response.StatusCode}): {TrimForError(body)}");
        }

        var release = await response.Content
            .ReadFromJsonAsync<GitHubReleaseDto>(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (release is null || string.IsNullOrWhiteSpace(release.TagName))
            throw new InvalidOperationException("GitHub returned an empty release payload.");

        if (!UpdateVersion.TryParse(release.TagName, out var version))
            throw new InvalidOperationException($"Could not parse release tag '{release.TagName}'.");

        var asset = release.Assets?
            .FirstOrDefault(a =>
                !string.IsNullOrWhiteSpace(a.Name)
                && a.Name.StartsWith("KioskWatchdogSetup-", StringComparison.OrdinalIgnoreCase)
                && a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                && a.BrowserDownloadUrl is not null);

        if (asset?.BrowserDownloadUrl is null || string.IsNullOrWhiteSpace(asset.Name))
        {
            throw new InvalidOperationException(
                $"Release {release.TagName} has no KioskWatchdogSetup-*.exe asset.");
        }

        return new GitHubReleaseInfo
        {
            TagName = release.TagName,
            Version = version,
            SetupFileName = asset.Name,
            DownloadUrl = asset.BrowserDownloadUrl
        };
    }

    public async Task<string> DownloadSetupAsync(
        Uri downloadUrl,
        string fileName,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(downloadUrl);
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("File name is required.", nameof(fileName));

        var directory = Path.Combine(Path.GetTempPath(), "KioskWatchdog", "updates");
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, Path.GetFileName(fileName));

        using var response = await _http
            .GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var target = new FileStream(
            destination,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            FileOptions.Asynchronous);

        var buffer = new byte[81920];
        long readTotal = 0;
        int read;
        while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                   .ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            readTotal += read;
            if (total is > 0)
                progress?.Report(Math.Clamp(readTotal / (double)total.Value, 0, 1));
        }

        progress?.Report(1);
        return destination;
    }

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }

    private static string TrimForError(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return "(empty body)";
        var trimmed = body.Trim();
        return trimmed.Length <= 200 ? trimmed : trimmed[..200] + "…";
    }

    private sealed class GitHubReleaseDto
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("assets")]
        public List<GitHubAssetDto>? Assets { get; set; }
    }

    private sealed class GitHubAssetDto
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("browser_download_url")]
        public Uri? BrowserDownloadUrl { get; set; }
    }
}
