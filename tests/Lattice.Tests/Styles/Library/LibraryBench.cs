using System;
using System.Collections.Generic;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Styles.Library;

namespace Lattice.Tests.Styles.Library;

/// <summary>
/// A drawn thing as the facets a style rule can read: what it asks for, which part, its state, finding and status, and
/// whether it is the viewer's, a draft, pending, a structure overlay, and which sets hold it.
/// </summary>
internal sealed record Thing(IntentKind Intent, PartKind Part, StateKind State, FindingKind Finding, StatusKind Status)
{
    /// <summary>The viewer's untinted Build ghost, planned, not checked yet.</summary>
    public static Thing Planned => new(IntentKind.Build, PartKind.Placement, StateKind.Planned, FindingKind.Unchecked, StatusKind.None);

    public bool Mine { get; init; } = true;

    public bool Draft { get; init; }

    public bool Pending { get; init; }

    public bool Structure { get; init; }

    /// <summary>A build or swap that carries a finish (a finish-only ghost always does).</summary>
    public bool Finish { get; init; }

    public PieceFamily Family { get; init; } = PieceFamilies.Pipe;

    public ValueList<StyleSetId> Sets { get; init; } = ValueList<StyleSetId>.Empty;

    public Thing In(StyleSetId set) => this with { Sets = ValueList.From(Sets.Append(set)) };

    /// <summary>The atomic matches that hold for this thing.</summary>
    public IReadOnlyCollection<StyleMatch> Facets
    {
        get
        {
            var facets = new HashSet<StyleMatch>
            {
                new StyleMatch.Everything(),
                new StyleMatch.Intent(Intent),
                new StyleMatch.Part(Part),
                new StyleMatch.State(State),
                new StyleMatch.Finding(Finding, Option<ReasonCode>.None),
                new StyleMatch.Status(Status, Option<ExecutorId>.None),
                new StyleMatch.Family(Family),
            };
            if (Mine) facets.Add(new StyleMatch.Mine());
            if (Draft) facets.Add(new StyleMatch.Draft());
            if (Pending) facets.Add(new StyleMatch.Pending());
            if (Structure) facets.Add(new StyleMatch.Structure());
            if (Finish || Intent == IntentKind.FinishOnly) facets.Add(new StyleMatch.Finishes());
            foreach (var set in Sets)
                facets.Add(new StyleMatch.InSet(set));
            return facets;
        }
    }
}

/// <summary>
/// The starter library over a small cascade that stands in for style resolution: a rule holds when its match holds for
/// a thing's facets, and per property the last rule that sets it wins.
/// </summary>
internal static class LibraryBench
{
    public static StarterLibrary Starter { get; } = new();

    /// <summary>A thing in the state each row styles (one the later rows leave that row's opinions on).</summary>
    public static Thing ThingFor(LibraryRow row) => row switch
    {
        LibraryRow.Planned or LibraryRow.Unchecked or LibraryRow.Done => Thing.Planned,
        LibraryRow.Started => Thing.Planned with { State = StateKind.Started },
        LibraryRow.Remove => Thing.Planned with { Intent = IntentKind.Remove, Part = PartKind.Existing },
        LibraryRow.SwapOld => Thing.Planned with { Intent = IntentKind.Swap, Part = PartKind.Existing },
        LibraryRow.Swap => Thing.Planned with { Intent = IntentKind.Swap },
        LibraryRow.Finished => Thing.Planned with { Finish = true },
        LibraryRow.Finish => Thing.Planned with { Intent = IntentKind.FinishOnly, Part = PartKind.Existing },
        LibraryRow.Draft => Thing.Planned with { Draft = true },
        LibraryRow.Next => Thing.Planned with { Finding = FindingKind.Passes },
        LibraryRow.Waits => Thing.Planned with { Finding = FindingKind.WaitsFor },
        LibraryRow.WaitsHovered => (Thing.Planned with { Finding = FindingKind.WaitsFor }).In(LibrarySets.Hovered),
        LibraryRow.Fails => Thing.Planned with { Finding = FindingKind.WillFail },
        LibraryRow.FailsHovered => (Thing.Planned with { Finding = FindingKind.WillFail }).In(LibrarySets.Hovered),
        LibraryRow.Warns => Thing.Planned with { Finding = FindingKind.Warns },
        LibraryRow.Stale => Thing.Planned with { State = StateKind.Stale },
        LibraryRow.Pending => Thing.Planned with { Draft = true, Pending = true },
        LibraryRow.Working => Thing.Planned with { Status = StatusKind.Working },
        LibraryRow.Waiting => Thing.Planned with { Status = StatusKind.Waiting },
        LibraryRow.Paused => Thing.Planned with { Status = StatusKind.Paused },
        LibraryRow.Others => Thing.Planned with { Mine = false },
        LibraryRow.Selected => Thing.Planned.In(LibrarySets.Selected),
        LibraryRow.Hovered => Thing.Planned.In(LibrarySets.Hovered),
        _ => throw new ArgumentOutOfRangeException(nameof(row), row, null),
    };

    /// <summary>The states every pair of which must differ on a channel besides hue, each as a thing in it.</summary>
    public static IReadOnlyList<(string Name, Thing Thing)> States { get; } = new (string, Thing)[]
    {
        ("unchecked", Thing.Planned),
        ("next", ThingFor(LibraryRow.Next)),
        ("waits", ThingFor(LibraryRow.Waits)),
        ("fails", ThingFor(LibraryRow.Fails)),
        ("warns", ThingFor(LibraryRow.Warns)),
        ("started", ThingFor(LibraryRow.Started)),
        ("stale", ThingFor(LibraryRow.Stale)),
        ("remove", ThingFor(LibraryRow.Remove)),
        ("swap new", ThingFor(LibraryRow.Swap)),
        ("swap old", ThingFor(LibraryRow.SwapOld)),
        ("finished build", ThingFor(LibraryRow.Finished)),
        ("finish", ThingFor(LibraryRow.Finish)),
        ("draft", ThingFor(LibraryRow.Draft)),
        ("pending", ThingFor(LibraryRow.Pending)),
        ("working", ThingFor(LibraryRow.Working)),
        ("waiting", ThingFor(LibraryRow.Waiting)),
        ("paused", ThingFor(LibraryRow.Paused)),
        ("others", ThingFor(LibraryRow.Others)),
        ("selected", ThingFor(LibraryRow.Selected)),
        ("hovered", ThingFor(LibraryRow.Hovered)),
    };

    /// <summary>The thing's look over the library alone.</summary>
    public static Style Resolve(Thing thing) => Resolve(Starter.Rules, thing);

    /// <summary>The thing's look over these rules, in order.</summary>
    public static Style Resolve(IEnumerable<StyleRule> rules, Thing thing)
    {
        var facets = thing.Facets;
        var look = Style.Empty;
        foreach (var rule in rules)
            if (Holds(rule.Match, facets))
                look = Overlay(look, rule.Style);
        return look;
    }

    public static bool Holds(StyleMatch match, IReadOnlyCollection<StyleMatch> facets) => match switch
    {
        StyleMatch.Everything => true,
        StyleMatch.All all => all.Of.All(m => Holds(m, facets)),
        StyleMatch.Any any => any.Of.Any(m => Holds(m, facets)),
        StyleMatch.Not not => !Holds(not.Match, facets),
        _ => facets.Contains(match),
    };

    /// <summary>Per property, the upper style's opinion where it has one, else the lower's.</summary>
    public static Style Overlay(Style lower, Style upper) => new()
    {
        Presence = Pick(lower.Presence, upper.Presence),
        Fill = Pick(lower.Fill, upper.Fill),
        FillPaint = Pick(lower.FillPaint, upper.FillPaint),
        FillOpacity = Pick(lower.FillOpacity, upper.FillOpacity),
        Edge = Pick(lower.Edge, upper.Edge),
        Pattern = Pick(lower.Pattern, upper.Pattern),
        Glow = Pick(lower.Glow, upper.Glow),
        Effect = Pick(lower.Effect, upper.Effect),
        Enter = Pick(lower.Enter, upper.Enter),
        Ease = Pick(lower.Ease, upper.Ease),
        Appear = Pick(lower.Appear, upper.Appear),
        Vanish = Pick(lower.Vanish, upper.Vanish),
        SeeThrough = Pick(lower.SeeThrough, upper.SeeThrough),
        BeyondClip = Pick(lower.BeyondClip, upper.BeyondClip),
        DistanceFade = Pick(lower.DistanceFade, upper.DistanceFade),
        NearLimit = Pick(lower.NearLimit, upper.NearLimit),
        Dim = Pick(lower.Dim, upper.Dim),
        Label = Pick(lower.Label, upper.Label),
        Icon = Pick(lower.Icon, upper.Icon),
        OpenEnds = Pick(lower.OpenEnds, upper.OpenEnds),
        Ports = Pick(lower.Ports, upper.Ports),
        FlowArrows = Pick(lower.FlowArrows, upper.FlowArrows),
        WaitsLinks = Pick(lower.WaitsLinks, upper.WaitsLinks),
        Mark = Pick(lower.Mark, upper.Mark),
        Material = Pick(lower.Material, upper.Material),
        Pickable = Pick(lower.Pickable, upper.Pickable),
    };

    /// <summary>The opinions a style sets, by property name, as boxed values (for comparing a look against a resolved one).</summary>
    public static IReadOnlyDictionary<string, object> Opinions(Style style)
    {
        var opinions = new Dictionary<string, object>();
        foreach (var property in typeof(Style).GetProperties())
        {
            if (property.PropertyType == typeof(Style))
                continue;
            var option = property.GetValue(style)!;
            var isSome = (bool)property.PropertyType.GetProperty(nameof(Option<int>.IsSome))!.GetValue(option)!;
            if (isSome)
                opinions[property.Name] = option;
        }
        return opinions;
    }

    /// <summary>Every paint a style names.</summary>
    public static IEnumerable<Paint> Paints(Style style)
    {
        if (style.FillPaint.TryGet(out var fill))
            yield return fill;
        if (style.Edge.TryGet(out var edge) && edge is EdgeLook.Line line)
            yield return line.Paint;
        if (style.Glow.TryGet(out var glow) && glow is GlowLook.Lit lit)
            yield return lit.Paint;
    }

    /// <summary>Every leaf match inside a match.</summary>
    public static IEnumerable<StyleMatch> Leaves(StyleMatch match) => match switch
    {
        StyleMatch.All all => all.Of.SelectMany(Leaves),
        StyleMatch.Any any => any.Of.SelectMany(Leaves),
        StyleMatch.Not not => Leaves(not.Match),
        _ => new[] { match },
    };

    /// <summary>A paint's colour for an untinted thing under a palette.</summary>
    public static Rgba ColourOf(Paint paint, Palette palette) => paint switch
    {
        Paint.Fixed f => f.Colour,
        Paint.Token t => palette.Colours[t.Key],
        Paint.GhostTint g => palette.Colours[g.Fallback],
        Paint.OwnerColour => palette.OwnerRamp[0],
        Paint.FinishColour f => palette.Colours[f.Fallback],
        _ => throw new ArgumentOutOfRangeException(nameof(paint)),
    };

    public static T Must<T>(Result<T, Reason> result) where T : notnull =>
        result.Match(static v => v, static e => throw new InvalidOperationException(e.ToString()));

    private static Option<T> Pick<T>(Option<T> lower, Option<T> upper) where T : notnull => upper.IsSome ? upper : lower;
}
