using Lattice.Api;

namespace Lattice.Core.Steps;

/// <summary>The rule id and codes the steps module gives its reasons under.</summary>
internal static class StepCodes
{
    /// <summary>Who gives the steps module's reasons.</summary>
    public static RuleId Steps => RuleId.Known("lattice.steps");
}
