using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Commands;
using Lattice.Core.Slices;
using Lattice.TestKit;
using Lattice.Tests.Slices;
using static Lattice.Tests.Board.TestBoard;

namespace Lattice.Tests.Commands;

public sealed class SlicedExpansionTests
{
    private static readonly Cell From = new(100, 100, 100);
    private static readonly Cell To = new(200, 100, 100);
    private static readonly ModuleName Routes = TestKitIds.Must(ModuleName.Create("routes"));
    private static readonly TimeSpan StepCost = TimeSpan.FromMilliseconds(0.5);

    private static Command.PlanRun Run(Cell from, Cell to) =>
        new(new RunRequest(default, from, ValueList<Cell>.Empty, to, Option<PlanId>.None, TestMaker));

    /// <summary>A search of a fixed number of steps on the fake clock that finds a straight at each end.</summary>
    private sealed class FakeSearch(TestCommands c, Command.PlanRun run, int steps, Option<Reason> refuse) : IExpansionWork
    {
        public int Stepped { get; private set; }

        public Result<Command, Reason> Found { get; private set; } = Result<Command, Reason>.Error(Reason.Of(ReasonCodes.UnknownKind, LatticeIdentity.Board, ValueMap<string, string>.Empty));

        public StepOutcome Step()
        {
            c.Clock.Advance(StepCost);
            if (++Stepped < steps)
                return StepOutcome.More;
            Found = refuse.TryGet(out var why)
                ? Result<Command, Reason>.Error(why)
                : Result<Command, Reason>.Ok(new Command.Batch(ValueList.From<Command>([
                    new Command.Place(ValueList.Of(c.T.Draft("StructureCableStraight", run.Request.From))),
                    new Command.Place(ValueList.Of(c.T.Draft("StructureCableStraight", run.Request.To))),
                ])));
            return StepOutcome.Done;
        }
    }

    private static List<FakeSearch> Register(TestCommands c, int steps = 20, Reason? refuse = null)
    {
        var started = new List<FakeSearch>();
        Assert.True(c.Expanders.RegisterSliced(new SlicedExpander<Command.PlanRun>(Routes, input =>
        {
            var search = new FakeSearch(c, input.Command, steps, refuse is null ? Option<Reason>.None : Option.Some(refuse));
            started.Add(search);
            return search;
        })).IsOk);
        return started;
    }

    private static List<(Ticket Ticket, Outcome Outcome)> Answers(TestCommands c)
    {
        var answers = new List<(Ticket, Outcome)>();
        c.T.Events.Answered += (ticket, outcome) => answers.Add((ticket, outcome));
        return answers;
    }

    private static void FramesUntil(TestCommands c, Func<bool> done, int most = 1000)
    {
        for (var i = 0; i < most && !done(); i++)
            c.Frame();
        Assert.True(done(), "not done within the frames given");
    }

    [Fact]
    public void A_sliced_expansion_is_sent_now_then_applied_and_answered_when_its_work_finishes()
    {
        var c = new TestCommands();
        Register(c);
        var answers = Answers(c);
        var id = c.NextId();

        var submission = c.Service.Submit(id, Run(From, To), Alice);

        Assert.Equal(new Submission.Sent(new Ticket(id)), submission);
        Assert.Empty(c.T.Board.Index.Ghosts);
        FramesUntil(c, () => answers.Count > 0);
        var (ticket, outcome) = Assert.Single(answers);
        Assert.Equal(new Ticket(id), ticket);
        Assert.Equal(2, Applied(outcome).Added.Count);
        Assert.Equal(id, Applied(outcome).Command);
        Assert.Equal(new Submission.Answered(new Ticket(id), outcome), c.Service.Submit(id, Run(From, To), Alice));
    }

    [Fact]
    public void A_resent_id_while_its_work_runs_is_sent_again_and_starts_no_second_search()
    {
        var c = new TestCommands();
        var started = Register(c);
        var id = c.NextId();

        c.Service.Submit(id, Run(From, To), Alice);
        c.Frame();
        var again = c.Service.Submit(id, Run(From, To), Alice);
        var fromClient = c.Service.SubmitFromClient(id, Run(From, To), Alice);

        Assert.Equal(new Submission.Sent(new Ticket(id)), again);
        Assert.Equal(new Submission.Sent(new Ticket(id)), fromClient);
        Assert.Single(started);
    }

    [Fact]
    public void Each_frame_spends_no_more_than_the_checking_budget_on_the_search()
    {
        var c = new TestCommands();
        var started = Register(c, steps: 200);
        c.Service.Submit(c.NextId(), Run(From, To), Alice);
        var frames = 0;

        while (started[0].Stepped < 200)
        {
            var before = c.Clock.Now;
            c.Frame();
            Assert.True(c.Clock.Now - before <= FrameBudgets.Default.Checking.Length, $"frame took {c.Clock.Now - before}");
            frames++;
        }

        Assert.True(frames >= 50, $"200 steps of 0.5 ms in {frames} frames");
    }

    [Fact]
    public void A_search_that_finds_no_expansion_answers_with_its_reason()
    {
        var c = new TestCommands();
        var why = Reason.Of(ReasonCodes.NotFound, LatticeIdentity.Board, ValueMap.Of("problem", "no-route"));
        Register(c, refuse: why);
        var answers = Answers(c);

        c.Service.Submit(c.NextId(), Run(From, To), Alice);
        FramesUntil(c, () => answers.Count > 0);

        Assert.Equal(why, Refused(Assert.Single(answers).Outcome).Why);
        Assert.Empty(c.T.Board.Index.Ghosts);
    }

    [Fact]
    public void A_batch_with_two_sliced_steps_waits_for_both_and_applies_as_one_change_set()
    {
        var c = new TestCommands();
        var started = Register(c);
        var answers = Answers(c);
        var batch = new Command.Batch(ValueList.From<Command>([
            Run(From, To),
            new Command.Place(ValueList.Of(c.T.Draft("StructureCableStraight", new Cell(300, 100, 100)))),
            Run(new Cell(100, 200, 100), new Cell(200, 200, 100)),
        ]));

        c.Service.Submit(c.NextId(), batch, Alice);
        FramesUntil(c, () => answers.Count > 0);

        Assert.Equal(2, started.Count);
        Assert.Equal(5, Applied(Assert.Single(answers).Outcome).Added.Count);
        Assert.Equal(new Revision(1), c.T.Board.Revision);
    }

    [Fact]
    public void Commands_sent_while_a_search_runs_are_applied_at_once()
    {
        var c = new TestCommands();
        Register(c);

        c.Service.Submit(c.NextId(), Run(From, To), Alice);
        var place = c.Submit(new Command.Place(ValueList.Of(c.T.Draft("StructureCableStraight", new Cell(300, 100, 100)))));

        Assert.Single(Applied(place).Added);
    }

    [Fact]
    public void A_preview_is_pending_until_the_search_finishes_then_ready_and_kept_for_the_revision()
    {
        var c = new TestCommands();
        var started = Register(c);
        var ready = new List<Revision>();
        c.T.Events.AnswersReady += ready.Add;
        var run = Run(From, To);

        var first = c.Service.Expansion(run, Alice);
        Assert.Equal(new Answer<Result<Command, ValueList<Refusal>>>.Pending(c.T.Board.Revision), first);
        Assert.IsType<Answer<Result<Command, ValueList<Refusal>>>.Pending>(c.Service.Expansion(run, Alice));
        FramesUntil(c, () => ready.Count > 0);

        Assert.Equal(c.T.Board.Revision, Assert.Single(ready));
        var answer = Assert.IsType<Answer<Result<Command, ValueList<Refusal>>>.Ready>(c.Service.Expansion(run, Alice));
        Assert.True(answer.Value.IsOk);
        Assert.Equal(answer, c.Service.Expansion(run, Alice));
        Assert.Single(started);
        Assert.Empty(c.T.Board.Index.Ghosts);
    }

    [Fact]
    public void A_submit_after_a_ready_preview_at_the_same_revision_reuses_it_and_is_answered_at_once()
    {
        var c = new TestCommands();
        var started = Register(c);
        var run = Run(From, To);
        c.Service.Expansion(run, Alice);
        FramesUntil(c, () => c.Service.Expansion(run, Alice) is Answer<Result<Command, ValueList<Refusal>>>.Ready);

        var outcome = c.Submit(run);

        Assert.Equal(2, Applied(outcome).Added.Count);
        Assert.Single(started);
    }

    [Fact]
    public void A_preview_for_another_actor_or_after_the_board_moves_searches_again()
    {
        var c = new TestCommands();
        var started = Register(c);
        var run = Run(From, To);
        c.Service.Expansion(run, Alice);
        FramesUntil(c, () => c.Service.Expansion(run, Alice) is Answer<Result<Command, ValueList<Refusal>>>.Ready);

        c.Service.Expansion(run, Bob);
        c.Submit(new Command.Place(ValueList.Of(c.T.Draft("StructureCableStraight", new Cell(300, 100, 100)))));
        var after = c.Service.Expansion(run, Alice);

        Assert.Equal(new Answer<Result<Command, ValueList<Refusal>>>.Pending(new Revision(1)), after);
        Assert.Equal(3, started.Count);
    }

    [Fact]
    public void A_preview_of_a_command_with_no_sliced_part_is_ready_at_once()
    {
        var c = new TestCommands();
        Register(c);
        var place = new Command.Place(ValueList.Of(c.T.Draft("StructureCableStraight", From)));

        var answer = Assert.IsType<Answer<Result<Command, ValueList<Refusal>>>.Ready>(c.Service.Expansion(place, Alice));

        Assert.Equal(Result<Command, ValueList<Refusal>>.Ok(place), answer.Value);
    }

    [Fact]
    public void A_kind_takes_one_expander_plain_or_sliced_and_primitive_kinds_take_no_sliced_one()
    {
        var c = new TestCommands();
        Register(c);

        var plain = c.Expanders.Register(new CommandExpander<Command.PlanRun>(Routes, static _ => Result<Command, Reason>.Ok(new Command.Batch(ValueList<Command>.Empty))));
        var primitive = c.Expanders.RegisterSliced(new SlicedExpander<Command.Place>(Routes, static _ => throw new InvalidOperationException()));

        Assert.Equal(ReasonCodes.AlreadyRegistered, plain.ErrorOf().Code);
        Assert.Equal(ReasonCodes.InvalidValue, primitive.ErrorOf().Code);
    }
}
