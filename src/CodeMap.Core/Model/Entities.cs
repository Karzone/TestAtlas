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
public sealed record IndexResult(
    MapMeta Meta,
    IReadOnlyList<ProjectEntity> Projects,
    IReadOnlyList<ClassEntity> Classes,
    IReadOnlyList<MethodEntity> Methods,
    IReadOnlyList<StepDefinitionEntity> StepDefinitions,
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
