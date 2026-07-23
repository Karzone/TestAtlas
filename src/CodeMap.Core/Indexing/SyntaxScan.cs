using Microsoft.CodeAnalysis;
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
