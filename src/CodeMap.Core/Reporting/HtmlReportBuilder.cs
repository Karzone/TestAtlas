using System.Net;
using System.Text;
using TestAtlas.Core.Model;
using TestAtlas.Core.Storage;

namespace TestAtlas.Core.Reporting;

/// <summary>
/// Renders a <see cref="MapDocument"/> into a single self-contained HTML report — no external CSS,
/// JS, fonts, or network calls, so it opens offline straight from disk. The drill-down mirrors the
/// map's shape: summary → binding coverage → class kinds → per-project table → feature/scenario/step
/// tree (each step tagged bound / ambiguous / unbound with its resolved step definition) → diagnostics.
/// Deterministic: the only volatile value (the generated timestamp) is read from the map, not the clock.
/// </summary>
public static class HtmlReportBuilder
{
    /// <summary>Builds the full HTML document as a string.</summary>
    public static string Build(MapDocument doc)
    {
        var sb = new StringBuilder(64 * 1024);

        // ---- pre-compute the binding view over scenario steps -------------------------------
        var stepDefById = doc.StepDefinitions.ToDictionary(d => d.Id);
        var edgesByStep = doc.Edges
            .Where(e => e.FromKind == RefKinds.ScenarioStep)
            .GroupBy(e => e.FromId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var binding = new Dictionary<int, StepBindingView>();
        foreach (var step in doc.ScenarioSteps)
        {
            edgesByStep.TryGetValue(step.Id, out var edges);
            binding[step.Id] = Classify(edges, stepDefById);
        }

        var boundCount = binding.Values.Count(b => b.Status == BindStatus.Bound);
        var ambiguousCount = binding.Values.Count(b => b.Status == BindStatus.Ambiguous);
        var unboundCount = binding.Values.Count(b => b.Status == BindStatus.Unbound);
        var totalSteps = doc.ScenarioSteps.Count;
        var resolved = boundCount + ambiguousCount; // any step that reached ≥1 definition
        var coveragePct = totalSteps == 0 ? 0 : (int)Math.Round(100.0 * resolved / totalSteps);

        // ---- document shell -----------------------------------------------------------------
        var solutionPath = Meta(doc, MapSchema.MetaSolutionPath, "(unknown solution)");
        var solutionName = Path.GetFileName(solutionPath);
        var generated = Meta(doc, MapSchema.MetaGeneratedUtc, "");
        var toolVersion = Meta(doc, MapSchema.MetaToolVersion, "");

        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.Append("<title>TestAtlas — ").Append(E(solutionName)).Append("</title>");
        sb.Append("<style>").Append(Css).Append("</style></head><body>");

        // ---- header -------------------------------------------------------------------------
        sb.Append("<header class=\"top\"><div class=\"wrap\">");
        sb.Append("<div class=\"brand\">Test<span>Atlas</span> <span class=\"tag\">map report</span></div>");
        sb.Append("<h1>").Append(E(solutionName)).Append("</h1>");
        sb.Append("<p class=\"meta\">");
        sb.Append("<span title=\"").Append(E(solutionPath)).Append("\">").Append(E(solutionPath)).Append("</span>");
        if (generated.Length > 0) sb.Append(" · generated ").Append(E(generated));
        sb.Append(" · schema v").Append(doc.UserVersion);
        if (toolVersion.Length > 0) sb.Append(" · tool ").Append(E(toolVersion));
        sb.Append("</p></div></header>");

        sb.Append("<main class=\"wrap\">");

        // ---- stale-schema banner ------------------------------------------------------------
        // A map written by an older schema is missing whole facets (e.g. a v2 map has no Gherkin
        // features / edges), so the empty sections below would otherwise look like a bug. Say so.
        if (doc.UserVersion != MapSchema.Version)
        {
            sb.Append("<div class=\"banner\"><b>This map was written by schema v").Append(doc.UserVersion)
              .Append("; this tool is v").Append(MapSchema.Version).Append(".</b> ");
            sb.Append(doc.UserVersion < MapSchema.Version
                ? "It predates newer data (e.g. Gherkin features, step-binding coverage, and search). "
                  + "Re-run <code>testatlas index</code> to populate them."
                : "It was written by a newer tool; some sections may be incomplete. Upgrade this tool to read it fully.");
            sb.Append("</div>");
        }

        // ---- summary cards ------------------------------------------------------------------
        sb.Append("<section class=\"cards\">");
        Card(sb, doc.Projects.Count, "projects");
        Card(sb, doc.Classes.Count, "classes");
        Card(sb, doc.Methods.Count, "methods");
        Card(sb, doc.StepDefinitions.Count, "step definitions");
        Card(sb, doc.Features.Count, "features");
        Card(sb, doc.Scenarios.Count, "scenarios");
        Card(sb, totalSteps, "scenario steps");
        sb.Append("</section>");

        // ---- binding coverage ---------------------------------------------------------------
        sb.Append("<details class=\"panel\" open><summary class=\"p-sum\"><h2>Step binding coverage</h2></summary>");
        sb.Append("<div class=\"coverage\">");
        sb.Append("<div class=\"cov-num\">").Append(coveragePct).Append("<span>%</span></div>");
        sb.Append("<div class=\"cov-body\">");
        sb.Append("<div class=\"bar\">");
        Segment(sb, "bound", boundCount, totalSteps);
        Segment(sb, "ambiguous", ambiguousCount, totalSteps);
        Segment(sb, "unbound", unboundCount, totalSteps);
        sb.Append("</div>");
        sb.Append("<ul class=\"legend\">");
        LegendItem(sb, "bound", boundCount, "resolve to exactly one step definition");
        LegendItem(sb, "ambiguous", ambiguousCount, "match more than one step definition");
        LegendItem(sb, "unbound", unboundCount, "no step definition matches");
        sb.Append("</ul></div></div></details>");

        // ---- class kinds --------------------------------------------------------------------
        var kindGroups = doc.Classes.GroupBy(c => c.Kind)
            .Select(g => (Kind: g.Key, Count: g.Count()))
            .OrderByDescending(g => g.Count).ThenBy(g => g.Kind).ToList();
        if (kindGroups.Count > 0)
        {
            var maxKind = kindGroups.Max(g => g.Count);
            sb.Append("<details class=\"panel\" open><summary class=\"p-sum\"><h2>Class kinds</h2></summary><div class=\"kinds\">");
            foreach (var g in kindGroups)
            {
                var w = maxKind == 0 ? 0 : (int)Math.Round(100.0 * g.Count / maxKind);
                sb.Append("<div class=\"kind-row\"><span class=\"kind-label\">").Append(E(g.Kind)).Append("</span>");
                sb.Append("<span class=\"kind-track\"><span class=\"kind-fill\" style=\"width:").Append(w).Append("%\"></span></span>");
                sb.Append("<span class=\"kind-count\">").Append(g.Count).Append("</span></div>");
            }
            sb.Append("</div></details>");
        }

        // ---- per-project table --------------------------------------------------------------
        sb.Append("<details class=\"panel\" open><summary class=\"p-sum\"><h2>Projects</h2></summary><table class=\"grid\"><thead><tr>");
        foreach (var h in new[] { "project", "kind", "classes", "methods", "features", "scenarios", "steps" })
            sb.Append("<th>").Append(h).Append("</th>");
        sb.Append("</tr></thead><tbody>");
        foreach (var p in doc.Projects.OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            var featureIds = doc.Features.Where(f => f.ProjectId == p.Id).Select(f => f.Id).ToHashSet();
            var scenarios = doc.Scenarios.Count(s => featureIds.Contains(s.FeatureId));
            var steps = doc.ScenarioSteps.Count(st => st.ProjectId == p.Id);
            sb.Append("<tr><td class=\"name\">").Append(E(p.Name)).Append("</td>");
            sb.Append("<td><span class=\"chip\">").Append(E(p.Kind)).Append("</span></td>");
            Num(sb, doc.Classes.Count(c => c.ProjectId == p.Id));
            Num(sb, doc.Methods.Count(m => m.ProjectId == p.Id));
            Num(sb, featureIds.Count);
            Num(sb, scenarios);
            Num(sb, steps);
            sb.Append("</tr>");
        }
        sb.Append("</tbody></table></details>");

        // ---- collaborators (page objects / API clients + who drives them) -------------------
        AppendCollaborators(sb, doc);

        // ---- feature / scenario / step drill-down -------------------------------------------
        if (doc.Features.Count > 0)
        {
            sb.Append("<details class=\"panel\" open><summary class=\"p-sum\"><h2>Features</h2>");
            sb.Append("<span class=\"subtle\">").Append(doc.Features.Count).Append(" feature")
              .Append(doc.Features.Count == 1 ? "" : "s").Append(" · ").Append(doc.Scenarios.Count)
              .Append(" scenario").Append(doc.Scenarios.Count == 1 ? "" : "s").Append("</span></summary>");
            sb.Append("<div class=\"tree-controls\">");
            sb.Append("<input id=\"filter\" type=\"search\" placeholder=\"filter features, scenarios, steps…\" ")
              .Append("oninput=\"filterTree(this.value)\" autocomplete=\"off\">");
            sb.Append("<button type=\"button\" class=\"mini\" onclick=\"setAllFeatures(true)\">expand all</button>");
            sb.Append("<button type=\"button\" class=\"mini\" onclick=\"setAllFeatures(false)\">collapse all</button>");
            sb.Append("</div>");
            sb.Append("<div id=\"tree\">");

            var scenariosByFeature = doc.Scenarios.GroupBy(s => s.FeatureId)
                .ToDictionary(g => g.Key, g => g.OrderBy(s => s.LineStart).ToList());
            var stepsByScenario = doc.ScenarioSteps.GroupBy(s => s.ScenarioId)
                .ToDictionary(g => g.Key, g => g.OrderBy(s => s.Ordinal).ToList());

            foreach (var feature in doc.Features.OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                scenariosByFeature.TryGetValue(feature.Id, out var scen);
                scen ??= new List<ScenarioRow>();

                // Per-feature binding health, so a collapsed feature still shows if it has gaps.
                var featUnbound = scen
                    .SelectMany(s => stepsByScenario.TryGetValue(s.Id, out var st) ? st : Enumerable.Empty<ScenarioStepRow>())
                    .Count(st => (binding.TryGetValue(st.Id, out var bv) ? bv.Status : BindStatus.Unbound) == BindStatus.Unbound);

                sb.Append("<details class=\"feature\"><summary>");
                sb.Append("<span class=\"f-name\">").Append(E(feature.Name)).Append("</span>");
                sb.Append("<span class=\"f-meta\">").Append(scen.Count).Append(" scenario")
                  .Append(scen.Count == 1 ? "" : "s").Append("</span>");
                if (featUnbound > 0)
                    sb.Append("<span class=\"badge unbound\">").Append(featUnbound).Append(" unbound</span>");
                if (!string.IsNullOrEmpty(feature.Tags))
                    sb.Append("<span class=\"tags\">").Append(E(feature.Tags!)).Append("</span>");
                sb.Append("<span class=\"path\">").Append(E(feature.FilePath)).Append("</span>");
                sb.Append("</summary>");

                foreach (var scenario in scen)
                {
                    stepsByScenario.TryGetValue(scenario.Id, out var steps);
                    steps ??= new List<ScenarioStepRow>();
                    sb.Append("<div class=\"scenario\"><div class=\"s-head\">");
                    if (scenario.Kind == "scenario_outline")
                        sb.Append("<span class=\"chip outline\">outline</span>");
                    sb.Append("<span class=\"s-name\">").Append(E(scenario.Name)).Append("</span>");
                    if (!string.IsNullOrEmpty(scenario.Tags))
                        sb.Append("<span class=\"tags\">").Append(E(scenario.Tags!)).Append("</span>");
                    sb.Append("</div><ul class=\"steps\">");

                    foreach (var step in steps)
                    {
                        var view = binding.TryGetValue(step.Id, out var b) ? b : StepBindingView.None;
                        var cls = view.Status switch
                        {
                            BindStatus.Bound => "bound",
                            BindStatus.Ambiguous => "ambiguous",
                            _ => "unbound",
                        };
                        sb.Append("<li class=\"step ").Append(cls).Append("\">");
                        sb.Append("<span class=\"dot\"></span>");
                        sb.Append("<span class=\"kw\">").Append(E(step.Keyword)).Append("</span> ");
                        sb.Append("<span class=\"txt\">").Append(E(step.Text)).Append("</span>");
                        sb.Append("<span class=\"binding\">").Append(BindingLabel(view)).Append("</span>");
                        sb.Append("</li>");
                    }
                    sb.Append("</ul></div>");
                }
                sb.Append("</details>");
            }
            sb.Append("</div><p id=\"nomatch\" class=\"nomatch\" hidden>No steps match that filter.</p></details>");
        }

        // ---- diagnostics --------------------------------------------------------------------
        if (doc.Diagnostics.Count > 0)
        {
            sb.Append("<details class=\"panel\" open><summary class=\"p-sum\"><h2>Diagnostics</h2><span class=\"subtle\">")
              .Append(doc.Diagnostics.Count).Append("</span></summary><div class=\"table-scroll\"><table class=\"grid diag\"><thead><tr>");
            foreach (var h in new[] { "severity", "code", "message", "location" })
                sb.Append("<th>").Append(h).Append("</th>");
            sb.Append("</tr></thead><tbody>");
            foreach (var d in doc.Diagnostics
                         .OrderByDescending(d => d.Severity == "error").ThenBy(d => d.Code, StringComparer.Ordinal))
            {
                sb.Append("<tr><td><span class=\"sev ").Append(E(d.Severity)).Append("\">").Append(E(d.Severity)).Append("</span></td>");
                sb.Append("<td class=\"mono\">").Append(E(d.Code)).Append("</td>");
                sb.Append("<td>").Append(E(d.Message)).Append("</td>");
                sb.Append("<td class=\"mono dim\">").Append(E(d.Location ?? "")).Append("</td></tr>");
            }
            sb.Append("</tbody></table></div></details>");
        }

        sb.Append("<footer>Generated by TestAtlas — static semantic map for .NET test-automation solutions.</footer>");
        sb.Append("</main>");
        sb.Append("<script>").Append(Js).Append("</script>");
        sb.Append("</body></html>");
        return sb.ToString();
    }

    // ---- binding classification --------------------------------------------------------------
    private enum BindStatus { Unbound, Bound, Ambiguous }

    private sealed record StepBindingView(BindStatus Status, IReadOnlyList<StepDefinitionRow> Targets)
    {
        public static readonly StepBindingView None = new(BindStatus.Unbound, Array.Empty<StepDefinitionRow>());
    }

    private static StepBindingView Classify(List<EdgeRow>? edges, IReadOnlyDictionary<int, StepDefinitionRow> defs)
    {
        if (edges is null || edges.Count == 0 || edges.All(e => e.EdgeKind == EdgeKinds.Unbound))
            return StepBindingView.None;

        var targets = edges
            .Where(e => e.EdgeKind == EdgeKinds.BindsTo && e.ToId is int id && defs.ContainsKey(id))
            .Select(e => defs[e.ToId!.Value])
            .OrderBy(d => d.Expression, StringComparer.Ordinal)
            .ToList();

        var ambiguous = edges.Any(e => e.Confidence == BindConfidence.Ambiguous) || targets.Count > 1;
        return new StepBindingView(ambiguous ? BindStatus.Ambiguous : BindStatus.Bound, targets);
    }

    private static string BindingLabel(StepBindingView view)
    {
        return view.Status switch
        {
            BindStatus.Bound when view.Targets.Count == 1 =>
                "→ <code>" + E(view.Targets[0].Expression) + "</code> <span class=\"loc\">"
                + E(Path.GetFileName(view.Targets[0].FilePath)) + ":" + view.Targets[0].LineStart + "</span>",
            BindStatus.Ambiguous =>
                "⚠ " + view.Targets.Count + " candidates: "
                + string.Join(", ", view.Targets.Select(t => "<code>" + E(t.Expression) + "</code>")),
            _ => "no matching step definition",
        };
    }

    // ---- collaborators panel -----------------------------------------------------------------
    private const int MaxCollaboratorRows = 60;

    /// <summary>
    /// The page objects / API clients the suite is built on, ranked by how many distinct methods
    /// drive them (incoming <c>uses_type</c> edges). Surfaces the leaned-on classes and, just as
    /// usefully, the <b>orphans</b> nothing in the map drives — candidate dead code.
    /// </summary>
    private static void AppendCollaborators(StringBuilder sb, MapDocument doc)
    {
        var collaborators = doc.Classes.Where(c => c.Kind is Kinds.PageObject or Kinds.ApiClient).ToList();
        if (collaborators.Count == 0) return;

        var driversByClass = doc.Edges
            .Where(e => e.EdgeKind == EdgeKinds.UsesType && e.ToId is int)
            .GroupBy(e => e.ToId!.Value)
            .ToDictionary(g => g.Key, g => g.Select(e => e.FromId).Distinct().Count());

        var classNameById = doc.Classes.ToDictionary(c => c.Id, c => c.Name);
        var extendsByClass = doc.Edges
            .Where(e => e.EdgeKind == EdgeKinds.Inherits && e.ToId is int t && classNameById.ContainsKey(t))
            .GroupBy(e => e.FromId)
            .ToDictionary(g => g.Key, g => g.Select(e => classNameById[e.ToId!.Value]).OrderBy(n => n, StringComparer.Ordinal).ToList());

        var pageObjects = collaborators.Count(c => c.Kind == Kinds.PageObject);
        var apiClients = collaborators.Count(c => c.Kind == Kinds.ApiClient);
        var orphans = collaborators.Count(c => !driversByClass.ContainsKey(c.Id));

        var ranked = collaborators
            .Select(c => (Class: c, Drivers: driversByClass.TryGetValue(c.Id, out var d) ? d : 0))
            .OrderByDescending(x => x.Drivers)
            .ThenBy(x => x.Class.Name, StringComparer.Ordinal)
            .ToList();

        sb.Append("<details class=\"panel\" open><summary class=\"p-sum\"><h2>Collaborators</h2>");
        sb.Append("<span class=\"subtle\">").Append(pageObjects).Append(" page object").Append(pageObjects == 1 ? "" : "s");
        if (apiClients > 0) sb.Append(" · ").Append(apiClients).Append(" API client").Append(apiClients == 1 ? "" : "s");
        if (orphans > 0) sb.Append(" · <span class=\"warn-text\">").Append(orphans).Append(" unused</span>");
        sb.Append("</span></summary>");

        sb.Append("<table class=\"grid\"><thead><tr>");
        foreach (var h in new[] { "collaborator", "kind", "driven by", "extends" })
            sb.Append("<th>").Append(h).Append("</th>");
        sb.Append("</tr></thead><tbody>");

        foreach (var (c, drivers) in ranked.Take(MaxCollaboratorRows))
        {
            sb.Append("<tr><td class=\"name\">").Append(E(c.Name)).Append("</td>");
            sb.Append("<td><span class=\"chip\">").Append(E(c.Kind)).Append("</span></td>");
            if (drivers == 0)
                sb.Append("<td><span class=\"tag-unused\">unused</span></td>");
            else
                sb.Append("<td class=\"num\">").Append(drivers).Append(" method").Append(drivers == 1 ? "" : "s").Append("</td>");
            var bases = extendsByClass.TryGetValue(c.Id, out var b) ? string.Join(", ", b) : "";
            sb.Append("<td class=\"mono dim\">").Append(E(bases)).Append("</td></tr>");
        }
        sb.Append("</tbody></table>");
        if (ranked.Count > MaxCollaboratorRows)
            sb.Append("<p class=\"subtle more\">… and ").Append(ranked.Count - MaxCollaboratorRows)
              .Append(" more (query the map db for the full list).</p>");
        sb.Append("</details>");
    }

    // ---- small emit helpers ------------------------------------------------------------------
    private static void Card(StringBuilder sb, int value, string label)
        => sb.Append("<div class=\"card\"><div class=\"c-num\">").Append(value)
             .Append("</div><div class=\"c-label\">").Append(E(label)).Append("</div></div>");

    private static void Num(StringBuilder sb, int n) => sb.Append("<td class=\"num\">").Append(n).Append("</td>");

    private static void Segment(StringBuilder sb, string cls, int count, int total)
    {
        if (count == 0 || total == 0) return;
        var pct = 100.0 * count / total;
        sb.Append("<span class=\"seg ").Append(cls).Append("\" style=\"width:")
          .Append(pct.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))
          .Append("%\" title=\"").Append(count).Append(' ').Append(cls).Append("\"></span>");
    }

    private static void LegendItem(StringBuilder sb, string cls, int count, string desc)
        => sb.Append("<li><span class=\"swatch ").Append(cls).Append("\"></span><b>").Append(count)
             .Append("</b> <span class=\"lk\">").Append(cls).Append("</span> <span class=\"ld\">")
             .Append(E(desc)).Append("</span></li>");

    private static string Meta(MapDocument doc, string key, string fallback)
        => doc.Meta.TryGetValue(key, out var v) && v.Length > 0 ? v : fallback;

    private static string E(string s) => WebUtility.HtmlEncode(s);

    // ---- assets (inlined; no external requests) ----------------------------------------------
    private const string Css = """
        :root{--bg:#f6f7f9;--card:#fff;--ink:#1a1d21;--dim:#5b6470;--faint:#8b94a1;--line:#e4e7ec;
        --accent:#3b5bdb;--bound:#2f9e44;--amber:#e8890c;--unbound:#e03131;--mono:ui-monospace,"SFMono-Regular",Menlo,Consolas,monospace;}
        @media(prefers-color-scheme:dark){:root{--bg:#0f1216;--card:#171b21;--ink:#e6e9ee;--dim:#9aa4b2;
        --faint:#6b7484;--line:#252b33;--accent:#748ffc;--bound:#51cf66;--amber:#ffa94d;--unbound:#ff6b6b;}}
        *{box-sizing:border-box}html{-webkit-text-size-adjust:100%}
        body{margin:0;font:15px/1.5 system-ui,-apple-system,Segoe UI,Roboto,sans-serif;background:var(--bg);color:var(--ink)}
        .wrap{max-width:1080px;margin:0 auto;padding:0 24px}
        .top{background:linear-gradient(180deg,var(--card),transparent);border-bottom:1px solid var(--line);padding:28px 0 22px}
        .brand{font-weight:600;letter-spacing:.2px;color:var(--dim)}.brand span{color:var(--accent)}
        .brand .tag{color:var(--faint);font-weight:500;font-size:12px;text-transform:uppercase;letter-spacing:1px;margin-left:6px}
        h1{margin:10px 0 4px;font-size:26px;font-weight:650;letter-spacing:-.3px}
        .meta{margin:0;color:var(--faint);font-size:12.5px;font-family:var(--mono);word-break:break-all}
        main{padding:24px 24px 64px}
        .banner{background:color-mix(in srgb,var(--amber) 12%,var(--card));border:1px solid var(--amber);
        border-radius:10px;padding:12px 16px;margin:16px 0 4px;font-size:13.5px;line-height:1.55}
        .banner code{font-family:var(--mono);font-size:12px;background:var(--card);border:1px solid var(--line);
        border-radius:5px;padding:1px 6px}
        .cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(120px,1fr));gap:12px;margin:8px 0 24px}
        .card{background:var(--card);border:1px solid var(--line);border-radius:12px;padding:16px}
        .c-num{font-size:28px;font-weight:680;font-variant-numeric:tabular-nums;letter-spacing:-.5px}
        .c-label{color:var(--dim);font-size:12.5px;margin-top:2px}
        .panel{background:var(--card);border:1px solid var(--line);border-radius:14px;padding:20px 22px;margin:16px 0}
        .panel-head{display:flex;align-items:center;justify-content:space-between;gap:16px;flex-wrap:wrap}
        h2{margin:0 0 14px;font-size:15px;font-weight:640;letter-spacing:.2px}
        .panel-head h2{margin:0}
        details.panel>summary.p-sum{cursor:pointer;list-style:none;display:flex;align-items:center;gap:10px}
        details.panel>summary.p-sum::-webkit-details-marker{display:none}
        details.panel>summary.p-sum h2{margin:0}
        details.panel>summary.p-sum::before{content:"";width:6px;height:6px;flex:none;
        border-right:2px solid var(--faint);border-bottom:2px solid var(--faint);transform:rotate(-45deg)}
        details.panel[open]>summary.p-sum{margin-bottom:14px}
        details.panel[open]>summary.p-sum::before{transform:rotate(45deg)}
        details.panel>summary.p-sum .subtle{margin-left:auto}
        .tree-controls{display:flex;gap:8px;align-items:center;flex-wrap:wrap;margin:0 0 12px}
        button.mini{font:12px system-ui,sans-serif;padding:6px 11px;border:1px solid var(--line);border-radius:7px;
        background:var(--bg);color:var(--dim);cursor:pointer}
        button.mini:hover{color:var(--ink);border-color:var(--faint)}
        .badge{font-size:11px;font-family:var(--mono);border-radius:20px;padding:1px 8px;border:1px solid}
        .badge.unbound{color:var(--unbound);border-color:var(--unbound);background:color-mix(in srgb,var(--unbound) 10%,transparent)}
        .coverage{display:flex;align-items:center;gap:22px;flex-wrap:wrap}
        .cov-num{font-size:44px;font-weight:700;font-variant-numeric:tabular-nums;letter-spacing:-1px;line-height:1}
        .cov-num span{font-size:20px;color:var(--dim);margin-left:2px}
        .cov-body{flex:1;min-width:260px}
        .bar{display:flex;height:14px;border-radius:8px;overflow:hidden;background:var(--line)}
        .seg{display:block;height:100%}.seg.bound{background:var(--bound)}.seg.ambiguous{background:var(--amber)}.seg.unbound{background:var(--unbound)}
        .legend{list-style:none;display:flex;gap:20px;flex-wrap:wrap;margin:12px 0 0;padding:0;font-size:13px}
        .legend b{font-variant-numeric:tabular-nums}
        .swatch{display:inline-block;width:10px;height:10px;border-radius:3px;margin-right:6px;vertical-align:baseline}
        .swatch.bound{background:var(--bound)}.swatch.ambiguous{background:var(--amber)}.swatch.unbound{background:var(--unbound)}
        .lk{font-weight:600}.ld{color:var(--dim)}
        .kinds{display:flex;flex-direction:column;gap:8px}
        .kind-row{display:grid;grid-template-columns:150px 1fr 44px;align-items:center;gap:12px}
        .kind-label{font-size:13px;color:var(--dim);font-family:var(--mono)}
        .kind-track{background:var(--line);border-radius:6px;height:10px;overflow:hidden}
        .kind-fill{display:block;height:100%;background:var(--accent);border-radius:6px}
        .kind-count{text-align:right;font-variant-numeric:tabular-nums;font-size:13px;color:var(--dim)}
        table.grid{width:100%;border-collapse:collapse;font-size:13.5px}
        .grid th{text-align:left;color:var(--faint);font-weight:600;font-size:11.5px;text-transform:uppercase;letter-spacing:.6px;
        padding:0 12px 8px;border-bottom:1px solid var(--line)}
        .grid td{padding:9px 12px;border-bottom:1px solid var(--line)}
        .grid tr:last-child td{border-bottom:0}
        .grid td.num{text-align:right;font-variant-numeric:tabular-nums;color:var(--dim)}
        .grid td.name{font-weight:600}
        .table-scroll{overflow-x:auto;max-width:100%}
        .grid.diag{table-layout:fixed}
        .grid.diag th:nth-child(1),.grid.diag td:nth-child(1){width:84px}
        .grid.diag th:nth-child(2),.grid.diag td:nth-child(2){width:180px}
        .grid.diag th:nth-child(4),.grid.diag td:nth-child(4){width:22%}
        .grid.diag td{overflow-wrap:anywhere;word-break:break-word;vertical-align:top}
        .chip{display:inline-block;font-size:11.5px;font-family:var(--mono);color:var(--dim);background:var(--bg);
        border:1px solid var(--line);border-radius:20px;padding:1px 9px}
        .chip.outline{color:var(--amber);border-color:var(--amber)}
        .subtle{color:var(--faint);font-size:12.5px}
        .warn-text{color:var(--amber);font-weight:600}
        .more{margin:10px 0 0}
        .tag-unused{display:inline-block;font-size:11px;font-family:var(--mono);color:var(--amber);
        background:color-mix(in srgb,var(--amber) 12%,transparent);border:1px solid var(--amber);
        border-radius:20px;padding:1px 9px}
        details.feature{border:1px solid var(--line);border-radius:10px;margin:10px 0;overflow:hidden;background:var(--bg)}
        details.feature>summary{cursor:pointer;list-style:none;padding:12px 14px;display:flex;align-items:center;gap:12px;flex-wrap:wrap;
        background:var(--card)}
        details.feature>summary::-webkit-details-marker{display:none}
        .f-name{font-weight:550;font-size:14px}.f-meta{color:var(--faint);font-size:12.5px}
        .tags{font-family:var(--mono);font-size:11.5px;color:var(--accent)}
        .path{margin-left:auto;font-family:var(--mono);font-size:11px;color:var(--faint);word-break:break-all}
        .scenario{padding:6px 14px 12px 20px;border-top:1px solid var(--line)}
        .s-head{display:flex;align-items:center;gap:10px;margin:10px 0 6px}
        .s-name{font-weight:600;font-size:13.5px}
        .steps{list-style:none;margin:0;padding:0}
        .step{display:flex;align-items:baseline;gap:8px;padding:5px 8px;border-radius:7px;font-size:13px;flex-wrap:wrap}
        .step+.step{margin-top:1px}
        .step .dot{width:7px;height:7px;border-radius:50%;flex:none;position:relative;top:1px}
        .step.bound .dot{background:var(--bound)}.step.ambiguous .dot{background:var(--amber)}.step.unbound .dot{background:var(--unbound)}
        .step.unbound{background:color-mix(in srgb,var(--unbound) 8%,transparent)}
        .step.ambiguous{background:color-mix(in srgb,var(--amber) 8%,transparent)}
        .kw{font-weight:700;color:var(--dim);font-size:12px;min-width:38px}
        .txt{color:var(--ink)}
        .binding{margin-left:auto;color:var(--dim);font-size:12px}
        .binding code{font-family:var(--mono);font-size:11.5px;background:var(--card);border:1px solid var(--line);
        border-radius:5px;padding:0 5px;color:var(--ink)}
        .binding .loc{color:var(--faint);font-family:var(--mono);font-size:11px;margin-left:4px}
        .step.unbound .binding{color:var(--unbound)}.step.ambiguous .binding{color:var(--amber)}
        #filter{font:13px system-ui,sans-serif;padding:7px 12px;border:1px solid var(--line);border-radius:8px;
        background:var(--bg);color:var(--ink);min-width:240px}
        #filter:focus{outline:2px solid var(--accent);outline-offset:1px;border-color:transparent}
        .nomatch{color:var(--faint);font-size:13px;text-align:center;padding:16px}
        .sev{font-family:var(--mono);font-size:11px;font-weight:700;text-transform:uppercase;letter-spacing:.5px}
        .sev.error{color:var(--unbound)}.sev.warning{color:var(--amber)}.sev.info{color:var(--dim)}
        .mono{font-family:var(--mono);font-size:12px}.dim{color:var(--faint)}
        footer{color:var(--faint);font-size:12px;text-align:center;padding:8px 0}
        @media(max-width:720px){.kind-row{grid-template-columns:110px 1fr 38px}.path{display:none}.binding{margin-left:0;flex-basis:100%}}
        @media(prefers-reduced-motion:no-preference){.seg,.kind-fill{transition:width .4s ease}}
        """;

    private const string Js = """
        function filterTree(q){
          q=(q||'').trim().toLowerCase();
          var features=document.querySelectorAll('#tree > details.feature');
          var anyVisible=false;
          features.forEach(function(f){
            var fVisible=false;
            f.querySelectorAll('.scenario').forEach(function(sc){
              var scText=sc.textContent.toLowerCase();
              var scVisible=false;
              sc.querySelectorAll('.step').forEach(function(st){
                var hit=q===''||st.textContent.toLowerCase().indexOf(q)>-1;
                st.style.display=hit?'':'none';
                if(hit)scVisible=true;
              });
              // a scenario whose name matches keeps all its steps
              if(q!==''&&scText.indexOf(q)>-1){sc.querySelectorAll('.step').forEach(function(st){st.style.display='';});scVisible=true;}
              sc.style.display=scVisible?'':'none';
              if(scVisible)fVisible=true;
            });
            f.style.display=fVisible?'':'none';
            if(fVisible){anyVisible=true; if(q!=='')f.open=true;}
          });
          if(q==='')setAllFeatures(false); // clearing the filter returns to the collapsed default
          document.getElementById('nomatch').hidden=anyVisible||q==='';
        }
        function setAllFeatures(open){
          document.querySelectorAll('#tree > details.feature').forEach(function(f){f.open=open;});
        }
        """;
}
