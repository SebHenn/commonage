using System.Reflection;

namespace Commonage.Web;

public static class AppVersion
{
    /// <summary>The version from Directory.Build.props, such as "0.1.0", without the build's commit hash.</summary>
    public static string Current { get; } =
        (typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown")
        .Split('+')[0];
}
