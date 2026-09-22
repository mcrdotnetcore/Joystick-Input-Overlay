using System.Reflection;

namespace JoystickInputOverlay;

/// <summary>
/// Identity of the running build, read from the assembly rather than hardcoded, so bumping
/// &lt;Version&gt; in the csproj is the only place a release number has to be maintained.
/// </summary>
internal static class AppInfo
{
    public const string Name = "Joystick Input Overlay";

    /// <summary>Just the release number, e.g. "1.0.1" — without the commit hash the SDK appends.</summary>
    public static string Version { get; } = ResolveVersion();

    public static string NameWithVersion => $"{Name} v{Version}";

    private static string ResolveVersion()
    {
        string? informational = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            // The SDK writes "1.0.1+<commit sha>"; only the part before the plus is useful here.
            int plus = informational.IndexOf('+');
            return plus > 0 ? informational[..plus] : informational;
        }

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        return version is null ? "?" : $"{version.Major}.{version.Minor}.{version.Build}";
    }
}
