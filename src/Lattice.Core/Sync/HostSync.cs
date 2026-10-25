using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Commands;
using Lattice.Core.Slices;

namespace Lattice.Core.Sync;

/// <summary>
/// The host's side of sync. A client's command arrives as text from a connection; the host stamps it with the player
/// the game says owns that connection, never with anything the message says, puts it through the client checks and
/// the board (<see cref="CommandService.SubmitFromClient"/>) and answers that connection. Every change set the board
/// commits goes to every joined client as its lines; a client that joins gets the board's plans and ghosts in parts
/// first, then every change set after them. Nothing is sent from inside a call: messages queue per connection and go
/// out in the sending budget (<see cref="WorkKind.Sending"/>), one message encoded and sent per step, connections
/// taken in turn, each connection's messages in order. Main thread. Dispose at world end.
/// </summary>
internal sealed class HostSync : ISlicedWork, IDisposable
{
    private readonly CommandService _commands;
    private readonly GhostBoard _board;
    private readonly FrameScheduler _scheduler;
    private readonly ISyncLink _link;
    private readonly SyncLimits _limits;
    private readonly Action<Reason> _report;
    private readonly Dictionary<ConnectionId, Peer> _peers = new();
    private readonly List<Peer> _order = new();
    private readonly Dictionary<CommandId, ConnectionId> _waiting = new();
    private readonly Action<ChangeSet> _onChanged;
    private readonly Action<Ticket, Outcome> _onAnswered;
    private SlicedJob? _job;
    private int _turn;

    /// <summary>Sync over the board; <c>report</c> takes what it drops: a message over the size limit or one it cannot read.</summary>
    public HostSync(CommandService commands, GhostBoard board, FrameScheduler scheduler, ISyncLink link, SyncLimits limits, Action<Reason> report)
    {
        _commands = commands;
        _board = board;
        _scheduler = scheduler;
        _link = link;
        _limits = limits;
        _report = report;
        _onChanged = OnChanged;
        _onAnswered = OnAnswered;
        board.Events.Changed += _onChanged;
        board.Events.Answered += _onAnswered;
    }

    /// <summary>Messages queued and not sent yet, over every connection.</summary>
    public int Queued
    {
        get
        {
            var queued = 0;
            foreach (var peer in _order)
                queued += peer.Outbox.Count;
            return queued;
        }
    }

    /// <summary>
    /// A client is ready for messages: it gets the board as it is now (plans, then ghosts in parts), then every change
    /// set after it. Joining again starts over. Cost: linear in the board's ghosts (a copy of the list); the parts are
    /// written as they are sent.
    /// </summary>
    public void Joined(ConnectionId connection)
    {
        Left(connection);
        var snapshot = TakeSnapshot();
        var peer = new Peer(connection, snapshot.Revision);
        var parts = JoinParts.Count(snapshot.Ghosts.Count, _limits.JoinGhostsPerPart);
        for (var part = 0; part < parts; part++)
            peer.Outbox.Enqueue(new Outgoing(SyncKind.Join, new JoinText(snapshot, part, _limits.JoinGhostsPerPart)));
        _peers.Add(connection, peer);
        _order.Add(peer);
        Wake();
    }

    /// <summary>The client left: what was queued for it is dropped, and answers still owed to it are not sent.</summary>
    public void Left(ConnectionId connection)
    {
        if (!_peers.Remove(connection, out var peer))
            return;
        _order.Remove(peer);
        var owed = new List<CommandId>();
        foreach (var pair in _waiting)
            if (pair.Value == connection)
                owed.Add(pair.Key);
        foreach (var id in owed)
            _waiting.Remove(id);
    }

    /// <summary>
    /// A command message from a connection, sent by <paramref name="player"/> as the game knows the connection. A text
    /// over the size limit or one that does not read is reported and dropped; otherwise the command is submitted as
    /// that player and the answer queued for the connection. Cost: linear in the text, then the command's own.
    /// </summary>
    public void Receive(ConnectionId from, ClientId player, string text)
    {
        var bytes = SyncLimits.Bytes(text);
        if (bytes > _limits.MaxMessageBytes)
        {
            _report(SyncRules.TooLarge(bytes, _limits.MaxMessageBytes).WithConnection(from));
            return;
        }

        var read = CommandForm.Read(text);
        if (!read.TryGet(out var sent))
        {
            if (read.TryGetError(out var why))
                _report(why.WithConnection(from));
            return;
        }

        switch (_commands.SubmitFromClient(sent.Id, sent.Command, new Actor.Player(player)))
        {
            case Submission.Answered answered:
                Answer(from, sent.Id, answered.Outcome);
                break;
            case Submission.Sent:
                _waiting[sent.Id] = from;
                break;
        }
    }

    /// <summary>Sends the next queued message. Constant but for writing that message (linear in its size).</summary>
    public StepOutcome Step()
    {
        for (var tried = 0; tried < _order.Count; tried++)
        {
            _turn = (_turn + 1) % _order.Count;
            var peer = _order[_turn];
            if (peer.Outbox.Count == 0)
                continue;
            var outgoing = peer.Outbox.Peek();
            var frame = outgoing.Text.Frame(_limits.MaxMessageBytes, outgoing.Sent);
            _link.ToClient(peer.Connection, outgoing.Kind, frame);
            if (++outgoing.Sent == outgoing.Text.Frames(_limits.MaxMessageBytes))
                peer.Outbox.Dequeue();
            return Queued > 0 ? StepOutcome.More : StepOutcome.Done;
        }

        return StepOutcome.Done;
    }

    public void Dispose()
    {
        _board.Events.Changed -= _onChanged;
        _board.Events.Answered -= _onAnswered;
        _job?.Cancel();
        _peers.Clear();
        _order.Clear();
        _waiting.Clear();
    }

    private void OnChanged(ChangeSet set)
    {
        LinesText? text = null;
        foreach (var peer in _order)
            if (set.Revision.Value > peer.JoinedAt.Value)
                peer.Outbox.Enqueue(new Outgoing(SyncKind.Lines, text ??= new LinesText(set)));
        if (text is not null)
            Wake();
    }

    private void OnAnswered(Ticket ticket, Outcome outcome)
    {
        if (_waiting.Remove(ticket.Command, out var connection))
            Answer(connection, ticket.Command, outcome);
    }

    private void Answer(ConnectionId to, CommandId id, Outcome outcome)
    {
        if (!_peers.TryGetValue(to, out var peer))
            return;
        var answer = outcome switch
        {
            Outcome.Applied applied => (Answer)new Answer.Applied(applied.Changes.Id, applied.Changes.Revision),
            Outcome.Refused refused => new Answer.Refused(refused.Why),
            _ => new Answer.Refused(ValueList.Of(new Refusal(Unknown.Kind(outcome), ValueList<GhostId>.Empty))),
        };
        peer.Outbox.Enqueue(new Outgoing(SyncKind.Answer, new AnswerText(id, answer)));
        Wake();
    }

    private void Wake()
    {
        if (_job is { State: JobState.Running })
            return;
        _job = _scheduler.Schedule(WorkKind.Sending, this, Priority.Normal).Match(static job => job, static _ => (SlicedJob?)null);
    }

    private Snapshot TakeSnapshot()
    {
        var plans = new List<Plan>(_board.Index.Plans.Count);
        foreach (var entry in _board.Index.Plans.Values)
            plans.Add(entry.Plan);
        plans.Sort(static (a, b) => a.Id.Value.CompareTo(b.Id.Value));
        var ghosts = new List<Ghost>(_board.Index.Ghosts.Count);
        foreach (var entry in _board.Index.Ghosts.Values)
            ghosts.Add(entry.Ghost);
        ghosts.Sort(static (a, b) => a.Id.Value.CompareTo(b.Id.Value));
        return new Snapshot(_board.Revision, plans, ghosts);
    }

    /// <summary>A joined client: what is queued for it and the revision its join holds.</summary>
    private sealed class Peer(ConnectionId connection, Revision joinedAt)
    {
        public ConnectionId Connection { get; } = connection;

        public Revision JoinedAt { get; } = joinedAt;

        public Queue<Outgoing> Outbox { get; } = new();
    }

    /// <summary>One queued message and how many of its frames are sent.</summary>
    private sealed class Outgoing(SyncKind kind, MessageText text)
    {
        public SyncKind Kind { get; } = kind;

        public MessageText Text { get; } = text;

        public int Sent { get; set; }
    }

    private sealed record Snapshot(Revision Revision, List<Plan> Plans, List<Ghost> Ghosts);

    /// <summary>A message's text, written once when first sent (shared by every connection it goes to), and its frames.</summary>
    private abstract class MessageText
    {
        private string? _text;
        private List<int>? _starts;

        public int Frames(int maxBytes) => Starts(maxBytes).Count;

        public string Frame(int maxBytes, int index) => Sync.Frames.Frame(Text(), Starts(maxBytes), index);

        protected abstract string Write();

        private string Text() => _text ??= Write();

        private List<int> Starts(int maxBytes) => _starts ??= Sync.Frames.Cut(Text(), maxBytes);
    }

    private sealed class LinesText(ChangeSet set) : MessageText
    {
        protected override string Write() => SetLines.Write(set);
    }

    private sealed class JoinText(Snapshot snapshot, int part, int perPart) : MessageText
    {
        protected override string Write() => JoinParts.Write(snapshot.Revision, snapshot.Plans, snapshot.Ghosts, part, perPart);
    }

    private sealed class AnswerText(CommandId id, Answer answer) : MessageText
    {
        protected override string Write() => AnswerForm.Write(id, answer);
    }
}

/// <summary>Sync's facts on a reason.</summary>
internal static class SyncFacts
{
    /// <summary>The reason with the connection it came from.</summary>
    public static Reason WithConnection(this Reason reason, ConnectionId connection) =>
        reason with { Facts = reason.Facts.With("connection", connection.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)) };
}
