using System.Text.RegularExpressions;
using TestAtlas.Core.Model;

namespace TestAtlas.Core.Indexing;

/// <summary>Overridable knobs for the classifier (spec §8 config maps onto these).</summary>
public sealed class ClassifierOptions
{
    public IReadOnlyList<string> PageObjectSuffixes { get; init; } = new[] { "Page", "PageObject", "Screen", "Component" };
    public IReadOnlyList<string> ApiClientSuffixes { get; init; } = new[] { "Client", "Api", "Service", "Endpoint" };

    public static readonly ClassifierOptions Default = new();
}

/// <summary>
/// The syntactic signals gathered from one type declaration — everything the class heuristics need,
/// separated from Roslyn so the rules are pure and unit-testable.
/// </summary>
public sealed record ClassFacts(
    string Name,
    string? BaseTypeName,          // simple name of the first base type, if any
    bool HasBindingAttribute,      // [Binding]
    bool HasTestClassAttribute,    // [TestFixture] / [TestClass]
    int MethodCount,
    int StepMethodCount,           // methods carrying a step attribute
    int TestMethodCount,           // methods carrying a test attribute
    int HookMethodCount,           // methods carrying a hook attribute
    int InstanceMemberCount,       // fields + properties + methods (non-static)
    int UiReferencingMembers,      // instance members referencing a UI-automation type
    int ApiReferencingMembers,     // methods referencing a RestSharp/HttpClient type
    bool ReferencesUiType,
    bool ReferencesApiType,
    bool HoldsOrConstructsApiMarker = false, // holds/constructs a RestSharp/HttpClient marker type
    IReadOnlyList<string>? ConstructedTypeNames = null, // simple names of `new X()` in the class body
    bool IsStatic = false);        // a `static class` — never instantiated, so never a collaborator

/// <summary>Per-method signals for method-kind classification.</summary>
public sealed record MethodFacts(
    bool HasStepAttribute,
    bool HasHookAttribute,
    bool HasTestAttribute);

/// <summary>
/// Classifies types and methods into the spec's <see cref="Kinds"/> vocabulary (spec §6). Ordered,
/// first-match-wins, and — per constraint G2 — never throws: any fault degrades to
/// <see cref="Kinds.Other"/>. The <c>helper</c> kind depends on usage edges and is deferred to a
/// later slice.
/// </summary>
public static class Classifier
{
    /// <summary>
    /// Classify a type from its gathered facts. <paramref name="kindOf"/> resolves a type simple name
    /// to an already-assigned kind (for the inherits-a-page-object / -api-client and wraps-an-api-client
    /// rules); pass <c>_ => null</c> on the first pass and re-run to a fixpoint.
    /// </summary>
    public static string ClassifyClass(ClassFacts f, ClassifierOptions opts, Func<string?, string?> kindOf)
    {
        try
        {
            opts ??= ClassifierOptions.Default;

            // 1. Step class — carries [Binding] or contains a step-attributed method.
            if (f.HasBindingAttribute || f.StepMethodCount >= 1)
                return Kinds.StepClass;

            // 2. Inheritance is the STRONGEST collaborator signal — an explicit base already classified
            //    as a page object / API client wins over the member-ratio and name heuristics below. A
            //    `*ApiService : BaseApiService` that also touches UI types is an api_client, not a page
            //    object (which the greedy UI-ratio rule would otherwise claim first).
            var baseKind = kindOf(f.BaseTypeName);
            if (baseKind == Kinds.PageObject) return Kinds.PageObject;
            if (baseKind == Kinds.ApiClient) return Kinds.ApiClient;

            // 3. Page object / API client are INSTANTIATED collaborators; a `static class` is never one
            //    (it holds no state and is never `new`-ed), so it skips these rules entirely even when it
            //    references RestSharp/UI types — a static RestSharp helper is a helper, not a client.
            if (!f.IsStatic)
            {
                // Page object.
                if (f.InstanceMemberCount > 0 && f.UiReferencingMembers * 2 >= f.InstanceMemberCount)
                    return Kinds.PageObject;
                if (NameHasSuffix(f.Name, opts.PageObjectSuffixes) && f.ReferencesUiType)
                    return Kinds.PageObject;

                // API client — references RestSharp/HttpClient directly, holds a transport marker,
                // matches a name suffix while touching an API type, OR is a named wrapper that constructs
                // one. The last rule walks the service layer that hides HTTP behind a typed request
                // (BaseRequest → BaseApiService → *ApiService); it needs the inherits/uses fixpoint.
                if (f.MethodCount > 0 && f.ApiReferencingMembers * 2 >= f.MethodCount)
                    return Kinds.ApiClient;
                if (f.HoldsOrConstructsApiMarker)
                    return Kinds.ApiClient;
                if (NameHasSuffix(f.Name, opts.ApiClientSuffixes) && f.ReferencesApiType)
                    return Kinds.ApiClient;
                // Constructing an api_client only makes a class part of the API layer when it is ALSO named
                // like one (Api/Client/Service/Endpoint). Composition alone is *usage*, not *identity*: a
                // utility/helper/resolver that news up a request wrapper to CALL it is a consumer — exactly
                // as a step class that constructs a page object is not a page object (page objects likewise
                // propagate only by inheritance, never composition). Without this name gate every
                // *Utilities/*Helper that internally did `new BaseRequest<T>()` was mis-promoted — 40+ false
                // positives on a real 28-project solution, against ~90 genuine clients. The genuine base
                // wrappers (e.g. BaseApiService) are conventionally *…Service and keep their badge.
                if (NameHasSuffix(f.Name, opts.ApiClientSuffixes)
                    && f.ConstructedTypeNames is { } constructed
                    && constructed.Any(n => kindOf(n) == Kinds.ApiClient))
                    return Kinds.ApiClient;
            }

            // 4. Test class.
            if (f.HasTestClassAttribute || f.TestMethodCount >= 1)
                return Kinds.TestClass;

            // 5. Hook class.
            if (f.HookMethodCount >= 1)
                return Kinds.HookClass;

            // 6. Helper — needs usage edges; deferred. 7. Otherwise other.
            return Kinds.Other;
        }
        catch
        {
            return Kinds.Other;
        }
    }

    /// <summary>Classify a method from its own attributes plus its containing class's kind.</summary>
    public static string ClassifyMethod(MethodFacts f, string classKind)
    {
        try
        {
            if (f.HasStepAttribute) return Kinds.StepDefinitionMethod;
            if (f.HasHookAttribute) return Kinds.HookMethod;
            if (f.HasTestAttribute) return Kinds.TestMethod;
            return classKind switch
            {
                Kinds.PageObject => Kinds.PageObjectMethod,
                Kinds.ApiClient => Kinds.ApiMethod,
                Kinds.Helper => Kinds.HelperMethod,
                _ => Kinds.Other,
            };
        }
        catch
        {
            return Kinds.Other;
        }
    }

    /// <summary>
    /// Summarise a project's kind from its classified classes (spec §5.1): <b>any</b> BDD step class
    /// ⇒ <c>bdd_tests</c> (a project that hosts step definitions is BDD test-automation even when it
    /// also carries generated test fixtures — those often outnumber the step classes); otherwise test
    /// classes ⇒ <c>unit_tests</c>; otherwise <c>shared_library</c> when it holds classes, else
    /// <c>other</c>.
    /// </summary>
    public static string SummariseProject(IEnumerable<ClassEntity> classes)
    {
        try
        {
            var list = classes as IReadOnlyList<ClassEntity> ?? classes.ToList();
            if (list.Count == 0) return Kinds.Other;

            var steps = list.Count(c => c.Kind == Kinds.StepClass);
            var tests = list.Count(c => c.Kind == Kinds.TestClass);

            // Step definitions are the defining BDD signal — a step-hosting project that other
            // projects bind to must not be mislabelled unit_tests just because it also holds many
            // generated [TestFixture]/[TestClass] codebehind classes.
            if (steps > 0) return Kinds.BddTests;
            if (tests > 0) return Kinds.UnitTests;
            return Kinds.SharedLibrary;
        }
        catch
        {
            return Kinds.Other;
        }
    }

    /// <summary>Choose an expression kind for a step expression (spec §5.1).</summary>
    public static string DetectExpressionKind(string expression, string frameworkDefault)
    {
        var e = expression ?? string.Empty;
        // A cucumber parameter placeholder — {}, {int}, {string}, {word}, … — is decisive.
        if (Regex.IsMatch(e, @"\{\w*\}")) return ExpressionKinds.CucumberExpression;
        // Regex metacharacters mark it as a regex.
        if (Regex.IsMatch(e, @"[\\^$.|?*+()\[\]]")) return ExpressionKinds.Regex;
        // A plain literal is valid as either — fall back to the framework's default.
        return frameworkDefault;
    }

    private static bool NameHasSuffix(string name, IReadOnlyList<string> suffixes)
        => name is not null && suffixes.Any(s => name.EndsWith(s, StringComparison.Ordinal));
}
