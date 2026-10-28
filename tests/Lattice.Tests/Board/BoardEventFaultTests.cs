using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;

namespace Lattice.Tests.Board;

public sealed class BoardEventFaultTests
{
    private static BoardEvents Queued(BoardEvents events)
    {
        events.Queue(new BoardResetEvent());
        events.Queue(new BoardResetEvent());
        return events;
    }

    [Fact]
    public void A_throwing_handler_is_reported_and_the_handlers_after_it_still_run()
    {
        var faults = new List<(string Source, Exception Error)>();
        var events = new BoardEvents { Faulted = (source, error) => faults.Add((source, error)) };
        var after = 0;
        events.BoardReset += () => throw new InvalidOperationException("bug");
        events.BoardReset += () => after++;

        Queued(events).Flush();

        Assert.Equal(2, after);
        Assert.Equal(2, faults.Count);
        Assert.Equal("bug", faults[0].Error.Message);
        Assert.Contains(nameof(BoardEventFaultTests), faults[0].Source);
    }

    [Fact]
    public void Without_a_fault_sink_the_first_fault_leaves_the_flush_after_every_event_is_raised()
    {
        var events = new BoardEvents();
        var after = 0;
        events.BoardReset += () => throw new InvalidOperationException("bug");
        events.BoardReset += () => after++;

        var thrown = Assert.Throws<InvalidOperationException>(Queued(events).Flush);

        Assert.Equal("bug", thrown.Message);
        Assert.Equal(2, after);
        Assert.Equal(0, events.Waiting);
        events.Flush();
    }
}
