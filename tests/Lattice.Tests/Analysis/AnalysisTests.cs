using System;
using System.Collections.Generic;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Analysis;
using Lattice.Core.Ports;
using Lattice.Core.Support;
using Lattice.TestKit;
using Lattice.Tests.Validation;
using static Lattice.Tests.Validation.ValidationBench;

namespace Lattice.Tests.Analysis;

public sealed class AnalyzersTests
{
    private static readonly ReasonCode Seen = TestKitIds.Must(ReasonCode.Create(TestKitIds.Mod, "seen"));

    internal static AnalyzerDescriptor Counting(string name, CallbackThread thread = CallbackThread.Main) =>
        new(Rule(name), input => ValueList.Of(new AnalyzerFact(Rule(name), Seen, ValueMap<string, string>.Of("ghosts", input.Ghosts.Count.ToString()))))
        {
            Thread = thread,
        };

    [Fact]
    public void Analyzers_answer_in_registration_order_a_second_id_is_refused_and_a_disposed_one_is_gone()
    {
        var analyzers = new Analyzers();
        var first = TestKitIds.Must(analyzers.Register(Counting("first")));
        TestKitIds.Must(analyzers.Register(Counting("second")));

        Assert.False(analyzers.Register(Counting("first")).IsOk);
        var facts = new List<AnalyzerFact>();
        analyzers.Facts(CallbackThread.Main, new AnalyzerInput(ValueList<Ghost>.Empty, new FakeQuery()), facts);
        Assert.Equal(new[] { Rule("first"), Rule("second") }, facts.Select(f => f.From));

        first.Dispose();
        facts.Clear();
        analyzers.Facts(CallbackThread.Main, new AnalyzerInput(ValueList<Ghost>.Empty, new FakeQuery()), facts);
        Assert.Equal(new[] { Rule("second") }, facts.Select(f => f.From));
    }

    private sealed class FakeQuery : IWorldQuery
    {
        public Option<StructureInfo> Structure(ReferenceId id) => Option<StructureInfo>.None;

        public ValueList<StructureInfo> At(Cell cell) => ValueList<StructureInfo>.Empty;

        public Result<ValueList<StructureRef>, Reason> Select(StructureSelector selector) => Result<ValueList<StructureRef>, Reason>.Ok(ValueList<StructureRef>.Empty);

        public Option<NetworkRef> NetworkOf(ReferenceId id) => Option<NetworkRef>.None;

        public Option<RoomRef> RoomAt(Cell cell) => Option<RoomRef>.None;

        public Option<GhostId> AssumedFrom(StructureRef structure) => Option<GhostId>.None;
    }
}

public sealed class AnalysisServiceTests
{
    private static AnalysisService Service(ValidationBench bench) =>
        new(new AnalysisParts(bench.Board, Lattice.Tests.Board.TestBoard.Alice, bench.World, bench.Mirror,
            new PlanEffects(Pieces, ValidationBench.Grid), bench.Analyzers, bench.Service));

    [Fact]
    public void A_selection_gets_a_main_thread_analyzers_fact_on_the_planned_world_and_is_kept_until_the_board_moves()
    {
        var bench = new ValidationBench();
        var assumed = new List<bool>();
        bench.Analyzers.Register(new AnalyzerDescriptor(Rule("modded"), input =>
        {
            assumed.Add(input.World.At(Large(0, 0, 0)).Any(s => input.World.AssumedFrom(s.Ref).IsSome));
            return ValueList.Of(new AnalyzerFact(Rule("modded"), TestKitIds.Must(ReasonCode.Create(TestKitIds.Mod, "heat")), ValueMap<string, string>.Of("watts", "40")));
        }));
        bench.Start();
        var frame = bench.Ghost("ModStackFrame", Large(0, 0, 0));
        var analysis = Service(bench);
        var which = new Selector.Ids(ValueList.Of(frame));

        var report = Ready(analysis, which);
        var again = Ready(analysis, which);

        Assert.Equal("40", Assert.Single(report.Facts).Values["watts"]);
        Assert.Same(report, again);
        Assert.Equal(new[] { true }, assumed);
        var before = GC.GetAllocatedBytesForCurrentThread();
        analysis.Of(which);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);

        bench.Ghost("ModStackFrame", Large(3, 0, 0));
        Assert.NotSame(report, Ready(analysis, which));
        Assert.Equal(2, assumed.Count);
    }

    [Fact]
    public void A_worker_analyzer_answers_on_the_worker_and_the_answer_is_pending_until_then()
    {
        var bench = new ValidationBench();
        var onWorker = new List<bool>();
        bench.Analyzers.Register(new AnalyzerDescriptor(Rule("worker"), input =>
        {
            onWorker.Add(bench.Worker.Running);
            return ValueList.Of(new AnalyzerFact(Rule("worker"), TestKitIds.Must(ReasonCode.Create(TestKitIds.Mod, "count")), ValueMap<string, string>.Of("n", input.Ghosts.Count.ToString())));
        })
        {
            Thread = CallbackThread.Worker,
        });
        bench.Start();
        var ghosts = bench.Ghosts(("ModStackFrame", Large(0, 0, 0)), ("ModStackFrame", Large(0, 1, 0)));
        bench.Pump();
        var analysis = Service(bench);
        var which = new Selector.Ids(ghosts);

        Assert.IsType<Answer<AnalysisReport>.Pending>(TestKitIds.Must(analysis.Of(which)));
        var heard = 0;
        bench.Events.AnswersReady += _ => heard++;
        for (var i = 0; i < 8 && TestKitIds.Must(analysis.Of(which)) is Answer<AnalysisReport>.Pending; i++)
            bench.Frame();

        var ready = Assert.IsType<Answer<AnalysisReport>.Ready>(TestKitIds.Must(analysis.Of(which)));
        Assert.Equal("2", Assert.Single(ready.Value.Facts).Values["n"]);
        Assert.Equal(new[] { true }, onWorker);
        bench.Events.Flush();
        Assert.True(heard > 0);
    }

    [Fact]
    public void A_worker_analyzer_runs_on_its_own_thread_so_one_that_hangs_leaves_only_its_answers_pending()
    {
        var bench = new ValidationBench { AnalyzerWorker = new ManualWorker() };
        var where = new List<(bool Validation, bool Analyzers)>();
        bench.Analyzers.Register(new AnalyzerDescriptor(Rule("slow"), input =>
        {
            where.Add((bench.Worker.Running, bench.AnalyzerWorker.Running));
            var assumed = input.World.At(Large(0, 0, 0)).Any(s => input.World.AssumedFrom(s.Ref).IsSome);
            return ValueList.Of(new AnalyzerFact(Rule("slow"), TestKitIds.Must(ReasonCode.Create(TestKitIds.Mod, "seen")), ValueMap<string, string>.Of("assumed", assumed.ToString())));
        })
        {
            Thread = CallbackThread.Worker,
        });
        bench.Start();
        var ghost = bench.Ghost("ModStackFrame", Large(0, 0, 0));
        bench.Pump();
        var analysis = Service(bench);
        var which = new Selector.Ids(ValueList.Of(ghost));
        analysis.Of(which);

        for (var i = 0; i < 8; i++)
            bench.Frame();
        bench.Ghost("ModStackFrame", Large(0, 1, 0));
        var later = new Selector.Ids(ValueList.Of(ghost));
        analysis.Of(later);
        for (var i = 0; i < 8; i++)
            bench.Frame();

        Assert.Empty(where);
        Assert.IsType<Answer<AnalysisReport>.Pending>(TestKitIds.Must(analysis.Of(later)));
        Assert.IsType<Answer<ValidationReport>.Ready>(TestKitIds.Must(bench.Service.Check(bench.All())));

        bench.AnalyzerWorker.RunAll();
        bench.Frame();

        var ready = Assert.IsType<Answer<AnalysisReport>.Ready>(TestKitIds.Must(analysis.Of(later)));
        Assert.Equal("True", Assert.Single(ready.Value.Facts).Values["assumed"]);
        Assert.Contains((false, true), where);
        Assert.DoesNotContain(where, w => w.Validation);
    }

    [Fact]
    public void A_worker_analyzer_that_throws_leaves_validations_frame_and_validation_stays_on()
    {
        var bench = new ValidationBench();
        bench.Analyzers.Register(new AnalyzerDescriptor(Rule("broken"), _ => throw new InvalidOperationException("analyzer bug"))
        {
            Thread = CallbackThread.Worker,
        });
        bench.Start();
        var ghost = bench.Ghost("ModStackFrame", Large(0, 0, 0));
        bench.Pump();
        var analysis = Service(bench);
        analysis.Of(new Selector.Ids(ValueList.Of(ghost)));

        bench.Service.Frame();
        bench.Worker.RunAll();
        var thrown = Assert.Throws<InvalidOperationException>(bench.Service.Frame);

        Assert.Equal("analyzer bug", thrown.Message);
        Assert.False(bench.Service.Faulted);
        bench.Ghost("ModStackFrame", Large(3, 0, 0));
        bench.Pump();
    }

    [Fact]
    public void A_main_thread_analyzer_that_throws_leaves_the_analysis_call()
    {
        var bench = new ValidationBench();
        bench.Analyzers.Register(new AnalyzerDescriptor(Rule("broken"), _ => throw new InvalidOperationException("analyzer bug")));
        bench.Start();
        var ghost = bench.Ghost("ModStackFrame", Large(0, 0, 0));

        Assert.Throws<InvalidOperationException>(() => Service(bench).Of(new Selector.Ids(ValueList.Of(ghost))));
    }

    private static AnalysisReport Ready(AnalysisService analysis, Selector which) =>
        Assert.IsType<Answer<AnalysisReport>.Ready>(TestKitIds.Must(analysis.Of(which))).Value;
}
