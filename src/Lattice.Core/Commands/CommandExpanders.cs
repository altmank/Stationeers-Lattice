using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Slices;

namespace Lattice.Core.Commands;

/// <summary>
/// The command-expander registry: one expander per module-owned command kind, keyed by the command's exact type.
/// Primitive kinds (the ones the board applies itself) take none. Expanders are plain calls: what one throws leaves the
/// call that ran it. Main thread; constant cost per lookup.
/// </summary>
internal sealed class CommandExpanders
{
    private readonly Dictionary<Type, Registration> _byKind = new();

    /// <summary>Whether the board applies this kind itself: Place, Remove, Change, Move, CreatePlan, EditPlan, MoveToPlan, Revert, Batch, FinishPlan, CatchUp.</summary>
    public static bool IsPrimitive(Type kind) =>
        kind == typeof(Command.Place) || kind == typeof(Command.Remove) || kind == typeof(Command.Change) || kind == typeof(Command.Move) ||
        kind == typeof(Command.CreatePlan) || kind == typeof(Command.EditPlan) || kind == typeof(Command.MoveToPlan) ||
        kind == typeof(Command.Revert) || kind == typeof(Command.Batch) || kind == typeof(Command.FinishPlan) ||
        kind == typeof(Command.CatchUp);

    /// <summary>
    /// Adds the expander for a kind. Refused <see cref="ReasonCodes.InvalidValue"/> for a primitive kind or the command
    /// base, <see cref="ReasonCodes.AlreadyRegistered"/> while the kind has one. Disposing ends this registration only.
    /// </summary>
    public Result<IDisposable, Reason> Register<TCommand>(CommandExpander<TCommand> expander) where TCommand : Command =>
        Add(typeof(TCommand), new Typed<TCommand>(this, expander));

    /// <summary>
    /// Adds a sliced expander for a kind: its expansion is worked out over several frames in the checking budget. The
    /// same refusals as <see cref="Register{TCommand}"/>; a kind takes one expander, plain or sliced.
    /// </summary>
    public Result<IDisposable, Reason> RegisterSliced<TCommand>(SlicedExpander<TCommand> expander) where TCommand : Command =>
        Add(typeof(TCommand), new Sliced<TCommand>(this, expander));

    private Result<IDisposable, Reason> Add(Type kind, Registration registration)
    {
        if (kind.IsAbstract || IsPrimitive(kind))
            return Result<IDisposable, Reason>.Error(Reason.Of(
                ReasonCodes.InvalidValue, LatticeIdentity.Board, ValueMap.Of("command", kind.Name).With("problem", "primitive")));
        if (_byKind.ContainsKey(kind))
            return Result<IDisposable, Reason>.Error(Reason.Of(ReasonCodes.AlreadyRegistered, LatticeIdentity.Board, ValueMap.Of("command", kind.Name)));
        _byKind.Add(kind, registration);
        return Result<IDisposable, Reason>.Ok(registration);
    }

    /// <summary>The expander registered for the command's kind, if any.</summary>
    public Option<Registration> For(Command command) =>
        _byKind.TryGetValue(command.GetType(), out var registration) ? Option.Some(registration) : Option<Registration>.None;

    private void Remove(Type kind, Registration registration)
    {
        if (_byKind.TryGetValue(kind, out var current) && ReferenceEquals(current, registration))
            _byKind.Remove(kind);
    }

    /// <summary>One registered expander; disposing it frees its kind unless a newer registration holds it.</summary>
    internal abstract class Registration : IDisposable
    {
        public abstract ModuleName Module { get; }

        /// <summary>The expansion now, or the work that finds it.</summary>
        public abstract Begun Begin(Command command, CommandId id, Actor actor, IGhostBoard board, IWorldQuery world);

        public abstract void Dispose();
    }

    private sealed class Typed<TCommand>(CommandExpanders owner, CommandExpander<TCommand> expander) : Registration
        where TCommand : Command
    {
        public override ModuleName Module => expander.Module;

        public override Begun Begin(Command command, CommandId id, Actor actor, IGhostBoard board, IWorldQuery world) =>
            new Begun.Now(expander.Expand(new ExpansionInput<TCommand>((TCommand)command, id, actor, board, world)));

        public override void Dispose() => owner.Remove(typeof(TCommand), this);
    }

    private sealed class Sliced<TCommand>(CommandExpanders owner, SlicedExpander<TCommand> expander) : Registration
        where TCommand : Command
    {
        public override ModuleName Module => expander.Module;

        public override Begun Begin(Command command, CommandId id, Actor actor, IGhostBoard board, IWorldQuery world) =>
            new Begun.Later(expander.Start(new ExpansionInput<TCommand>((TCommand)command, id, actor, board, world)));

        public override void Dispose() => owner.Remove(typeof(TCommand), this);
    }
}

/// <summary>How an expansion begins: done at once, or as work over several frames.</summary>
internal abstract record Begun
{
    private Begun()
    {
    }

    /// <summary>The expansion, or why there is none.</summary>
    public sealed record Now(Result<Command, Reason> Expansion) : Begun;

    /// <summary>The work that finds the expansion; nothing has been stepped yet.</summary>
    public sealed record Later(IExpansionWork Work) : Begun;
}

/// <summary>
/// Work that finds a command's expansion a small step at a time, keeping its own place between steps. Main thread;
/// <see cref="Found"/> is read once a step has returned <see cref="StepOutcome.Done"/>.
/// </summary>
internal interface IExpansionWork : ISlicedWork
{
    /// <summary>The expansion, or why there is none.</summary>
    Result<Command, Reason> Found { get; }
}

/// <summary>
/// Work whose refusal names the ghosts it concerns: Lattice's own modules answer one refusal per problem, each listing
/// its ghosts in <see cref="Refusal.Ghosts"/>; its <see cref="IExpansionWork.Found"/> error is the first of them. A
/// mod's expander answers one <see cref="Reason"/>, which reaches the caller with no ghosts.
/// </summary>
internal interface IGhostRefusals
{
    /// <summary>The refusals once <see cref="IExpansionWork.Found"/> is an error; empty otherwise.</summary>
    ValueList<Refusal> Refusals { get; }
}

/// <summary>
/// Expands a module-owned command over several frames: <paramref name="Start"/> sets the work up from the input and
/// does no search itself. Lattice's own modules use it for searches longer than a frame; on the host a command with
/// such a step is answered once its work finishes.
/// </summary>
/// <param name="Module">The module that owns the command.</param>
/// <param name="Start">Sets up the work for one command.</param>
internal sealed record SlicedExpander<TCommand>(ModuleName Module, Func<ExpansionInput<TCommand>, IExpansionWork> Start)
    where TCommand : Command;
