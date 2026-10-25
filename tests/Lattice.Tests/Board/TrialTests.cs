using System;
using Lattice.Api;
using Lattice.Core.Board;
using static Lattice.Tests.Board.TestBoard;

namespace Lattice.Tests.Board;

public sealed class TrialTests
{
    private static readonly Cell At = new(100, 100, 100);
    private static readonly Cell Next = new(100, 100, 105);
    private static readonly ReasonCode NotYours = Lattice.TestKit.TestKitIds.Must(ReasonCode.Create(Lattice.TestKit.TestKitIds.Mod, "not-yours"));

    [Fact]
    public void A_trial_gives_the_change_set_an_apply_would_make_and_changes_nothing()
    {
        var t = new TestBoard();
        t.Placed(t.Draft("StructureCableStraight", At));
        t.Events.Flush();
        var revision = t.Board.Revision;
        var heard = 0;
        t.Events.Changed += _ => heard++;
        var place = new Command.Place(ValueList.From(new[] { t.Draft("StructureCableStraight", Next) }));

        var trial = t.Board.Try(place, Alice);

        var would = Assert.Single(Ok(trial).Changes.Added);
        Assert.Equal(revision, t.Board.Revision);
        Assert.Empty(t.View.At(Next));
        t.Events.Flush();
        Assert.Equal(0, heard);

        var applied = Assert.Single(Applied(t.Apply(place)).Added);
        Assert.Equal(applied.Id, would.Id);
        Assert.Equal(applied.Intent, would.Intent);
        Assert.Equal(applied.Revision, would.Revision);
    }

    [Fact]
    public void While_a_trial_is_staged_reads_see_the_change_and_afterwards_they_do_not()
    {
        var t = new TestBoard();
        var trial = Ok(t.Board.Try(new Command.Place(ValueList.From(new[] { t.Draft("StructureCableStraight", At) })), Alice));
        var epoch = t.Board.Epoch;

        var seen = trial.While(() => t.View.At(At).Count);

        Assert.Equal(1, seen);
        Assert.Empty(t.View.At(At));
        Assert.True(t.Board.Epoch > epoch);
        var placed = Assert.Single(t.Placed(t.Draft("StructureCableStraight", At)).Added);
        Assert.Equal(trial.Changes.Added[0].Id, placed.Id);
    }

    [Fact]
    public void A_trial_is_unstaged_when_the_read_throws()
    {
        var t = new TestBoard();
        var trial = Ok(t.Board.Try(new Command.Place(ValueList.From(new[] { t.Draft("StructureCableStraight", At) })), Alice));

        Assert.Throws<InvalidOperationException>(() => trial.While<int>(() => throw new InvalidOperationException("reader")));

        Assert.Empty(t.View.At(At));
    }

    [Fact]
    public void A_trial_gets_the_policys_refusal()
    {
        var t = new TestBoard();
        Assert.True(t.Policies.Install(new AccessPolicy(
            (_, _) => true, (_, _) => true, _ => Result<Unit, Reason>.Error(Reason.Of(NotYours, RuleId.Contract)))).IsOk);

        var trial = t.Board.Try(new Command.Place(ValueList.From(new[] { t.Draft("StructureCableStraight", At) })), Bob);

        var refusals = trial.Match(_ => ValueList<Refusal>.Empty, r => r);
        Assert.Equal(NotYours, Assert.Single(refusals).Why.Code);
    }

    [Fact]
    public void A_trial_of_an_unknown_piece_is_refused_like_the_apply()
    {
        var t = new TestBoard();
        var draft = TestBoard.Draft(new Intent.Build(new Placement(new PrefabKey(123456), At, default), Option<Finish>.None));

        var refusals = t.Board.Try(new Command.Place(ValueList.From(new[] { draft })), Alice).Match(_ => ValueList<Refusal>.Empty, r => r);

        Assert.Equal(Refused(t.Apply(new Command.Place(ValueList.From(new[] { draft })))).Why.Code, Assert.Single(refusals).Why.Code);
    }

    [Fact]
    public void A_trial_staged_after_the_board_moved_is_a_bug()
    {
        var t = new TestBoard();
        var trial = Ok(t.Board.Try(new Command.Place(ValueList.From(new[] { t.Draft("StructureCableStraight", At) })), Alice));
        t.Placed(t.Draft("StructureCableStraight", Next));

        Assert.Throws<InvalidOperationException>(() => trial.While(() => 0));
    }

    private static GhostBoard.Trial Ok(Result<GhostBoard.Trial, ValueList<Refusal>> trial) =>
        trial.Match(x => x, r => throw new Xunit.Sdk.XunitException("refused: " + r[0].Why.Code));
}
