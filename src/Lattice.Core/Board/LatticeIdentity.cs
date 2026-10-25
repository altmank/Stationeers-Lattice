using System;
using Lattice.Api;

namespace Lattice.Core.Board;

/// <summary>Lattice's own identity: the actor of the changes it makes itself, and the rule its board refusals carry.</summary>
internal static class LatticeIdentity
{
    /// <summary>Lattice's mod id, the same as its About.xml ModID, so logs, actors and mod dependencies name one id.</summary>
    public const string ModIdText = "net.xceled.stationeers.lattice";

    public static readonly ModId Mod = ModId.Create(ModIdText)
        .Match(static id => id, static reason => throw new InvalidOperationException(reason.ToString()));

    /// <summary>The actor of changes Lattice makes from what it sees in the world (started, stale, done, target gone).</summary>
    public static readonly Actor Actor = new Actor.Mod(Mod);

    /// <summary>The rule id of the board's own refusals.</summary>
    public static readonly RuleId Board = RuleId.Known("lattice.board");
}
