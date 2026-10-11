using System.Globalization;
using System.Net;
using System.Text;
using TestAtlas.Core.Model;
using TestAtlas.Core.Storage;

namespace TestAtlas.Core.Reporting;

/// <summary>
/// Renders a project-level dependency graph as a single self-contained HTML page (inline CSS, inline
/// SVG edges, HTML project cards and a little vanilla JS for pan / zoom / search / pinning — no external
/// libraries, opens offline). A directed edge A→B means "project A depends on project B": some entity
/// in A binds to / uses / inherits / holds an entity in B (derived from the map's cross-project
/// <c>binds_to</c> / <c>uses_type</c> / <c>inherits</c> / <c>holds</c> edges); a bare ProjectReference
/// with none of those is drawn dashed as a build-only reference. Projects sit in left-to-right layers —
/// entry points, shared code, foundation — ordered within each layer to cut edge crossings.
/// Deterministic: the layered layout is computed here, and the only volatile value (the timestamp) is
/// read from the map.
/// </summary>
public static class ProjectMapBuilder
{
    private sealed record Node(int Id, string Name, string Short, string Kind, int Col, int Row, double X, double Y,
        int InDegree, int OutDegree, int Endpoints);
    private sealed record ClassBit(string Name, string Kind, int Weight);
    private sealed record Link(int From, int To, int Weight, string Detail, IReadOnlyList<(string Kind, int Count)> Kinds,
        IReadOnlyList<ClassBit> Classes, bool ReferencesOnly);

    private const int MaxClassRows = 20;
    private const int MaxEndpointRows = 60;

    // Layout constants (comfortable density): card size, column / row gaps, header band above the cards.
    private const double CardW = 232, CardH = 62, GapX = 124, GapY = 18, Top = 40;
    private const double ArrowLen = 7;
    private const int SweepIterations = 3;

    // The semantic kinds a link phrase is built from, in display order.
    private static readonly string[] PhraseOrder = { EdgeKinds.UsesType, EdgeKinds.BindsTo, EdgeKinds.Inherits, EdgeKinds.Holds };

    public static string Build(MapDocument doc)
    {
        // ---- project-id lookups per entity kind (to resolve an edge's endpoints to projects) ----
        var stepProj = doc.ScenarioSteps.ToDictionary(s => (long)s.Id, s => s.ProjectId);
        var stepDefProj = doc.StepDefinitions.ToDictionary(s => (long)s.Id, s => s.ProjectId);
        var methodProj = doc.Methods.ToDictionary(m => (long)m.Id, m => m.ProjectId);
        var classProj = doc.Classes.ToDictionary(c => (long)c.Id, c => c.ProjectId);
        var projectIds = doc.Projects.Select(p => p.Id).ToHashSet();

        int? ProjectOf(string kind, int? id) => id is not int i ? null : kind switch
        {
            RefKinds.ScenarioStep => stepProj.TryGetValue(i, out var p) ? p : null,
            RefKinds.StepDefinition => stepDefProj.TryGetValue(i, out var p) ? p : null,
            RefKinds.Method => methodProj.TryGetValue(i, out var p) ? p : null,
            RefKinds.Class => classProj.TryGetValue(i, out var p) ? p : null,
            RefKinds.Project => i, // a references edge is already project-level
            _ => null,
        };

        // Resolve a binds_to target (a step definition) to the class that hosts it — so the panel can
        // drill from a project dependency down to the specific step / page-object classes behind it.
        var stepDefClass = doc.StepDefinitions.ToDictionary(s => (long)s.Id, s => s.ClassId);
        var classNameById = doc.Classes.ToDictionary(c => c.Id, c => c.Name);

        int? TargetClassOf(EdgeRow e) => e.ToId is not int t ? null : e.EdgeKind switch
        {
            EdgeKinds.BindsTo => stepDefClass.TryGetValue(t, out var cid) ? cid : (int?)null,
            EdgeKinds.UsesType or EdgeKinds.Inherits => classNameById.ContainsKey(t) ? t : (int?)null,
            _ => null,
        };

        // ---- aggregate cross-project edges: weight + kind tally (project) and per-target-class ----
        var agg = new Dictionary<(int From, int To), Dictionary<string, int>>();
        var classAgg = new Dictionary<(int From, int To), Dictionary<(int Class, string Kind), int>>();
        foreach (var e in doc.Edges)
        {
            if (e.EdgeKind == EdgeKinds.Unbound) continue;
            var from = ProjectOf(e.FromKind, e.FromId);
            var to = ProjectOf(e.ToKind, e.ToId);
            if (from is not int a || to is not int b || a == b) continue;
            if (!projectIds.Contains(a) || !projectIds.Contains(b)) continue;
            if (!agg.TryGetValue((a, b), out var byKind)) agg[(a, b)] = byKind = new();
            byKind[e.EdgeKind] = byKind.TryGetValue(e.EdgeKind, out var c) ? c + 1 : 1;

            if (TargetClassOf(e) is int tc)
            {
                if (!classAgg.TryGetValue((a, b), out var byClass)) classAgg[(a, b)] = byClass = new();
                var key = (tc, e.EdgeKind);
                byClass[key] = byClass.TryGetValue(key, out var cc) ? cc + 1 : 1;
            }
        }

        // ---- per-project API surface: the endpoints each project's methods call (slice 4) ----
        var endpointById = doc.Endpoints.ToDictionary(e => e.Id);
        var projectEndpoints = new Dictionary<int, List<(string Verb, string Route)>>();
        var endpointSets = new Dictionary<int, HashSet<(string Verb, string Route)>>();
        foreach (var e in doc.Edges)
        {
            if (e.EdgeKind != EdgeKinds.CallsEndpoint || e.ToId is not int epId) continue;
            if (!methodProj.TryGetValue(e.FromId, out var pid)) continue;
            if (!endpointById.TryGetValue(epId, out var ep)) continue;
            if (!endpointSets.TryGetValue(pid, out var set)) endpointSets[pid] = set = new();
            set.Add((ep.Verb, ep.Route));
        }
        foreach (var (pid, set) in endpointSets)
            projectEndpoints[pid] = set
                .OrderBy(e => e.Route, StringComparer.Ordinal).ThenBy(e => e.Verb, StringComparer.Ordinal)
                .Take(MaxEndpointRows).ToList();

        var links = agg
            .Select(kv =>
            {
                var classes = classAgg.TryGetValue(kv.Key, out var byClass)
                    ? byClass.OrderByDescending(x => x.Value)
                        .ThenBy(x => classNameById.TryGetValue(x.Key.Class, out var nm) ? nm : "", StringComparer.Ordinal)
                        .Take(MaxClassRows)
                        .Select(x => new ClassBit(
                            classNameById.TryGetValue(x.Key.Class, out var nm) ? nm : "?", x.Key.Kind, x.Value))
                        .ToList()
                    : new List<ClassBit>();
                // Weight is the SEMANTIC coupling (binds_to/uses_type/inherits/holds); a bare .csproj
                // reference isn't semantic weight. A link carrying only a `references` kind is a build/DI
                // dependency the semantic edges missed — drawn dashed so an infra lib connects instead of
                // reading dead.
                var semanticKinds = kv.Value.Where(x => x.Key != EdgeKinds.References).ToList();
                var semantic = semanticKinds.Sum(x => x.Value);
                var referencesOnly = semantic == 0;
                var detail = referencesOnly
                    ? "build reference (ProjectReference)"
                    : string.Join(", ", semanticKinds.OrderByDescending(x => x.Value).Select(x => $"{x.Value} {x.Key}"));
                var kinds = semanticKinds
                    .OrderBy(x => Array.IndexOf(PhraseOrder, x.Key) is var i and >= 0 ? i : PhraseOrder.Length)
                    .ThenBy(x => x.Key, StringComparer.Ordinal)
                    .Select(x => (x.Key, x.Value))
                    .ToList();
                return new Link(kv.Key.From, kv.Key.To, semantic, detail, kinds, classes, referencesOnly);
            })
            .OrderBy(l => l.From).ThenBy(l => l.To)
            .ToList();
        var maxWeight = links.Count == 0 || links.All(l => l.Weight == 0) ? 1 : links.Max(l => l.Weight);

        // ---- layered layout (deterministic: projects ordered by name, then by barycentre sweeps) ----
        var ordered = doc.Projects.OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
        var n = ordered.Count;
        var prefix = CommonPrefix(ordered.Select(p => p.Name).ToList());
        var shortName = ordered.ToDictionary(p => p.Id, p => p.Name[prefix.Length..]);
        var cols = Layer(ordered.Select(p => p.Id).ToList(), shortName, links);

        var colHeights = cols.Select(c => c.Count * CardH + Math.Max(0, c.Count - 1) * GapY).ToList();
        var maxColH = colHeights.Count == 0 ? 0 : colHeights.Max();
        var canvasW = cols.Count * CardW + Math.Max(0, cols.Count - 1) * GapX;
        var canvasH = Top + maxColH;

        var outDeg = links.GroupBy(l => l.From).ToDictionary(g => g.Key, g => g.Count());
        var inDeg = links.GroupBy(l => l.To).ToDictionary(g => g.Key, g => g.Count());
        var byId = ordered.ToDictionary(p => p.Id);
        var nodes = new Dictionary<int, Node>();
        for (var ci = 0; ci < cols.Count; ci++)
        {
            for (var ri = 0; ri < cols[ci].Count; ri++)
            {
                var p = byId[cols[ci][ri]];
                nodes[p.Id] = new Node(p.Id, p.Name, shortName[p.Id], p.Kind, ci, ri,
                    ci * (CardW + GapX), Top + (maxColH - colHeights[ci]) / 2 + ri * (CardH + GapY),
                    inDeg.TryGetValue(p.Id, out var ind) ? ind : 0, outDeg.TryGetValue(p.Id, out var od) ? od : 0,
                    projectEndpoints.TryGetValue(p.Id, out var eps) ? eps.Count : 0);
            }
        }

        // ---- render ----
        var solutionName = Path.GetFileName(Meta(doc, MapSchema.MetaSolutionPath, "(solution)"));
        var sb = new StringBuilder(48 * 1024);
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.Append("<title>TestAtlas map — ").Append(E(solutionName)).Append("</title><style>").Append(Css).Append("</style>");
        sb.Append("<script>").Append(ThemeInit).Append("</script></head><body>");

        AppendHeader(sb, solutionName, n, links, withSearch: n > 0);

        if (n == 0)
        {
            sb.Append("<p class=\"empty\">No projects in this map.</p>");
            sb.Append("<script>").Append(ThemeJs).Append("</script></body></html>");
            return sb.ToString();
        }

        // The canvas: one stage that pans and zooms, holding the edge SVG (under), column headers and cards.
        sb.Append("<div id=\"view\"><div id=\"stage\" style=\"width:").Append(F(canvasW)).Append("px;height:")
          .Append(F(canvasH)).Append("px\">");

        sb.Append("<svg id=\"g\" width=\"").Append(F(canvasW)).Append("\" height=\"").Append(F(canvasH))
          .Append("\" aria-hidden=\"true\"><g id=\"edges\">");
        for (var i = 0; i < links.Count; i++)
        {
            var l = links[i];
            var (path, arrow) = EdgePath(nodes[l.From], nodes[l.To]);
            var w = l.ReferencesOnly ? 1.25 : 1.25 + 2.5 * l.Weight / maxWeight;
            sb.Append("<g class=\"eg").Append(l.ReferencesOnly ? " rf" : "").Append("\" data-i=\"").Append(i).Append("\">");
            sb.Append("<path class=\"edge").Append(l.ReferencesOnly ? " ref" : "").Append("\" data-a=\"")
              .Append(l.From).Append("\" data-b=\"").Append(l.To).Append("\" d=\"")
              .Append(path).Append("\" stroke-width=\"").Append(F(w)).Append("\"/>");
            sb.Append("<path class=\"arw\" d=\"").Append(arrow).Append("\"/></g>");
        }
        sb.Append("</g><g id=\"hits\">");
        for (var i = 0; i < links.Count; i++)
        {
            var (path, _) = EdgePath(nodes[links[i].From], nodes[links[i].To]);
            sb.Append("<path class=\"hit\" data-i=\"").Append(i).Append("\" d=\"").Append(path).Append("\"/>");
        }
        sb.Append("</g></svg>");

        for (var ci = 0; ci < cols.Count; ci++)
        {
            sb.Append("<div class=\"colh\" style=\"left:").Append(F(ci * (CardW + GapX))).Append("px;width:").Append(F(CardW))
              .Append("px\"><b>").Append(ColumnLabel(ci, cols.Count)).Append("</b><span>").Append(cols[ci].Count).Append("</span></div>");
        }

        foreach (var p in ordered)
        {
            var nd = nodes[p.Id];
            var kindLabel = KindLabel(p.Kind);
            // (kind labels are fixed ASCII; the separator stays literal rather than an &#183; entity)
            var sub = nd.Endpoints == 0 ? kindLabel
                : $"{kindLabel} · {nd.Endpoints} endpoint{(nd.Endpoints == 1 ? "" : "s")}";
            sb.Append("<div class=\"node k-").Append(KindClass(p.Kind)).Append("\" data-id=\"").Append(p.Id)
              .Append("\" role=\"button\" tabindex=\"0\" aria-pressed=\"false\" aria-label=\"")
              .Append(E($"{nd.Short}, {kindLabel}, uses {nd.OutDegree}, used by {nd.InDegree}"))
              .Append("\" style=\"left:").Append(F(nd.X)).Append("px;top:").Append(F(nd.Y)).Append("px\">");
            sb.Append("<div class=\"tile\">").Append(Icon(p.Kind, 17)).Append("</div>");
            sb.Append("<div class=\"txt\"><div class=\"nm\">").Append(E(nd.Short)).Append("</div><div class=\"sub\">")
              .Append(sub).Append("</div></div>");
            sb.Append("<span class=\"dbadge\" hidden></span>");
            if (nd.OutDegree > 0) sb.Append("<span class=\"port\" aria-hidden=\"true\"></span>");
            sb.Append("</div>");
        }
        sb.Append("</div></div>");

        AppendDock(sb);

        // Details panel (project or dependency) and the edge tooltip — populated by JS from the blob.
        sb.Append("<aside id=\"panel\" aria-label=\"Details\" hidden></aside>");
        sb.Append("<div id=\"tip\" role=\"tooltip\" hidden></div>");

        // Data blob: node labels / kinds / layout + directed weighted links, for the panel and highlights.
        sb.Append("<script>window.__MAP__=");
        AppendJson(sb, solutionName, prefix, ordered, nodes, links, projectEndpoints, canvasW, canvasH);
        sb.Append(";</script>");

        sb.Append("<script>").Append(ThemeJs).Append(Js).Append("</script></body></html>");
        return sb.ToString();
    }

    /// <summary>
    /// Longest run of leading dot-segments shared by every name, where each name keeps at least one
    /// segment after it — "SampleShop.Core" + "SampleShop.Tests.Api" → "SampleShop.". "" when none.
    /// </summary>
    private static string CommonPrefix(IReadOnlyList<string> names)
    {
        if (names.Count == 0) return "";
        var segs = names.Select(s => s.Split('.')).ToList();
        var pre = 0;
        while (segs.All(s => s.Length > pre + 1 && s[pre] == segs[0][pre])) pre++;
        return pre == 0 ? "" : string.Join('.', segs[0].Take(pre)) + ".";
    }

    /// <summary>
    /// Assigns each project to a column by its longest outgoing path (all links, build-only included):
    /// entry points land in column 0, foundations in the last. Cycles are guarded — a back edge counts as
    /// 0. Within a column, projects start in name order, then three down/up barycentre sweeps reorder
    /// them to cut crossings (stable: ties keep their previous position).
    /// </summary>
    private static List<List<int>> Layer(List<int> idsByName, IReadOnlyDictionary<int, string> shortName, List<Link> links)
    {
        var outs = idsByName.ToDictionary(id => id, _ => new List<int>());
        var ins = idsByName.ToDictionary(id => id, _ => new List<int>());
        foreach (var l in links)
        {
            outs[l.From].Add(l.To);
            ins[l.To].Add(l.From);
        }

        var depth = new Dictionary<int, int>();
        var visiting = new HashSet<int>();
        int Depth(int id)
        {
            if (depth.TryGetValue(id, out var known)) return known;
            if (!visiting.Add(id)) return 0; // back edge in a cycle
            var d = 0;
            foreach (var to in outs[id]) d = Math.Max(d, 1 + Depth(to));
            visiting.Remove(id);
            depth[id] = d;
            return d;
        }
        foreach (var id in idsByName) Depth(id);

        var maxDepth = idsByName.Count == 0 ? 0 : idsByName.Max(id => depth[id]);
        var cols = Enumerable.Range(0, idsByName.Count == 0 ? 0 : maxDepth + 1).Select(_ => new List<int>()).ToList();
        foreach (var id in idsByName) cols[maxDepth - depth[id]].Add(id);
        for (var c = 0; c < cols.Count; c++)
            cols[c] = cols[c].OrderBy(id => shortName[id], StringComparer.Ordinal).ToList();

        var pos = new Dictionary<int, double>();
        void SetPos()
        {
            foreach (var col in cols)
                for (var i = 0; i < col.Count; i++) pos[col[i]] = i - (col.Count - 1) / 2.0;
        }
        void Sweep(int c, Dictionary<int, List<int>> neighbours)
        {
            var key = cols[c].ToDictionary(id => id, id =>
            {
                var nb = neighbours[id];
                if (nb.Count == 0) return pos[id];
                var sum = 0.0;
                foreach (var x in nb) sum += pos[x];
                return sum / nb.Count;
            });
            cols[c] = cols[c].OrderBy(id => key[id]).ThenBy(id => pos[id]).ToList();
            SetPos();
        }

        SetPos();
        for (var it = 0; it < SweepIterations; it++)
        {
            for (var c = 1; c < cols.Count; c++) Sweep(c, ins);
            for (var c = cols.Count - 2; c >= 0; c--) Sweep(c, outs);
        }
        return cols;
    }

    private static string ColumnLabel(int col, int count) =>
        count == 1 ? "Projects"
        : col == 0 ? "Entry points"
        : col == count - 1 ? "Foundation"
        : count == 3 ? "Shared code"
        : "Shared code, tier " + (count - 1 - col).ToString(CultureInfo.InvariantCulture);

    /// <summary>A left-to-right cubic from a's output port to just before b's left edge, plus its arrowhead.</summary>
    private static (string Path, string Arrow) EdgePath(Node a, Node b)
    {
        var sx = a.X + CardW + 5;
        var sy = a.Y + CardH / 2;
        var tip = b.X - 2;
        var ey = b.Y + CardH / 2;
        var ex = tip - ArrowLen + 1;
        var dx = Math.Max(40, (ex - sx) * 0.5);
        var path = $"M{F(sx)} {F(sy)}C{F(sx + dx)} {F(sy)} {F(ex - dx)} {F(ey)} {F(ex)} {F(ey)}";
        var arrow = $"M{F(tip - ArrowLen)} {F(ey - 4.5)}L{F(tip)} {F(ey)}L{F(tip - ArrowLen)} {F(ey + 4.5)}Z";
        return (path, arrow);
    }

    private static void AppendHeader(StringBuilder sb, string solutionName, int projects, List<Link> links, bool withSearch)
    {
        var codeLinks = links.Count(l => !l.ReferencesOnly);
        var refOnlyLinks = links.Count - codeLinks;

        sb.Append("<header class=\"top\" id=\"top\"><div class=\"brandg\"><div class=\"brand\">")
          .Append("<span class=\"wm\">Test<span>Atlas</span></span><span class=\"tag\">Project map</span></div>")
          .Append("<span class=\"vr\" aria-hidden=\"true\"></span><h1>").Append(E(solutionName)).Append("</h1></div>");
        sb.Append("<div class=\"figs\">");
        Figure(sb, projects, "project", "projects");
        Figure(sb, codeLinks, "code dependency", "code dependencies");
        Figure(sb, refOnlyLinks, "build-only reference", "build-only references");
        sb.Append("</div><div class=\"right\">");
        if (withSearch)
        {
            sb.Append("<div class=\"search\" id=\"search\"><input id=\"q\" type=\"text\" placeholder=\"Search projects\" ")
              .Append("aria-label=\"Search projects\" autocomplete=\"off\" spellcheck=\"false\" role=\"combobox\" ")
              .Append("aria-expanded=\"false\" aria-controls=\"qres\" aria-autocomplete=\"list\">")
              .Append("<span class=\"kbd\" aria-hidden=\"true\">/</span>")
              .Append("<div id=\"qres\" role=\"listbox\" aria-label=\"Projects\" hidden></div></div>");
        }
        sb.Append("<button type=\"button\" class=\"ic\" id=\"themeBtn\" onclick=\"toggleTheme()\" title=\"Switch to light theme\" ")
          .Append("aria-label=\"Switch to light theme\">◐</button>");
        sb.Append("</div></header>");
    }

    private static void Figure(StringBuilder sb, int count, string one, string many)
        => sb.Append("<div class=\"fig\"><b>").Append(count).Append("</b><span>").Append(count == 1 ? one : many).Append("</span></div>");

    private static void AppendDock(StringBuilder sb)
    {
        sb.Append("<div class=\"dock\" id=\"dock\"><div class=\"zrow\">");
        sb.Append("<button type=\"button\" class=\"ic\" id=\"zout\" title=\"Zoom out (−)\" aria-label=\"Zoom out (−)\">−</button>");
        sb.Append("<button type=\"button\" class=\"zpct\" id=\"zpct\" title=\"Reset zoom to 100% (0)\" aria-label=\"Reset zoom to 100% (0)\">100%</button>");
        sb.Append("<button type=\"button\" class=\"ic\" id=\"zin\" title=\"Zoom in (+)\" aria-label=\"Zoom in (+)\">+</button>");
        sb.Append("<button type=\"button\" class=\"ic\" id=\"zfit\" title=\"Fit to view (F)\" aria-label=\"Fit to view (F)\">")
          .Append("<svg class=\"ico\" width=\"16\" height=\"16\" viewBox=\"0 0 24 24\" aria-hidden=\"true\">").Append(FitGlyph).Append("</svg></button>");
        sb.Append("</div><div class=\"key\"><div class=\"key-t\">Arrows point to what a project depends on</div><div class=\"key-g\">");
        KeySample(sb, "stroke:var(--ink-faint);stroke-width:2;stroke-linecap:round", "var(--ink-faint)", "Code dependency");
        KeySample(sb, "stroke:var(--ink-faint);stroke-width:1.4;stroke-dasharray:4 3", "var(--ink-faint)", "Build-only reference");
        KeySample(sb, "stroke:var(--fill-primary);stroke-width:2;stroke-linecap:round", "var(--fill-primary)", "What it uses");
        KeySample(sb, "stroke:var(--violet);stroke-width:2;stroke-linecap:round", "var(--violet)", "What uses it");
        sb.Append("</div><div class=\"key-s\">/ search · F fit · Arrow keys move · T scope · Esc clear</div></div></div>");
    }

    private static void KeySample(StringBuilder sb, string lineStyle, string fill, string label)
        => sb.Append("<span><svg width=\"30\" height=\"10\" viewBox=\"0 0 30 10\" aria-hidden=\"true\"><path d=\"M1 5H22\" style=\"")
             .Append(lineStyle).Append("\"/><path d=\"M22 1.5 29 5 22 8.5z\" style=\"fill:").Append(fill).Append("\"/></svg>")
             .Append(label).Append("</span>");

    private static string Icon(string kind, int size)
        => "<svg class=\"ico\" width=\"" + size + "\" height=\"" + size + "\" viewBox=\"0 0 24 24\" aria-hidden=\"true\">"
           + KindClass(kind) switch
           {
               "bdd_tests" => "<path d=\"M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8z\"/><path d=\"M14 3v5h5\"/><path d=\"m9 14.5 2 2 4-4.5\"/>",
               "unit_tests" => "<path d=\"M9.5 3h5\"/><path d=\"M10.5 3v6L5.2 18.1A2 2 0 0 0 6.9 21h10.2a2 2 0 0 0 1.7-2.9L13.5 9V3\"/><path d=\"M7.6 15h8.8\"/>",
               "shared_library" => "<path d=\"M12 3 3 8l9 5 9-5-9-5z\"/><path d=\"M3 13l9 5 9-5\"/>",
               _ => "<rect x=\"4\" y=\"4\" width=\"16\" height=\"16\" rx=\"3\"/><path d=\"M4 9.5h16\"/>",
           }
           + "</svg>";

    private const string FitGlyph =
        "<path d=\"M4 9V5a1 1 0 0 1 1-1h4\"/><path d=\"M15 4h4a1 1 0 0 1 1 1v4\"/><path d=\"M20 15v4a1 1 0 0 1-1 1h-4\"/><path d=\"M9 20H5a1 1 0 0 1-1-1v-4\"/>";

    private static void AppendJson(StringBuilder sb, string solutionName, string prefix, List<ProjectRow> ordered,
        IReadOnlyDictionary<int, Node> nodes, List<Link> links,
        IReadOnlyDictionary<int, List<(string Verb, string Route)>> projectEndpoints, double canvasW, double canvasH)
    {
        sb.Append("{\"solution\":").Append(J(solutionName)).Append(",\"prefix\":").Append(J(prefix));
        sb.Append(",\"lay\":{\"W\":").Append(F(CardW)).Append(",\"H\":").Append(F(CardH))
          .Append(",\"w\":").Append(F(canvasW)).Append(",\"h\":").Append(F(canvasH)).Append('}');
        sb.Append(",\"n\":{");
        var first = true;
        foreach (var p in ordered)
        {
            if (!first) sb.Append(',');
            first = false;
            var nd = nodes[p.Id];
            sb.Append('"').Append(p.Id).Append("\":{\"name\":").Append(J(p.Name))
              .Append(",\"short\":").Append(J(nd.Short))
              .Append(",\"kind\":").Append(J(p.Kind))
              .Append(",\"in\":").Append(nd.InDegree).Append(",\"out\":").Append(nd.OutDegree)
              .Append(",\"col\":").Append(nd.Col).Append(",\"row\":").Append(nd.Row)
              .Append(",\"x\":").Append(F(nd.X)).Append(",\"y\":").Append(F(nd.Y));

            if (projectEndpoints.TryGetValue(p.Id, out var eps) && eps.Count > 0)
            {
                sb.Append(",\"eps\":[");
                for (var k = 0; k < eps.Count; k++)
                {
                    if (k > 0) sb.Append(',');
                    sb.Append("{\"v\":").Append(J(eps[k].Verb)).Append(",\"r\":").Append(J(eps[k].Route)).Append('}');
                }
                sb.Append(']');
            }
            sb.Append('}');
        }
        sb.Append("},\"links\":[");
        for (var i = 0; i < links.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var l = links[i];
            sb.Append("{\"a\":").Append(l.From).Append(",\"b\":").Append(l.To)
              .Append(",\"w\":").Append(l.Weight).Append(",\"d\":").Append(J(l.Detail)).Append(",\"k\":{");
            for (var j = 0; j < l.Kinds.Count; j++)
            {
                if (j > 0) sb.Append(',');
                sb.Append(J(l.Kinds[j].Kind)).Append(':').Append(l.Kinds[j].Count);
            }
            sb.Append("},\"ref\":").Append(l.ReferencesOnly ? "true" : "false").Append(",\"cls\":[");
            for (var j = 0; j < l.Classes.Count; j++)
            {
                if (j > 0) sb.Append(',');
                var cb = l.Classes[j];
                sb.Append("{\"c\":").Append(J(cb.Name)).Append(",\"k\":").Append(J(cb.Kind))
                  .Append(",\"w\":").Append(cb.Weight).Append('}');
            }
            sb.Append("]}");
        }
        sb.Append("]}");
    }

    /// <summary>JSON string literal, safe to embed inside a &lt;script&gt; (escapes &lt; and &amp;).</summary>
    private static string J(string s)
    {
        var sb = new StringBuilder(s.Length + 2).Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '<': sb.Append("\\u003c"); break;
                case '&': sb.Append("\\u0026"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    private static string KindClass(string kind) => kind switch
    {
        Kinds.BddTests => "bdd_tests",
        Kinds.UnitTests => "unit_tests",
        Kinds.SharedLibrary => "shared_library",
        _ => "other",
    };

    private static string KindLabel(string kind) => kind switch
    {
        Kinds.BddTests => "BDD tests",
        Kinds.UnitTests => "Unit tests",
        Kinds.SharedLibrary => "Library",
        _ => "Other",
    };

    private static string Meta(MapDocument doc, string key, string fallback)
        => doc.Meta.TryGetValue(key, out var v) && v.Length > 0 ? v : fallback;

    private static string F(double d) => d.ToString("0.##", CultureInfo.InvariantCulture);
    private static string E(string s) => WebUtility.HtmlEncode(s);

    // Design tokens (JobFocus design system). Dark is the default; light applies when the OS prefers it
    // (unless the user forced dark) or when the user forced light.
    private const string LightTokens = """
        --canvas:#e2e7ef;--surface-card:#ffffff;--surface-card-muted:#f4f6fa;--surface-raised:#ffffff;
        --surface-input:#eef1f7;--bg-2:#d9dfe9;--line:#e0e3eb;--line-soft:#ebedf3;--line-strong:#ced4e0;
        --row-hover:#f2f5f9;--ink:#1a1f2b;--ink-dim:#475061;--ink-faint:#5d6575;--ink-ghost:#c5cad4;
        --fill-primary:#056aa6;--fill-primary-hover:#055a8f;--amber-glow:rgba(6,118,184,0.10);--violet:#6e4dd9;
        --mint:#1e9266;--sky:#0e6cc0;--rose:#c93b50;--atmosphere-tint-a:rgba(6,118,184,0.05);
        --atmosphere-tint-b:rgba(14,108,192,0.04);--card-shadow:0 1px 2px rgba(20,30,60,0.07);
        --card-shadow-hover:0 2px 6px rgba(20,30,60,0.12);
        --shadow-lift:0 1px 0 rgba(255,255,255,0.6) inset,0 18px 36px -18px rgba(20,30,60,0.18);color-scheme:light;
        """;

    private const string Css = """
        :root,[data-theme="dark"]{--canvas:#0a0c11;--surface-card:#14171f;--surface-card-muted:#181c26;--surface-raised:#1e2330;
        --surface-input:#0e1117;--bg-2:#11141b;--line:#262c3a;--line-soft:#1c212c;--line-strong:#343c4d;--row-hover:#1b2129;
        --ink:#eef1f6;--ink-dim:#aab3c5;--ink-faint:#7d8798;--ink-ghost:#444c5e;--fill-primary:#38bdf8;--fill-primary-hover:#0ea5e0;
        --amber-glow:rgba(56,189,248,0.16);--violet:#b79bff;--mint:#5ce0a8;--sky:#6fb7ff;--rose:#ff7a8a;
        --atmosphere-tint-a:rgba(56,189,248,0.16);--atmosphere-tint-b:rgba(111,183,255,0.06);
        --card-shadow:0 1px 0 rgba(255,255,255,0.04) inset,0 1px 2px rgba(0,0,0,0.45);
        --card-shadow-hover:0 1px 0 rgba(255,255,255,0.05) inset,0 2px 6px rgba(0,0,0,0.55);
        --shadow-lift:0 1px 0 rgba(255,255,255,0.05) inset,0 28px 60px -28px rgba(0,0,0,0.95);color-scheme:dark;
        --sans:'Hanken Grotesk',system-ui,-apple-system,sans-serif;--serif:Fraunces,Georgia,'Times New Roman',serif;
        --mono:'JetBrains Mono',ui-monospace,'SFMono-Regular',Consolas,monospace}
        """
        + "@media(prefers-color-scheme:light){:root:not([data-theme=\"dark\"]){" + LightTokens + "}}"
        + ":root[data-theme=\"light\"]{" + LightTokens + "}"
        + """
        *{box-sizing:border-box}html,body{height:100%;overflow:hidden}[hidden]{display:none!important}
        body{margin:0;font:14px/1.5 var(--sans);color:var(--ink);background-color:var(--canvas);
        background-image:radial-gradient(900px 520px at 78% -8%,var(--atmosphere-tint-a),transparent 60%),
        radial-gradient(700px 600px at -5% 8%,var(--atmosphere-tint-b),transparent 55%);-webkit-font-smoothing:antialiased}
        button{font:inherit;color:inherit}
        :focus-visible{outline:2px solid var(--fill-primary);outline-offset:2px}
        .ico{fill:none;stroke:currentColor;stroke-width:1.8;stroke-linecap:round;stroke-linejoin:round}
        .empty{position:fixed;inset:0;display:flex;align-items:center;justify-content:center;margin:0;color:var(--ink-faint)}
        #view{position:fixed;inset:0;overflow:hidden;touch-action:none;cursor:grab;-webkit-user-select:none;user-select:none;
        background-image:linear-gradient(var(--line-soft) 1px,transparent 1px),linear-gradient(90deg,var(--line-soft) 1px,transparent 1px)}
        #view.grabbing{cursor:grabbing}
        #stage{position:absolute;left:0;top:0;transform-origin:0 0}
        #g{position:absolute;left:0;top:0;overflow:visible;pointer-events:none}
        .colh{position:absolute;top:0;display:flex;align-items:baseline;gap:8px;padding-left:2px;white-space:nowrap;pointer-events:none}
        .colh b{font-size:12.5px;font-weight:600;color:var(--ink-dim)}
        .colh span{font-family:var(--mono);font-size:11.5px;color:var(--ink-faint);font-variant-numeric:tabular-nums}
        .node{position:absolute;width:232px;height:62px;display:flex;align-items:center;gap:10px;padding:0 12px 0 10px;
        background:var(--surface-card);border:1px solid var(--line);border-radius:14px;box-shadow:var(--card-shadow);
        cursor:pointer;user-select:none;-webkit-user-select:none}
        .node:hover{transform:translateY(-1px)}
        .tile{flex:none;width:34px;height:34px;border-radius:9px;display:flex;align-items:center;justify-content:center;
        background:var(--surface-card-muted);border:1px solid var(--line-soft);color:var(--ink-dim)}
        .txt{flex:1;min-width:0;display:flex;flex-direction:column;gap:1px}
        .nm{font-size:13.5px;font-weight:600;line-height:1.3;color:var(--ink);white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
        .sub{font-size:12px;line-height:1.3;color:var(--ink-faint);white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
        .port{position:absolute;right:-5px;top:50%;width:9px;height:9px;margin-top:-4.5px;border-radius:999px;
        background:var(--surface-card);border:1.5px solid var(--line-strong)}
        .dbadge{position:absolute;top:-9px;right:12px;font-family:var(--mono);font-size:11px;font-weight:500;line-height:1;
        padding:3px 7px;border-radius:999px;font-variant-numeric:tabular-nums;white-space:nowrap;--h:var(--fill-primary);color:var(--h);
        background:color-mix(in srgb,var(--h) 14%,var(--surface-card));border:1px solid color-mix(in srgb,var(--h) 45%,var(--surface-card))}
        .dbadge.up{--h:var(--violet)}
        .node.r-focus{border-color:var(--ink-ghost);box-shadow:var(--card-shadow-hover)}
        .node.r-focus .tile{color:var(--ink)}
        .node.r-pin{border-color:var(--fill-primary);box-shadow:0 0 0 1px var(--fill-primary),var(--card-shadow-hover)}
        .node.r-pin .tile{color:var(--fill-primary)}
        .node.r-focus .port,.node.r-pin .port{border-color:var(--fill-primary)}
        .node.r-down{border-color:color-mix(in srgb,var(--fill-primary) 55%,var(--line))}
        .node.r-down .tile{color:var(--fill-primary)}.node.r-down .port{border-color:var(--fill-primary)}
        .node.r-up{border-color:color-mix(in srgb,var(--violet) 55%,var(--line))}
        .node.r-up .tile{color:var(--violet)}.node.r-up .port{border-color:var(--violet)}
        .node.r-end{border-color:var(--ink-faint)}.node.r-end .tile{color:var(--ink)}
        .node.r-dim{opacity:.32}
        .eg{opacity:.55}.eg.rf{opacity:.4}
        .edge{fill:none;stroke:var(--ink-faint);stroke-linecap:round}
        .edge.ref{stroke-dasharray:4 4;stroke-linecap:butt}
        .arw{fill:var(--ink-faint)}
        .eg.r-hover,.eg.r-down,.eg.r-up,.eg.r-sel{opacity:1}
        .eg.r-hover .edge{stroke:var(--ink-dim)}.eg.r-hover .arw{fill:var(--ink-dim)}
        .eg.r-down .edge{stroke:var(--fill-primary)}.eg.r-down .arw{fill:var(--fill-primary)}
        .eg.r-up .edge{stroke:var(--violet)}.eg.r-up .arw{fill:var(--violet)}
        .eg.r-sel .edge{stroke:var(--ink)}.eg.r-sel .arw{fill:var(--ink)}
        .eg.r-dim{opacity:.1}
        .hit{fill:none;stroke:transparent;stroke-width:14;pointer-events:stroke;cursor:pointer}
        @media(prefers-reduced-motion:no-preference){
        .node{transition:opacity .14s ease,border-color .14s ease,box-shadow .14s ease,transform .14s ease}
        .tile,.port{transition:color .14s ease,border-color .14s ease}
        .eg{transition:opacity .14s ease}.edge{transition:stroke .14s ease}.arw{transition:fill .14s ease}}
        .top{position:fixed;top:0;left:0;right:0;z-index:70;display:flex;flex-wrap:wrap;align-items:center;gap:10px 28px;
        padding:10px 20px;background:linear-gradient(180deg,color-mix(in srgb,var(--surface-card) 92%,transparent),
        color-mix(in srgb,var(--surface-card) 80%,transparent));backdrop-filter:blur(12px);-webkit-backdrop-filter:blur(12px);
        border-bottom:1px solid var(--line)}
        .brandg{display:flex;align-items:center;gap:14px;min-width:0}
        .brand{display:flex;align-items:baseline;gap:8px;white-space:nowrap}
        .wm{font-size:15px;font-weight:700;letter-spacing:-.01em;color:var(--ink-dim)}.wm span{color:var(--fill-primary)}
        .tag{font-size:12.5px;color:var(--ink-faint)}
        .vr{flex:none;width:1px;height:22px;background:var(--line)}
        h1{margin:0;min-width:0;font-family:var(--serif);font-size:19px;font-weight:600;line-height:1.2;letter-spacing:-.01em;
        white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
        .figs{display:flex;align-items:baseline;gap:6px 20px;flex-wrap:wrap}
        .fig{display:flex;align-items:baseline;gap:6px;white-space:nowrap}
        .fig b{font-family:var(--mono);font-size:17px;font-weight:700;letter-spacing:-.5px;font-variant-numeric:tabular-nums;color:var(--ink)}
        .fig span{font-size:12.5px;font-weight:500;color:var(--ink-faint)}
        .right{margin-left:auto;display:flex;align-items:center;gap:8px}
        .search{position:relative;width:min(320px,70vw)}
        #q{display:block;width:100%;font:inherit;font-size:13.5px;line-height:1.4;padding:9px 16px;border-radius:999px;
        background:var(--surface-input);border:1px solid var(--line-strong);color:var(--ink);outline:none;
        transition:border-color .14s ease,box-shadow .14s ease}
        #q::placeholder{color:var(--ink-faint)}
        #q:focus{border-color:var(--fill-primary);box-shadow:0 0 0 3px var(--amber-glow)}
        .kbd{position:absolute;right:12px;top:50%;transform:translateY(-50%);pointer-events:none;font-family:var(--mono);
        font-size:11px;line-height:1;color:var(--ink-faint);border:1px solid var(--line-strong);border-radius:6px;padding:3px 6px}
        .search.has .kbd{display:none}
        #qres{position:absolute;top:calc(100% + 6px);left:0;right:0;z-index:80;display:flex;flex-direction:column;gap:2px;
        max-height:360px;overflow:auto;padding:6px;background:var(--surface-raised);border:1px solid var(--line);
        border-radius:14px;box-shadow:var(--shadow-lift)}
        .opt{display:flex;align-items:center;gap:10px;padding:8px 10px;border-radius:9px;cursor:pointer}
        .opt.act{background:var(--row-hover);box-shadow:inset 2px 0 0 var(--fill-primary)}
        .opt .on{flex:1;min-width:0;font-size:13.5px;font-weight:600;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
        .opt .ok{flex:none;font-size:12px;color:var(--ink-faint)}
        .nores{padding:10px;font-size:12.5px;color:var(--ink-faint)}
        .ic{flex:none;width:32px;height:30px;padding:0;display:inline-flex;align-items:center;justify-content:center;
        border-radius:9px;border:1px solid var(--line);background:var(--surface-card);color:var(--ink);font-size:14px;
        line-height:1;cursor:pointer;transition:background .14s ease,border-color .14s ease,color .14s ease}
        .ic:hover{background:var(--row-hover);border-color:var(--ink-ghost)}
        .dock{position:fixed;left:16px;bottom:16px;z-index:40;display:flex;flex-direction:column;align-items:flex-start;
        gap:8px;max-width:calc(100vw - 32px)}
        .zrow{display:flex;align-items:center;gap:4px}
        .zpct{height:30px;min-width:56px;padding:0 8px;border-radius:9px;border:1px solid var(--line);background:var(--surface-card);
        color:var(--ink-dim);font-family:var(--mono);font-size:12px;font-variant-numeric:tabular-nums;cursor:pointer;
        transition:border-color .14s ease,color .14s ease}
        .zpct:hover{border-color:var(--ink-ghost);color:var(--ink)}
        .key{display:flex;flex-direction:column;gap:8px;padding:11px 14px;background:var(--surface-card);border:1px solid var(--line);
        border-radius:14px;box-shadow:var(--card-shadow);max-width:100%}
        .key-t{font-size:12px;font-weight:600;color:var(--ink)}
        .key-g{display:grid;grid-template-columns:repeat(2,max-content);gap:6px 18px;font-size:12px;color:var(--ink-dim)}
        .key-g span{display:flex;align-items:center;gap:8px}
        .key-s{font-size:11.5px;color:var(--ink-faint)}
        #panel{position:fixed;top:72px;right:12px;bottom:12px;width:min(372px,calc(100vw - 24px));z-index:60;display:flex;
        flex-direction:column;background:var(--surface-card);border:1px solid var(--line);border-radius:14px;
        box-shadow:var(--shadow-lift);overflow:hidden}
        .ph{padding:16px 16px 14px;display:flex;flex-direction:column;gap:14px;border-bottom:1px solid var(--line-soft)}
        .ph-row{display:flex;align-items:flex-start;gap:12px}
        .ptile{flex:none;width:42px;height:42px;border-radius:9px;display:flex;align-items:center;justify-content:center;
        background:var(--surface-card-muted);border:1px solid var(--line-soft);color:var(--fill-primary)}
        .ptxt{flex:1;min-width:0;display:flex;flex-direction:column;gap:5px}
        .pt{font-family:var(--serif);font-size:20px;font-weight:600;line-height:1.2;letter-spacing:-.01em;overflow-wrap:anywhere}
        .pmeta{display:flex;align-items:center;gap:6px 8px;flex-wrap:wrap}
        .kbadge{font-size:11px;font-weight:700;line-height:1.3;padding:3px 8px;border-radius:6px;border:1px solid var(--line);
        background:var(--bg-2);color:var(--ink-dim)}
        .pfull{font-family:var(--mono);font-size:11.5px;color:var(--ink-faint);overflow-wrap:anywhere}
        .scope{display:flex;align-items:center;gap:8px;flex-wrap:wrap}
        .scope>span{font-size:12px;font-weight:500;color:var(--ink-faint)}
        .chip{font-size:12px;line-height:1.3;padding:5px 11px;border-radius:999px;border:1px solid var(--line);
        background:var(--surface-card-muted);color:var(--ink-dim);cursor:pointer;
        transition:background .14s ease,border-color .14s ease,color .14s ease}
        .chip:hover{border-color:var(--ink-ghost)}
        .chip.on{color:var(--fill-primary);border-color:var(--fill-primary);background:var(--amber-glow)}
        .stats{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:8px}
        .stat{display:flex;align-items:baseline;justify-content:space-between;gap:8px;padding:11px 13px;background:var(--surface-card);
        border:1px solid var(--line);border-radius:9px;box-shadow:var(--card-shadow)}
        .sv{font-family:var(--mono);font-size:21px;font-weight:700;line-height:1.1;letter-spacing:-.5px;
        font-variant-numeric:tabular-nums;color:var(--ink)}
        .sl{font-size:11.5px;font-weight:500;color:var(--ink-faint)}
        .impact{font-size:12.5px;line-height:1.5;color:var(--ink-dim);background:var(--surface-card-muted);
        border:1px solid var(--line-soft);border-radius:9px;padding:9px 11px;text-wrap:pretty}
        .pb{flex:1;min-height:0;overflow:auto;padding:6px 6px 16px}
        .sec{display:flex;align-items:center;gap:8px;padding:12px 10px 6px}.sec.s2{padding-top:16px}.sec.s3{padding-bottom:8px}
        .sec b{font-size:12.5px;font-weight:600;color:var(--ink)}
        .sec .c{font-family:var(--mono);font-size:11.5px;color:var(--ink-faint)}
        .dot{flex:none;width:8px;height:8px;border-radius:999px;background:var(--fill-primary)}.dot.up{background:var(--violet)}
        .pempty{padding:2px 10px 8px;font-size:12.5px;color:var(--ink-faint)}
        .grp{padding:8px 10px 2px;font-size:11.5px;font-weight:500;color:var(--ink-faint)}
        .row{display:flex;flex-direction:column;gap:2px;padding:8px 10px;border-radius:9px;cursor:pointer;transition:background .14s ease}
        .row:hover{background:var(--row-hover)}
        .r1{display:flex;align-items:baseline;gap:10px}
        .rn{flex:1;min-width:0;font-size:13.5px;font-weight:600;line-height:1.3;overflow-wrap:anywhere}
        .rk{flex:none;font-size:12px;color:var(--ink-faint)}
        .rd{font-size:12px;line-height:1.4;color:var(--ink-faint)}
        .clsw{padding:0 10px 6px}
        .clist{margin:0 10px 8px;border:1px solid var(--line-soft);border-radius:9px;background:var(--surface-card-muted);overflow:hidden}
        .crow{display:flex;justify-content:space-between;align-items:baseline;gap:10px;padding:7px 10px;border-top:1px solid var(--line-soft)}
        .crow:first-child{border-top-color:transparent}
        .ecls .clist{margin:0 10px}.ecls .crow{padding:8px 10px}
        .cn{font-family:var(--mono);font-size:12px;color:var(--ink);overflow-wrap:anywhere}
        .cw{flex:none;font-size:12px;color:var(--ink-faint)}
        .ep{display:flex;align-items:center;gap:10px;padding:6px 10px}
        .verb{flex:none;min-width:52px;text-align:center;font-family:var(--mono);font-size:10.5px;font-weight:700;line-height:1.4;
        padding:1px 6px;border-radius:6px;--h:var(--ink-faint);color:var(--h);border:1px solid color-mix(in srgb,var(--h) 35%,transparent);
        background:color-mix(in srgb,var(--h) 10%,transparent)}
        .verb.v-get{--h:var(--mint)}.verb.v-post,.verb.v-put,.verb.v-patch{--h:var(--sky)}.verb.v-delete{--h:var(--rose)}
        .route{flex:1;min-width:0;font-family:var(--mono);font-size:12px;color:var(--ink);overflow-wrap:anywhere}
        .opill{flex:none;font-size:11px;color:var(--ink-faint);border:1px solid var(--line);border-radius:999px;padding:1px 7px}
        .eh{padding:16px;display:flex;flex-direction:column;gap:14px;border-bottom:1px solid var(--line-soft)}
        .ek{display:flex;align-items:center;gap:8px}.ek>span{flex:1;font-size:12.5px;font-weight:600;color:var(--ink-dim)}
        .et{font-family:var(--serif);font-size:19px;font-weight:600;line-height:1.3;letter-spacing:-.01em;text-wrap:pretty}
        .ft{display:grid;grid-template-columns:minmax(0,1fr) auto minmax(0,1fr);align-items:center;gap:8px}
        .mini{min-width:0;display:flex;flex-direction:column;gap:2px;padding:8px 10px;border:1px solid var(--line);border-radius:9px;
        background:var(--surface-card-muted);cursor:pointer;transition:border-color .14s ease}
        .mini:hover{border-color:var(--ink-ghost)}
        .mini b{font-size:13px;font-weight:600;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
        .mini span{font-size:11.5px;color:var(--ink-faint)}
        .arr{font-size:16px;color:var(--ink-faint)}
        .es{font-size:13px;line-height:1.5;color:var(--ink-dim);text-wrap:pretty}
        #tip{position:fixed;z-index:90;pointer-events:none;max-width:300px;display:flex;flex-direction:column;gap:3px;padding:8px 11px;
        background:var(--surface-raised);border:1px solid var(--line-strong);border-radius:9px;box-shadow:var(--shadow-lift)}
        .tt{font-size:12.5px;font-weight:600;color:var(--ink)}.td{font-size:12px;color:var(--ink-dim)}.th{font-size:11.5px;color:var(--ink-faint)}
        """;

    // Runs in <head> before paint so a saved manual theme choice applies without a flash.
    private const string ThemeInit = """
        (function(){try{var s=localStorage.getItem('testatlas:theme');if(s)document.documentElement.setAttribute('data-theme',s);}catch(e){}})();
        """;

    // Light / dark toggle: with nothing saved the page follows prefers-color-scheme (dark when there is none).
    private const string ThemeJs = """
        function currentTheme(){
          var t=document.documentElement.getAttribute('data-theme');
          if(t==='light'||t==='dark') return t;
          return window.matchMedia&&matchMedia('(prefers-color-scheme: light)').matches?'light':'dark';
        }
        function syncThemeButton(){
          var b=document.getElementById('themeBtn'); if(!b) return;
          var l=currentTheme()==='dark'?'Switch to light theme':'Switch to dark theme';
          b.title=l; b.setAttribute('aria-label',l);
        }
        function toggleTheme(){
          var next=currentTheme()==='dark'?'light':'dark';
          document.documentElement.setAttribute('data-theme',next);
          try{localStorage.setItem('testatlas:theme',next);}catch(e){}
          syncThemeButton();
        }
        syncThemeButton();

        """;

    private const string Js = """
        (function(){
          var view=document.getElementById('view'); if(!view) return;
          var DATA=window.__MAP__||{n:{},links:[]};
          var N=DATA.n, E=DATA.links, LAY=DATA.lay, ids=Object.keys(N);
          var stage=document.getElementById('stage'), layer=document.getElementById('edges');
          var bar=document.getElementById('top'), dock=document.getElementById('dock');
          var panel=document.getElementById('panel'), tip=document.getElementById('tip'), zpct=document.getElementById('zpct');
          var qIn=document.getElementById('q'), qRes=document.getElementById('qres'), qBox=document.getElementById('search');
          var KIND={bdd_tests:'BDD tests',unit_tests:'Unit tests',shared_library:'Library'};
          var ROLES=['focus','pin','down','up','end','dim'], EROLES=['none','hover','down','up','sel','dim'];
          var Z={dim:0,none:1,hover:2,up:3,down:3,sel:4};

          function kl(k){return KIND[k]||'Other';}
          function isTest(k){return k==='bdd_tests'||k==='unit_tests';}
          function cap(t){return t?t.charAt(0).toUpperCase()+t.slice(1):t;}
          function esc(s){return String(s==null?'':s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');}
          function vcls(v){v=(v||'').toUpperCase();return v==='GET'||v==='POST'||v==='PUT'||v==='PATCH'||v==='DELETE'?v.toLowerCase():'any';}

          // ---- graph ----
          var nodeEl={}, COLS=[], edgeG=[], edgeLine=[], baseW=[];
          ids.forEach(function(id){
            var n=N[id]; n.id=id; n.o=[]; n.i=[]; n.eps=n.eps||[];
            nodeEl[id]=stage.querySelector('.node[data-id="'+id+'"]');
            (COLS[n.col]||(COLS[n.col]=[]))[n.row]=id;
          });
          E.forEach(function(e,i){
            e.i=i; e.a=String(e.a); e.b=String(e.b); e.k=e.k||{}; e.cls=e.cls||[];
            N[e.a].o.push(i); N[e.b].i.push(i);
            edgeG[i]=layer.querySelector('.eg[data-i="'+i+'"]');
            edgeLine[i]=edgeG[i].querySelector('.edge');
            baseW[i]=parseFloat(edgeLine[i].getAttribute('stroke-width'))||1.25;
          });

          // Plain-language phrasing of a link and of one class behind it.
          function phrase(e){
            if(e.ref) return 'build reference only';
            var k=e.k, o=[];
            if(k.uses_type) o.push('uses types '+k.uses_type+'×');
            if(k.binds_to) o.push('binds steps '+k.binds_to+'×');
            if(k.inherits) o.push('inherits '+k.inherits+'×');
            if(k.holds) o.push('holds as members '+k.holds+'×');
            return o.join(' · ');
          }
          function classWhat(c){
            if(c.k==='uses_type') return 'used '+c.w+'×';
            if(c.k==='inherits') return 'inherited '+c.w+'×';
            if(c.k==='binds_to') return c.w+(c.w===1?' step bound':' steps bound');
            if(c.k==='holds') return 'held '+c.w+'×';
            return c.w+' '+c.k;
          }

          // Breadth-first reach from a project, downstream (what it uses) or upstream (what uses it).
          function bfs(start,dir,codeOnly){
            var depth={}, via={}, viaEdge={}, q=[start]; depth[start]=0;
            while(q.length){
              var id=q.shift();
              (dir==='down'?N[id].o:N[id].i).forEach(function(ei){
                var e=E[ei]; if(codeOnly&&e.ref) return;
                var nx=dir==='down'?e.b:e.a;
                if(depth[nx]==null){depth[nx]=depth[id]+1; via[nx]=id; viaEdge[nx]=ei; q.push(nx);}
              });
            }
            delete depth[start];
            return {depth:depth,via:via,viaEdge:viaEdge};
          }
          // Transitive reach prefers code paths; a project reachable only through a build-only reference
          // takes its depth and parent from the all-links walk.
          function reach(start,dir,trans){
            var out={depth:{},via:{},viaEdge:{}};
            if(!trans){
              (dir==='down'?N[start].o:N[start].i).forEach(function(ei){
                var e=E[ei], nx=dir==='down'?e.b:e.a;
                out.depth[nx]=1; out.via[nx]=start; out.viaEdge[nx]=ei;
              });
              return out;
            }
            var code=bfs(start,dir,true), all=bfs(start,dir,false);
            Object.keys(all.depth).forEach(function(id){
              var src=code.depth[id]!=null?code:all;
              out.depth[id]=src.depth[id]; out.via[id]=src.via[id]; out.viaEdge[id]=src.viaEdge[id];
            });
            return out;
          }

          // ---- state ----
          var S={tx:0,ty:0,k:1,pinned:null,hover:null,hoverEdge:null,tipX:0,tipY:0,selEdge:null,scope:'transitive',
            q:'',searchFocused:false,searchIdx:0,open:{},barH:60};
          var userMoved=false, down=null, wasDrag=false, grabbing=false, raf=0, F=null, panelKey='', edgeOrder='', results=[];
          var reduced=!!(window.matchMedia&&matchMedia('(prefers-reduced-motion: reduce)').matches);
          function panelOpen(){return !!S.pinned||S.selEdge!=null;}

          // ---- highlight roles ----
          function compute(){
            var selE=S.selEdge!=null?E[S.selEdge]:null, focus=selE?null:(S.pinned||S.hover);
            var trans=!!S.pinned&&!selE&&S.scope==='transitive';
            F={selE:selE,focus:focus,trans:trans,
              down:focus?reach(focus,'down',trans):null, up:focus?reach(focus,'up',trans):null};
          }
          function nodeRole(id){
            if(F.selE) return id===F.selE.a||id===F.selE.b?'end':'dim';
            if(!F.focus) return 'none';
            if(id===F.focus) return S.pinned===id?'pin':'focus';
            if(F.down.depth[id]!=null) return 'down';
            if(F.up.depth[id]!=null) return 'up';
            return 'dim';
          }
          function edgeRole(e){
            var r;
            if(F.selE) r=e.i===F.selE.i?'sel':'dim';
            else if(!F.focus) r='none';
            else if(F.trans){
              if((e.a===F.focus||F.down.depth[e.a]!=null)&&F.down.depth[e.b]!=null) r='down';
              else if((e.b===F.focus||F.up.depth[e.b]!=null)&&F.up.depth[e.a]!=null) r='up';
              else r='dim';
            } else r=e.a===F.focus?'down':e.b===F.focus?'up':'dim';
            if(S.hoverEdge===e.i&&(r==='none'||r==='dim')) r='hover';
            return r;
          }
          function applyRoles(){
            ids.forEach(function(id){
              var el=nodeEl[id], r=nodeRole(id);
              ROLES.forEach(function(x){el.classList.toggle('r-'+x,x===r);});
              el.setAttribute('aria-pressed',r==='pin'?'true':'false');
              var b=el.querySelector('.dbadge'), d=r==='down'?F.down.depth[id]:r==='up'?F.up.depth[id]:null;
              if(F.trans&&d!=null){b.textContent=(r==='down'?'→ ':'← ')+d; b.classList.toggle('up',r==='up'); b.hidden=false;}
              else b.hidden=true;
            });
            // Highlighted edges draw last, so their arrowheads win where several arrows meet at one card.
            var items=E.map(function(e){return {i:e.i,r:edgeRole(e)};});
            items.forEach(function(it){
              var g=edgeG[it.i];
              EROLES.forEach(function(x){g.classList.toggle('r-'+x,x===it.r);});
              edgeLine[it.i].setAttribute('stroke-width',String(baseW[it.i]+(it.r==='hover'||it.r==='sel'?0.75:0)));
            });
            items.sort(function(x,y){return Z[x.r]-Z[y.r]||x.i-y.i;});
            var ord=items.map(function(it){return it.i;}).join(',');
            if(ord!==edgeOrder){edgeOrder=ord; items.forEach(function(it){layer.appendChild(edgeG[it.i]);});}
          }

          // ---- details panel ----
          function icon(id,size){
            var s=nodeEl[id].querySelector('.tile svg').cloneNode(true);
            s.setAttribute('width',size); s.setAttribute('height',size);
            return s.outerHTML;
          }
          var closeBtn='<button type="button" class="ic" data-act="close" title="Close details (Esc)" aria-label="Close details (Esc)">✕</button>';
          function clsRows(cls){
            return cls.map(function(c){return '<div class="crow"><span class="cn">'+esc(c.c)+'</span><span class="cw">'+esc(classWhat(c))+'</span></div>';}).join('');
          }
          function rows(R,dir,trans){
            var list=Object.keys(R.depth), last=0, h='';
            list.sort(function(x,y){
              return R.depth[x]-R.depth[y]||E[R.viaEdge[y]].w-E[R.viaEdge[x]].w||N[x].short.localeCompare(N[y].short);
            });
            list.forEach(function(oid){
              var d=R.depth[oid], e=E[R.viaEdge[oid]], key=dir+':'+e.i, open=!!S.open[key], n=N[oid];
              if(trans&&d!==last) h+='<div class="grp">'+(d===1?'Direct':d+' steps away')+'</div>';
              last=d;
              h+='<div class="row" role="button" tabindex="0" data-pin="'+oid+'" title="Pin this project"><div class="r1"><span class="rn">'
                +esc(n.short)+'</span><span class="rk">'+esc(kl(n.kind))+'</span></div><div class="rd">'
                +esc(cap((d>1?'via '+N[R.via[oid]].short+' · ':'')+phrase(e)))+'</div></div>';
              if(e.cls.length){
                h+='<div class="clsw"><button type="button" class="chip'+(open?' on':'')+'" data-toggle="'+key+'" aria-expanded="'+open+'">'
                  +e.cls.length+(e.cls.length===1?' class ':' classes ')+(open?'▴':'▾')+'</button></div>';
                if(open) h+='<div class="clist">'+clsRows(e.cls)+'</div>';
              }
            });
            return {n:list.length,html:h};
          }
          function impact(id){
            var all=F.trans?F.up:reach(id,'up',true);
            var t=Object.keys(all.depth).filter(function(x){return isTest(N[x].kind);})
              .map(function(x){return N[x].short;}).sort(function(a,b){return a.localeCompare(b);});
            if(!t.length) return 'No test project depends on this, directly or indirectly.';
            var shown=t.slice(0,4), more=t.length-shown.length;
            var list=more?shown.join(', ')+' and '+more+' more'
              :shown.length>1?shown.slice(0,-1).join(', ')+' and '+shown[shown.length-1]:shown[0];
            return 'Changes here can affect '+t.length+(t.length===1?' test project: ':' test projects: ')+list+'.';
          }
          function sec(cls,title,count){
            return '<div class="sec'+cls+'">'+(cls.indexOf('s3')<0?'<span class="dot'+(cls.indexOf('s2')>=0?' up':'')+'" aria-hidden="true"></span>':'')
              +'<b>'+title+'</b><span class="c">'+count+'</span></div>';
          }
          function chip(scope,label){
            var on=S.scope===scope;
            return '<button type="button" class="chip'+(on?' on':'')+'" data-scope="'+scope+'" aria-pressed="'+on+'">'+label+'</button>';
          }
          function projectPanel(id){
            var n=N[id], dn=rows(F.down,'d',F.trans), up=rows(F.up,'u',F.trans);
            var h='<div class="ph"><div class="ph-row"><div class="ptile">'+icon(id,20)+'</div><div class="ptxt"><div class="pt">'
              +esc(n.short)+'</div><div class="pmeta"><span class="kbadge">'+esc(kl(n.kind))+'</span><span class="pfull">'
              +esc(n.name)+'</span></div></div>'+closeBtn+'</div>';
            h+='<div class="scope"><span>Show</span>'+chip('direct','Direct')+chip('transitive','Transitive')+'</div>';
            h+='<div class="stats"><div class="stat"><span class="sv">'+dn.n+'</span><span class="sl">Uses</span></div>'
              +'<div class="stat"><span class="sv">'+up.n+'</span><span class="sl">Used by</span></div></div>';
            if(!isTest(n.kind)) h+='<div class="impact">'+esc(impact(id))+'</div>';
            h+='</div><div class="pb">';
            h+=sec('','Depends on',dn.n)+(dn.n?dn.html:'<div class="pempty">Depends on no other project in this solution.</div>');
            h+=sec(' s2','Used by',up.n)+(up.n?up.html:'<div class="pempty">No project in this solution depends on it.</div>');
            if(n.eps.length){
              h+='<div class="sec s2"><b>API endpoints called</b><span class="c">'+n.eps.length+'</span></div>';
              h+=n.eps.map(function(e){
                return '<div class="ep"><span class="verb v-'+vcls(e.v)+'">'+esc(e.v)+'</span><span class="route">'+esc(e.r)+'</span>'
                  +(e.r.indexOf('/')<0?'<span class="opill">operation</span>':'')+'</div>';
              }).join('');
            }
            return h+'</div>';
          }
          function mini(id){
            return '<div class="mini" role="button" tabindex="0" data-pin="'+id+'" title="Pin this project"><b>'+esc(N[id].short)
              +'</b><span>'+esc(kl(N[id].kind))+'</span></div>';
          }
          function edgePanel(e){
            var A=N[e.a], B=N[e.b];
            var h='<div class="eh"><div class="ek"><span>'+(e.ref?'Build-only reference':'Code dependency')+'</span>'+closeBtn+'</div>';
            h+='<div class="et">'+esc(A.short+' depends on '+B.short)+'</div>';
            h+='<div class="ft">'+mini(e.a)+'<span class="arr" aria-hidden="true">→</span>'+mini(e.b)+'</div>';
            h+='<div class="es">'+esc(e.ref?'The project file of '+A.short+' references '+B.short
              +', but no code in it uses, binds to or inherits anything there.':cap(phrase(e))+'.')+'</div></div>';
            if(e.cls.length) h+='<div class="pb ecls">'+sec(' s3','Classes behind it',e.cls.length)+'<div class="clist">'+clsRows(e.cls)+'</div></div>';
            return h;
          }
          function renderPanel(){
            var open=panelOpen();
            var key=!open?'':S.selEdge!=null?'e'+S.selEdge:'p'+S.pinned+'|'+S.scope+'|'+JSON.stringify(S.open);
            if(key===panelKey) return;
            var same=open&&panelKey.split('|')[0]===key.split('|')[0], pb=panel.querySelector('.pb'), st=pb&&same?pb.scrollTop:0;
            var ae=document.activeElement, fsel=null;
            if(same&&ae&&panel.contains(ae)) ['data-toggle','data-scope','data-pin','data-act'].some(function(a){
              var v=ae.getAttribute(a); if(v!=null){fsel='['+a+'="'+v+'"]'; return true;} return false;
            });
            panelKey=key;
            if(!open){panel.hidden=true; panel.innerHTML=''; return;}
            panel.style.top=(S.barH+12)+'px';
            panel.innerHTML=S.selEdge!=null?edgePanel(E[S.selEdge]):projectPanel(S.pinned);
            panel.hidden=false;
            pb=panel.querySelector('.pb'); if(pb) pb.scrollTop=st;
            if(fsel){var f=panel.querySelector(fsel); if(f) f.focus({preventScroll:true});}
          }
          panel.addEventListener('click',function(ev){
            var t=ev.target.closest('[data-pin],[data-toggle],[data-scope],[data-act]'); if(!t) return;
            if(t.hasAttribute('data-pin')) pin(t.getAttribute('data-pin'));
            else if(t.hasAttribute('data-toggle')){var k=t.getAttribute('data-toggle'); S.open[k]=!S.open[k]; update();}
            else if(t.hasAttribute('data-scope')){S.scope=t.getAttribute('data-scope'); update();}
            else unpin();
          });

          // ---- edge tooltip ----
          function placeTip(){
            tip.style.left=Math.max(8,Math.min(S.tipX+14,innerWidth-316))+'px';
            tip.style.top=Math.min(S.tipY+16,innerHeight-90)+'px';
          }
          function renderTip(){
            var te=S.hoverEdge!=null&&!grabbing?E[S.hoverEdge]:null;
            if(!te||S.selEdge===S.hoverEdge){tip.hidden=true; return;}
            tip.innerHTML='<div class="tt">'+esc(N[te.a].short+' → '+N[te.b].short)+'</div><div class="td">'+esc(cap(phrase(te)))
              +'</div><div class="th">'+(te.cls.length?'Click to see the classes behind it':'Click for details')+'</div>';
            tip.hidden=false; placeTip();
          }

          function update(){compute(); applyRoles(); renderPanel(); renderTip();}

          // ---- view: pan / zoom / fit ----
          function applyView(){
            stage.style.transform='translate('+S.tx+'px,'+S.ty+'px) scale('+S.k+')';
            var g=34*S.k*(S.k<0.6?2:1);
            view.style.backgroundSize=g+'px '+g+'px';
            view.style.backgroundPosition=S.tx+'px '+S.ty+'px';
            zpct.textContent=Math.round(S.k*100)+'%';
          }
          function setView(t){S.tx=t.tx; S.ty=t.ty; S.k=t.k; applyView();}
          function region(){
            var vw=innerWidth, vh=innerHeight, p=panelOpen()&&vw>760?384:0;
            var lh=dock?dock.getBoundingClientRect().height+28:150;
            return {L:28,R:vw-28-p,T:S.barH+24,B:vh-lh};
          }
          function fitK(){var R=region(); return Math.max(0.3,Math.min(1.15,(R.R-R.L)/LAY.w,(R.B-R.T)/LAY.h));}
          function fitView(anim){
            var R=region(), k=fitK(), t={k:k,tx:R.L+(R.R-R.L-LAY.w*k)/2,ty:R.T+(R.B-R.T-LAY.h*k)/2};
            if(anim) animateTo(t); else {cancelAnimationFrame(raf); setView(t);}
          }
          function centerOn(id){
            var n=N[id], R=region(), k=Math.max(S.k,0.8);
            userMoved=true;
            animateTo({k:k,tx:(R.L+R.R)/2-(n.x+LAY.W/2)*k,ty:(R.T+R.B)/2-(n.y+LAY.H/2)*k});
          }
          function ensureVisible(id){
            var n=N[id], R=region(), m=12;
            var x0=n.x*S.k+S.tx, y0=n.y*S.k+S.ty, x1=x0+LAY.W*S.k, y1=y0+LAY.H*S.k, dx=0, dy=0;
            if(x0<R.L+m) dx=R.L+m-x0; else if(x1>R.R-m) dx=R.R-m-x1;
            if(y0<R.T+m) dy=R.T+m-y0; else if(y1>R.B-m) dy=R.B-m-y1;
            if(dx||dy) animateTo({k:S.k,tx:S.tx+dx,ty:S.ty+dy});
          }
          function afterPanelOpen(id){ if(!userMoved&&fitK()>=0.8) fitView(true); else if(id) ensureVisible(id); }
          function animateTo(t,dur){
            cancelAnimationFrame(raf);
            if(reduced){setView(t); return;}
            var f={tx:S.tx,ty:S.ty,k:S.k}, t0=performance.now(), D=dur||380;
            function step(now){
              var q=Math.max(0,Math.min(1,(now-t0)/D)), e=1-Math.pow(1-q,3);
              setView({tx:f.tx+(t.tx-f.tx)*e,ty:f.ty+(t.ty-f.ty)*e,k:f.k+(t.k-f.k)*e});
              if(q<1) raf=requestAnimationFrame(step);
            }
            raf=requestAnimationFrame(step);
          }
          function clampK(k){return Math.max(0.3,Math.min(2.5,k));}
          function zoomAt(mx,my,f){
            userMoved=true; cancelAnimationFrame(raf);
            var k=clampK(S.k*f), r=k/S.k;
            setView({k:k,tx:mx-(mx-S.tx)*r,ty:my-(my-S.ty)*r});
          }
          function zoomBy(f){
            var R=region(), k=clampK(S.k*f), r=k/S.k, mx=(R.L+R.R)/2, my=(R.T+R.B)/2;
            userMoved=true;
            animateTo({k:k,tx:mx-(mx-S.tx)*r,ty:my-(my-S.ty)*r},220);
          }

          // ---- selection ----
          window.pin=function(id,opts){
            id=String(id); if(!N[id]) return;
            var was=panelOpen();
            S.pinned=id; S.selEdge=null; S.hoverEdge=null; update();
            if(opts&&opts.center) centerOn(id); else if(!was) afterPanelOpen(id); else ensureVisible(id);
          };
          window.unpin=function(){
            var had=panelOpen();
            S.pinned=null; S.selEdge=null; update();
            if(had&&!userMoved) fitView(true);
          };
          var pin=window.pin, unpin=window.unpin;
          function selectEdge(i){
            var was=panelOpen();
            S.selEdge=i; S.pinned=null; S.hoverEdge=null; update();
            if(!was) afterPanelOpen(null);
          }
          function move(key){
            var cur=S.pinned, target=null;
            if(!cur){var first=COLS[0]&&COLS[0][0]; if(first){pin(first); nodeEl[first].focus({preventScroll:true});} return;}
            var n=N[cur];
            if(key==='ArrowUp'||key==='ArrowDown') target=COLS[n.col][n.row+(key==='ArrowUp'?-1:1)]||null;
            else {
              var list=key==='ArrowRight'?n.o.map(function(ei){return E[ei].b;}):n.i.map(function(ei){return E[ei].a;});
              list.sort(function(a,b){return Math.abs(N[a].y-n.y)-Math.abs(N[b].y-n.y);});
              target=list[0]||null;
            }
            if(target){pin(target); nodeEl[target].focus({preventScroll:true});}
          }
          function consumeDrag(){var w=wasDrag; wasDrag=false; return w;}

          ids.forEach(function(id){
            var el=nodeEl[id];
            el.addEventListener('click',function(ev){ev.stopPropagation(); if(consumeDrag()) return; pin(id);});
            el.addEventListener('mouseenter',function(){ if(!(down&&down.moved)&&S.hover!==id){S.hover=id; update();} });
            el.addEventListener('mouseleave',function(){ if(S.hover===id){S.hover=null; update();} });
          });
          [].forEach.call(document.querySelectorAll('#hits .hit'),function(h){
            var i=+h.getAttribute('data-i');
            h.addEventListener('mouseenter',function(ev){
              if(down&&down.moved) return;
              S.tipX=ev.clientX; S.tipY=ev.clientY;
              if(S.hoverEdge!==i){S.hoverEdge=i; update();} else placeTip();
            });
            h.addEventListener('mousemove',function(ev){ if(S.hoverEdge===i){S.tipX=ev.clientX; S.tipY=ev.clientY; placeTip();} });
            h.addEventListener('mouseleave',function(){ if(S.hoverEdge===i){S.hoverEdge=null; update();} });
            h.addEventListener('click',function(ev){ev.stopPropagation(); if(consumeDrag()) return; selectEdge(i);});
          });

          // ---- pointer, wheel ----
          // Dragging anywhere (cards included) pans. IMPORTANT: capture the pointer only once a drag actually
          // MOVES — capturing on pointerdown retargets the click to the canvas, so card clicks never reach pin().
          view.addEventListener('pointerdown',function(ev){
            if(ev.button!==0) return;
            wasDrag=false; down={x:ev.clientX,y:ev.clientY,id:ev.pointerId,moved:false};
          });
          view.addEventListener('pointermove',function(ev){
            if(!down) return;
            var dx=ev.clientX-down.x, dy=ev.clientY-down.y;
            if(!down.moved){
              if(Math.abs(dx)+Math.abs(dy)<=3) return;   // still a click, not a drag — don't capture
              down.moved=true; wasDrag=true; grabbing=true; view.classList.add('grabbing');
              try{view.setPointerCapture(down.id);}catch(e){}
              cancelAnimationFrame(raf); S.hover=null; S.hoverEdge=null; update();
            }
            down.x=ev.clientX; down.y=ev.clientY; userMoved=true;
            setView({tx:S.tx+dx,ty:S.ty+dy,k:S.k});
          });
          function end(){ if(down&&down.moved){grabbing=false; view.classList.remove('grabbing');} down=null; }
          view.addEventListener('pointerup',end); view.addEventListener('pointercancel',end);
          // A background click (not a card or edge, not the tail of a drag) clears the pin or selection.
          view.addEventListener('click',function(){ if(consumeDrag()) return; if(panelOpen()) unpin(); });
          view.addEventListener('wheel',function(e){
            e.preventDefault();
            var u=e.deltaMode===1?16:e.deltaMode===2?400:1;
            if(!e.ctrlKey&&Math.abs(e.deltaX)>Math.abs(e.deltaY)){
              userMoved=true; cancelAnimationFrame(raf); setView({tx:S.tx-e.deltaX*u,ty:S.ty,k:S.k}); return;
            }
            zoomAt(e.clientX,e.clientY,Math.exp(-e.deltaY*u*(e.ctrlKey?0.01:0.0015)));
          },{passive:false});

          document.getElementById('zout').addEventListener('click',function(){zoomBy(1/1.2);});
          document.getElementById('zin').addEventListener('click',function(){zoomBy(1.2);});
          zpct.addEventListener('click',function(){zoomBy(1/S.k);});
          document.getElementById('zfit').addEventListener('click',function(){userMoved=false; fitView(true);});

          // ---- search ----
          function computeResults(){
            var q=S.q.trim().toLowerCase(), res=ids.map(function(id){return N[id];});
            if(q){
              var sw=function(n){return n.short.toLowerCase().indexOf(q)===0?1:0;};
              res=res.filter(function(n){return n.name.toLowerCase().indexOf(q)>=0||kl(n.kind).toLowerCase().indexOf(q)>=0;})
                .sort(function(a,b){return sw(b)-sw(a)||a.short.localeCompare(b.short);});
            } else res.sort(function(a,b){return a.col-b.col||a.row-b.row;});
            return res.slice(0,8);
          }
          function renderSearch(){
            qBox.classList.toggle('has',S.searchFocused||!!S.q);
            qIn.setAttribute('aria-expanded',S.searchFocused?'true':'false');
            if(!S.searchFocused){qRes.hidden=true; qIn.removeAttribute('aria-activedescendant'); return;}
            results=computeResults();
            var idx=Math.max(0,Math.min(S.searchIdx,results.length-1));
            qRes.innerHTML=results.length?results.map(function(n,i){
              return '<div class="opt'+(i===idx?' act':'')+'" role="option" id="qo'+i+'" aria-selected="'+(i===idx)+'" data-id="'+n.id
                +'" data-ix="'+i+'"><span class="on">'+esc(n.short)+'</span><span class="ok">'+esc(kl(n.kind))+'</span></div>';
            }).join(''):'<div class="nores">No project matches “'+esc(S.q.trim())+'”</div>';
            if(results.length) qIn.setAttribute('aria-activedescendant','qo'+idx); else qIn.removeAttribute('aria-activedescendant');
            qRes.hidden=false;
          }
          function pickResult(id){
            S.q=''; S.searchIdx=0; qIn.value=''; qIn.blur();
            pin(id,{center:true});
          }
          qIn.addEventListener('input',function(){S.q=qIn.value; S.searchIdx=0; renderSearch();});
          qIn.addEventListener('focus',function(){S.searchFocused=true; renderSearch();});
          qIn.addEventListener('blur',function(){S.searchFocused=false; renderSearch();});
          qIn.addEventListener('keydown',function(e){
            if(e.key==='ArrowDown'||e.key==='ArrowUp'){
              e.preventDefault();
              S.searchIdx=e.key==='ArrowDown'?Math.min(results.length-1,S.searchIdx+1):Math.max(0,S.searchIdx-1);
              renderSearch();
              var a=qRes.querySelector('.act'); if(a) a.scrollIntoView({block:'nearest'});
            } else if(e.key==='Enter'){
              e.preventDefault();
              var n=results[Math.max(0,Math.min(S.searchIdx,results.length-1))]; if(n) pickResult(n.id);
            } else if(e.key==='Escape'){
              e.preventDefault(); S.q=''; qIn.value=''; qIn.blur();
            }
          });
          qRes.addEventListener('mousedown',function(e){
            e.preventDefault();
            var o=e.target.closest('.opt'); if(o) pickResult(o.getAttribute('data-id'));
          });
          qRes.addEventListener('mouseover',function(e){
            var o=e.target.closest('.opt'); if(!o) return;
            var i=+o.getAttribute('data-ix'); if(i!==S.searchIdx){S.searchIdx=i; renderSearch();}
          });

          // ---- keyboard ----
          addEventListener('keydown',function(e){
            var t=e.target, typing=t&&(t.tagName==='INPUT'||t.tagName==='TEXTAREA'||t.isContentEditable);
            if(typing||e.metaKey||e.ctrlKey||e.altKey) return;
            var k=e.key;
            if(k==='/'){e.preventDefault(); qIn.focus(); return;}
            if(k==='Escape'){ if(panelOpen()) unpin(); return; }
            if(k==='f'||k==='F'){userMoved=false; fitView(true); return;}
            if(k==='+'||k==='='){zoomBy(1.2); return;}
            if(k==='-'||k==='_'){zoomBy(1/1.2); return;}
            if(k==='0'){zoomBy(1/S.k); return;}
            if((k==='t'||k==='T')&&S.pinned){S.scope=S.scope==='transitive'?'direct':'transitive'; update(); return;}
            if(k.indexOf('Arrow')===0){
              if(t&&t.closest&&t.closest('#panel')) return;
              e.preventDefault(); move(k); return;
            }
            if((k==='Enter'||k===' ')&&t&&t.getAttribute&&t.getAttribute('role')==='button'){e.preventDefault(); t.click();}
          });

          // ---- layout lifecycle ----
          function measureBar(){
            var h=Math.round(bar.getBoundingClientRect().height);
            if(h&&h!==S.barH){S.barH=h; panel.style.top=(h+12)+'px'; if(!userMoved) fitView(false);}
          }
          if(window.ResizeObserver) new ResizeObserver(measureBar).observe(bar);
          addEventListener('resize',function(){measureBar(); if(!userMoved) fitView(false);});
          measureBar(); update(); fitView(false);
          requestAnimationFrame(function(){ if(!userMoved) fitView(false); });
        })();
        """;
}
