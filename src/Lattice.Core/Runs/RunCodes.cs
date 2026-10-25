using Lattice.Api;

namespace Lattice.Core.Runs;

/// <summary>The rule id the piece chooser, split planner and run planner give their reasons under.</summary>
internal static class RunCodes
{
    /// <summary>Who gives the runs module's reasons.</summary>
    public static RuleId Runs => RuleId.Known("lattice.runs");
}
