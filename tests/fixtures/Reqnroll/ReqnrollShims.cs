// Local shims for the Reqnroll binding attributes, in the real Reqnroll namespace.
using System;

namespace Reqnroll
{
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class BindingAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public sealed class GivenAttribute : Attribute { public GivenAttribute(string cucumberExpression) { Expression = cucumberExpression; } public string Expression { get; } }

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public sealed class WhenAttribute : Attribute { public WhenAttribute(string cucumberExpression) { Expression = cucumberExpression; } public string Expression { get; } }

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public sealed class ThenAttribute : Attribute { public ThenAttribute(string cucumberExpression) { Expression = cucumberExpression; } public string Expression { get; } }

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public sealed class StepDefinitionAttribute : Attribute { public StepDefinitionAttribute(string cucumberExpression) { Expression = cucumberExpression; } public string Expression { get; } }
}
