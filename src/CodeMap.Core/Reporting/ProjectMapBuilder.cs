using System.Globalization;
using System.Net;
using System.Text;
using TestAtlas.Core.Model;
using TestAtlas.Core.Storage;

namespace TestAtlas.Core.Reporting;

/// <summary>
/// Renders a project-level dependency graph as a single self-contained HTML page (inline SVG + a
/// little vanilla JS for pan / zoom / hover — no external libraries, opens offline). A directed edge
/// A→B means "project A depends on project B": some entity in A binds to / uses / inherits an entity
/// in B (derived from the map's cross-project <c>binds_to</c> / <c>uses_type</c> / <c>inherits</c>
/// edges). Node size grows with in-degree, so shared step-definition / page-object libraries — the
/// projects many others lean on — stand out. Deterministic: node positions are a fixed circular
/// layout computed here, and the only volatile value (the timestamp) is read from the map.
/// </summary>
public static class ProjectMapBuilder
{
    private sealed record Node(int Id, string Name, string Kind, double X, double Y, double R, int InDegree, int OutDegree);
    private sealed record Link(int From, int To, int Weight, string Detail);

    // SVG canvas constants.
    private const double Cx = 500, Cy = 512;

    public static string Build(MapDocument doc)
    {
        // ---- project-id lookups per entity kind (to resolve an edge's endpoints to projects) ----
        var stepProj = doc.ScenarioSteps.ToDictionary(s => (long)s.Id, s => s.ProjectId);
        var stepDefProj = doc.StepDefinitions.ToDictionary(s => (long)s.Id, s => s.ProjectId);
        var methodProj = doc.Methods.ToDictionary(m => (long)m.Id, m => m.ProjectId);
        var classProj = doc.Classes.ToDictionary(c => (long)c.Id, c => c.ProjectId);

        int? ProjectOf(string kind, int? id) => id is not int i ? null : kind switch
        {
            RefKinds.ScenarioStep => stepProj.TryGetValue(i, out var p) ? p : null,
            RefKinds.StepDefinition => stepDefProj.TryGetValue(i, out var p) ? p : null,
            RefKinds.Method => methodProj.TryGetValue(i, out var p) ? p : null,
            RefKinds.Class => classProj.TryGetValue(i, out var p) ? p : null,
            _ => null,
        };

        // ---- aggregate cross-project edges into weighted, kind-tallied project links ----
        var agg = new Dictionary<(int From, int To), Dictionary<string, int>>();
        foreach (var e in doc.Edges)
        {
            if (e.EdgeKind == EdgeKinds.Unbound) continue;
            var from = ProjectOf(e.FromKind, e.FromId);
            var to = ProjectOf(e.ToKind, e.ToId);
            if (from is not int a || to is not int b || a == b) continue;
            if (!agg.TryGetValue((a, b), out var byKind)) agg[(a, b)] = byKind = new();
            byKind[e.EdgeKind] = byKind.TryGetValue(e.EdgeKind, out var c) ? c + 1 : 1;
        }

        var inDeg = new Dictionary<int, int>();
        var outDeg = new Dictionary<int, int>();
        foreach (var ((a, b), _) in agg)
        {
            outDeg[a] = outDeg.TryGetValue(a, out var o) ? o + 1 : 1;
            inDeg[b] = inDeg.TryGetValue(b, out var i) ? i + 1 : 1;
        }

        // ---- circular layout (deterministic: projects ordered by name) ----
        var ordered = doc.Projects.OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
        var n = ordered.Count;
        var radius = n <= 1 ? 0 : Math.Min(400, 150 + n * 9);
        var maxIn = inDeg.Count == 0 ? 0 : inDeg.Values.Max();

        var nodes = new Dictionary<int, Node>();
        for (var i = 0; i < n; i++)
        {
            var p = ordered[i];
            var angle = -Math.PI / 2 + 2 * Math.PI * i / Math.Max(1, n);
            var x = Cx + radius * Math.Cos(angle);
            var y = Cy + radius * Math.Sin(angle);
            var indeg = inDeg.TryGetValue(p.Id, out var d) ? d : 0;
            var r = 7 + (maxIn == 0 ? 0 : 13.0 * indeg / maxIn);
            nodes[p.Id] = new Node(p.Id, p.Name, p.Kind, x, y, r, indeg, outDeg.TryGetValue(p.Id, out var od) ? od : 0);
        }

        var links = agg
            .Select(kv => new Link(kv.Key.From, kv.Key.To, kv.Value.Values.Sum(),
                string.Join(", ", kv.Value.OrderByDescending(x => x.Value).Select(x => $"{x.Value} {x.Key}"))))
            .OrderBy(l => l.From).ThenBy(l => l.To)
            .ToList();
        var maxWeight = links.Count == 0 ? 1 : links.Max(l => l.Weight);

        // ---- render ----
        var solutionName = Path.GetFileName(Meta(doc, MapSchema.MetaSolutionPath, "(solution)"));
        var sb = new StringBuilder(32 * 1024);
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.Append("<title>TestAtlas map — ").Append(E(solutionName)).Append("</title><style>").Append(Css).Append("</style>");
        sb.Append("<script>").Append(ThemeInit).Append("</script></head><body>");

        // Floating, collapsible panel so the graph can use the whole viewport.
        sb.Append("<header class=\"top\" id=\"top\"><div class=\"hdr-row\">");
        sb.Append("<div class=\"brand\">Test<span>Atlas</span> <span class=\"tag\">project map</span></div>");
        sb.Append("<div class=\"hdr-actions\">");
        sb.Append("<button class=\"ic\" onclick=\"toggleTheme()\" title=\"Toggle light / dark\" aria-label=\"Toggle theme\">◐</button>");
        sb.Append("<button class=\"ic\" id=\"collapseBtn\" onclick=\"toggleHeader()\" title=\"Collapse panel\" aria-label=\"Collapse panel\">–</button>");
        sb.Append("</div></div>");
        sb.Append("<div class=\"hdr-detail\">");
        sb.Append("<h1>").Append(E(solutionName)).Append("</h1>");
        sb.Append("<p class=\"meta\">").Append(n).Append(" projects · ").Append(links.Count)
          .Append(" dependencies · an arrow A→B means A depends on B (binds to / uses / inherits something in B)</p>");
        sb.Append("<div class=\"legend\">");
        LegendSwatch(sb, "bdd_tests", "BDD tests");
        LegendSwatch(sb, "shared_library", "shared library");
        LegendSwatch(sb, "unit_tests", "unit tests");
        LegendSwatch(sb, "other", "other");
        sb.Append("<span class=\"hint\">drag to pan · scroll to zoom · hover a project to isolate its links</span>");
        sb.Append("</div></div></header>");

        if (n == 0)
        {
            sb.Append("<p class=\"empty\">No projects in this map.</p></body></html>");
            return sb.ToString();
        }

        sb.Append("<svg id=\"g\" viewBox=\"0 0 1000 1040\" preserveAspectRatio=\"xMidYMid meet\">");
        sb.Append("<defs>");
        sb.Append("<marker id=\"arrow\" viewBox=\"0 0 10 10\" refX=\"9\" refY=\"5\" markerWidth=\"7\" markerHeight=\"7\" orient=\"auto-start-reverse\">")
          .Append("<path d=\"M0 0 L10 5 L0 10 z\" class=\"arrowhead\"/></marker>");
        sb.Append("</defs>");

        // edges first (under nodes)
        sb.Append("<g id=\"edges\">");
        foreach (var l in links)
        {
            var a = nodes[l.From];
            var b = nodes[l.To];
            var (path, _) = CurvePath(a, b);
            var w = 1.0 + 3.5 * l.Weight / maxWeight;
            sb.Append("<path class=\"edge\" data-a=\"").Append(l.From).Append("\" data-b=\"").Append(l.To).Append("\" d=\"")
              .Append(path).Append("\" stroke-width=\"").Append(F(w)).Append("\" marker-end=\"url(#arrow)\">");
            sb.Append("<title>").Append(E(a.Name)).Append(" → ").Append(E(b.Name)).Append("  (").Append(E(l.Detail)).Append(")</title>");
            sb.Append("</path>");
        }
        sb.Append("</g>");

        // nodes
        sb.Append("<g id=\"nodes\">");
        foreach (var p in ordered)
        {
            var node = nodes[p.Id];
            var cos = radius == 0 ? 0 : (node.X - Cx) / radius;
            var sin = radius == 0 ? 0 : (node.Y - Cy) / radius;
            var lx = node.X + (node.R + 8) * cos;
            var ly = node.Y + (node.R + 8) * sin + 4;
            var anchor = cos > 0.2 ? "start" : cos < -0.2 ? "end" : "middle";

            sb.Append("<g class=\"node kind-").Append(E(KindClass(p.Kind))).Append("\" data-id=\"").Append(p.Id).Append("\">");
            sb.Append("<circle cx=\"").Append(F(node.X)).Append("\" cy=\"").Append(F(node.Y)).Append("\" r=\"").Append(F(node.R)).Append("\"/>");
            sb.Append("<text x=\"").Append(F(lx)).Append("\" y=\"").Append(F(ly)).Append("\" text-anchor=\"").Append(anchor).Append("\">")
              .Append(E(p.Name)).Append("</text>");
            sb.Append("<title>").Append(E(p.Name)).Append(" · ").Append(E(p.Kind))
              .Append("  (depended on by ").Append(node.InDegree).Append(", depends on ").Append(node.OutDegree).Append(")</title>");
            sb.Append("</g>");
        }
        sb.Append("</g></svg>");

        sb.Append("<script>").Append(Js).Append("</script></body></html>");
        return sb.ToString();
    }

    /// <summary>A quadratic curve from a's edge to b's edge, bowed to one side so A→B and B→A don't overlap.</summary>
    private static (string Path, double _) CurvePath(Node a, Node b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        var len = Math.Max(1e-6, Math.Sqrt(dx * dx + dy * dy));
        double ux = -dy / len, uy = dx / len;           // unit perpendicular (consistent side per direction)
        var off = len * 0.14;
        double mx = (a.X + b.X) / 2 + ux * off, my = (a.Y + b.Y) / 2 + uy * off;

        // Start/end pulled back to the circle edges (so the line and arrowhead sit on the rim, not the centre).
        var (sx, sy) = PullTo(a.X, a.Y, mx, my, a.R + 2);
        var (ex, ey) = PullTo(b.X, b.Y, mx, my, b.R + 4);
        return ($"M{F(sx)} {F(sy)} Q{F(mx)} {F(my)} {F(ex)} {F(ey)}", 0);
    }

    private static (double, double) PullTo(double px, double py, double tx, double ty, double dist)
    {
        double dx = tx - px, dy = ty - py;
        var len = Math.Max(1e-6, Math.Sqrt(dx * dx + dy * dy));
        return (px + dx / len * dist, py + dy / len * dist);
    }

    private static void LegendSwatch(StringBuilder sb, string kindClass, string label)
        => sb.Append("<span class=\"lg\"><span class=\"sw kind-").Append(kindClass).Append("\"></span>").Append(E(label)).Append("</span>");

    private static string KindClass(string kind) => kind switch
    {
        Kinds.BddTests => "bdd_tests",
        Kinds.UnitTests => "unit_tests",
        Kinds.SharedLibrary => "shared_library",
        _ => "other",
    };

    private static string Meta(MapDocument doc, string key, string fallback)
        => doc.Meta.TryGetValue(key, out var v) && v.Length > 0 ? v : fallback;

    private static string F(double d) => d.ToString("0.##", CultureInfo.InvariantCulture);
    private static string E(string s) => WebUtility.HtmlEncode(s);

    private const string Css = """
        :root{--bg:#f6f7f9;--ink:#1a1d21;--dim:#5b6470;--faint:#8b94a1;--line:#e4e7ec;--card:#fff;
        --bdd:#3b5bdb;--shared:#2f9e44;--unit:#e8890c;--other:#98a2b3;--mono:ui-monospace,Menlo,Consolas,monospace}
        /* dark = OS-dark unless the user forced light; or explicitly forced dark */
        @media(prefers-color-scheme:dark){:root:not([data-theme="light"]){--bg:#0f1216;--ink:#e6e9ee;--dim:#9aa4b2;
        --faint:#6b7484;--line:#252b33;--card:#171b21;--bdd:#748ffc;--shared:#51cf66;--unit:#ffa94d;--other:#7b8494}}
        :root[data-theme="dark"]{--bg:#0f1216;--ink:#e6e9ee;--dim:#9aa4b2;--faint:#6b7484;--line:#252b33;--card:#171b21;
        --bdd:#748ffc;--shared:#51cf66;--unit:#ffa94d;--other:#7b8494}
        *{box-sizing:border-box}html,body{height:100%}
        body{margin:0;font:14px/1.5 system-ui,-apple-system,Segoe UI,Roboto,sans-serif;background:var(--bg);color:var(--ink)}
        svg{position:fixed;inset:0;width:100vw;height:100vh;touch-action:none;cursor:grab}
        svg.grabbing{cursor:grabbing}
        .top{position:fixed;top:12px;left:12px;z-index:5;max-width:min(680px,calc(100vw - 24px));
        background:color-mix(in srgb,var(--card) 90%,transparent);backdrop-filter:blur(8px);
        border:1px solid var(--line);border-radius:12px;padding:12px 16px;box-shadow:0 2px 12px rgba(0,0,0,.10)}
        .hdr-row{display:flex;align-items:center;gap:12px}
        .brand{font-weight:600;color:var(--dim)}.brand span{color:var(--bdd)}
        .brand .tag{color:var(--faint);font-size:11px;text-transform:uppercase;letter-spacing:1px;margin-left:6px}
        .hdr-actions{margin-left:auto;display:flex;gap:6px}
        .ic{width:26px;height:26px;line-height:1;font-size:14px;border:1px solid var(--line);border-radius:7px;
        background:var(--bg);color:var(--dim);cursor:pointer;display:flex;align-items:center;justify-content:center}
        .ic:hover{color:var(--ink);border-color:var(--faint)}
        .top.collapsed .hdr-detail{display:none}
        h1{margin:10px 0 3px;font-size:20px;font-weight:600;letter-spacing:-.3px}
        .meta{margin:0;color:var(--faint);font-size:12.5px}
        .legend{display:flex;gap:14px;align-items:center;flex-wrap:wrap;margin-top:10px;font-size:12px;color:var(--dim)}
        .lg{display:inline-flex;align-items:center;gap:6px}
        .sw{width:10px;height:10px;border-radius:50%;display:inline-block}
        .sw.kind-bdd_tests{background:var(--bdd)}.sw.kind-shared_library{background:var(--shared)}
        .sw.kind-unit_tests{background:var(--unit)}.sw.kind-other{background:var(--other)}
        .hint{color:var(--faint);font-size:11.5px;flex-basis:100%}
        .empty{color:var(--faint);text-align:center;padding:40px}
        .edge{fill:none;stroke:var(--faint);opacity:.4}
        .arrowhead{fill:var(--faint);opacity:.6}
        .node circle{stroke:var(--card);stroke-width:2;cursor:pointer}
        .node.kind-bdd_tests circle{fill:var(--bdd)}.node.kind-shared_library circle{fill:var(--shared)}
        .node.kind-unit_tests circle{fill:var(--unit)}.node.kind-other circle{fill:var(--other)}
        .node text{fill:var(--ink);font-size:12px;font-weight:500;pointer-events:none;paint-order:stroke;
        stroke:var(--bg);stroke-width:3px}
        /* hover-to-isolate: dim everything, then the .active prefix wins on specificity */
        svg.has-focus .edge{opacity:.05}
        svg.has-focus .node{opacity:.18}
        svg.has-focus .edge.active{opacity:.95;stroke:var(--bdd)}
        svg.has-focus .node.active{opacity:1}
        @media(prefers-reduced-motion:no-preference){.edge,.node{transition:opacity .12s}}
        """;

    // Runs in <head> before paint so a saved manual theme choice applies without a flash.
    private const string ThemeInit = """
        (function(){try{var s=localStorage.getItem('testatlas:theme');if(s)document.documentElement.setAttribute('data-theme',s);}catch(e){}})();
        """;

    private const string Js = """
        (function(){
          var svg=document.getElementById('g'); if(!svg) return;
          var edges=[].slice.call(svg.querySelectorAll('.edge'));
          var nodes=[].slice.call(svg.querySelectorAll('.node'));
          // hover-to-isolate: highlight a node's in/out edges and their endpoints.
          nodes.forEach(function(nd){
            nd.addEventListener('mouseenter',function(){
              var id=nd.getAttribute('data-id'); var keep={}; keep[id]=1;
              edges.forEach(function(e){
                var a=e.getAttribute('data-a'),b=e.getAttribute('data-b');
                if(a===id||b===id){e.classList.add('active');keep[a]=1;keep[b]=1;}
              });
              nodes.forEach(function(m){ if(keep[m.getAttribute('data-id')]) m.classList.add('active'); });
              svg.classList.add('has-focus');
            });
            nd.addEventListener('mouseleave',function(){
              svg.classList.remove('has-focus');
              edges.forEach(function(e){e.classList.remove('active');});
              nodes.forEach(function(m){m.classList.remove('active');});
            });
          });
          // pan + zoom via viewBox.
          var vb={x:0,y:0,w:1000,h:1040};
          function apply(){svg.setAttribute('viewBox',vb.x+' '+vb.y+' '+vb.w+' '+vb.h);}
          svg.addEventListener('wheel',function(ev){
            ev.preventDefault();
            var r=svg.getBoundingClientRect();
            var mx=vb.x+(ev.clientX-r.left)/r.width*vb.w, my=vb.y+(ev.clientY-r.top)/r.height*vb.h;
            var k=ev.deltaY<0?0.9:1.1111;
            k=Math.max(0.2, Math.min(6, k*(vb.w/1000)))/(vb.w/1000);
            vb.x=mx-(mx-vb.x)*k; vb.y=my-(my-vb.y)*k; vb.w*=k; vb.h*=k; apply();
          },{passive:false});
          var drag=null;
          svg.addEventListener('pointerdown',function(ev){drag={x:ev.clientX,y:ev.clientY};svg.classList.add('grabbing');svg.setPointerCapture(ev.pointerId);});
          svg.addEventListener('pointermove',function(ev){
            if(!drag) return; var r=svg.getBoundingClientRect();
            vb.x-=(ev.clientX-drag.x)/r.width*vb.w; vb.y-=(ev.clientY-drag.y)/r.height*vb.h;
            drag.x=ev.clientX; drag.y=ev.clientY; apply();
          });
          function end(){drag=null;svg.classList.remove('grabbing');}
          svg.addEventListener('pointerup',end); svg.addEventListener('pointercancel',end);
        })();
        function toggleHeader(){
          var t=document.getElementById('top'); t.classList.toggle('collapsed');
          document.getElementById('collapseBtn').textContent=t.classList.contains('collapsed')?'+':'–';
        }
        function toggleTheme(){
          var r=document.documentElement, cur=r.getAttribute('data-theme');
          var dark = cur ? cur==='dark' : matchMedia('(prefers-color-scheme:dark)').matches;
          var next = dark?'light':'dark'; r.setAttribute('data-theme',next);
          try{localStorage.setItem('testatlas:theme',next);}catch(e){}
        }
        """;
}
