using Lattice.Api;

namespace Lattice.Core.Support;

/// <summary>The rule ids of support and order.</summary>
internal static class SupportCodes
{
    /// <summary>The rule build-order reasons come from (cycles, removals that leave a ghost unsupported).</summary>
    public static RuleId Order => RuleId.Known("lattice.order");
}
