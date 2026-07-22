// Local shims for the SpecFlow binding attributes, in the real TechTalk.SpecFlow namespace.
// This lets the fixture carry authentic-looking [Binding]/[Given]/… without a package reference.
using System;

namespace TechTalk.SpecFlow
{
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class BindingAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public sealed class GivenAttribute : Attribute { public GivenAttribute(string regex) { Regex = regex; } public string Regex { get; } }

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public sealed class WhenAttribute : Attribute { public WhenAttribute(string regex) { Regex = regex; } public string Regex { get; } }

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public sealed class ThenAttribute : Attribute { public ThenAttribute(string regex) { Regex = regex; } public string Regex { get; } }

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public sealed class StepDefinitionAttribute : Attribute { public StepDefinitionAttribute(string regex) { Regex = regex; } public string Regex { get; } }
}
