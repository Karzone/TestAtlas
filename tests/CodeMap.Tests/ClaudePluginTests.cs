using Xunit;

namespace TestAtlas.Tests;

/// <summary>
/// The Claude Code plugin under <c>claude-plugin/</c> ships its own copy of <c>scripts/check-map-age.py</c>,
/// because a plugin hook may only reference files inside the plugin root. Two copies drift unless
/// something refuses to let them; this does.
/// </summary>
public sealed class ClaudePluginTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TestAtlas.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("TestAtlas.sln not found above the test binary");
    }

    [Fact]
    public void The_plugins_copy_of_the_staleness_checker_is_byte_identical_to_the_canonical_one()
    {
        var root = RepoRoot();
        var canonical = File.ReadAllBytes(Path.Combine(root, "scripts", "check-map-age.py"));
        var copy = File.ReadAllBytes(Path.Combine(root, "claude-plugin", "scripts", "check-map-age.py"));
        Assert.True(canonical.AsSpan().SequenceEqual(copy),
            "claude-plugin/scripts/check-map-age.py differs from scripts/check-map-age.py; copy the canonical file over it.");
    }

    [Fact]
    public void The_marketplace_entry_and_the_plugin_manifest_agree_on_the_name()
    {
        // A mismatch makes `claude plugin install testatlas@testatlas` report the plugin as not found.
        var root = RepoRoot();
        using var market = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(root, ".claude-plugin", "marketplace.json")));
        using var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "claude-plugin", ".claude-plugin", "plugin.json")));
        var entry = market.RootElement.GetProperty("plugins")[0];
        Assert.Equal(manifest.RootElement.GetProperty("name").GetString(), entry.GetProperty("name").GetString());
        Assert.Equal("./claude-plugin", entry.GetProperty("source").GetString());
    }

    [Fact]
    public void Every_skill_is_named_after_its_folder_and_offers_the_same_two_installs()
    {
        // A skill whose `name:` differs from its folder is not the slash command the README promises, and a
        // skill that drives the command-line tools must be able to say how to get both of them.
        var skills = Directory.GetDirectories(Path.Combine(RepoRoot(), "claude-plugin", "skills"));
        Assert.Contains(skills, d => Path.GetFileName(d) == "report");
        foreach (var dir in skills)
        {
            var text = File.ReadAllText(Path.Combine(dir, "SKILL.md"));
            Assert.Contains($"name: {Path.GetFileName(dir)}", File.ReadAllLines(Path.Combine(dir, "SKILL.md")));
            Assert.Contains("dotnet tool install --global TestAtlas.Cli", text);
            Assert.Contains("dotnet tool install --global TestAtlas.Mcp", text);
        }
    }
}
