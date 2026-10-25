using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Slices;

namespace Lattice.Core.Commands;

/// <summary>
/// The host's <see cref="ICommands"/>: every change arrives here as data with an id. A command id is applied once: a
/// resent id gets the first outcome back and changes nothing, refusals included. Module-owned commands, inside batches
/// too, are expanded by their registered expander into primitive commands before the board applies them; with none
/// registered the board refuses them <see cref="ReasonCodes.ModuleMissing"/>. A command submitted while another is being
/// applied (from the access policy or an expander) is queued, applied right after it and answered through
/// <see cref="IGhostEvents.Answered"/>. A command with a sliced expansion is answered the same way: sent now, its
/// expansion worked out in the checking budget, then applied and answered in the frame the work finishes. Its parts
/// are expanded one at a time, each against the board as it stands when that part is reached; commands sent meanwhile
/// do not wait for it. Commands from clients go through <see cref="SubmitFromClient"/>, which adds the host's size and
/// sanity checks; local actors' commands are not size-checked. Main thread. Outcomes are kept for the world, one entry
/// per command id.
/// </summary>
internal sealed class CommandService(GhostBoard board, CommandExpanders expanders, IWorldQuery world, ClientChecks checks, FrameScheduler scheduler)
    : ICommands
{
    /// <summary>How many times one command may expand into another module-owned command before it is refused.</summary>
    public const int MaxExpansionDepth = 16;

    private readonly Dictionary<CommandId, Outcome> _answered = new();
    private readonly HashSet<CommandId> _waitingIds = new();
    private readonly Queue<Submitted> _queue = new();
    private readonly Dictionary<(Command Command, Actor Actor), Previewed> _previews = new();
    private Revision _previewsAt;
    private bool _applying;

    /// <summary>
    /// Submits a command for a local actor. Cost: the command's expansion and the board's apply; a resent id is a
    /// lookup; a sliced expansion costs only its set-up now.
    /// </summary>
    public Submission Submit(CommandId id, Command command, Actor actor)
    {
        if (id == default)
            return Answer(id, board.Apply(id, command, actor));
        if (_answered.TryGetValue(id, out var first))
            return Answer(id, first);
        if (_waitingIds.Contains(id))
            return Sent(id);

        var submitted = new Submitted(id, command, actor, PartsFor(command, actor));
        if (_applying)
        {
            _waitingIds.Add(id);
            _queue.Enqueue(submitted);
            return Sent(id);
        }

        return RunNow(submitted).TryGet(out var outcome) ? Answer(id, outcome) : Sent(id);
    }

    /// <summary>
    /// Submits a command a client sent: refused by the first size or sanity limit it breaks (<see cref="ClientChecks"/>),
    /// otherwise as <see cref="Submit"/>. A refusal here is kept for the id like any outcome. Cost: linear in the command.
    /// </summary>
    public Submission SubmitFromClient(CommandId id, Command command, Actor actor)
    {
        if (id == default || _answered.ContainsKey(id) || _waitingIds.Contains(id) || !checks.Check(command).TryGet(out var refusal))
            return Submit(id, command, actor);
        var refused = new Outcome.Refused(ValueList.Of(refusal));
        _answered.Add(id, refused);
        return Answer(id, refused);
    }

    /// <summary>
    /// The command's expansion for an actor at the board's current revision, for the preview: pending while sliced work
    /// finds it, then ready, with <see cref="IGhostEvents.AnswersReady"/> raised each time a sliced part is found at the
    /// revision still current. Answers are kept per command and actor until the revision moves, when unfinished work
    /// is cancelled; a submit of the same command by the same actor at that revision reuses the parts found. The
    /// expanders see the default command id. Cost: a lookup once ready; else the plain expansions and the set-up of the
    /// next sliced part.
    /// </summary>
    public Answer<Result<Command, Reason>> Expansion(Command command, Actor actor)
    {
        var at = board.Revision;
        if (_previewsAt != at)
            DropPreviews(at);
        var key = (command, actor);
        if (!_previews.TryGetValue(key, out var previewed))
        {
            previewed = new Previewed(at);
            _previews.Add(key, previewed);
        }

        if (previewed.Ready.TryGet(out var ready))
            return new Answer<Result<Command, Reason>>.Ready(ready, at);
        if (previewed.Searching is not null)
            return new Answer<Result<Command, Reason>>.Pending(at);

        switch (Expand(command, default, actor, previewed.Parts, 0))
        {
            case Expanded.Ready done:
                return Settle(previewed, Result<Command, Reason>.Ok(done.Command));
            case Expanded.Waits waits:
                var scheduled = Schedule(waits, found => FoundForPreview(previewed, waits.Part, found));
                if (!scheduled.TryGet(out var search))
                    return Settle(previewed, Result<Command, Reason>.Error(scheduled.ErrorOf()));
                previewed.Searching = search.Job;
                return new Answer<Result<Command, Reason>>.Pending(at);
            case Expanded.Refused refused:
                return Settle(previewed, Result<Command, Reason>.Error(refused.Why));
            default:
                return Settle(previewed, Result<Command, Reason>.Error(Unknown(command)));
        }
    }

    private static Answer<Result<Command, Reason>> Settle(Previewed previewed, Result<Command, Reason> expansion)
    {
        previewed.Ready = Option.Some(expansion);
        return new Answer<Result<Command, Reason>>.Ready(expansion, previewed.At);
    }

    private void FoundForPreview(Previewed previewed, Command part, Result<Command, Reason> found)
    {
        previewed.Searching = null;
        previewed.Parts.Add(part, found);
        if (previewed.At == _previewsAt && previewed.At == board.Revision)
            board.Events.Queue(new AnswersReadyEvent(previewed.At));
    }

    private void DropPreviews(Revision at)
    {
        foreach (var previewed in _previews.Values)
            previewed.Searching?.Cancel();
        _previews.Clear();
        _previewsAt = at;
    }

    private Parts PartsFor(Command command, Actor actor)
    {
        var parts = new Parts();
        if (_previewsAt == board.Revision && _previews.TryGetValue((command, actor), out var previewed))
            parts.CopyFrom(previewed.Parts);
        return parts;
    }

    /// <summary>
    /// Runs a read with the board staged (a preview's step): a command submitted from inside it is queued and applied
    /// after it returns, never on the staged board. Main thread.
    /// </summary>
    internal T WhileApplying<T>(Func<T> read)
    {
        if (_applying)
            return read();
        _applying = true;
        try
        {
            var result = read();
            Drain();
            return result;
        }
        finally
        {
            _applying = false;
        }
    }

    private void Drain()
    {
        while (_queue.Count > 0)
        {
            var next = _queue.Dequeue();
            if (!_answered.ContainsKey(next.Id) && Attempt(next).TryGet(out var answer))
                board.Events.Queue(new AnsweredEvent(new Ticket(next.Id), answer));
        }
    }

    private Option<Outcome> RunNow(Submitted first)
    {
        _applying = true;
        try
        {
            var outcome = Attempt(first);
            Drain();
            return outcome;
        }
        finally
        {
            _applying = false;
        }
    }

    /// <summary>Applies the command if its expansion is complete, else starts the work for its next part and parks it.</summary>
    private Option<Outcome> Attempt(Submitted submitted)
    {
        switch (Expand(submitted.Command, submitted.Id, submitted.Actor, submitted.Parts, 0))
        {
            case Expanded.Ready ready:
                return Keep(submitted, board.Apply(submitted.Id, ready.Command, submitted.Actor));
            case Expanded.Waits waits:
                var scheduled = Schedule(waits, found => Resume(submitted, waits.Part, found));
                if (!scheduled.IsOk)
                    return Keep(submitted, Refused(scheduled.ErrorOf()));
                _waitingIds.Add(submitted.Id);
                return Option<Outcome>.None;
            case Expanded.Refused refused:
                return Keep(submitted, Refused(refused.Why));
            default:
                return Keep(submitted, Refused(Unknown(submitted.Command)));
        }
    }

    private Option<Outcome> Keep(Submitted submitted, Outcome outcome)
    {
        _waitingIds.Remove(submitted.Id);
        _answered.Add(submitted.Id, outcome);
        return Option.Some(outcome);
    }

    private void Resume(Submitted submitted, Command part, Result<Command, Reason> found)
    {
        submitted.Parts.Add(part, found);
        if (_applying)
        {
            _queue.Enqueue(submitted);
            return;
        }

        if (RunNow(submitted).TryGet(out var outcome))
            board.Events.Queue(new AnsweredEvent(new Ticket(submitted.Id), outcome));
    }

    private Result<Search, Reason> Schedule(Expanded.Waits waits, Action<Result<Command, Reason>> done)
    {
        var search = new Search(waits.Work, done);
        if (!scheduler.Schedule(WorkKind.Checking, search, Priority.Normal).TryGet(out var job))
            return Result<Search, Reason>.Error(Unknown(waits.Part));
        search.Job = job;
        return Result<Search, Reason>.Ok(search);
    }

    /// <summary>
    /// The command with every module-owned command in it replaced by its expansion, against the board as the actor sees
    /// it, reusing the parts already found. A kind with no expander, and a kind this version does not know, pass through
    /// for the board to refuse. A sliced part not yet found stops the expansion with the work that finds it.
    /// </summary>
    private Expanded Expand(Command command, CommandId id, Actor actor, Parts parts, int depth)
    {
        if (command is Command.Batch batch)
            return ExpandSteps(batch, id, actor, parts, depth);
        if (CommandExpanders.IsPrimitive(command.GetType()) || !expanders.For(command).TryGet(out var expander))
            return new Expanded.Ready(command);
        if (depth >= MaxExpansionDepth)
            return new Expanded.Refused(Reason.Of(
                ReasonCodes.TooLarge,
                LatticeIdentity.Board,
                ValueMap.Of("expansion-depth", depth.ToString()).With("command", command.GetType().Name).With("module", expander.Module.Value)));

        if (!parts.TryGet(command, out var found))
        {
            switch (expander.Begin(command, id, actor, board.ViewFor(actor), world))
            {
                case Begun.Now now:
                    found = now.Expansion;
                    parts.Add(command, found);
                    break;
                case Begun.Later later:
                    return new Expanded.Waits(command, later.Work);
                default:
                    return new Expanded.Refused(Unknown(command));
            }
        }

        return found.Match(
            next => Expand(next, id, actor, parts, depth + 1),
            static why => new Expanded.Refused(why));
    }

    private Expanded ExpandSteps(Command.Batch batch, CommandId id, Actor actor, Parts parts, int depth)
    {
        Command[]? steps = null;
        for (var i = 0; i < batch.Steps.Count; i++)
        {
            Command step;
            switch (Expand(batch.Steps[i], id, actor, parts, depth))
            {
                case Expanded.Ready ready:
                    step = ready.Command;
                    break;
                case Expanded.Refused refused:
                    var why = refused.Why;
                    var at = why.Facts.TryGetValue("step", out var inner) ? i + "." + inner : i.ToString();
                    return new Expanded.Refused(why with { Facts = why.Facts.With("step", at) });
                case Expanded.Waits waits:
                    return waits;
                default:
                    return new Expanded.Refused(Unknown(batch.Steps[i]));
            }

            if (steps is null && !ReferenceEquals(step, batch.Steps[i]))
            {
                steps = new Command[batch.Steps.Count];
                for (var j = 0; j < i; j++)
                    steps[j] = batch.Steps[j];
            }

            if (steps is not null)
                steps[i] = step;
        }

        return new Expanded.Ready(steps is null ? batch : new Command.Batch(Lists.Own(steps)));
    }

    private static Reason Unknown(Command command) =>
        Reason.Of(ReasonCodes.UnknownKind, LatticeIdentity.Board, ValueMap.Of("command", command.GetType().Name));

    private static Outcome Refused(Reason why) => new Outcome.Refused(ValueList.Of(new Refusal(why, ValueList<GhostId>.Empty)));

    private static Submission Answer(CommandId id, Outcome outcome) => new Submission.Answered(new Ticket(id), outcome);

    private static Submission Sent(CommandId id) => new Submission.Sent(new Ticket(id));

    /// <summary>How far a command's expansion got.</summary>
    private abstract record Expanded
    {
        private Expanded()
        {
        }

        public sealed record Ready(Command Command) : Expanded;

        public sealed record Refused(Reason Why) : Expanded;

        public sealed record Waits(Command Part, IExpansionWork Work) : Expanded;
    }

    /// <summary>The expansions found so far for the module-owned parts of one command, by part.</summary>
    private sealed class Parts
    {
        private Dictionary<Command, Result<Command, Reason>>? _found;

        public bool TryGet(Command part, out Result<Command, Reason> found)
        {
            found = default;
            return _found is not null && _found.TryGetValue(part, out found);
        }

        public void Add(Command part, Result<Command, Reason> found) => (_found ??= new Dictionary<Command, Result<Command, Reason>>())[part] = found;

        public void CopyFrom(Parts other)
        {
            if (other._found is null)
                return;
            foreach (var pair in other._found)
                Add(pair.Key, pair.Value);
        }
    }

    /// <summary>A submitted command that has not been answered yet.</summary>
    private sealed record Submitted(CommandId Id, Command Command, Actor Actor, Parts Parts);

    /// <summary>A preview's expansion at one revision.</summary>
    private sealed class Previewed(Revision at)
    {
        public Revision At { get; } = at;

        public Parts Parts { get; } = new();

        public Option<Result<Command, Reason>> Ready { get; set; }

        public SlicedJob? Searching { get; set; }
    }

    /// <summary>One sliced part's work as the scheduler steps it; hands the expansion on in the step that finds it.</summary>
    private sealed class Search(IExpansionWork work, Action<Result<Command, Reason>> done) : ISlicedWork
    {
        public SlicedJob? Job { get; set; }

        public StepOutcome Step()
        {
            var outcome = work.Step();
            if (outcome == StepOutcome.Done)
                done(work.Found);
            return outcome;
        }
    }
}
