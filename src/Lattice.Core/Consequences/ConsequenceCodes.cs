using System.Collections.Generic;
using Lattice.Api;

namespace Lattice.Core.Consequences;

/// <summary>The rule the consequence predictions' reasons come from (their codes are in <see cref="ReasonCodes"/>).</summary>
internal static class ConsequenceCodes
{
    /// <summary>The rule every consequence reason comes from.</summary>
    public static RuleId Rule => RuleId.Known("lattice.consequences");

    /// <summary>A reason with the given facts, from <see cref="Rule"/>.</summary>
    public static Reason Of(ReasonCode code, ValueMap<string, string> facts) => Reason.Of(code, Rule, facts);

    /// <summary>A reason with no facts, from <see cref="Rule"/>.</summary>
    public static Reason Of(ReasonCode code) => Reason.Of(code, Rule);
}

/// <summary>Id lists in ascending order, and ids as fact text.</summary>
internal static class IdOrder
{
    /// <summary>Sorts the ids ascending.</summary>
    public static void SortIds(this List<ReferenceId> ids) => ids.Sort(static (a, b) => a.Value.CompareTo(b.Value));

    /// <summary>Sorts the networks ascending.</summary>
    public static void SortIds(this List<NetworkRef> ids) => ids.Sort(static (a, b) => a.Value.CompareTo(b.Value));

    /// <summary>The ids, ascending, in a new list.</summary>
    public static List<ReferenceId> Sorted(IEnumerable<ReferenceId> ids)
    {
        var list = new List<ReferenceId>(ids);
        list.SortIds();
        return list;
    }

    /// <summary>The ids as fact text: <c>"3, 4, 5"</c>.</summary>
    public static string Text(IEnumerable<ReferenceId> ids)
    {
        var values = new List<string>();
        foreach (var id in ids)
            values.Add(id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return string.Join(", ", values);
    }

    /// <summary>The networks as fact text: <c>"3, 4"</c>.</summary>
    public static string Text(IEnumerable<NetworkRef> ids)
    {
        var values = new List<string>();
        foreach (var id in ids)
            values.Add(id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return string.Join(", ", values);
    }
}
