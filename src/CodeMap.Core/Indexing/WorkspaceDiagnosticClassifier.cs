using TestAtlas.Core.Model;

namespace TestAtlas.Core.Indexing;

/// <summary>
/// Turns raw MSBuildWorkspace load messages into a categorised (code, severity) pair.
///
/// <para>The key rule — learned from indexing a real 28-project solution that emitted 428
/// "failures" while every project still loaded: a diagnostic on a project that nonetheless yielded
/// content is a <b>warning</b> (a NuGet audit note, a missing optional package, orphaned Reqnroll
/// code-behind), not a fatal error. Only a project that could not be loaded at all — or a message
/// that inherently means the project file itself is broken — is an <b>error</b>. This keeps the
/// error count and exit code honest.</para>
/// </summary>
public static class WorkspaceDiagnosticClassifier
{
    /// <summary>
    /// Classify one workspace message. <paramref name="projectLoadedWithContent"/> is true when the
    /// project the message refers to still produced classes/methods.
    /// </summary>
    public static (string Code, DiagnosticSeverity Severity) Classify(string message, bool projectLoadedWithContent)
    {
        var m = message ?? string.Empty;

        string code;
        var inherentlyFatal = false;

        if (m.Contains("Unable to find package", StringComparison.OrdinalIgnoreCase))
            code = "nuget_missing_package";
        else if (m.Contains("has a known", StringComparison.OrdinalIgnoreCase)
                 && m.Contains("vulnerability", StringComparison.OrdinalIgnoreCase))
            code = "nuget_vulnerability";
        else if (m.Contains("no feature file was found", StringComparison.OrdinalIgnoreCase))
            code = "reqnroll_orphan_codebehind";
        else if (m.Contains("could not be loaded", StringComparison.OrdinalIgnoreCase))
        {
            code = "project_load_failed";
            inherentlyFatal = true; // the project file itself is unreadable — a real error
        }
        else
        {
            code = "workspace_load";
        }

        // A project that still loaded usable content downgrades everything except an inherently
        // fatal load failure to a warning.
        var severity = projectLoadedWithContent && !inherentlyFatal
            ? DiagnosticSeverity.Warning
            : DiagnosticSeverity.Error;

        return (code, severity);
    }

    /// <summary>
    /// Pull the project path out of a message shaped like
    /// <c>… processing the file 'C:\…\X.csproj' with message: …</c>. Returns null if none is present.
    /// </summary>
    public static string? ExtractProjectPath(string message)
    {
        if (string.IsNullOrEmpty(message)) return null;
        var open = message.IndexOf('\'');
        if (open < 0) return null;
        var close = message.IndexOf('\'', open + 1);
        if (close <= open) return null;

        var candidate = message.Substring(open + 1, close - open - 1);
        return candidate.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ? candidate : null;
    }
}
