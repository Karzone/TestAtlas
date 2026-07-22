using Microsoft.Build.Locator;

namespace TestAtlas.Core.Indexing;

/// <summary>
/// MSBuildLocator must register a .NET SDK instance <em>before</em> any MSBuild or
/// MSBuildWorkspace type is touched, and it may only register once per process. This guard
/// centralises that: call <see cref="EnsureRegistered"/> at the top of every entry point that
/// will use <c>MSBuildWorkspace</c>. It is idempotent and thread-safe.
/// </summary>
public static class MsBuildGuard
{
    private static readonly object Gate = new();
    private static bool _registered;

    /// <summary>Register the default MSBuild instance exactly once. Safe to call repeatedly.</summary>
    public static void EnsureRegistered()
    {
        if (_registered) return;
        lock (Gate)
        {
            if (_registered) return;
            if (!MSBuildLocator.IsRegistered)
            {
                // RegisterDefaults picks the SDK resolved for this process (the installed .NET SDK).
                MSBuildLocator.RegisterDefaults();
            }
            _registered = true;
        }
    }
}
