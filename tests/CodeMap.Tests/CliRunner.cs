using System.Diagnostics;

namespace TestAtlas.Tests;

/// <summary>
/// Runs the built <c>testatlas</c> CLI as a separate process. MSBuildWorkspace/MSBuildLocator
/// cannot run inside the vstest host (it preloads a conflicting MSBuild), so every path that loads
/// a solution goes through the real executable — which also makes these true end-to-end tests
/// (exit codes and all). The CLI dll is copied next to the test assembly via the project reference.
/// </summary>
internal static class CliRunner
{
    public static (int Code, string StdOut, string StdErr) Run(params string[] args)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "TestAtlas.Cli.dll");
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(dll);
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, stdout, stderr);
    }

    public static (int Code, string StdOut, string StdErr) Index(string solution, string dbPath, params string[] extra)
    {
        var args = new List<string> { "index", solution, "--output", dbPath };
        args.AddRange(extra);
        return Run(args.ToArray());
    }
}
