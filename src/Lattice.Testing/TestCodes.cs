using Lattice.Api;

namespace Lattice.Testing;

/// <summary>The reason codes the test host refuses with, beside the contract's own (<see cref="ReasonCodes"/>).</summary>
public static class TestCodes
{
    /// <summary>The rule the host's refusals come from.</summary>
    public static readonly RuleId Host = RuleId.Known("lattice.testing");

    /// <summary><see cref="TestWorld.Settle"/> ran its most frames and work was still pending.</summary>
    public static readonly ReasonCode NotSettled = ReasonCode.Known("not-settled");

    /// <summary><see cref="TestWorld.Start"/> while a world already runs in this process (one at a time).</summary>
    public static readonly ReasonCode WorldRunning = ReasonCode.Known("world-running");

    internal static Reason Refuse(ReasonCode code, string problem) =>
        Reason.Of(code, Host, ValueMap.Of("problem", problem));

    internal static Reason Refuse(ReasonCode code, ValueMap<string, string> facts) => Reason.Of(code, Host, facts);
}
