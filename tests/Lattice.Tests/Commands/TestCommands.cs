using System;
using Lattice.Api;
using Lattice.Core.Commands;
using Lattice.Core.Slices;
using Lattice.Tests.Slices;
using Lattice.TestKit.Catalogue;
using Lattice.TestKit.World;
using TestBoard = Lattice.Tests.Board.TestBoard;

namespace Lattice.Tests.Commands;

/// <summary>A test board with the command service over it: expanders, client checks, an empty fake world.</summary>
internal sealed class TestCommands
{
    private int _ids;

    public TestCommands(CommandLimits? limits = null, params NamedPiece[] extraPieces)
    {
        T = new TestBoard(default, extraPieces);
        World = new FakeWorld(T.Catalogue);
        Checks = new ClientChecks(limits ?? CommandLimits.Default, T.Catalogue, Attachments);
        Scheduler = new FrameScheduler(Clock, FrameBudgets.Default);
        Service = new CommandService(T.Board, Expanders, new FakeWorldQuery(World), Checks, Scheduler);
    }

    public FakeClock Clock { get; } = new();

    public FrameScheduler Scheduler { get; }

    public TestBoard T { get; }

    public FakeWorld World { get; }

    public CommandExpanders Expanders { get; } = new();

    public AttachmentLimits Attachments { get; } = new();

    public ClientChecks Checks { get; }

    public CommandService Service { get; }

    public static CommandLimits Limits(int maxGhosts = 10_000, int selectorDepth = 8, int batchDepth = 4, Cell? min = null, Cell? max = null) =>
        CommandLimits.Create(maxGhosts, selectorDepth, batchDepth, min ?? CommandLimits.Default.Min, max ?? CommandLimits.Default.Max)
            .Match(static limits => limits, static why => throw new InvalidOperationException(why.ToString()));

    /// <summary>One frame: the scheduler's budgets, then the board's events.</summary>
    public void Frame()
    {
        Scheduler.RunFrame();
        T.Events.Flush();
    }

    public CommandId NextId() => new(new Guid(++_ids, 7, 7, new byte[8]));

    public Outcome Submit(Command command, Actor? actor = null) => Answer(Service.Submit(NextId(), command, actor ?? TestBoard.Alice));

    public Outcome FromClient(Command command) => Answer(Service.SubmitFromClient(NextId(), command, TestBoard.Bob));

    public static Outcome Answer(Submission submission) =>
        submission is Submission.Answered answered ? answered.Outcome : throw new InvalidOperationException($"not answered: {submission}");
}
