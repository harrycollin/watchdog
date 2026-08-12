namespace KioskWatchdog.Core.Updates;

public sealed class UpdateCheckResult
{
    public required Version CurrentVersion { get; init; }
    public required Version LatestVersion { get; init; }
    public required string TagName { get; init; }
    public required string SetupFileName { get; init; }
    public required Uri DownloadUrl { get; init; }
    public bool UpdateAvailable => LatestVersion > CurrentVersion;
}

public sealed class GitHubReleaseInfo
{
    public required string TagName { get; init; }
    public required Version Version { get; init; }
    public required string SetupFileName { get; init; }
    public required Uri DownloadUrl { get; init; }
}
