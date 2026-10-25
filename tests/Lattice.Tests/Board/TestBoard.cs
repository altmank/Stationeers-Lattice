using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Grid;
using Lattice.Core.Ports;
using Lattice.Core.Slices;
using Lattice.TestKit;
using Lattice.TestKit.Catalogue;

namespace Lattice.Tests.Board;

/// <summary>A board over the hand catalogue (plus any extra pieces), with its events, policy and size limit in reach.</summary>
internal sealed class TestBoard
{
    public static readonly Actor Alice = new Actor.Player(new ClientId(1));
    public static readonly Actor Bob = new Actor.Player(new ClientId(2));
    public static readonly Maker TestMaker = TestKitIds.Must(Maker.Create(TestKitIds.Mod, "board-test"));

    private int _commands;

    public TestBoard(Option<BoardSeed> seed = default, params NamedPiece[] extraPieces)
    {
        var hand = FakeCatalogue.Hand();
        var pieces = new List<NamedPiece>(hand.Pieces);
        pieces.AddRange(extraPieces);
        Catalogue = new FakeCatalogue(pieces, hand.BrokenToolNames);
        Board = new GhostBoard(new Shapes(Catalogue, Grid), Time, Policies, Limit, Events, seed);
    }

    public FakeCatalogue Catalogue { get; }

    public CubeGrid Grid { get; } = new();

    public FixedTime Time { get; } = new();

    public AccessPolicies Policies { get; } = new();

    public PlanSizeLimit Limit { get; } = new();

    public BoardEvents Events { get; } = new();

    public GhostBoard Board { get; }

    public IGhostBoard View => Board.ViewFor(Alice);

    public static Turn TurnSending(Direction from, Direction to, CubeGrid grid)
    {
        for (var i = 0; i < Turn.Count; i++)
            if (Turn.Create(i).TryGet(out var turn) && grid.Apply(turn, from) == to)
                return turn;
        throw new InvalidOperationException("no such turn");
    }

    public GhostDraft Draft(string piece, Cell at, Turn turn = default, Option<PlanId> plan = default) =>
        Draft(new Intent.Build(new Placement(Catalogue.Named(piece).Key, at, turn), Option<Finish>.None), plan);

    public static GhostDraft Draft(Intent intent, Option<PlanId> plan = default, ValueSet<GroupName>? groups = null) => new(
        intent, plan, TestMaker, new Visibility.Everyone(), Option<Label>.None, Option<Tint>.None,
        groups ?? ValueSet<GroupName>.Empty, ValueMap<AttachmentKey, Attachment>.Empty);

    public Outcome Apply(Command command, Actor? actor = null) =>
        Board.Apply(new CommandId(new Guid(++_commands, 0, 0, new byte[8])), command, actor ?? Alice);

    public Outcome Place(params GhostDraft[] drafts) => Apply(new Command.Place(ValueList.From(drafts)));

    public ChangeSet Placed(params GhostDraft[] drafts) => Applied(Place(drafts));

    public static ChangeSet Applied(Outcome outcome) =>
        outcome is Outcome.Applied applied ? applied.Changes : throw new InvalidOperationException($"refused: {outcome}");

    public static Refusal Refused(Outcome outcome) =>
        outcome is Outcome.Refused refused ? Assert.Single(refused.Why) : throw new InvalidOperationException($"applied: {outcome}");

    public static Selector Ids(params GhostId[] ids) => new Selector.Ids(ValueList.From(ids));
}

/// <summary>A clock that stands still unless a test moves it.</summary>
internal sealed class FixedTime : ITimeSource
{
    public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
}
