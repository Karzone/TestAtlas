using System.Diagnostics;
using Microsoft.Build.Locator;

namespace TestAtlas.Core.Indexing;

/// <summary>
/// Registers a .NET SDK MSBuild instance for MSBuildWorkspace. This must happen <em>before</em>
/// any MSBuild type is touched, and only once per process. It is idempotent and thread-safe.
///
/// <para>Registration is deliberately more robust than a bare <c>RegisterDefaults()</c>: that call
/// throws "No instances of MSBuild could be detected" on Windows/dev machines where MSBuildLocator's
/// built-in SDK discovery comes up empty, even though a perfectly good SDK is installed (the process
/// is literally running on it). The ladder below falls back to locating the SDK via
/// <c>dotnet --list-sdks</c>, which is far more reliable, and honours an explicit override.</para>
/// </summary>
public static class MsBuildGuard
{
    /// <summary>Point this at an SDK directory containing MSBuild.dll to bypass all discovery.</summary>
    public const string OverrideEnvVar = "TESTATLAS_MSBUILD_PATH";

    private static readonly object Gate = new();
    private static bool _registered;

    /// <summary>Register an MSBuild instance exactly once. Safe to call repeatedly.</summary>
    public static void EnsureRegistered()
    {
        if (_registered) return;
        lock (Gate)
        {
            if (_registered) return;
            if (!MSBuildLocator.IsRegistered)
                Register();
            _registered = true;
        }
    }

    private static void Register()
    {
        // 0) Explicit override — for locked-down machines or when discovery is unreliable.
        var overridePath = Environment.GetEnvironmentVariable(OverrideEnvVar);
        if (!string.IsNullOrWhiteSpace(overridePath) && Directory.Exists(overridePath))
        {
            MSBuildLocator.RegisterMSBuildPath(overridePath);
            return;
        }

        // 1) Preferred: let MSBuildLocator discover an installed .NET SDK. We restrict discovery to
        //    DotNetSdk — a .NET (Core) process cannot host Visual Studio's .NET Framework MSBuild,
        //    and DeveloperConsole instances (from a VS dev prompt) are a known source of breakage.
        try
        {
            var instance = MSBuildLocator
                .QueryVisualStudioInstances(new VisualStudioInstanceQueryOptions
                {
                    DiscoveryTypes = DiscoveryType.DotNetSdk
                })
                .OrderByDescending(i => i.Version)
                .FirstOrDefault();

            if (instance is not null)
            {
                MSBuildLocator.RegisterInstance(instance);
                return;
            }
        }
        catch
        {
            // Fall through to the dotnet CLI probe.
        }

        // 2) Fallback: ask the dotnet CLI where the SDKs are. Works whenever `dotnet` is on PATH
        //    (it must be — the tool is running under it), even when step 1's discovery finds nothing.
        var sdkDir = TryFindNewestSdkViaDotnetCli();
        if (sdkDir is not null)
        {
            MSBuildLocator.RegisterMSBuildPath(sdkDir);
            return;
        }

        throw new InvalidOperationException(
            "Could not locate MSBuild. Install a .NET SDK (verify with `dotnet --list-sdks`), or set the " +
            $"{OverrideEnvVar} environment variable to an SDK directory that contains MSBuild.dll " +
            @"(e.g. C:\Program Files\dotnet\sdk\8.0.400).");
    }

    /// <summary>
    /// Parse <c>dotnet --list-sdks</c> (lines like <c>8.0.400 [C:\Program Files\dotnet\sdk]</c>) and
    /// return the newest SDK's full directory, which contains MSBuild.dll. Returns null on any failure.
    /// </summary>
    internal static string? TryFindNewestSdkViaDotnetCli()
    {
        string output;
        try
        {
            var psi = new ProcessStartInfo("dotnet", "--list-sdks")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
        }
        catch
        {
            return null;
        }

        return ParseNewestSdkDir(output);
    }

    /// <summary>Pure parser for <c>dotnet --list-sdks</c> output — separated out so it is unit-testable.</summary>
    public static string? ParseNewestSdkDir(string listSdksOutput)
    {
        (Version ver, string path)? best = null;
        foreach (var raw in (listSdksOutput ?? string.Empty).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            var open = line.IndexOf('[');
            var close = line.LastIndexOf(']');
            if (open <= 0 || close <= open) continue;

            var versionText = line[..open].Trim();
            var baseDir = line[(open + 1)..close].Trim();

            // Version may carry a preview suffix (e.g. 9.0.100-preview.1); parse the numeric core.
            var core = versionText.Split('-')[0];
            if (!Version.TryParse(core, out var version)) continue;

            var full = Path.Combine(baseDir, versionText);
            if (best is null || version > best.Value.ver)
                best = (version, full);
        }

        return best?.path;
    }
}
