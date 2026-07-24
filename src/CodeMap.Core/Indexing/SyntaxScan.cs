using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TestAtlas.Core.Model;

namespace TestAtlas.Core.Indexing;

/// <summary>
/// Syntax-only extraction of the classification signals (spec §6): attributes, marker-type
/// references, and step-binding attributes. Deliberately works off syntax (not the semantic model)
/// so it holds up on projects that never restored — the reality on real machines.
/// </summary>
internal static class SyntaxScan
{
    // Marker type simple-names.

    private static readonly HashSet<string> StepAttrs = new(StringComparer.Ordinal)
        { "Given", "When", "Then", "StepDefinition" };
    private static readonly HashSet<string> BindingAttrs = new(StringComparer.Ordinal) { "Binding" };
    private static readonly HashSet<string> TestMethodAttrs = new(StringComparer.Ordinal)
        { "Test", "TestCase", "Fact", "Theory", "TestMethod", "DataTestMethod" };
    private static readonly HashSet<string> TestClassAttrs = new(StringComparer.Ordinal)
        { "TestFixture", "TestClass" };

    /// <summary>Rightmost identifier of an attribute name, with any trailing "Attribute" removed.</summary>
    public static string AttrSimpleName(AttributeSyntax attr)
    {
        var name = attr.Name switch
        {
            QualifiedNameSyntax q => q.Right.Identifier.ValueText,
            SimpleNameSyntax s => s.Identifier.ValueText,
            _ => attr.Name.ToString(),
        };
        return name.EndsWith("Attribute", StringComparison.Ordinal)
            ? name[..^"Attribute".Length]
            : name;
    }

    private static IEnumerable<AttributeSyntax> Attributes(SyntaxList<AttributeListSyntax> lists)
        => lists.SelectMany(l => l.Attributes);

    public static bool IsHookAttr(string name)
        => name.StartsWith("Before", StringComparison.Ordinal) || name.StartsWith("After", StringComparison.Ordinal);

    /// <summary>Step-binding attributes on a method → (keyword, expression) pairs (spec §5.1).</summary>
    public static List<(string Keyword, string Expression)> StepBindings(MethodDeclarationSyntax method)
    {
        var result = new List<(string, string)>();
        foreach (var attr in Attributes(method.AttributeLists))
        {
            var name = AttrSimpleName(attr);
            if (!StepAttrs.Contains(name)) continue;
            result.Add((name, FirstStringArgument(attr) ?? string.Empty));
        }
        return result;
    }

    public static MethodFacts GatherMethodFacts(MethodDeclarationSyntax method)
    {
        var names = Attributes(method.AttributeLists).Select(AttrSimpleName).ToList();
        return new MethodFacts(
            HasStepAttribute: names.Any(StepAttrs.Contains),
            HasHookAttribute: names.Any(IsHookAttr),
            HasTestAttribute: names.Any(TestMethodAttrs.Contains));
    }

    public static ClassFacts GatherClassFacts(TypeDeclarationSyntax type, ClassifierOptions? opts = null)
    {
        opts ??= ClassifierOptions.Default;
        var uiTypes = opts.UiMarkerTypes as HashSet<string> ?? opts.UiMarkerTypes.ToHashSet(StringComparer.Ordinal);
        var apiTypes = opts.ApiMarkerTypes as HashSet<string> ?? opts.ApiMarkerTypes.ToHashSet(StringComparer.Ordinal);

        var classAttrs = Attributes(type.AttributeLists).Select(AttrSimpleName).ToList();

        var methods = type.Members.OfType<MethodDeclarationSyntax>().ToList();
        var stepMethods = methods.Count(m => Attributes(m.AttributeLists).Select(AttrSimpleName).Any(StepAttrs.Contains));
        var testMethods = methods.Count(m => Attributes(m.AttributeLists).Select(AttrSimpleName).Any(TestMethodAttrs.Contains));
        var hookMethods = methods.Count(m => Attributes(m.AttributeLists).Select(AttrSimpleName).Any(IsHookAttr));

        // Instance members = non-static fields + properties + methods.
        var instanceMembers = type.Members
            .Where(m => m is FieldDeclarationSyntax or PropertyDeclarationSyntax or MethodDeclarationSyntax)
            .Where(m => !HasStaticModifier(m))
            .ToList();

        var uiMembers = instanceMembers.Count(m => ReferencesAny(m, uiTypes));
        var apiMethodRefs = methods.Count(m => ReferencesAny(m, apiTypes));

        var referencesUi = ReferencesAny(type, uiTypes);
        var referencesApi = ReferencesAny(type, apiTypes);

        // Simple names of every `new X()` / `new X<..>()` in the type — the signal that lets the
        // classifier propagate api_client-ness through a wrapper (a class that constructs an
        // HTTP-executing type is itself part of the API layer). `List`/`Dictionary`/etc. fall out
        // naturally: they are not solution classes, so they never resolve to a kind.
        var constructed = type.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
            .Select(oc => SimpleBaseName(oc.Type))
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        // Directly holding OR constructing a RestSharp/HttpClient marker type is the strongest
        // api_client signal — and, unlike the method-ratio rule, it survives the real-world shape
        // where the client lives in a FIELD (e.g. `IRestClient _client`) driven through a variable,
        // so the marker's type name never appears in a method BODY. Fields/props + `new X()` count.
        var holdsOrConstructsApiMarker = constructed.Any(apiTypes.Contains) || type.Members.Any(m =>
        {
            var t = m switch
            {
                FieldDeclarationSyntax f => f.Declaration.Type,
                PropertyDeclarationSyntax p => p.Type,
                _ => (TypeSyntax?)null,
            };
            return t is not null && TypeIdentifiers(t).Any(apiTypes.Contains);
        });

        return new ClassFacts(
            Name: type.Identifier.ValueText,
            BaseTypeName: SimpleBaseName(type.BaseList?.Types.FirstOrDefault()?.Type),
            HasBindingAttribute: classAttrs.Any(BindingAttrs.Contains),
            HasTestClassAttribute: classAttrs.Any(TestClassAttrs.Contains),
            MethodCount: methods.Count,
            StepMethodCount: stepMethods,
            TestMethodCount: testMethods,
            HookMethodCount: hookMethods,
            InstanceMemberCount: instanceMembers.Count,
            UiReferencingMembers: uiMembers,
            ApiReferencingMembers: apiMethodRefs,
            ReferencesUiType: referencesUi,
            ReferencesApiType: referencesApi,
            HoldsOrConstructsApiMarker: holdsOrConstructsApiMarker,
            ConstructedTypeNames: constructed,
            IsStatic: type.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)));
    }

    /// <summary>The step framework's default expression kind, inferred from the file's usings.</summary>
    public static string FrameworkExpressionDefault(SyntaxNode root)
    {
        foreach (var u in root.DescendantNodes().OfType<UsingDirectiveSyntax>())
        {
            var n = u.Name?.ToString() ?? string.Empty;
            if (n.StartsWith("Reqnroll", StringComparison.Ordinal)) return ExpressionKinds.CucumberExpression;
            if (n.StartsWith("TechTalk.SpecFlow", StringComparison.Ordinal)) return ExpressionKinds.Regex;
        }
        return ExpressionKinds.Regex; // SpecFlow-style regex is the safer historical default
    }

    /// <summary>
    /// Simple type-names a method uses (spec §5.2 <c>uses_type</c>): its parameter/return types, the
    /// types it constructs (<c>new Foo()</c>) or declares locally, and — the dominant test-automation
    /// pattern — the types of the containing class's fields/properties it dereferences by name (e.g. a
    /// step method calling <c>_loginPage.Open()</c>). Purely syntactic, so it holds up unrestored.
    /// </summary>
    public static HashSet<string> UsedTypeNames(MethodDeclarationSyntax method, TypeDeclarationSyntax containingType)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var t in TypeIdentifiers(method.ReturnType)) names.Add(t);
        foreach (var p in method.ParameterList.Parameters)
            foreach (var t in TypeIdentifiers(p.Type)) names.Add(t);

        SyntaxNode? body = method.Body;
        body ??= method.ExpressionBody;
        if (body is null) return names;

        foreach (var oc in body.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
            foreach (var t in TypeIdentifiers(oc.Type)) names.Add(t);
        foreach (var vd in body.DescendantNodes().OfType<VariableDeclarationSyntax>())
            foreach (var t in TypeIdentifiers(vd.Type)) names.Add(t);

        var memberTypes = MemberTypes(containingType);
        if (memberTypes.Count > 0)
            foreach (var id in body.DescendantNodes().OfType<IdentifierNameSyntax>())
                if (memberTypes.TryGetValue(id.Identifier.ValueText, out var types))
                    foreach (var t in types) names.Add(t);

        return names;
    }

    /// <summary>Field / auto-property member-name → the simple type-names of its declared type.</summary>
    private static Dictionary<string, string[]> MemberTypes(TypeDeclarationSyntax type)
    {
        var map = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var member in type.Members)
        {
            switch (member)
            {
                case FieldDeclarationSyntax f:
                    var ft = TypeIdentifiers(f.Declaration.Type).ToArray();
                    foreach (var v in f.Declaration.Variables) map[v.Identifier.ValueText] = ft;
                    break;
                case PropertyDeclarationSyntax p:
                    map[p.Identifier.ValueText] = TypeIdentifiers(p.Type).ToArray();
                    break;
            }
        }
        return map;
    }

    /// <summary>
    /// The simple type-names a type <b>holds or exposes</b> in its member signatures: field and property
    /// declared types, method return types, and method parameter types. This is the structural-reference
    /// signal a name-based construction scan misses — an aggregator holds a service as
    /// <c>WorkflowApiService Workflow { get; } = new(context);</c>, whose declared type is the collaborator
    /// even though the target-typed <c>new()</c> carries no type name; likewise a DI-injected
    /// <c>readonly SupplierApiService _svc</c>. Lets the report tell "held/used somewhere" from "genuinely
    /// orphaned" (referenced by nothing), instead of only seeing <c>new TypeName()</c> in method bodies.
    /// </summary>
    public static HashSet<string> HeldTypeNames(TypeDeclarationSyntax type)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        void AddAll(TypeSyntax? t) { foreach (var n in TypeIdentifiers(t)) names.Add(n); }
        foreach (var m in type.Members)
            switch (m)
            {
                case FieldDeclarationSyntax f: AddAll(f.Declaration.Type); break;
                case PropertyDeclarationSyntax p: AddAll(p.Type); break;
                case MethodDeclarationSyntax md:
                    AddAll(md.ReturnType);
                    foreach (var par in md.ParameterList.Parameters) AddAll(par.Type);
                    break;
            }
        return names;
    }

    /// <summary>All simple identifier names within a type syntax (the type itself + any generic args).</summary>
    private static IEnumerable<string> TypeIdentifiers(TypeSyntax? type)
    {
        if (type is null) yield break;
        foreach (var node in type.DescendantNodesAndSelf())
        {
            var name = node switch
            {
                GenericNameSyntax g => g.Identifier.ValueText,
                IdentifierNameSyntax id => id.Identifier.ValueText,
                _ => null,
            };
            if (name is not null && name != "var") yield return name;
        }
    }

    // ---- endpoint extraction (spec §5.1 Endpoint, slice 4) ---------------------------------------

    /// <summary>Known HTTP-client method names → verb (HttpClient / RestSharp / Flurl style).</summary>
    private static readonly Dictionary<string, string> HttpMethodNames = new(StringComparer.Ordinal)
    {
        ["GetAsync"] = "GET", ["GetStringAsync"] = "GET", ["GetStreamAsync"] = "GET",
        ["GetByteArrayAsync"] = "GET", ["GetFromJsonAsync"] = "GET", ["GetJsonAsync"] = "GET",
        ["PostAsync"] = "POST", ["PostAsJsonAsync"] = "POST", ["PostJsonAsync"] = "POST",
        ["PutAsync"] = "PUT", ["PutAsJsonAsync"] = "PUT", ["PutJsonAsync"] = "PUT",
        ["PatchAsync"] = "PATCH", ["PatchAsJsonAsync"] = "PATCH",
        ["DeleteAsync"] = "DELETE", ["DeleteFromJsonAsync"] = "DELETE", ["DeleteJsonAsync"] = "DELETE",
    };

    private static readonly string[] GenericVerbs = { "Get", "Post", "Put", "Patch", "Delete" };

    /// <summary>Refit-style HTTP attributes.</summary>
    private static readonly Dictionary<string, string> HttpAttrNames = new(StringComparer.Ordinal)
    {
        ["Get"] = "GET", ["Post"] = "POST", ["Put"] = "PUT", ["Patch"] = "PATCH",
        ["Delete"] = "DELETE", ["Head"] = "HEAD",
    };

    /// <summary>
    /// The HTTP endpoints a method calls, as (verb, route-template) pairs — purely syntactic, so it
    /// works on unrestored projects and is deliberately solution-agnostic. Ladder (spec §5.1):
    /// 1. known client method names (HttpClient/Flurl: <c>GetAsync("/x")</c>, <c>PostAsJsonAsync</c>…);
    /// 2. <c>new HttpRequestMessage(HttpMethod.X, "/x")</c> (covers Send/SendAsync);
    /// 3. <c>new RestRequest("/x", Method.X)</c> and <c>Resource = "/x"</c> assignments (RestSharp);
    /// 4. Refit-style <c>[Get("/x")]</c> attributes on the method;
    /// 5. verb-as-argument: ANY invocation passing <c>HttpMethod.X</c> / <c>Method.X</c> alongside a
    ///    route-like string (<c>Execute(HttpMethod.Get, "/x")</c> — central client wrappers);
    /// 6. generic fallback for custom wrappers: an invocation whose name starts with a verb word
    ///    (<c>Get/Post/Put/Patch/Delete</c>) passing a strictly route-like literal (starts with '/',
    ///    or contains '://' or '/{').
    /// Route arguments may be inline literals, interpolated strings (holes → <c>{expr}</c> template),
    /// or references to <c>const</c>/<c>static readonly</c> string fields of the containing class.
    /// XPath-shaped strings (<c>//…</c>, <c>…/text()</c>, <c>/ns:element</c>) are rejected — they are
    /// selectors, not routes. Anything unrecognised produces nothing — never an error.
    /// </summary>
    public static List<(string Verb, string Route)> EndpointCalls(
        MethodDeclarationSyntax method, TypeDeclarationSyntax? containingType = null)
    {
        var found = new List<(string, string)>();
        var consts = ConstStrings(containingType);

        string? Route(ExpressionSyntax? e) => RouteFromExpression(e, consts);

        // 4. Refit-style attributes on the method itself.
        foreach (var attr in Attributes(method.AttributeLists))
        {
            var name = AttrSimpleName(attr);
            if (!HttpAttrNames.TryGetValue(name, out var verb)) continue;
            var route = Route(attr.ArgumentList?.Arguments.FirstOrDefault()?.Expression);
            if (route is not null && IsRouteLike(route, strict: false)) found.Add((verb, route));
        }

        SyntaxNode? body = method.Body;
        body ??= method.ExpressionBody;
        if (body is null) return found;

        foreach (var node in body.DescendantNodes())
        {
            switch (node)
            {
                case InvocationExpressionSyntax inv:
                {
                    var name = inv.Expression switch
                    {
                        MemberAccessExpressionSyntax ma => ma.Name.Identifier.ValueText,
                        IdentifierNameSyntax id => id.Identifier.ValueText,
                        GenericNameSyntax g => g.Identifier.ValueText,
                        MemberBindingExpressionSyntax mb => mb.Name.Identifier.ValueText,
                        _ => null,
                    };
                    if (name is null) break;

                    var args = inv.ArgumentList.Arguments;
                    var firstArg = args.FirstOrDefault()?.Expression;
                    var route = Route(firstArg);

                    // 1. Known client method names — relaxed route check (relative "api/x" is common).
                    if (HttpMethodNames.TryGetValue(name, out var verb))
                    {
                        if (route is not null && IsRouteLike(route, strict: false)) found.Add((verb, route));
                        break;
                    }

                    // (HttpRequestMessage inside Send/SendAsync is seen by the ObjectCreation case
                    // below on its own — handling it here too would double-count the same call.)

                    // 5. Verb-as-argument: a central wrapper whose name says nothing, but an argument
                    //    is HttpMethod.X / Method.X and another is route-like — e.g.
                    //    ExecuteAsync(HttpMethod.Get, "/api/orders") or CallApi("/x", Method.POST).
                    var argVerb = args.Select(a => VerbFromMemberAccess(a.Expression)).FirstOrDefault(v => v is not null);
                    if (argVerb is not null)
                    {
                        var argRoute = args.Select(a => Route(a.Expression))
                            .FirstOrDefault(r => r is not null && IsRouteLike(r, strict: false));
                        if (argRoute is not null) found.Add((argVerb, argRoute));
                        break;
                    }

                    // 6. Generic fallback for custom wrappers — strict route check to avoid noise.
                    var genericVerb = GenericVerbs.FirstOrDefault(v =>
                        name.StartsWith(v, StringComparison.OrdinalIgnoreCase) &&
                        (name.Length == v.Length || !char.IsLower(name[v.Length])));
                    if (genericVerb is not null && route is not null && IsRouteLike(route, strict: true))
                        found.Add((genericVerb.ToUpperInvariant(), route));
                    break;
                }
                case ObjectCreationExpressionSyntax oc when SimpleBaseName(oc.Type) is "RestRequest":
                {
                    // 3. new RestRequest("/x"[, Method.Post]) — verb defaults to GET (RestSharp default).
                    var route = Route(oc.ArgumentList?.Arguments.FirstOrDefault()?.Expression);
                    if (route is null || !IsRouteLike(route, strict: false)) break;
                    var verb = "GET";
                    foreach (var arg in oc.ArgumentList!.Arguments.Skip(1))
                        if (VerbFromMemberAccess(arg.Expression) is { } v) verb = v;
                    found.Add((verb, route));
                    break;
                }
                case ObjectCreationExpressionSyntax oc when SimpleBaseName(oc.Type) is "HttpRequestMessage":
                {
                    if (TryHttpRequestMessage(oc, consts, out var v, out var r)) found.Add((v, r));
                    break;
                }
                case AssignmentExpressionSyntax assign
                    when assign.Left is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Resource" }
                      || assign.Left is IdentifierNameSyntax { Identifier.ValueText: "Resource" }:
                {
                    // 3b. RestSharp property style: request.Resource = "/x"; verb unknown here → ANY.
                    var route = Route(assign.Right);
                    if (route is not null && IsRouteLike(route, strict: false)) found.Add(("ANY", route));
                    break;
                }
            }
        }

        return found;
    }

    /// <summary>
    /// Operation-level endpoint candidates in a method body: <c>new Wrapper&lt;Request&gt;(…)</c>
    /// single-type-argument generic constructions, returned as (Wrapper, Request) simple-name pairs.
    /// Purely syntactic — whether the wrapper is genuinely an HTTP-executing type is decided
    /// solution-wide by the indexer (only it knows class kinds), which keeps only the pairs whose
    /// wrapper is a classified <c>api_client</c>. This is the shape frameworks use when the URL lives
    /// inside a typed request object rather than at the call site
    /// (<c>new BaseRequest&lt;GetUserRequest&gt;().ExecuteAsync()</c>): the request type IS the
    /// operation identity, so it becomes the endpoint's route (a bare type name, never containing '/').
    /// </summary>
    public static List<(string Wrapper, string Request)> GenericOperationCandidates(MethodDeclarationSyntax method)
    {
        var found = new List<(string, string)>();
        SyntaxNode? body = method.Body;
        body ??= method.ExpressionBody;
        if (body is null) return found;

        // Type parameters in scope (the method's own + every enclosing type's) are NOT concrete request
        // types: `new BaseRequest<TRequest>()` inside `RequestAsync<TRequest>()` names the parameter, not
        // an operation. Excluding them keeps generic plumbing (e.g. BaseApiService.RequestAsync) out of
        // the endpoint list — otherwise the literal `TRequest`/`TResponse` surfaces as a phantom endpoint
        // with a huge, meaningless blast radius.
        var typeParams = TypeParametersInScope(method);

        foreach (var oc in body.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
            if (oc.Type is GenericNameSyntax { TypeArgumentList.Arguments: { Count: 1 } targs } g
                && targs[0] is IdentifierNameSyntax req
                && !typeParams.Contains(req.Identifier.ValueText))
                found.Add((g.Identifier.ValueText, req.Identifier.ValueText));

        return found;
    }

    /// <summary>Every generic type-parameter name visible inside a method: its own plus each enclosing type's.</summary>
    private static HashSet<string> TypeParametersInScope(MethodDeclarationSyntax method)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (method.TypeParameterList is { } methodTps)
            foreach (var p in methodTps.Parameters) set.Add(p.Identifier.ValueText);
        for (SyntaxNode? n = method.Parent; n is not null; n = n.Parent)
            if (n is TypeDeclarationSyntax { TypeParameterList: { } typeTps })
                foreach (var p in typeTps.Parameters) set.Add(p.Identifier.ValueText);
        return set;
    }

    /// <summary>Ordered verb-word prefixes for inferring an operation's HTTP verb from its request-type name.</summary>
    private static readonly (string Prefix, string Verb)[] OperationVerbPrefixes =
    {
        ("Get", "GET"), ("Fetch", "GET"), ("List", "GET"), ("Query", "GET"), ("Read", "GET"), ("Search", "GET"),
        ("Create", "POST"), ("Add", "POST"), ("Insert", "POST"), ("Register", "POST"), ("Submit", "POST"),
        ("Post", "POST"), ("Save", "POST"), ("Send", "POST"),
        ("Update", "PUT"), ("Edit", "PUT"), ("Modify", "PUT"), ("Replace", "PUT"), ("Put", "PUT"),
        ("Patch", "PATCH"),
        ("Delete", "DELETE"), ("Remove", "DELETE"),
    };

    /// <summary>
    /// Best-effort HTTP verb for an operation named after its request type — inferred from the leading
    /// verb word (<c>GetUserRequest</c> → GET, <c>CreateOrderRequest</c> → POST). Returns <c>ANY</c>
    /// when the name starts with no recognised verb, so the map never claims a verb it cannot see.
    /// </summary>
    public static string VerbFromOperationName(string requestType)
    {
        foreach (var (prefix, verb) in OperationVerbPrefixes)
            if (requestType.StartsWith(prefix, StringComparison.Ordinal)
                && (requestType.Length == prefix.Length || char.IsUpper(requestType[prefix.Length])))
                return verb;
        return "ANY";
    }

    /// <summary><c>HttpMethod.X</c> / <c>Method.X</c> member access → the verb, else null.</summary>
    private static string? VerbFromMemberAccess(ExpressionSyntax expr)
        => expr is MemberAccessExpressionSyntax ma &&
           ma.Expression is IdentifierNameSyntax { Identifier.ValueText: "Method" or "HttpMethod" }
            ? ma.Name.Identifier.ValueText.ToUpperInvariant()
            : null;

    // Conventional property names a request-descriptor uses for its route and its logical API bucket.
    // Detection keys on the universal signal (a getter returning a RestSharp/System.Net Method member),
    // so this generalises past any one framework's interface name; the name sets just say which string
    // getter is the route vs the API.
    private static readonly HashSet<string> RouteProps = new(StringComparer.Ordinal)
        { "ServiceName", "Resource", "Route", "Path", "Endpoint", "Url", "Uri", "RequestUri", "Address" };
    private static readonly HashSet<string> TargetApiProps = new(StringComparer.Ordinal)
        { "TargetAPI", "TargetApi", "Api", "ApiName", "Host", "BaseUrl", "BaseAddress" };

    /// <summary>
    /// If a type is a <b>request descriptor</b> — a class exposing its HTTP verb as a getter returning a
    /// <c>Method</c>/<c>HttpMethod</c> member <b>and</b> its route as a string-literal getter
    /// (<c>ServiceName</c>/<c>Resource</c>/…) — return the statically-recovered
    /// <c>(Verb, Route, TargetApi)</c>. Reads the getter's <b>return expression</b>
    /// (<c>get { return "…"; }</c> or <c>=> "…"</c>), never a field or attribute. A route template
    /// (<c>".../{0}/submit"</c>) is returned verbatim — the concrete id isn't statically known. Null
    /// when the type is not a request descriptor.
    /// </summary>
    public static (string Verb, string Route, string? TargetApi)? RequestEndpointOf(TypeDeclarationSyntax type)
    {
        string? verb = null, route = null, targetApi = null;
        foreach (var prop in type.Members.OfType<PropertyDeclarationSyntax>())
        {
            var expr = GetterReturnExpression(prop);
            if (expr is null) continue;
            var name = prop.Identifier.ValueText;
            if (verb is null && VerbFromMemberAccess(expr) is { } v) verb = v;
            if (route is null && RouteProps.Contains(name) && StringLiteralOf(expr) is { } r) route = r;
            if (targetApi is null && TargetApiProps.Contains(name) && StringLiteralOf(expr) is { } t) targetApi = t;
        }
        return verb is not null && route is not null ? (verb, route, targetApi) : null;
    }

    /// <summary>The expression a property getter returns: expression-bodied property, expression-bodied
    /// getter, or the first <c>return</c> in a block getter. Null if it has no readable getter.</summary>
    private static ExpressionSyntax? GetterReturnExpression(PropertyDeclarationSyntax p)
    {
        if (p.ExpressionBody is { } propBody) return propBody.Expression;              // string X => "…";
        var getter = p.AccessorList?.Accessors.FirstOrDefault(a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
        if (getter is null) return null;
        if (getter.ExpressionBody is { } getBody) return getBody.Expression;           // get => "…";
        return getter.Body?.Statements.OfType<ReturnStatementSyntax>().FirstOrDefault()?.Expression; // get { return "…"; }
    }

    /// <summary>The literal value of a string-literal expression, else null (interpolations excluded).</summary>
    private static string? StringLiteralOf(ExpressionSyntax expr)
        => expr is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.StringLiteralExpression)
            ? lit.Token.ValueText : null;

    /// <summary>const / static readonly string fields of the containing class → name → value.</summary>
    private static Dictionary<string, string> ConstStrings(TypeDeclarationSyntax? type)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (type is null) return map;
        foreach (var f in type.Members.OfType<FieldDeclarationSyntax>())
        {
            var isConst = f.Modifiers.Any(m => m.ValueText == "const");
            var isStaticReadonly = f.Modifiers.Any(m => m.ValueText == "static") &&
                                   f.Modifiers.Any(m => m.ValueText == "readonly");
            if (!isConst && !isStaticReadonly) continue;
            foreach (var v in f.Declaration.Variables)
                if (v.Initializer?.Value is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.StringLiteralExpression))
                    map[v.Identifier.ValueText] = lit.Token.ValueText;
        }
        return map;
    }

    private static bool TryHttpRequestMessage(
        ObjectCreationExpressionSyntax oc, Dictionary<string, string> consts, out string verb, out string route)
    {
        verb = "ANY"; route = string.Empty;
        if (SimpleBaseName(oc.Type) is not "HttpRequestMessage" || oc.ArgumentList is null) return false;
        foreach (var arg in oc.ArgumentList.Arguments)
        {
            if (VerbFromMemberAccess(arg.Expression) is { } v)
                verb = v;
            else if (RouteFromExpression(arg.Expression, consts) is { } r && IsRouteLike(r, strict: false))
                route = r;
        }
        return route.Length > 0;
    }

    /// <summary>
    /// A string literal verbatim, an interpolated string as a route template (holes → <c>{expr}</c>),
    /// or an identifier resolving to a const / static readonly string of the containing class.
    /// </summary>
    private static string? RouteFromExpression(ExpressionSyntax? expr, Dictionary<string, string> consts) => expr switch
    {
        LiteralExpressionSyntax lit when lit.IsKind(SyntaxKind.StringLiteralExpression) => lit.Token.ValueText,
        InterpolatedStringExpressionSyntax interp => string.Concat(interp.Contents.Select(c => c switch
        {
            InterpolatedStringTextSyntax t => t.TextToken.ValueText,
            InterpolationSyntax h => "{" + h.Expression.ToString() + "}",
            _ => string.Empty,
        })),
        IdentifierNameSyntax id when consts.TryGetValue(id.Identifier.ValueText, out var v) => v,
        _ => null,
    };

    private static readonly System.Text.RegularExpressions.Regex XPathSegment =
        new(@"/[A-Za-z_][\w.-]*:[A-Za-z]", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Does a string look like a URL path? Relaxed (known client methods): contains '/', no whitespace.
    /// Strict (generic wrapper fallback): additionally must start with '/' or contain "://" or "/{".
    /// XPath selectors are rejected in both modes: leading <c>//</c>, function calls (<c>text()</c>),
    /// or namespace-prefixed segments (<c>/soapenv:Envelope</c>) — "://" is excluded from that check.
    /// </summary>
    private static bool IsRouteLike(string s, bool strict)
    {
        if (s.Length == 0 || s.Any(char.IsWhiteSpace) || !s.Contains('/')) return false;
        if (s.StartsWith("//", StringComparison.Ordinal) || s.Contains("()", StringComparison.Ordinal)) return false;
        if (XPathSegment.IsMatch(s.Replace("://", ""))) return false; // ns-prefixed segment = XPath
        return !strict || s.StartsWith('/') || s.Contains("://") || s.Contains("/{");
    }

    /// <summary>Simple name of a base type, stripping namespace qualifier and generic args.</summary>
    public static string? SimpleBaseName(TypeSyntax? type) => type switch
    {
        null => null,
        GenericNameSyntax g => g.Identifier.ValueText,
        QualifiedNameSyntax q => SimpleBaseName(q.Right),
        SimpleNameSyntax s => s.Identifier.ValueText,
        _ => type.ToString(),
    };

    private static bool HasStaticModifier(MemberDeclarationSyntax m) => m switch
    {
        FieldDeclarationSyntax f => f.Modifiers.Any(t => t.ValueText == "static"),
        PropertyDeclarationSyntax p => p.Modifiers.Any(t => t.ValueText == "static"),
        MethodDeclarationSyntax me => me.Modifiers.Any(t => t.ValueText == "static"),
        _ => false,
    };

    /// <summary>
    /// True when a marker type name appears in a <b>type position</b> under the node — a field/property/
    /// parameter/return/local/base type, a generic type argument, or the type of a <c>new</c>/<c>typeof</c>/
    /// cast/<c>is</c>/<c>as</c>/<c>foreach</c>. It deliberately does NOT match a bare identifier in an
    /// expression position, so a property or variable merely <i>named</i> <c>By</c> or <c>Component</c>
    /// no longer collides with Selenium's <c>By</c> marker and flips a class's kind. Syntax-only (no
    /// semantic model, to hold up on unrestored projects); the bias is conservative — a static-member
    /// access like <c>By.Id(…)</c> is not counted, which risks a miss, never a misclassification.
    /// </summary>
    private static bool ReferencesAny(SyntaxNode node, HashSet<string> markers)
    {
        foreach (var type in TypePositionTypes(node))
            foreach (var name in TypeIdentifiers(type))
                if (markers.Contains(name)) return true;
        return false;
    }

    /// <summary>Every <see cref="TypeSyntax"/> that sits in a genuine type slot under the node.</summary>
    private static IEnumerable<TypeSyntax> TypePositionTypes(SyntaxNode node)
    {
        foreach (var n in node.DescendantNodesAndSelf())
            switch (n)
            {
                case VariableDeclarationSyntax v: yield return v.Type; break;      // fields + locals
                case PropertyDeclarationSyntax p: yield return p.Type; break;
                case ParameterSyntax { Type: { } pt }: yield return pt; break;
                case MethodDeclarationSyntax m: yield return m.ReturnType; break;
                case BaseTypeSyntax b: yield return b.Type; break;                 // base list
                case ObjectCreationExpressionSyntax oc: yield return oc.Type; break; // new T()
                case TypeOfExpressionSyntax to: yield return to.Type; break;       // typeof(T)
                case CastExpressionSyntax c: yield return c.Type; break;           // (T)x
                case ForEachStatementSyntax fe: yield return fe.Type; break;
                case BinaryExpressionSyntax be
                    when (be.IsKind(SyntaxKind.IsExpression) || be.IsKind(SyntaxKind.AsExpression))
                         && be.Right is TypeSyntax rt:
                    yield return rt; break;
            }
    }

    private static string? FirstStringArgument(AttributeSyntax attr)
    {
        var arg = attr.ArgumentList?.Arguments.FirstOrDefault();
        if (arg?.Expression is LiteralExpressionSyntax lit)
            return lit.Token.ValueText; // handles both "..." and @"..."
        return null;
    }
}
