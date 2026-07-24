namespace TestAtlas.Core.Model;

/// <summary>
/// The entity/method/class "kind" values from the spec (§5). Stored as strings in the map file
/// so the db stays human-readable. In slice 1 every class/method is <see cref="Other"/> — the
/// classification heuristics arrive in a later slice — but the constants exist now so the writer,
/// stats and tests reference one source of truth rather than scattering string literals.
/// </summary>
public static class Kinds
{
    // The universal fallback. Constraint (spec G2): unrecognised code degrades to this, never throws.
    public const string Other = "other";

    // Class kinds.
    public const string StepClass = "step_class";
    public const string PageObject = "page_object";
    public const string ApiClient = "api_client";
    public const string TestClass = "test_class";
    public const string HookClass = "hook_class";
    public const string Helper = "helper";

    // Method kinds.
    public const string StepDefinitionMethod = "step_definition";
    public const string HookMethod = "hook";
    public const string TestMethod = "test_method";
    public const string PageObjectMethod = "page_object_method";
    public const string ApiMethod = "api_method";
    public const string HelperMethod = "helper_method";

    // Project-kind summaries.
    public const string BddTests = "bdd_tests";
    public const string UnitTests = "unit_tests";
    public const string SharedLibrary = "shared_library";
}

/// <summary>How a step-definition expression is interpreted (spec §5.1).</summary>
public static class ExpressionKinds
{
    public const string Regex = "regex";
    public const string CucumberExpression = "cucumber_expression";
}

/// <summary>
/// A step-definition binding (spec §5.1): one row per binding attribute on a method — a method
/// with <c>[Given]</c> + <c>[When]</c> yields two rows.
/// </summary>
public sealed record StepDefinitionEntity(
    int Id,
    int MethodId,
    int ClassId,
    int ProjectId,
    string Keyword,        // Given | When | Then | StepDefinition
    string Expression,
    string ExpressionKind, // regex | cucumber_expression
    string Parameters,     // comma-joined "type name" of the method's parameters
    string FilePath,
    int LineStart);

/// <summary>A project in the analysed solution (spec §5.1).</summary>
public sealed record ProjectEntity(
    int Id,
    string Name,
    string Path,
    string? TargetFramework,
    string Kind);

/// <summary>A type declaration — class/struct/record/interface (spec §5.1, "Class").</summary>
public sealed record ClassEntity(
    int Id,
    int ProjectId,
    string Name,
    string Namespace,
    string? BaseType,
    string Kind,
    string FilePath,
    int LineStart,
    int LineEnd);

/// <summary>A method declaration (spec §5.1).</summary>
public sealed record MethodEntity(
    int Id,
    int ClassId,
    int ProjectId,
    string Name,
    string Signature,
    string Visibility,
    string Kind,
    string FilePath,
    int LineStart,
    int LineEnd);

/// <summary>Severity of a diagnostic row (spec §9).</summary>
public enum DiagnosticSeverity
{
    Warning,
    Error
}

/// <summary>
/// A diagnostic captured during indexing (spec §9): a project that failed to load, a file that
/// failed to parse, etc. Diagnostics are first-class from slice 1 — a half-loaded solution must be
/// visible in the db and in <c>stats</c>, not silently dropped.
/// </summary>
public sealed record DiagnosticEntity(
    DiagnosticSeverity Severity,
    string Code,
    string Message,
    string? Location);

/// <summary>Immutable metadata block written to the <c>meta</c> table (spec §9).</summary>
public sealed record MapMeta(
    string ToolVersion,
    string GeneratedUtc,
    string SolutionPath,
    string InputHash);

/// <summary>
/// The full result of an indexing run: everything the SQLite writer needs, plus the
/// <see cref="ExitCode"/> the CLI should return (spec §7 exit-code contract).
/// </summary>
/// <summary>A Gherkin feature (spec §5.1).</summary>
public sealed record FeatureEntity(
    int Id, int ProjectId, string Name, string Description, string Tags, string FilePath);

/// <summary>A scenario or scenario outline (spec §5.1). <paramref name="Tags"/> are own + inherited.</summary>
public sealed record ScenarioEntity(
    int Id, int FeatureId, int ProjectId, string Name, string Kind, string Tags,
    int ExampleRowCount, string FilePath, int LineStart);

/// <summary>An ordered step within a scenario (spec §5.1).</summary>
public sealed record ScenarioStepEntity(
    int Id, int ScenarioId, int ProjectId, string Keyword, string Text, int Ordinal,
    bool HasDocString, bool HasDataTable, string FilePath, int LineStart);

/// <summary>
/// An HTTP endpoint referenced by test code (spec §5.1, slice 4): the verb + route template a method
/// calls (e.g. <c>POST /api/orders/{id}</c>). Deduplicated solution-wide on (Verb, Route); call sites
/// are the <c>calls_endpoint</c> edges. <paramref name="Verb"/> is <c>ANY</c> when it could not be
/// inferred from the call shape.
/// </summary>
public sealed record EndpointEntity(int Id, string Verb, string Route, string? Path = null, string? TargetApi = null);

/// <summary>Edge-kind vocabulary (spec §5.2).</summary>
public static class EdgeKinds
{
    public const string BindsTo = "binds_to";
    public const string Unbound = "unbound";
    public const string Inherits = "inherits";       // Class → Class (base type within the solution)
    public const string UsesType = "uses_type";      // Method → Class (constructs/receives/dereferences it)
    public const string Holds = "holds";             // Class → Class (declares it as a field/property/return/param type)
    public const string References = "references";    // Project → Project (a .csproj ProjectReference — build/DI dependency)
    public const string CallsEndpoint = "calls_endpoint"; // Method → Endpoint (HTTP call in the body)
}

/// <summary>from/to entity-kind labels used in the edges table.</summary>
public static class RefKinds
{
    public const string ScenarioStep = "scenario_step";
    public const string StepDefinition = "step_definition";
    public const string Class = "class";
    public const string Method = "method";
    public const string Endpoint = "endpoint";
    public const string Project = "project";
}

/// <summary>binds_to confidence (spec §5.2).</summary>
public static class BindConfidence
{
    public const string Exact = "exact";
    public const string Ambiguous = "ambiguous";
}

/// <summary>An edge in the map graph (spec §5.2). <paramref name="ToId"/> is null for <c>unbound</c>.</summary>
public sealed record EdgeEntity(
    string FromKind, int FromId, string ToKind, int? ToId, string EdgeKind, string Confidence);

public sealed record IndexResult(
    MapMeta Meta,
    IReadOnlyList<ProjectEntity> Projects,
    IReadOnlyList<ClassEntity> Classes,
    IReadOnlyList<MethodEntity> Methods,
    IReadOnlyList<StepDefinitionEntity> StepDefinitions,
    IReadOnlyList<FeatureEntity> Features,
    IReadOnlyList<ScenarioEntity> Scenarios,
    IReadOnlyList<ScenarioStepEntity> ScenarioSteps,
    IReadOnlyList<EndpointEntity> Endpoints,
    IReadOnlyList<EdgeEntity> Edges,
    IReadOnlyList<DiagnosticEntity> Diagnostics,
    IndexOutcome Outcome);

/// <summary>
/// Outcome of an index run, mapped to the spec's exit codes by the CLI:
/// <see cref="Success"/> → 0, <see cref="CompletedWithWarnings"/> → 1, <see cref="Fatal"/> → 2.
/// </summary>
public enum IndexOutcome
{
    Success,
    CompletedWithWarnings,
    Fatal
}
