using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.BuiltWorld;
using Lattice.Core.Slices;
using Lattice.Core.Support;
using Lattice.Core.Validation;
using Lattice.TestKit.Catalogue;
using Lattice.Tests.Contracts;
using Lattice.TestKit.Rules;
using Lattice.TestKit.World;
using Lattice.Tests.Support;

namespace Lattice.Tests.Validation;

/// <summary>
/// The test kit's rule suite against validation's own rule host: the engine on its worker side, worker
/// rules called through the given calls. Each check hands the ghost over again, so it is judged again, with the
/// game's answer for it given up front.
/// </summary>
public abstract class ValidationRuleHost : RuleHostContract
{
    private readonly ValidationRules _rules;
    private readonly ValidationEngine _engine;
    private long _revision;

    private protected ValidationRuleHost(IRuleCalls worker, IClock clock)
    {
        var catalogue = FakeCatalogue.Hand();
        var grid = SupportBench.Grid;
        var shapes = new Shapes(catalogue, grid);
        var world = new FakeWorld(catalogue);
        var mirror = new WorldMirror(shapes, world);
        mirror.Start(world, new FakeScan(world));
        var main = new InlineRuleCalls(clock);
        _rules = new ValidationRules(new RuleGuard(RuleGuard.DefaultLimit, worker, main));
        var support = new SupportJudge(new SupportRules(new RuleGuard(RuleGuard.DefaultLimit, worker, main)), new SupportGeometry(grid, new TestSupportFacts(catalogue), catalogue));
        _engine = new ValidationEngine(catalogue, grid, support, _rules, mirror.Snapshot());
    }

    internal override TimeSpan Limit => RuleGuard.DefaultLimit;

    internal override Result<IDisposable, Reason> Register(RuleDescriptor rule) => _rules.Register(rule);

    internal override ValueList<Finding> Check(Ghost ghost)
    {
        var inputs = new List<WorkInput> { new WorkInput.Ghosts(new Revision(++_revision), ValueList.Of(ghost), ValueList<GhostId>.Empty, Reset: false) };
        if (ghost.Intent is Intent.Build build)
            inputs.Add(new WorkInput.GameAnswered(build.Target, new GameAnswer.Accepts()));
        var outcome = _engine.Run(inputs);
        return outcome is RunOutcome.Ready ready ? ready.Result.Findings : throw new InvalidOperationException($"Not ready: {outcome}");
    }

    internal override void EndWorld() => _rules.End();
}

/// <summary>Worker rules called in line and timed on a clock that moves only when a rule lets time pass.</summary>
public sealed class ValidationRuleHostSuite : ValidationRuleHost
{
    private readonly ManualClock _clock;

    public ValidationRuleHostSuite()
        : this(new ManualClock())
    {
    }

    private ValidationRuleHostSuite(ManualClock clock)
        : base(new InlineRuleCalls(clock), clock) => _clock = clock;

    internal override void Elapse(TimeSpan time) => _clock.Advance(time);
}

/// <summary>
/// Worker rules on rule threads with a deadline, timed on the real clock: every rule must answer
/// within the limit in wall time, so the suite runs with the perf tests.
/// </summary>
[Trait(Perf.Trait, Perf.Value)]
public sealed class ValidationRuleThreadsSuite : ValidationRuleHost, IDisposable
{
    private readonly RuleThreads _threads;

    public ValidationRuleThreadsSuite()
        : this(new RuleThreads())
    {
    }

    private ValidationRuleThreadsSuite(RuleThreads threads)
        : base(threads, new StopwatchClock()) => _threads = threads;

    /// <summary>Time passes by sleeping (at least as long as asked).</summary>
    internal override void Elapse(TimeSpan time) => System.Threading.Thread.Sleep(time);

    public void Dispose() => _threads.Dispose();
}
