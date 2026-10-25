using Lattice.Api;

namespace Lattice.Core.Validation;

/// <summary>The rule ids and names of validation.</summary>
internal static class ValidationCodes
{
    /// <summary>The rule validation's own reasons come from (the game's check on the built world, a fault).</summary>
    public static RuleId Validation => RuleId.Known("lattice.validation");

    /// <summary>The module name, as a module-missing reason names it.</summary>
    public const string Module = "validation";
}
