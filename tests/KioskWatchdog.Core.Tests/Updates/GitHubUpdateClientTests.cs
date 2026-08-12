using System.Net;
using System.Text;
using KioskWatchdog.Core.Updates;

namespace KioskWatchdog.Core.Tests.Updates;

public class GitHubUpdateClientTests
{
    [Fact]
    public async Task CheckForUpdateAsync_detects_newer_release()
    {
        var json = """
            {
              "tag_name": "v1.5.0",
              "assets": [
                {
                  "name": "KioskWatchdog-win-x64-1.5.0.zip",
                  "browser_download_url": "https://example.com/zip"
                },
                {
                  "name": "KioskWatchdogSetup-1.5.0.exe",
                  "browser_download_url": "https://example.com/setup.exe"
                }
              ]
            }
            """;

        using var http = new HttpClient(new StubHandler(json));
        using var client = new GitHubUpdateClient("owner/repo", http);

        var result = await client.CheckForUpdateAsync(new Version(1, 4, 1));

        Assert.True(result.UpdateAvailable);
        Assert.Equal(new Version(1, 5, 0), result.LatestVersion);
        Assert.Equal("KioskWatchdogSetup-1.5.0.exe", result.SetupFileName);
        Assert.Equal("https://example.com/setup.exe", result.DownloadUrl.ToString());
    }

    [Fact]
    public async Task CheckForUpdateAsync_reports_up_to_date()
    {
        var json = """
            {
              "tag_name": "v1.4.1",
              "assets": [
                {
                  "name": "KioskWatchdogSetup-1.4.1.exe",
                  "browser_download_url": "https://example.com/setup.exe"
                }
              ]
            }
            """;

        using var http = new HttpClient(new StubHandler(json));
        using var client = new GitHubUpdateClient("owner/repo", http);

        var result = await client.CheckForUpdateAsync(new Version(1, 4, 1));

        Assert.False(result.UpdateAvailable);
        Assert.Equal(new Version(1, 4, 1), result.LatestVersion);
    }

    [Fact]
    public async Task GetLatestReleaseAsync_requires_setup_asset()
    {
        var json = """
            {
              "tag_name": "v1.5.0",
              "assets": [
                {
                  "name": "notes.txt",
                  "browser_download_url": "https://example.com/notes.txt"
                }
              ]
            }
            """;

        using var http = new HttpClient(new StubHandler(json));
        using var client = new GitHubUpdateClient("owner/repo", http);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetLatestReleaseAsync());
        Assert.Contains("KioskWatchdogSetup-", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _json;

        public StubHandler(string json) => _json = json;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }
}
