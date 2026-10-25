using System;

namespace Lattice.Api;

/// <summary>The thread Lattice calls a consumer's code on. Values are fixed for the life of the contract.</summary>
public enum CallbackThread
{
    /// <summary>The Unity main thread, inside Lattice's frame time slices.</summary>
    Main = 1,

    /// <summary>Lattice's own worker thread, on a snapshot; never touch Unity or the game from it.</summary>
    Worker = 2,
}

/// <summary>
/// What a mod's support rule does to the pieces it claims. Values are fixed for the life of the contract. Several rules
/// on one piece all run, in registration order; Lattice runs what the mod declares and does not judge it. A rule that
/// times out or is switched off counts as not registered for that judgement, with a
/// <see cref="ReasonCodes.RuleTimedOut"/> or <see cref="ReasonCodes.RuleSwitchedOff"/> warning.
/// </summary>
public enum SupportMode
{
    /// <summary>
    /// Checked as well as the piece's own support group or, for a piece Lattice cannot classify, the game's own check:
    /// all must pass, and the supports are the union.
    /// </summary>
    Adds = 1,

    /// <summary>
    /// Answers alone for the pieces it claims, in place of the group or the game's check (a mod that changes the game's
    /// own placement rules for a piece). Any replacing rule drops the group; several replacing rules must all pass.
    /// </summary>
    Replaces = 2,
}

/// <summary>What a rule is given: one ghost and the world with the ghosts before it assumed built.</summary>
/// <param name="Ghost">The ghost to check.</param>
/// <param name="World">The world, with earlier ghosts assumed built; a snapshot when the rule runs on the worker.</param>
public sealed record RuleInput(Ghost Ghost, IWorldQuery World);

/// <summary>A rule's answer for one ghost.</summary>
public abstract record RuleVerdict
{
    private RuleVerdict() { }

    /// <summary>Nothing to say.</summary>
    public sealed record Pass : RuleVerdict;

    /// <summary>The ghost will fail.</summary>
    /// <param name="Why">The reason.</param>
    public sealed record Fail(Reason Why) : RuleVerdict;

    /// <summary>The ghost can be built, with a warning.</summary>
    /// <param name="Why">The reason.</param>
    public sealed record Warn(Reason Why) : RuleVerdict;
}

/// <summary>
/// A validation rule from another mod (a layout lint, for example), used by players and agents alike.
/// Called on <see cref="Thread"/> once per ghost validation checks, in registration order among rules.
/// A rule that throws is switched off for the rest of the world and reported once (<see cref="ReasonCodes.RuleSwitchedOff"/>).
/// Every rule gets Lattice's time limit per call, the same for all rules (2 ms in this version): a rule that has not
/// answered within it gives a <see cref="ReasonCodes.RuleTimedOut"/> finding and validation completes without it.
/// Results carry the revision they were computed for.
/// </summary>
/// <param name="Id">The rule's id; its reasons carry it.</param>
/// <param name="Check">The check.</param>
public sealed record RuleDescriptor(RuleId Id, Func<RuleInput, RuleVerdict> Check)
{
    /// <summary>The thread it runs on. Default: the worker.</summary>
    public CallbackThread Thread { get; init; } = CallbackThread.Worker;
}

/// <summary>What a support rule is given: a placement and the world with the ghosts before it assumed built.</summary>
/// <param name="At">The placement to support.</param>
/// <param name="World">The world, with earlier ghosts assumed built.</param>
public sealed record SupportInput(Placement At, IWorldQuery World);

/// <summary>A support rule's answer.</summary>
public abstract record SupportVerdict
{
    private SupportVerdict() { }

    /// <summary>Held up by these structures; any that are only assumed make the ghost wait for theirs.</summary>
    /// <param name="Supports">The supporting structures.</param>
    public sealed record SupportedBy(ValueList<StructureRef> Supports) : SupportVerdict;

    /// <summary>Nothing holds it up.</summary>
    /// <param name="Why">The reason (<see cref="ReasonCodes.NeedsSupport"/> or the mod's own).</param>
    public sealed record Unsupported(Reason Why) : SupportVerdict;
}

/// <summary>
/// A support rule for a group of pieces (a rocket mod's launch-mount rules, for example). Called on
/// <see cref="Thread"/> for every placement of a piece it applies to, in registration order. <see cref="AppliesTo"/>
/// is the only way a piece is bound to a support rule. A rule like any other: switched off if it throws, timed out
/// past Lattice's time limit, as for <see cref="RuleDescriptor"/>.
/// </summary>
/// <param name="Id">The rule's id.</param>
/// <param name="AppliesTo">Whether it governs a piece. Called once per piece per world.</param>
/// <param name="Check">The check.</param>
public sealed record SupportRuleDescriptor(RuleId Id, Func<PrefabKey, bool> AppliesTo, Func<SupportInput, SupportVerdict> Check)
{
    /// <summary>The thread it runs on. Default: the worker.</summary>
    public CallbackThread Thread { get; init; } = CallbackThread.Worker;

    /// <summary>Whether it adds to the piece's own support check or replaces it. Default: <see cref="SupportMode.Adds"/>.</summary>
    public SupportMode Mode { get; init; } = SupportMode.Adds;
}

/// <summary>What an analyzer is given: a set of ghosts and the world with them assumed built.</summary>
/// <param name="Ghosts">The ghosts.</param>
/// <param name="World">The world, with the ghosts assumed built.</param>
public sealed record AnalyzerInput(ValueList<Ghost> Ghosts, IWorldQuery World);

/// <summary>
/// An analyzer from another mod: facts about a set of ghosts shown in previews ("heat added to this room").
/// Called once per preview or analysis, in registration order among analyzers of the same thread, as a plain call:
/// Lattice does not time it, catch it or switch it off.
/// <list type="bullet">
/// <item>A <see cref="CallbackThread.Main"/> analyzer runs inside the preview's step in the frame time slice, or inside
/// <see cref="IAnalysis.Of"/>, with the world as it would be. Its exception leaves that step or call: the preview
/// step ends there, Lattice logs it as it logs any step that throws, and nothing is switched off.</item>
/// <item>A <see cref="CallbackThread.Worker"/> analyzer runs on a thread of its own, never Lattice's validation worker,
/// with a snapshot world. The previews and analyses waiting for its facts stay pending while it runs; validation and
/// every other answer go on. Its exception leaves <c>Frame</c> of Lattice's validation (logged by Lattice), and the
/// analysis that called it stays pending.</item>
/// </list>
/// Registered per world (<see cref="IRegistries"/>); a second registration of the same id is refused.
/// </summary>
/// <param name="Id">The analyzer's id; its facts carry it.</param>
/// <param name="Analyze">The analysis.</param>
public sealed record AnalyzerDescriptor(RuleId Id, Func<AnalyzerInput, ValueList<AnalyzerFact>> Analyze)
{
    /// <summary>The thread it runs on. Default: the main thread.</summary>
    public CallbackThread Thread { get; init; } = CallbackThread.Main;
}

/// <summary>What an access policy is asked when an actor changes something.</summary>
/// <param name="Actor">The actor.</param>
/// <param name="Command">The command (primitive, after expansion).</param>
/// <param name="Plan">The plan the change lands in.</param>
/// <param name="Ghost">The existing ghost it changes, if any.</param>
public sealed record AccessRequest(Actor Actor, Command Command, PlanId Plan, Option<Ghost> Ghost);

/// <summary>
/// The one installed access policy: may this actor see this ghost or plan, and may it make this change. The host asks
/// it in every apply; reads filter by it. Called on the main thread; the read questions are asked per ghost per read,
/// so they must not allocate. Without one, everything is allowed. Its failures are its own.
/// </summary>
/// <param name="MaySeeGhost">Whether an actor may see a ghost.</param>
/// <param name="MaySeePlan">Whether an actor may see a plan.</param>
/// <param name="MayChange">Whether an actor may make a change, or the reason not.</param>
public sealed record AccessPolicy(
    Func<Actor, Ghost, bool> MaySeeGhost,
    Func<Actor, Plan, bool> MaySeePlan,
    Func<AccessRequest, Result<Unit, Reason>> MayChange);

/// <summary>What a command expander is given.</summary>
/// <param name="Command">The command to expand.</param>
/// <param name="Id">The command's id.</param>
/// <param name="Actor">The actor.</param>
/// <param name="Board">The board as the actor sees it.</param>
/// <param name="World">The world.</param>
public sealed record ExpansionInput<TCommand>(TCommand Command, CommandId Id, Actor Actor, IGhostBoard Board, IWorldQuery World)
    where TCommand : Command;

/// <summary>
/// Expands a command a module owns into primitive commands (a <see cref="Command.Batch"/> of them, usually). Called on
/// the main thread by the host's apply and by previews, once per command. Its failures are its owner's.
/// </summary>
/// <param name="Module">The module that owns the command.</param>
/// <param name="Expand">The expansion, or the reason it cannot be done.</param>
public sealed record CommandExpander<TCommand>(ModuleName Module, Func<ExpansionInput<TCommand>, Result<Command, Reason>> Expand)
    where TCommand : Command;

/// <summary>Declares attached data under a key, so the inspector can name it and the host can size-check it.</summary>
/// <param name="Key">The key.</param>
/// <param name="Description">What the data is, for the inspector.</param>
public sealed record AttachmentDescriptor(AttachmentKey Key, DisplayName Description)
{
    /// <summary>The longest value the host accepts from a client. Default: 1024 characters.</summary>
    public int MaxLength { get; init; } = 1024;
}

/// <summary>A fact the bridge cannot read for a modded piece.</summary>
public abstract record CatalogueAddition
{
    private CatalogueAddition() { }

    /// <summary>A piece's mirror image is another piece.</summary>
    /// <param name="Piece">The piece.</param>
    /// <param name="Mirror">Its mirror image.</param>
    public sealed record MirrorPartner(PrefabKey Piece, PrefabKey Mirror) : CatalogueAddition;

    /// <summary>
    /// A piece belongs to a family: a modded piece Lattice leaves at <see cref="PieceFamilies.Other"/>, or any piece a
    /// mod wants in another. It comes before the family Lattice finds by type, for every style rule and filter that
    /// reads families, until the handle is disposed. The family is the piece's narrowest: its chain is that family and
    /// every family above it, so a piece put in <see cref="PieceFamilies.Window"/> is a wall too, and a piece put in a
    /// mod's own family has only that one. Refused (<c>unknown-piece</c>) for a piece the catalogue lacks,
    /// (<c>invalid-value</c>) for a <c>default</c> family, (<c>already-registered</c>) while the piece has one.
    /// Added in contract 0.2.
    /// </summary>
    /// <param name="Piece">The piece.</param>
    /// <param name="Family">Its family: one of <see cref="PieceFamilies"/> or a mod's own (<see cref="PieceFamily.Create"/>).</param>
    public sealed record InFamily(PrefabKey Piece, PieceFamily Family) : CatalogueAddition;
}
