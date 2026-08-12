namespace KioskWatchdog.Core.Configuration;

/// <summary>Optional in-app update checks against a public GitHub Releases page.</summary>
public sealed class UpdatesConfig
{
    public const string DefaultGitHubRepository = "harrycollin/watchdog";

    /// <summary>When true, the UI checks GitHub for a newer release when it opens.</summary>
    public bool CheckOnStartup { get; set; } = true;

    /// <summary>
    /// Public <c>owner/repo</c> whose Releases host the installer
    /// (asset name <c>KioskWatchdogSetup-*.exe</c>).
    /// </summary>
    public string GitHubRepository { get; set; } = DefaultGitHubRepository;
}
