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
    private static readonly HashSet<string> UiTypes = new(StringComparer.Ordinal)
        { "IPage", "ILocator", "IWebDriver", "IWebElement", "By" };
    private static readonly HashSet<string> ApiTypes = new(StringComparer.Ordinal)
        { "RestClient", "IRestClient", "RestRequest", "IRestRequest", "HttpClient" };

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

    public static ClassFacts GatherClassFacts(TypeDeclarationSyntax type)
    {
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

        var uiMembers = instanceMembers.Count(m => ReferencesAny(m, UiTypes));
        var apiMethodRefs = methods.Count(m => ReferencesAny(m, ApiTypes));

        var referencesUi = ReferencesAny(type, UiTypes);
        var referencesApi = ReferencesAny(type, ApiTypes);

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
            ReferencesApiType: referencesApi);
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
    /// 2. <c>SendAsync(new HttpRequestMessage(HttpMethod.X, "/x"))</c>;
    /// 3. <c>new RestRequest("/x", Method.X)</c> (RestSharp);
    /// 4. Refit-style <c>[Get("/x")]</c> attributes on the method;
    /// 5. generic fallback for custom wrappers: any invocation whose name starts with a verb word
    ///    (<c>Get/Post/Put/Patch/Delete</c>) passing a strictly route-like literal (starts with '/',
    ///    or contains '://' or '/{'). Interpolated strings become templates (<c>$"/o/{id}"</c> →
    ///    <c>/o/{id}</c>). Anything unrecognised produces nothing — never an error.
    /// </summary>
    public static List<(string Verb, string Route)> EndpointCalls(MethodDeclarationSyntax method)
    {
        var found = new List<(string, string)>();

        // 4. Refit-style attributes on the method itself.
        foreach (var attr in Attributes(method.AttributeLists))
        {
            var name = AttrSimpleName(attr);
            if (!HttpAttrNames.TryGetValue(name, out var verb)) continue;
            var route = RouteFromExpression(attr.ArgumentList?.Arguments.FirstOrDefault()?.Expression);
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

                    var firstArg = inv.ArgumentList.Arguments.FirstOrDefault()?.Expression;
                    var route = RouteFromExpression(firstArg);

                    // 1. Known client method names — relaxed route check (relative "api/x" is common).
                    if (HttpMethodNames.TryGetValue(name, out var verb))
                    {
                        if (route is not null && IsRouteLike(route, strict: false)) found.Add((verb, route));
                        break;
                    }

                    // 2. Send/SendAsync(new HttpRequestMessage(…)) needs no special case here — the
                    //    ObjectCreation case below sees the nested HttpRequestMessage on its own
                    //    (handling it here too would double-count the same call).

                    // 5. Generic fallback for custom wrappers — strict route check to avoid noise.
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
                    var route = RouteFromExpression(oc.ArgumentList?.Arguments.FirstOrDefault()?.Expression);
                    if (route is null || !IsRouteLike(route, strict: false)) break;
                    var verb = "GET";
                    foreach (var arg in oc.ArgumentList!.Arguments.Skip(1))
                        if (arg.Expression is MemberAccessExpressionSyntax ma &&
                            ma.Expression is IdentifierNameSyntax { Identifier.ValueText: "Method" or "HttpMethod" })
                            verb = ma.Name.Identifier.ValueText.ToUpperInvariant();
                    found.Add((verb, route));
                    break;
                }
                case ObjectCreationExpressionSyntax oc when SimpleBaseName(oc.Type) is "HttpRequestMessage":
                {
                    if (TryHttpRequestMessage(oc, out var v, out var r)) found.Add((v, r));
                    break;
                }
            }
        }

        return found;
    }

    private static bool TryHttpRequestMessage(ObjectCreationExpressionSyntax oc, out string verb, out string route)
    {
        verb = "ANY"; route = string.Empty;
        if (SimpleBaseName(oc.Type) is not "HttpRequestMessage" || oc.ArgumentList is null) return false;
        foreach (var arg in oc.ArgumentList.Arguments)
        {
            if (arg.Expression is MemberAccessExpressionSyntax ma &&
                ma.Expression is IdentifierNameSyntax { Identifier.ValueText: "HttpMethod" })
                verb = ma.Name.Identifier.ValueText.ToUpperInvariant();
            else if (RouteFromExpression(arg.Expression) is { } r && IsRouteLike(r, strict: false))
                route = r;
        }
        return route.Length > 0;
    }

    /// <summary>A string literal verbatim, or an interpolated string as a route template (holes → <c>{expr}</c>).</summary>
    private static string? RouteFromExpression(ExpressionSyntax? expr) => expr switch
    {
        LiteralExpressionSyntax lit when lit.IsKind(SyntaxKind.StringLiteralExpression) => lit.Token.ValueText,
        InterpolatedStringExpressionSyntax interp => string.Concat(interp.Contents.Select(c => c switch
        {
            InterpolatedStringTextSyntax t => t.TextToken.ValueText,
            InterpolationSyntax h => "{" + h.Expression.ToString() + "}",
            _ => string.Empty,
        })),
        _ => null,
    };

    /// <summary>
    /// Does a string look like a URL path? Relaxed (known client methods): contains '/', no whitespace.
    /// Strict (generic wrapper fallback): additionally must start with '/' or contain "://" or "/{".
    /// </summary>
    private static bool IsRouteLike(string s, bool strict)
    {
        if (s.Length == 0 || s.Any(char.IsWhiteSpace) || !s.Contains('/')) return false;
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

    private static bool ReferencesAny(SyntaxNode node, HashSet<string> markers)
        => node.DescendantNodes().OfType<IdentifierNameSyntax>()
            .Any(id => markers.Contains(id.Identifier.ValueText));

    private static string? FirstStringArgument(AttributeSyntax attr)
    {
        var arg = attr.ArgumentList?.Arguments.FirstOrDefault();
        if (arg?.Expression is LiteralExpressionSyntax lit)
            return lit.Token.ValueText; // handles both "..." and @"..."
        return null;
    }
}
