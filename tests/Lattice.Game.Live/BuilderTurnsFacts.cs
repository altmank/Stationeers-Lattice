using System;
using System.Collections.Generic;
using System.Globalization;

namespace Lattice.Game.Live;

/// <summary>A rotation key the game's placement reads while a piece is held as a cursor.</summary>
internal enum TurnMove
{
    Left,
    Right,
    Up,
    Down,
    RollLeft,
    RollRight,
    Smart,
}

/// <summary>The pure parts of the builder-turns run: the closure of the turns the builder reaches and its log lines.</summary>
internal static class BuilderTurnsFacts
{
    /// <summary>
    /// Every turn reachable from <paramref name="start"/> by pressing <paramref name="moves"/>, in the order first reached.
    /// <paramref name="press"/> answers the turn a move gives from a turn, or -1 when the game gave a turn that is none of the 24.
    /// </summary>
    public static List<int> Reach(int start, IReadOnlyList<TurnMove> moves, Func<int, TurnMove, int> press)
    {
        var seen = new List<int> { start };
        for (var i = 0; i < seen.Count; i++)
            foreach (var move in moves)
            {
                var next = press(seen[i], move);
                if (next >= 0 && !seen.Contains(next))
                    seen.Add(next);
            }

        return seen;
    }

    /// <summary>The log line of one placement attempt: <c>builder-turns: &lt;prefab&gt; turn &lt;i&gt; placed|refused &lt;msg&gt;</c>.</summary>
    public static string PlacementLine(string prefab, int turn, bool placed, string message) =>
        $"builder-turns: {prefab} turn {turn.ToString(CultureInfo.InvariantCulture)} {(placed ? "placed" : "refused")} {message}".TrimEnd();
}
