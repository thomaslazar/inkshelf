using System.Reflection;

namespace Inkshelf;

// The build's own version string, shown on the libraries and login pages so a
// deployed image can be identified. InformationalVersion, not AssemblyVersion,
// because the Docker build stamps non-release images as "<version>+pr-34.a1b2c3d"
// (see the csproj note); a version with no "+" suffix means a release build.
public static class AppVersion
{
    public static string Current { get; } =
        typeof(AppVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AppVersion).Assembly.GetName().Version?.ToString(3)
        ?? "0";
}
