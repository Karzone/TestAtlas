using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using TestAtlas.Core.Model;
using DiagnosticSeverity = TestAtlas.Core.Model.DiagnosticSeverity;

namespace TestAtlas.Core.Indexing;

/// <summary>
/// Slice-1 indexer: loads a solution via MSBuildWorkspace and extracts Project/Class/Method
/// entities with file/line locations, capturing load diagnostics as first-class rows. Read-only,
/// offline, no AI (spec constraints). Classification is stubbed to <c>other</c> (see
/// <see cref="Classifier"/>); the Gherkin/binding layers arrive in later slices.
/// </summary>
public sealed class SolutionIndexer
{
    private static readonly string ToolVersion =
        typeof(SolutionIndexer).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    private readonly Func<DateTime> _nowUtc;

    public SolutionIndexer(Func<DateTime>? nowUtc = null)
        => _nowUtc = nowUtc ?? (() => DateTime.UtcNow);

    public async Task<IndexResult> IndexAsync(IndexOptions options, CancellationToken ct = default)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        MsBuildGuard.EnsureRegistered();

        var fullPath = Path.GetFullPath(options.SolutionPath);
        var rootDir = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
        var diagnostics = new List<DiagnosticEntity>();
        var hasher = new InputHasher();

        void Verbose(string m) => options.VerboseLog?.Invoke(m);

        using var workspace = MSBuildWorkspace.Create();
        workspace.WorkspaceFailed += (_, e) =>
        {
            var severity = e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure
                ? DiagnosticSeverity.Error
                : DiagnosticSeverity.Warning;
            diagnostics.Add(new DiagnosticEntity(
                severity,
                "workspace_load",
                e.Diagnostic.Message,
                Location: null));
        };

        IReadOnlyList<Project> csProjects;
        int expectedCsProjects;
        try
        {
            if (fullPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            {
                Verbose($"Opening solution {fullPath}");
                var solution = await workspace.OpenSolutionAsync(fullPath, cancellationToken: ct);
                csProjects = solution.Projects
                    .Where(p => p.Language == LanguageNames.CSharp)
                    .ToList();
                expectedCsProjects = CountCsharpProjectsInSln(fullPath);
            }
            else
            {
                Verbose($"Opening project {fullPath}");
                var project = await workspace.OpenProjectAsync(fullPath, cancellationToken: ct);
                csProjects = project.Language == LanguageNames.CSharp
                    ? new[] { project }
                    : Array.Empty<Project>();
                expectedCsProjects = 1;
            }
        }
        catch (Exception ex)
        {
            // A total failure to open is fatal — but still return a (project-less) result so the
            // CLI can write an empty map + diagnostic rather than crashing.
            diagnostics.Add(new DiagnosticEntity(
                DiagnosticSeverity.Error, "open_failed", ex.Message, Location: null));
            var failMeta = new MapMeta(ToolVersion, FormatUtc(_nowUtc()), fullPath, hasher.Compute());
            return new IndexResult(
                failMeta,
                Array.Empty<ProjectEntity>(),
                Array.Empty<ClassEntity>(),
                Array.Empty<MethodEntity>(),
                diagnostics,
                IndexOutcome.Fatal);
        }

        // Apply include/exclude project filters (globs over name + path).
        var selected = csProjects
            .Where(p => IsSelected(p, options))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ThenBy(p => p.FilePath ?? string.Empty, StringComparer.Ordinal)
            .ToList();

        // ---- Extract (into ID-less holders so IDs can be assigned in canonical order) ----
        var projectHolders = new List<ProjectHolder>();
        foreach (var project in selected)
        {
            ct.ThrowIfCancellationRequested();
            Verbose($"Indexing project {project.Name}");
            var holder = new ProjectHolder
            {
                Name = project.Name,
                Path = ToRelative(rootDir, project.FilePath),
                TargetFramework = ReadTargetFramework(project),
            };

            if (project.FilePath is { } pf && File.Exists(pf))
                hasher.Add(ToRelative(rootDir, pf), SafeRead(pf));

            foreach (var document in project.Documents)
            {
                ct.ThrowIfCancellationRequested();
                if (document.FilePath is not { } docPath) continue;
                if (!docPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;

                var relDoc = ToRelative(rootDir, docPath);
                if (File.Exists(docPath)) hasher.Add(relDoc, SafeRead(docPath));

                await ExtractDocumentAsync(document, relDoc, holder, ct);
            }

            projectHolders.Add(holder);
        }

        // ---- Assign deterministic IDs in canonical order ----
        var projects = new List<ProjectEntity>();
        var classes = new List<ClassEntity>();
        var methods = new List<MethodEntity>();
        var projectId = 0;
        var classId = 0;
        var methodId = 0;

        foreach (var ph in projectHolders) // already ordered by (Name, Path)
        {
            projectId++;
            var pid = projectId;

            var orderedClasses = ph.Classes
                .OrderBy(c => c.Namespace, StringComparer.Ordinal)
                .ThenBy(c => c.Name, StringComparer.Ordinal)
                .ThenBy(c => c.FilePath, StringComparer.Ordinal)
                .ThenBy(c => c.LineStart)
                .ToList();

            projects.Add(new ProjectEntity(
                pid, ph.Name, ph.Path, ph.TargetFramework,
                Classifier.SummariseProject(Enumerable.Empty<ClassEntity>())));

            foreach (var ch in orderedClasses)
            {
                classId++;
                var cid = classId;
                classes.Add(new ClassEntity(
                    cid, pid, ch.Name, ch.Namespace, ch.BaseType, ch.Kind,
                    ch.FilePath, ch.LineStart, ch.LineEnd));

                var orderedMethods = ch.Methods
                    .OrderBy(m => m.Name, StringComparer.Ordinal)
                    .ThenBy(m => m.Signature, StringComparer.Ordinal)
                    .ThenBy(m => m.LineStart)
                    .ToList();

                foreach (var mh in orderedMethods)
                {
                    methodId++;
                    methods.Add(new MethodEntity(
                        methodId, cid, pid, mh.Name, mh.Signature, mh.Visibility, mh.Kind,
                        mh.FilePath, mh.LineStart, mh.LineEnd));
                }
            }
        }

        // ---- Outcome (spec §7 exit-code contract) ----
        var loadedCount = selected.Count;
        var anyLoadError = diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
        if (loadedCount < expectedCsProjects && expectedCsProjects > 0)
        {
            diagnostics.Add(new DiagnosticEntity(
                DiagnosticSeverity.Error,
                "project_not_loaded",
                $"{expectedCsProjects - loadedCount} of {expectedCsProjects} C# project(s) failed to load.",
                Location: null));
            anyLoadError = true;
        }

        var outcome = csProjects.Count == 0
            ? IndexOutcome.Fatal
            : anyLoadError
                ? IndexOutcome.CompletedWithWarnings
                : IndexOutcome.Success;

        var meta = new MapMeta(ToolVersion, FormatUtc(_nowUtc()), fullPath, hasher.Compute());
        return new IndexResult(meta, projects, classes, methods, diagnostics, outcome);
    }

    private static async Task ExtractDocumentAsync(
        Document document, string relDoc, ProjectHolder holder, CancellationToken ct)
    {
        try
        {
            if (await document.GetSyntaxRootAsync(ct) is not { } root) return;
            var tree = root.SyntaxTree;

            foreach (var type in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                // LineStart points at the declared name (the `class X` line), not any leading
                // attribute; LineEnd is the closing brace.
                var ch = new ClassHolder
                {
                    Name = type.Identifier.ValueText,
                    Namespace = ResolveNamespace(type),
                    BaseType = type.BaseList?.Types.FirstOrDefault()?.Type.ToString(),
                    Kind = Classifier.ClassifyClass(type, model: null),
                    FilePath = relDoc,
                    LineStart = tree.GetLineSpan(type.Identifier.Span).StartLinePosition.Line + 1,
                    LineEnd = tree.GetLineSpan(type.Span).EndLinePosition.Line + 1,
                };

                foreach (var method in type.Members.OfType<MethodDeclarationSyntax>())
                {
                    ch.Methods.Add(new MethodHolder
                    {
                        Name = method.Identifier.ValueText,
                        Signature = BuildSignature(method),
                        Visibility = ResolveVisibility(method.Modifiers, memberDefaultPrivate: true),
                        Kind = Classifier.ClassifyMethod(method, model: null),
                        FilePath = relDoc,
                        LineStart = tree.GetLineSpan(method.Identifier.Span).StartLinePosition.Line + 1,
                        LineEnd = tree.GetLineSpan(method.Span).EndLinePosition.Line + 1,
                    });
                }

                holder.Classes.Add(ch);
            }
        }
        catch
        {
            // A single malformed document must never break the run (spec G4/G2). Record and move on.
            holder.Classes.Add(new ClassHolder
            {
                Name = "<parse-error>",
                Namespace = string.Empty,
                Kind = Kinds.Other,
                FilePath = relDoc,
                LineStart = 0,
                LineEnd = 0,
            });
        }
    }

    private static string ResolveNamespace(SyntaxNode node)
    {
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            if (current is BaseNamespaceDeclarationSyntax ns)
                return ns.Name.ToString();
        }
        return string.Empty;
    }

    private static string BuildSignature(MethodDeclarationSyntax method)
    {
        var types = method.ParameterList.Parameters
            .Select(p => p.Type?.ToString() ?? "?");
        return $"{method.Identifier.ValueText}({string.Join(", ", types)})";
    }

    private static string ResolveVisibility(SyntaxTokenList modifiers, bool memberDefaultPrivate)
    {
        var isPublic = modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword));
        var isProtected = modifiers.Any(m => m.IsKind(SyntaxKind.ProtectedKeyword));
        var isInternal = modifiers.Any(m => m.IsKind(SyntaxKind.InternalKeyword));
        var isPrivate = modifiers.Any(m => m.IsKind(SyntaxKind.PrivateKeyword));

        if (isPublic) return "public";
        if (isProtected && isInternal) return "protected internal";
        if (isProtected) return "protected";
        if (isInternal) return "internal";
        if (isPrivate) return "private";
        return memberDefaultPrivate ? "private" : "internal";
    }

    private static string? ReadTargetFramework(Project project)
    {
        // ParseOptions/CompilationOptions don't carry the TFM cleanly; read it from the csproj text.
        if (project.FilePath is not { } path || !File.Exists(path)) return null;
        try
        {
            var text = File.ReadAllText(path);
            var single = Regex.Match(text, "<TargetFramework>([^<]+)</TargetFramework>");
            if (single.Success) return single.Groups[1].Value.Trim();
            var multi = Regex.Match(text, "<TargetFrameworks>([^<]+)</TargetFrameworks>");
            if (multi.Success) return multi.Groups[1].Value.Trim();
        }
        catch
        {
            // Non-fatal.
        }
        return null;
    }

    private static int CountCsharpProjectsInSln(string slnPath)
    {
        try
        {
            var count = 0;
            foreach (var line in File.ReadLines(slnPath))
            {
                // Project("{GUID}") = "Name", "relative\path.csproj", "{GUID}"
                var m = Regex.Match(line, "^Project\\(\"\\{[^}]+\\}\"\\)\\s*=\\s*\"[^\"]*\",\\s*\"([^\"]+)\"");
                if (m.Success && m.Groups[1].Value.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                    count++;
            }
            return count;
        }
        catch
        {
            return 0;
        }
    }

    private static string ToRelative(string rootDir, string? path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        try
        {
            return Path.GetRelativePath(rootDir, path).Replace('\\', '/');
        }
        catch
        {
            return path.Replace('\\', '/');
        }
    }

    private static string SafeRead(string path)
    {
        try { return File.ReadAllText(path); }
        catch { return string.Empty; }
    }

    private static bool IsSelected(Project project, IndexOptions options)
    {
        var name = project.Name;
        var path = project.FilePath ?? string.Empty;

        if (options.Include.Count > 0 &&
            !options.Include.Any(g => Glob.IsMatch(g, name, path)))
            return false;

        if (options.Exclude.Any(g => Glob.IsMatch(g, name, path)))
            return false;

        return true;
    }

    private static string FormatUtc(DateTime utc)
        => utc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");

    // ---- ID-less extraction holders ----
    private sealed class ProjectHolder
    {
        public string Name = string.Empty;
        public string Path = string.Empty;
        public string? TargetFramework;
        public List<ClassHolder> Classes { get; } = new();
    }

    private sealed class ClassHolder
    {
        public string Name = string.Empty;
        public string Namespace = string.Empty;
        public string? BaseType;
        public string Kind = Kinds.Other;
        public string FilePath = string.Empty;
        public int LineStart;
        public int LineEnd;
        public List<MethodHolder> Methods { get; } = new();
    }

    private sealed class MethodHolder
    {
        public string Name = string.Empty;
        public string Signature = string.Empty;
        public string Visibility = "private";
        public string Kind = Kinds.Other;
        public string FilePath = string.Empty;
        public int LineStart;
        public int LineEnd;
    }
}
