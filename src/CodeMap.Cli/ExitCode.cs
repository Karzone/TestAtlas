namespace TestAtlas.Cli;

/// <summary>The spec §7 exit-code contract, in one place.</summary>
public static class ExitCode
{
    public const int Success = 0;
    public const int Warnings = 1; // completed, but a project failed to load / gaps noted
    public const int Fatal = 2;    // no loadable projects, unwritable output
    public const int BadArgs = 3;  // usage error
}
