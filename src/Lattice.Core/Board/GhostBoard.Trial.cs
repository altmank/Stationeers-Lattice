using System;
using Lattice.Api;

namespace Lattice.Core.Board;

internal sealed partial class GhostBoard
{
    /// <summary>
    /// What applying a primitive command would do, without doing it: the same default-value check, preparation and
    /// access-policy questions as <see cref="Apply"/>, and the change set a commit would make now (its revision, ids
    /// and change-set id), or the refusals. Nothing changes: no revision, history, id counters or events. The default
    /// command id stands in for the id the command will be sent with. Cost: as <see cref="Apply"/> without the commit.
    /// </summary>
    public Result<Trial, ValueList<Refusal>> Try(Command command, Actor actor)
    {
        if (DefaultValues.In(command, actor).TryGet(out var defaultValue))
            return Refuse(new Refusal(defaultValue, ValueList<GhostId>.Empty));

        var pending = new Pending(new Revision(Revision.Value + 1), _time.Now);
        var prepared = Prepare(command, actor, pending);
        if (!prepared.TryGet(out var ready))
            return Refuse(prepared.ErrorOf());

        foreach (var (request, ghosts) in ready.Asks)
            if (Policies.MayChange(request).TryGetError(out var why))
                return Refuse(new Refusal(why, ghosts));

        var changes = ChangesOf(ready, new ChangeSetId(_nextChangeSet), default, actor);
        return Result<Trial, ValueList<Refusal>>.Ok(new Trial(
            changes,
            () => Revision.Value + 1 == ready.At.Value,
            () =>
            {
                Stage(ready, byCommand: true);
                Epoch++;
            },
            () =>
            {
                Unstage(ready);
                Epoch++;
            }));
    }

    private static Result<Trial, ValueList<Refusal>> Refuse(Refusal refusal) =>
        Result<Trial, ValueList<Refusal>>.Error(ValueList.Of(refusal));

    /// <summary>
    /// A change the board would make, prepared at one revision. <see cref="While{T}"/> shows it to readers for the
    /// length of one call; staging it at a later revision is a bug. Main thread.
    /// </summary>
    internal sealed class Trial
    {
        private readonly Func<bool> _current;
        private readonly Action _stage;
        private readonly Action _unstage;
        private bool _staged;

        internal Trial(ChangeSet changes, Func<bool> current, Action stage, Action unstage)
        {
            Changes = changes;
            _current = current;
            _stage = stage;
            _unstage = unstage;
        }

        /// <summary>The change set a commit would make.</summary>
        public ChangeSet Changes { get; }

        /// <summary>
        /// Runs <paramref name="read"/> with the change in the board's index, so every read (views, selectors, the
        /// planned world) sees the board as it would be, then takes it out again, whatever the read does. Views built
        /// before or during the call are dropped (the epoch moves on both sides). Not re-entrant.
        /// </summary>
        public T While<T>(Func<T> read)
        {
            if (_staged)
                throw new InvalidOperationException("A board trial is already staged.");
            if (!_current())
                throw new InvalidOperationException($"A board trial prepared for revision {Changes.Revision.Value} cannot be staged after the board moved.");

            _staged = true;
            _stage();
            try
            {
                return read();
            }
            finally
            {
                _unstage();
                _staged = false;
            }
        }
    }
}
