using System;
using Lattice.Api;
using Lattice.Testing;

// One TestWorld per process: test classes never run in parallel.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Lattice.Testing.Tests;

/// <summary>What the consumer tests share: a modded conduit, drafts, and unwrapping results that must succeed.</summary>
internal static class Hosted
{
    public static readonly ModId Mod = Must(ModId.Create("hostmod"));

    public static readonly Maker Made = Must(Maker.Create(Mod, "tests"));

    /// <summary>
    /// A conduit no base game has: a small-grid straight along local y (the game's cables run along z), joining power
    /// and data at both ends, in the cable slot, clashing with cables.
    /// </summary>
    public static readonly TestPiece Conduit = Must(TestPiece.Create("ModConduitLine", "ItemModConduit", PieceGrid.Small))
        .WithEnd(new CellOffset(0, 0, 0), SignedAxis.YPlus, ValueSet.From(new[] { TestNetwork.Power, TestNetwork.Data }))
        .WithEnd(new CellOffset(0, 0, 0), SignedAxis.YMinus, ValueSet.From(new[] { TestNetwork.Power, TestNetwork.Data }))
        .WithCell(new CellOffset(0, 0, 0), TestSlot.Cable)
        .WithCollision(2);

    public static TestScenario WithConduit => TestScenario.Empty.WithPiece(Conduit);

    /// <summary>A small cell well inside the world; small cells sit every 5 decimetres.</summary>
    public static Cell At(int x, int y = 0, int z = 0) => new(100 + 5 * x, 100 + 5 * y, 100 + 5 * z);

    public static PrefabKey Hand(string name) => TestCatalogue.Hand.Key(name).Reduce(() => throw new InvalidOperationException(name));

    public static GhostDraft Draft(PrefabKey piece, Cell at, Turn turn = default) => new(
        new Intent.Build(new Placement(piece, at, turn), Option<Finish>.None),
        Option<PlanId>.None,
        Made,
        new Visibility.Everyone(),
        Option<Label>.None,
        Option<Tint>.None,
        ValueSet<GroupName>.Empty,
        ValueMap<AttachmentKey, Attachment>.Empty);

    public static TestWorld Start(TestScenario scenario) => Must(TestWorld.Start(scenario));

    public static T Must<T>(Result<T, Reason> result) where T : notnull =>
        result.Match(static value => value, static why => throw new Xunit.Sdk.XunitException($"refused: {Describe(why)}"));

    public static string Describe(Reason why)
    {
        var text = new System.Text.StringBuilder(why.Code.Value);
        foreach (var fact in why.Facts)
            text.Append(' ').Append(fact.Key).Append('=').Append(fact.Value);
        return text.ToString();
    }

    /// <summary>Asks until the answer is ready, settling the world between asks.</summary>
    public static T Ready<T>(TestWorld world, Func<Answer<T>> ask) where T : notnull
    {
        for (var i = 0; i < 16; i++)
        {
            if (ask() is Answer<T>.Ready ready)
                return ready.Value;
            Must(world.Settle());
        }

        throw new Xunit.Sdk.XunitException("the answer stayed pending; Lattice logged: " + string.Join(" | ", world.Log));
    }

    /// <summary>Submits a command and returns its outcome: at once, or by its ticket's answer after frames.</summary>
    public static Outcome Submit(TestWorld world, Command command, Actor? actor = null)
    {
        var answers = new System.Collections.Generic.Dictionary<Ticket, Outcome>();
        Action<Ticket, Outcome> heard = (ticket, outcome) => answers[ticket] = outcome;
        world.System.Events.Answered += heard;
        try
        {
            switch (world.System.Commands.Submit(CommandId.New(), command, actor ?? world.Viewer))
            {
                case Submission.Answered answered:
                    return answered.Outcome;
                case Submission.Sent sent:
                    for (var i = 0; i < 16 && !answers.ContainsKey(sent.Ticket); i++)
                        Must(world.Settle());
                    return answers.TryGetValue(sent.Ticket, out var outcome)
                        ? outcome
                        : throw new Xunit.Sdk.XunitException("the ticket was never answered");
                case var other:
                    throw new Xunit.Sdk.XunitException($"not answered: {other}");
            }
        }
        finally
        {
            world.System.Events.Answered -= heard;
        }
    }
}
