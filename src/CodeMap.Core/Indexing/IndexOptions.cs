namespace TestAtlas.Core.Indexing;

/// <summary>Inputs to <see cref="SolutionIndexer.IndexAsync"/> (spec §7 options that affect the map).</summary>
public sealed class IndexOptions
{
    /// <summary>Path to the <c>.sln</c> or <c>.csproj</c> to analyse.</summary>
    public required string SolutionPath { get; init; }

    /// <summary>Project-name/path globs to include. Empty = include all.</summary>
    public IReadOnlyList<string> Include { get; init; } = Array.Empty<string>();

    /// <summary>Project-name/path globs to exclude. Applied after include.</summary>
    public IReadOnlyList<string> Exclude { get; init; } = Array.Empty<string>();

    /// <summary>Optional sink for <c>--verbose</c> per-project / heuristic progress lines.</summary>
    public Action<string>? VerboseLog { get; init; }
}
