using System.Linq;
using Lattice.Api;
using Lattice.Core.Ports;
using Lattice.Core.Validation;
using Lattice.Tests.Board;
using static Lattice.Tests.Validation.ValidationBench;

namespace Lattice.Tests.Validation;

/// <summary>A change not on the board is judged on the worker like a run would judge it, and validation's own result stays as it was.</summary>
public sealed class ValidationTrialTests
{
    [Fact]
    public void A_trial_ghost_waiting_on_a_planned_one_waits_for_it_and_the_report_is_unchanged()
    {
        var bench = new ValidationBench().Start();
        var floor = bench.Ghost("ModStackFrame", Large(0, 0, 0));
        bench.Pump();
        var before = bench.Report();

        var trial = Try(bench, ("ModStackFrame", Large(0, 1, 0)));
        var findings = Ready(bench, trial);

        var upper = trial.Changes.Added[0].Id;
        AssertWaitsFor(Assert.Single(findings, f => f is not Finding.Warns), floor);
        Assert.All(findings, f => Assert.Equal(upper, f.Ghost));
        Assert.Equal(before, bench.Report());
        Assert.Single(bench.Board.Index.Ghosts);
    }

    [Fact]
    public void A_trial_frame_light_on_nothing_will_fail_and_a_trial_after_it_is_judged_on_the_board_as_it_is()
    {
        var bench = new ValidationBench().Start();
        bench.Pump();

        var lonely = Ready(bench, Try(bench, ("ModFrameLight", new Cell(20, 10, 10))));
        Assert.IsType<Finding.WillFail>(Assert.Single(lonely, f => f is not Finding.Warns));

        var frame = Ready(bench, Try(bench, ("ModStackFrame", Large(0, 0, 0))));
        Assert.DoesNotContain(frame, f => f is Finding.WillFail);
        Assert.Empty(bench.Report().Findings);
    }

    [Fact]
    public void A_trial_that_changes_a_ghost_judges_the_change_and_puts_the_ghost_back()
    {
        var bench = new ValidationBench().Start();
        var floor = bench.Ghost("ModStackFrame", Large(0, 0, 0));
        var light = bench.Ghost("ModFrameLight", new Cell(20, 10, 10), Facing(Direction.XPlus));
        bench.Pump();
        var before = bench.Report();
        AssertWaitsFor(Verdict(before, light), floor);

        var trial = TestKitOk(bench.Board.Try(new Command.Remove(new Selector.Ids(ValueList.Of(floor))), TestBoard.Alice));
        var vt = bench.Service.Trial(trial.Changes);
        var findings = Ready(bench, vt);

        Assert.Empty(findings);
        Assert.Equal(before, bench.Report());
    }

    [Fact]
    public void A_main_thread_rule_answers_for_a_trial_ghost()
    {
        var bench = new ValidationBench();
        bench.Rules.Register(new RuleDescriptor(Rule("main"), input =>
            input.Ghost.Intent is Intent.Build { Target.At: var at } && at == Large(3, 0, 0)
                ? new RuleVerdict.Fail(Why("main"))
                : new RuleVerdict.Pass())
        {
            Thread = CallbackThread.Main,
        });
        bench.Start();
        bench.Ghost("StructureFrameIron", Large(0, 0, 0));
        bench.Pump();
        var before = bench.Report();

        var findings = Ready(bench, Try(bench, ("StructureFrameIron", Large(3, 0, 0))));

        var fail = Assert.IsType<Finding.WillFail>(Assert.Single(findings, f => f is not Finding.Warns));
        Assert.Equal(Why("main").Code, fail.Why.Code);
        Assert.Equal(before, bench.Report());
    }

    [Fact]
    public void A_trial_for_a_revision_the_board_has_left_is_dropped()
    {
        var bench = new ValidationBench().Start();
        bench.Pump();
        var trial = Try(bench, ("ModStackFrame", Large(0, 0, 0)));

        bench.Ghost("ModStackFrame", Large(4, 0, 0));
        bench.Pump();

        Assert.False(trial.IsReady);
        Assert.False(bench.Service.Busy);
    }

    private static ValidationTrial Try(ValidationBench bench, params (string Piece, Cell At)[] pieces)
    {
        var drafts = pieces.Select(p => TestBoard.Draft(new Intent.Build(new Placement(Pieces.Named(p.Piece).Key, p.At, default), Option<Finish>.None))).ToArray();
        var trial = TestKitOk(bench.Board.Try(new Command.Place(ValueList.From(drafts)), TestBoard.Alice));
        return bench.Service.Trial(trial.Changes);
    }

    private static T TestKitOk<T>(Result<T, ValueList<Refusal>> result) where T : notnull =>
        result.Match(ok => ok, why => throw new Xunit.Sdk.XunitException($"Refused: {why}"));

    private static ValueList<Finding> Ready(ValidationBench bench, ValidationTrial trial)
    {
        for (var i = 0; i < 64 && !trial.IsReady; i++)
            bench.Frame();
        return Assert.IsType<Answer<ValueList<Finding>>.Ready>(trial.Answer).Value;
    }
}
