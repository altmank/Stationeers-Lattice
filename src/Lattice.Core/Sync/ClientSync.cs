using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Executors;

namespace Lattice.Core.Sync;

/// <summary>
/// A client's side of sync and its <see cref="ICommands"/>. Submitting sends the command to the host and returns
/// <see cref="Submission.Sent"/>; the host's outcome arrives later as <see cref="IGhostEvents.Answered"/>, after the
/// change set it made has reached the board. The host's answer is the only one: whatever a client showed before it
/// (a preview, a faint draft) is its owner's to drop. A command this side cannot send (a kind it does not know, a
/// text over the size limit) is answered refused the same way, never sent. The board holds nothing until the host's
/// join arrives; from then on it takes the host's change sets in revision order. The host's status messages (its
/// executors' claims and status) go to the client seat's <see cref="ExecutorMirror"/>, after the change sets they
/// follow; a join clears the mirror and the join's own status message rebuilds it.
/// <para>
/// A change set the board cannot take (its lines do not read against the board, or the board refuses it) stops the
/// mirroring: the drop is reported once, with its facts and <c>then=rejoin</c>, and the client asks the host for the
/// board again (<see cref="SyncKind.Rejoin"/>). Until that join arrives every change set and status message is skipped
/// without a report; the join resets the board and mirroring goes on from it. A host that never answers leaves the
/// board as it was at the drop, said once.
/// </para>
/// <para>
/// Every submitted command is answered once through <see cref="IGhostEvents.Answered"/>, a resync included: the rejoin
/// names the commands still pending, and the host sends their outcomes again after the join (an applied one with its
/// change set whole), answers later those it has not finished, and answers <see cref="ReasonCodes.OutcomeLost"/> for
/// one it no longer has. An answer for a command already settled is skipped.
/// </para>
/// Main thread.
/// </summary>
internal sealed class ClientSync : ICommands
{
    private readonly IReplicaBoard _board;
    private readonly ISyncLink _link;
    private readonly SyncLimits _limits;
    private readonly Action<Reason> _report;
    private readonly FrameGather _lines = new();
    private readonly FrameGather _join = new();
    private readonly FrameGather _answers = new();
    private readonly FrameGather _status = new();
    private readonly ExecutorMirror? _executors;
    private readonly JoinGather _parts = new();
    private readonly HashSet<CommandId> _pending = new();
    private readonly Dictionary<CommandId, ChangeSet> _applied = new();
    private readonly Dictionary<CommandId, Answer.Applied> _early = new();
    private bool _dropped;

    /// <summary>
    /// Sync for a client's board, and for the executors' mirror when <paramref name="executors"/> is given;
    /// <c>report</c> takes what it drops (a message it cannot read or fit).
    /// </summary>
    public ClientSync(IReplicaBoard board, ISyncLink link, SyncLimits limits, Action<Reason> report, ExecutorMirror? executors = null)
    {
        _board = board;
        _executors = executors;
        _link = link;
        _limits = limits;
        _report = report;
    }

    /// <summary>Whether the host's join has arrived.</summary>
    public bool Joined { get; private set; }

    /// <summary>Whether a change set was dropped and the client waits for the host's join to mirror again.</summary>
    public bool Rejoining => _dropped;

    /// <summary>Sends the command to the host. The actor is not sent: the host acts as the player of this connection.</summary>
    public Submission Submit(CommandId id, Command command, Actor actor)
    {
        var ticket = new Ticket(id);
        var written = CommandForm.Write(id, command);
        if (!written.TryGet(out var text))
        {
            Refuse(ticket, written.TryGetError(out var why) ? why : SyncRules.Problem("unwritable"));
            return new Submission.Sent(ticket);
        }

        var bytes = SyncLimits.Bytes(text);
        if (bytes > _limits.MaxMessageBytes)
        {
            Refuse(ticket, SyncRules.TooLarge(bytes, _limits.MaxMessageBytes));
            return new Submission.Sent(ticket);
        }

        _pending.Add(id);
        _link.ToHost(SyncKind.Command, text);
        return new Submission.Sent(ticket);
    }

    /// <summary>A frame from the host. Cost: linear in the frame; the last frame of a message also applies it.</summary>
    public void Receive(SyncKind kind, string frame)
    {
        var gather = kind switch
        {
            SyncKind.Lines => _lines,
            SyncKind.Join => _join,
            SyncKind.Answer => _answers,
            SyncKind.Status => _status,
            _ => null,
        };
        if (gather is null)
        {
            _report(SyncRules.Problem("not a host message: " + kind));
            return;
        }

        var taken = gather.Take(frame);
        if (!taken.TryGet(out var whole))
        {
            Report(taken);
            return;
        }

        if (!whole.TryGet(out var text))
            return;
        switch (kind)
        {
            case SyncKind.Lines:
                Lines(text);
                break;
            case SyncKind.Join:
                Join(text);
                break;
            case SyncKind.Answer:
                Answered(text);
                break;
            case SyncKind.Status:
                Status(text);
                break;
        }
    }

    private void Lines(string text)
    {
        if (!Joined || _dropped)
            return;
        var read = SetLines.Read(text, _board);
        if (!read.TryGet(out var set))
        {
            Drop(read);
            return;
        }

        if (set.Revision.Value <= _board.Revision.Value)
            return;
        var mirrored = _board.Mirror(set);
        if (!mirrored.TryGet(out var changes))
        {
            Drop(mirrored);
            return;
        }

        if (!_pending.Contains(changes.Command))
            return;
        if (_early.Remove(changes.Command, out _))
            Settle(changes.Command, new Outcome.Applied(changes));
        else
            _applied[changes.Command] = changes;
    }

    private void Join(string text)
    {
        var read = JoinParts.Read(text);
        if (!read.TryGet(out var part))
        {
            Report(read);
            return;
        }

        var taken = _parts.Take(part);
        if (!taken.TryGet(out var seed))
        {
            Report(taken);
            return;
        }

        if (!seed.TryGet(out var board))
            return;
        _board.Reset(board);
        _executors?.Clear();
        _applied.Clear();
        Joined = true;
        _dropped = false;
    }

    // The board missed a change set: stop mirroring, say so once, and ask the host for the board again.
    private void Drop<T>(Result<T, Reason> failed)
        where T : notnull
    {
        _dropped = true;
        if (failed.TryGetError(out var why))
            _report(why with { Facts = why.Facts.With("then", "rejoin") });
        _link.ToHost(SyncKind.Rejoin, RejoinForm.Write(_pending));
    }

    private void Status(string text)
    {
        if (!Joined || _dropped)
            return;
        var read = StatusForm.Read(text);
        if (!read.TryGet(out var update))
        {
            Report(read);
            return;
        }

        _executors?.Take(update.Join, update.Entries);
    }

    private void Answered(string text)
    {
        if (AppliedForm.Is(text))
        {
            Whole(text);
            return;
        }

        var read = AnswerForm.Read(text);
        if (!read.TryGet(out var answer))
        {
            Report(read);
            return;
        }

        switch (answer.Answer)
        {
            case Answer.Refused refused when _pending.Contains(answer.Id):
                _early.Remove(answer.Id);
                _applied.Remove(answer.Id);
                Settle(answer.Id, new Outcome.Refused(refused.Why));
                break;
            case Answer.Refused:
                break;
            case Answer.Applied when _applied.Remove(answer.Id, out var changes):
                Settle(answer.Id, new Outcome.Applied(changes));
                break;
            case Answer.Applied applied:
                _pending.Add(answer.Id);
                _early[answer.Id] = applied;
                break;
        }
    }

    // An applied outcome sent whole after a resync join: it settles a command still pending, once.
    private void Whole(string text)
    {
        var read = AppliedForm.Read(text);
        if (!read.TryGet(out var whole))
        {
            Report(read);
            return;
        }

        if (!_pending.Contains(whole.Id))
            return;
        _early.Remove(whole.Id);
        _applied.Remove(whole.Id);
        Settle(whole.Id, new Outcome.Applied(whole.Set));
    }

    private void Settle(CommandId id, Outcome outcome)
    {
        _pending.Remove(id);
        _board.Events.Queue(new AnsweredEvent(new Ticket(id), outcome));
    }

    private void Refuse(Ticket ticket, Reason why) =>
        _board.Events.Queue(new AnsweredEvent(ticket, new Outcome.Refused(ValueList.Of(new Refusal(why, ValueList<GhostId>.Empty)))));

    private void Report<T>(Result<T, Reason> failed)
        where T : notnull
    {
        if (failed.TryGetError(out var why))
            _report(why);
    }
}
