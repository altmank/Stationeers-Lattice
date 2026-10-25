using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Ports;
using Lattice.Core.Slices;
using Lattice.Core.Styles;
using Lattice.Core.Validation;
using Lattice.TestKit.Catalogue;
using Lattice.Tests.Board;

namespace Lattice.Tests.Styles;

/// <summary>Families and bounds by piece name, with every other piece <c>other</c> and boundless.</summary>
internal sealed class FakePieceLooks(FakeCatalogue catalogue) : IPieceLooks
{
    private readonly Dictionary<PrefabKey, PieceFamily> _families = new();
    private readonly Dictionary<PrefabKey, LocalBox> _bounds = new();

    public FakePieceLooks Family(string piece, PieceFamily family)
    {
        _families[catalogue.Named(piece).Key] = family;
        return this;
    }

    public FakePieceLooks Bounds(string piece, LocalBox box)
    {
        _bounds[catalogue.Named(piece).Key] = box;
        return this;
    }

    public PieceFamily Family(PrefabKey piece)
    {
        return _families.TryGetValue(piece, out var family) ? family : PieceFamilies.Other;
    }

    public Option<LocalBox> Bounds(PrefabKey piece) => _bounds.TryGetValue(piece, out var box) ? Option.Some(box) : Option<LocalBox>.None;
}

/// <summary>Verdict changes a test raises by hand.</summary>
internal sealed class FakeVerdicts : IVerdictFeed
{
    private readonly Subscribers<Action<ValueList<VerdictChange>>> _changed = new();

    public event Action<ValueList<VerdictChange>> VerdictsChanged { add => _changed.Add(value); remove => _changed.Remove(value); }

    public void Raise(params (GhostId Ghost, Finding? After)[] changes)
    {
        var list = new List<VerdictChange>(changes.Length);
        foreach (var (ghost, after) in changes)
            list.Add(new VerdictChange(ghost, Option<Finding>.None, after is null ? Option<Finding>.None : Option.Some(after)));
        var value = ValueList.From(list);
        foreach (var handler in _changed.Current)
            handler(value);
    }
}

/// <summary>Executor reports a test sets by hand.</summary>
internal sealed class FakeStatuses : IStatusReader
{
    private readonly Dictionary<GhostId, ValueList<ExecutorReport>> _reports = new();

    public void Set(GhostId ghost, params ExecutorReport[] reports) => _reports[ghost] = ValueList.From(reports);

    public ValueList<ExecutorReport> StatusOf(GhostId ghost) =>
        _reports.TryGetValue(ghost, out var reports) ? reports : ValueList<ExecutorReport>.Empty;
}

/// <summary>A clock that moves a fixed step each time it is read, so the scheduler sees work take time.</summary>
internal sealed class SteppingClock : IClock
{
    private TimeSpan _now;

    public TimeSpan Step { get; set; }

    public TimeSpan Now
    {
        get
        {
            _now += Step;
            return _now;
        }
    }

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>
/// The styles over a test board (the hand catalogue plus a modded piece), with a scheduler, a clock, fake piece looks,
/// verdicts and statuses. <see cref="Frame"/> runs a world frame as the world does: slices, then the board's events,
/// then the styles' frame.
/// </summary>
internal sealed class StyleBench
{
    public const string Modded = "ModConduitRun";

    public static readonly ModId Mod = Lattice.TestKit.TestKitIds.Must(ModId.Create("planner"));

    public StyleBench(bool drawing = true, params NamedPiece[] extra)
        : this(drawing, null, extra)
    {
    }

    /// <summary>Styles with Lattice's starter library as the Library band (as a world's styles have), or without it.</summary>
    public StyleBench(bool drawing, Lattice.Core.Styles.Library.StarterLibrary? library, params NamedPiece[] extra)
        : this(drawing, library, null, extra)
    {
    }

    /// <summary>Styles with or without the starter library, and with the game's paint colours given (none: unknown).</summary>
    public StyleBench(bool drawing, Lattice.Core.Styles.Library.StarterLibrary? library, IPaintColours? paints, params NamedPiece[] extra)
    {
        var pieces = new List<NamedPiece>(extra) { ModdedPiece() };
        T = new TestBoard(default, pieces.ToArray());
        Looks = new FakePieceLooks(T.Catalogue);
        Scheduler = new FrameScheduler(Clock, FrameBudgets.Default);
        Styles = new GhostStyles(new StyleParts(T.Board, TestBoard.Alice, new Shapes(T.Catalogue, T.Grid), T.Grid, Scheduler, Clock, Looks)
        {
            Verdicts = Verdicts,
            Statuses = Statuses,
            Drawing = drawing,
            Library = library,
            Paints = paints ?? new NoPaintColours(),
        });
    }

    public TestBoard T { get; }

    public SteppingClock Clock { get; } = new();

    public FrameScheduler Scheduler { get; }

    public FakePieceLooks Looks { get; }

    public FakeVerdicts Verdicts { get; } = new();

    public FakeStatuses Statuses { get; } = new();

    public GhostStyles Styles { get; }

    public static T Must<T>(Result<T, Reason> result) where T : notnull =>
        result.Match(static value => value, static why => throw new Xunit.Sdk.XunitException($"refused: {why}"));

    public static StyleLayerId Layer(string name) => Must(StyleLayerId.Create(Mod, name));

    public static StyleSetId SetId(string name) => Must(StyleSetId.Create(Mod, name));

    public static PieceFamily FamilyOf(string name) => Must(PieceFamily.Create(Mod, name));

    public static Style Edge(LineStyle line, PaletteKey colour) => new()
    {
        Edge = Option.Some<EdgeLook>(new EdgeLook.Line(line, Must(Pixels.Create(2f)), new Paint.Token(colour))),
    };

    public static StyleLayerDescriptor LayerOf(string name, StyleBand band, int order, params StyleRule[] rules) =>
        new(Layer(name), band, order, ValueList.From(rules));

    public static StyleRule Rule(StyleMatch match, Style style) => new(match, style);

    public IStyleLayer Add(StyleLayerDescriptor layer) => Must(Styles.Add(layer));

    /// <summary>Places ghosts as the viewer and settles the styles.</summary>
    public ChangeSet Place(params GhostDraft[] drafts)
    {
        var placed = T.Placed(drafts);
        Settle();
        return placed;
    }

    public Ghost PlaceOne(string piece, Cell at, Turn turn = default) => Assert.Single(Place(T.Draft(piece, at, turn)).Added);

    public GhostDraft Draft(string piece, Cell at, Turn turn = default) => T.Draft(piece, at, turn);

    /// <summary>One world frame: slices, the board's events, then the styles' frame.</summary>
    public void Frame()
    {
        Scheduler.RunFrame();
        T.Events.Flush();
        Styles.Frame();
    }

    /// <summary>Frames until the styles have nothing left to work out.</summary>
    public int Settle(int most = 10_000)
    {
        for (var i = 0; i < most; i++)
        {
            Frame();
            if (!Styles.Busy && T.Events.Waiting == 0)
                return i + 1;
        }

        throw new Xunit.Sdk.XunitException("the styles never settled");
    }

    public StyleStory Story(GhostId ghost) => Must(Styles.Explain(ghost));

    public ResolvedLook Look(GhostId ghost) =>
        Styles.LookOf(ghost).Match(static look => look, static () => throw new Xunit.Sdk.XunitException("not drawn"));

    private static NamedPiece ModdedPiece()
    {
        var hand = FakeCatalogue.Hand();
        var straight = hand.Named("StructureCableStraight");
        return new NamedPiece(Modded, ValueList.Of("ModKitConduit"), straight with { Key = PrefabHash.Piece(Modded), Kits = ValueList<KitPlace>.Empty },
            ValueMap<ItemKey, string>.Empty);
    }
}
