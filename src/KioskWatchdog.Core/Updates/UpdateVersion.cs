namespace KioskWatchdog.Core.Updates;

public static class UpdateVersion
{
    /// <summary>
    /// Parses <c>v1.4.1</c>, <c>1.4.1</c>, or informational versions with a suffix
    /// into a comparable three-part <see cref="Version"/>.
    /// </summary>
    public static bool TryParse(string? tagOrVersion, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(tagOrVersion))
            return false;

        var s = tagOrVersion.Trim();
        if (s.StartsWith('v') || s.StartsWith('V'))
            s = s[1..];

        var cut = s.IndexOfAny(['-', '+']);
        if (cut >= 0)
            s = s[..cut];

        if (!Version.TryParse(s, out var parsed))
            return false;

        version = Normalize(parsed);
        return true;
    }

    public static Version Normalize(Version version)
        => new(
            Math.Max(version.Major, 0),
            Math.Max(version.Minor, 0),
            version.Build < 0 ? 0 : version.Build);

    public static Version FromAssembly(System.Reflection.Assembly assembly)
    {
        var informational = assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion;

        if (TryParse(informational, out var fromInfo))
            return fromInfo;

        var nameVersion = assembly.GetName().Version;
        return nameVersion is null ? new Version(0, 0, 0) : Normalize(nameVersion);
    }
}
