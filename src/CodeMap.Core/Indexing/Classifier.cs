using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TestAtlas.Core.Model;

namespace TestAtlas.Core.Indexing;

/// <summary>
/// Classifies types and methods into the spec's <see cref="Kinds"/> vocabulary.
///
/// Slice 1 is a deliberate stub: every type and method is <see cref="Kinds.Other"/>. The
/// heuristics (page object / api client / step class / …) land in a later slice. What matters
/// *now* is the enforced spec constraint (G2): classification never throws — any exception, any
/// unrecognised shape, degrades to <see cref="Kinds.Other"/>. Establishing that seam + its tests
/// here is far cheaper than retrofitting a try/catch discipline once real heuristics exist.
/// </summary>
public static class Classifier
{
    /// <summary>Classify a type declaration. Always returns a valid kind; never throws.</summary>
    public static string ClassifyClass(TypeDeclarationSyntax? type, SemanticModel? model)
    {
        try
        {
            // Slice 1: no heuristics yet. Everything is "other".
            _ = type;
            _ = model;
            return Kinds.Other;
        }
        catch
        {
            // Constraint G2: a classifier fault must never break indexing.
            return Kinds.Other;
        }
    }

    /// <summary>Classify a method declaration. Always returns a valid kind; never throws.</summary>
    public static string ClassifyMethod(MethodDeclarationSyntax? method, SemanticModel? model)
    {
        try
        {
            _ = method;
            _ = model;
            return Kinds.Other;
        }
        catch
        {
            return Kinds.Other;
        }
    }

    /// <summary>
    /// Summarise a project's kind from its classified classes. Slice 1 has no class kinds to
    /// summarise, so this is <see cref="Kinds.Other"/> for now; still routed through the same
    /// never-throw contract.
    /// </summary>
    public static string SummariseProject(IEnumerable<ClassEntity> classes)
    {
        try
        {
            _ = classes;
            return Kinds.Other;
        }
        catch
        {
            return Kinds.Other;
        }
    }
}
