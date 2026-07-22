using System.Text;
using System.Text.RegularExpressions;

namespace TestAtlas.Core.Indexing;

/// <summary>
/// Minimal glob → regex for project include/exclude filters (spec §7 <c>--include</c>/<c>--exclude</c>
/// and §8 config <c>exclude</c>). Supports <c>*</c> (any run except '/'), <c>**</c> (any run incl.
/// '/'), and <c>?</c>. A pattern is tested against both the project name and its file path, so both
/// <c>LegacyTests</c> and <c>**/LegacyTests.csproj</c> select the same project.
/// </summary>
public static class Glob
{
    public static bool IsMatch(string pattern, string projectName, string projectPath)
    {
        var rx = ToRegex(pattern);
        var normalisedPath = projectPath.Replace('\\', '/');
        return rx.IsMatch(projectName) || rx.IsMatch(normalisedPath);
    }

    private static Regex ToRegex(string pattern)
    {
        var p = pattern.Replace('\\', '/');
        var sb = new StringBuilder("^");
        for (var i = 0; i < p.Length; i++)
        {
            var c = p[i];
            switch (c)
            {
                case '*':
                    if (i + 1 < p.Length && p[i + 1] == '*')
                    {
                        sb.Append(".*"); // ** — cross directory separators
                        i++;
                    }
                    else
                    {
                        sb.Append("[^/]*"); // * — within a path segment
                    }
                    break;
                case '?':
                    sb.Append("[^/]");
                    break;
                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }

        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
