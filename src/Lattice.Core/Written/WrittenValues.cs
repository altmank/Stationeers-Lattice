using System;
using System.Globalization;
using Lattice.Api;
using Lattice.Core.Board;

namespace Lattice.Core.Written;

/// <summary>
/// How single values are written inside a field: invariant numbers, UTC times to the tick with trailing zeros cut,
/// command ids in URL-safe base64, actors as <c>player:&lt;client&gt;</c>, <c>agent:&lt;id&gt;</c> or
/// <c>mod:&lt;id&gt;</c>, placements as <c>&lt;piece&gt;@x,y,z/turn</c> (no turn: <c>/0</c> left out) and structures as
/// <c>&lt;id&gt;:&lt;piece&gt;@x,y,z</c>. Readers return the problem as text.
/// </summary>
internal static class WrittenValues
{
    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'";
    private const string WholeSecondFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    public static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    public static Result<long, string> Long(string text) =>
        IsCanonicalInteger(text) && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
            ? Result<long, string>.Ok(value)
            : Result<long, string>.Error($"bad number '{text}'");

    public static Result<int, string> Int(string text) =>
        IsCanonicalInteger(text) && int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
            ? Result<int, string>.Ok(value)
            : Result<int, string>.Error($"bad number '{text}'");

    /// <summary>An integer as this form writes it: optional minus, no leading zeros, no plus.</summary>
    public static bool IsCanonicalInteger(string text)
    {
        var start = text.Length > 0 && text[0] == '-' ? 1 : 0;
        if (text.Length == start || text.Length - start > 19)
            return false;
        if (text[start] == '0')
            return text.Length == start + 1 && start == 0;
        for (var i = start; i < text.Length; i++)
            if (text[i] is < '0' or > '9')
                return false;
        return true;
    }

    public static string Double(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    public static Result<double, string> Double(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? Result<double, string>.Ok(value)
            : Result<double, string>.Error($"bad number '{text}'");

    public static string Time(DateTimeOffset at) =>
        at.UtcDateTime.ToString(at.UtcTicks % TimeSpan.TicksPerSecond == 0 ? WholeSecondFormat : TimeFormat, CultureInfo.InvariantCulture);

    public static Result<DateTimeOffset, string> Time(string text) =>
        DateTimeOffset.TryParseExact(
            text,
            new[] { WholeSecondFormat, TimeFormat },
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var at)
            ? Result<DateTimeOffset, string>.Ok(at)
            : Result<DateTimeOffset, string>.Error($"bad time '{text}'");

    public static string Command(CommandId id) =>
        Convert.ToBase64String(id.Value.ToByteArray()).Substring(0, 22).Replace('+', '-').Replace('/', '_');

    public static Result<CommandId, string> Command(string text)
    {
        if (text.Length != 22)
            return Result<CommandId, string>.Error($"bad command id '{text}'");
        try
        {
            var bytes = Convert.FromBase64String(text.Replace('-', '+').Replace('_', '/') + "==");
            return Result<CommandId, string>.Ok(new CommandId(new Guid(bytes)));
        }
        catch (FormatException)
        {
            return Result<CommandId, string>.Error($"bad command id '{text}'");
        }
    }

    public static string Actor(Actor actor) => ActorText.Of(actor);

    public static Result<Actor, string> Actor(string text)
    {
        var colon = text.IndexOf(':');
        if (colon <= 0)
            return Result<Actor, string>.Error($"bad actor '{text}'");
        var rest = text.Substring(colon + 1);
        return text.Substring(0, colon) switch
        {
            "player" => ulong.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out var client)
                ? Result<Actor, string>.Ok(new Actor.Player(new ClientId(client)))
                : Result<Actor, string>.Error($"bad actor '{text}'"),
            "agent" => AgentId.Create(rest).Match(static id => Result<Actor, string>.Ok(new Actor.Agent(id)), _ => Result<Actor, string>.Error($"bad actor '{text}'")),
            "mod" => ModId.Create(rest).Match(static id => Result<Actor, string>.Ok(new Actor.Mod(id)), _ => Result<Actor, string>.Error($"bad actor '{text}'")),
            _ => Result<Actor, string>.Error($"unknown actor kind '{text}'"),
        };
    }

    public static string Visibility(Visibility visibility) => visibility switch
    {
        Api.Visibility.Everyone => "everyone",
        Api.Visibility.OwnerOnly => "owner",
        Api.Visibility.Team team => "team:" + team.Name.Value,
        _ => "unknown:" + visibility.GetType().Name,
    };

    public static Result<Visibility, string> Visibility(string text) => text switch
    {
        "everyone" => Result<Visibility, string>.Ok(new Visibility.Everyone()),
        "owner" => Result<Visibility, string>.Ok(new Visibility.OwnerOnly()),
        _ when text.StartsWith("team:", StringComparison.Ordinal) =>
            TeamName.Create(text.Substring(5)).Match(static n => Result<Visibility, string>.Ok(new Visibility.Team(n)), _ => Result<Visibility, string>.Error($"bad team '{text}'")),
        _ => Result<Visibility, string>.Error($"unknown visibility '{text}'"),
    };

    public static string Cell(Cell cell) =>
        Number(cell.X) + "," + Number(cell.Y) + "," + Number(cell.Z);

    public static Result<Cell, string> Cell(string text)
    {
        var parts = text.Split(',');
        if (parts.Length != 3)
            return Result<Cell, string>.Error($"bad cell '{text}'");
        return Int(parts[0]).Bind(x => Int(parts[1]).Bind(y => Int(parts[2]).Map(z => new Cell(x, y, z))))
            .MapError(_ => $"bad cell '{text}'");
    }

    public static string Placement(Placement placement) =>
        Number(placement.Piece.Hash) + "@" + Cell(placement.At) + (placement.Turn.Index == 0 ? "" : "/" + Number(placement.Turn.Index));

    public static Result<Placement, string> Placement(string text)
    {
        var at = text.IndexOf('@');
        if (at <= 0)
            return Result<Placement, string>.Error($"bad placement '{text}'");
        var slash = text.IndexOf('/', at);
        var cellText = slash < 0 ? text.Substring(at + 1) : text.Substring(at + 1, slash - at - 1);
        var turn = slash < 0
            ? Result<Turn, string>.Ok(Turn.None)
            : Int(text.Substring(slash + 1)).Bind(static i => Turn.Create(i).MapError(static _ => "bad turn"));
        return Int(text.Substring(0, at)).Bind(piece => Cell(cellText).Bind(cell => turn.Map(t => new Placement(new PrefabKey(piece), cell, t))))
            .MapError(_ => $"bad placement '{text}'");
    }

    public static string Structure(StructureRef structure) =>
        Number(structure.Id.Value) + ":" + Number(structure.Piece.Hash) + "@" + Cell(structure.At);

    public static Result<StructureRef, string> Structure(string text)
    {
        var colon = text.IndexOf(':');
        var at = text.IndexOf('@');
        if (colon <= 0 || at <= colon + 1)
            return Result<StructureRef, string>.Error($"bad structure '{text}'");
        return Long(text.Substring(0, colon))
            .Bind(id => Int(text.Substring(colon + 1, at - colon - 1))
                .Bind(piece => Cell(text.Substring(at + 1)).Map(cell => new StructureRef(new ReferenceId(id), new PrefabKey(piece), cell))))
            .MapError(_ => $"bad structure '{text}'");
    }

    public static string Tint(Tint tint) => tint.R.ToString("x2") + tint.G.ToString("x2") + tint.B.ToString("x2") + tint.A.ToString("x2");

    public static Result<Tint, string> Tint(string text)
    {
        if (text.Length != 8 || !uint.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value) || text != text.ToLowerInvariant())
            return Result<Tint, string>.Error($"bad tint '{text}'");
        return Result<Tint, string>.Ok(new Tint((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value));
    }

    /// <summary>Wraps a contract value's refusal as a problem.</summary>
    public static Result<T, string> Checked<T>(Result<T, Reason> created, string what, string text)
        where T : notnull =>
        created.MapError(_ => $"bad {what} '{text}'");
}
