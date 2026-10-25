using Lattice.Api;

namespace Lattice.Core.Routes;

/// <summary>The rule id and codes the route planner gives its reasons under.</summary>
internal static class RouteCodes
{
    /// <summary>Who gives the routes module's reasons.</summary>
    public static RuleId Routes => RuleId.Known("lattice.routes");
}
