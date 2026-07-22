using System.Security.Cryptography;
using System.Text;

namespace TestAtlas.Core.Indexing;

/// <summary>
/// Computes the deterministic content hash of the indexed inputs (spec §9): project files plus
/// source files. Consumers use it to detect staleness. Order-independent — inputs are sorted by
/// their (already relative, forward-slashed) path before hashing — so the same tree always yields
/// the same hash regardless of enumeration order.
/// </summary>
public sealed class InputHasher
{
    private readonly SortedDictionary<string, string> _inputs =
        new(StringComparer.Ordinal);

    /// <summary>Record one input's relative path and its text content.</summary>
    public void Add(string relativePath, string content)
    {
        // Last write wins; a path should only be added once, but be defensive.
        _inputs[relativePath] = content ?? string.Empty;
    }

    /// <summary>Produce the lowercase hex SHA-256 over the canonical (path\0content\0…) stream.</summary>
    public string Compute()
    {
        using var sha = SHA256.Create();
        var buffer = new StringBuilder();
        foreach (var kvp in _inputs)
        {
            buffer.Append(kvp.Key).Append('\0').Append(kvp.Value).Append('\0');
        }

        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(buffer.ToString()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
