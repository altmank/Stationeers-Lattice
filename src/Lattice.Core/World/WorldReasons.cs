using System.Text;
using Lattice.Api;

namespace Lattice.Core.World;

/// <summary>The world bridge's rule ids and the reasons it gives.</summary>
internal static class WorldReasons
{
    /// <summary>Reads of the game world.</summary>
    public static readonly RuleId World = RuleId.Known("lattice.world");

    /// <summary>The game's construction cursor check.</summary>
    public static readonly RuleId Placement = RuleId.Known("lattice.world.placement");

    /// <summary>The game refused the placement: <see cref="ReasonCodes.GameRefused"/> with its message, tags removed.</summary>
    public static Reason Refused(Placement placement, string gameMessage) =>
        new(ReasonCodes.GameRefused, Facts(placement), Option.Some(GameText.Plain(gameMessage)), Placement);

    /// <summary>The game has no construction cursor for the piece, so it cannot be asked.</summary>
    public static Reason NoCursor(Placement placement) =>
        Reason.Of(ReasonCodes.UnknownPiece, Placement, Facts(placement).With("problem", "no-cursor"));

    private static ValueMap<string, string> Facts(Placement placement) =>
        ValueMap<string, string>.Empty
            .With("piece", placement.Piece.Hash.ToString())
            .With("cell", $"{placement.At.X},{placement.At.Y},{placement.At.Z}")
            .With("turn", placement.Turn.Index.ToString());
}

/// <summary>Game text as Lattice carries it.</summary>
internal static class GameText
{
    /// <summary>
    /// The text without Unity rich-text tags (<c>&lt;color=green&gt;Steel Frame&lt;/color&gt;</c> becomes
    /// <c>Steel Frame</c>); a <c>&lt;</c> that opens no tag is kept.
    /// </summary>
    public static string Plain(string text)
    {
        if (text.IndexOf('<') < 0)
            return text;
        var plain = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var close = text[i] == '<' ? text.IndexOf('>', i + 1) : -1;
            if (close > i + 1 && IsTag(text, i + 1, close))
            {
                i = close + 1;
                continue;
            }

            plain.Append(text[i]);
            i++;
        }

        return plain.ToString();
    }

    /// <summary>
    /// A game name as a Lattice token: words split at capitals, lowercase, joined by '-'
    /// (<c>LiquidCarbonDioxide</c> becomes <c>liquid-carbon-dioxide</c>).
    /// </summary>
    public static string Token(string gameName)
    {
        var token = new StringBuilder(gameName.Length + 4);
        for (var i = 0; i < gameName.Length; i++)
        {
            var c = gameName[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && (char.IsLower(gameName[i - 1]) || (i + 1 < gameName.Length && char.IsLower(gameName[i + 1]) && char.IsUpper(gameName[i - 1]))))
                    token.Append('-');
                token.Append(char.ToLowerInvariant(c));
            }
            else
            {
                token.Append(c);
            }
        }

        return token.ToString();
    }

    private static bool IsTag(string text, int start, int end)
    {
        if (text[start] == '/')
            start++;
        if (start >= end || !char.IsLetter(text[start]))
            return false;
        for (var i = start; i < end; i++)
            if (text[i] == '<')
                return false;
        return true;
    }
}
