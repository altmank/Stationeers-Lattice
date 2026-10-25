using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Catalogue;
using Lattice.Core.Ports;
using Lattice.Core.Validation;

namespace Lattice.Core.Support;

/// <summary>What support says about one placement.</summary>
internal abstract record SupportOutcome
{
    private SupportOutcome() { }

    /// <summary>It is held up: by these structures, and once these ghosts stand (none: it can be built now).</summary>
    /// <param name="By">The supporting structures, real or assumed.</param>
    /// <param name="WaitsFor">The ghosts it waits for.</param>
    public sealed record Holds(ValueList<StructureRef> By, ValueList<GhostId> WaitsFor) : SupportOutcome;

    /// <summary>Nothing holds it up, or the game refuses it.</summary>
    /// <param name="Why">The reason.</param>
    public sealed record Fails(Reason Why) : SupportOutcome;

    /// <summary>The game's answer for the placement is needed and not there yet.</summary>
    /// <param name="Ask">The placement to ask the game about.</param>
    public sealed record Pending(Placement Ask) : SupportOutcome;
}

/// <summary>
/// A placement's support outcome, the warnings that came with it (a mod's rule timed out or switched off), the
/// ghosts the game's answer turns on when the game's check answers for it (none when Lattice's rules answer), and
/// whether a mod's <see cref="SupportMode.Replaces"/> rule answered in place of the group and the game's check.
/// </summary>
internal sealed record SupportAnswer(SupportOutcome Outcome, ValueList<Reason> Warnings, ValueList<GhostId> TurnsOn)
{
    public bool Replaced { get; init; }
}

/// <summary>
/// Judges one placement. The rules that run, in this order:
/// <list type="number">
/// <item>Every mod's rule that claims the piece, in registration order (<see cref="SupportMode"/>).</item>
/// <item>Unless a claiming rule <see cref="SupportMode.Replaces"/>: the piece's census group (found by type, so a
/// modded subclass inherits it); a group Lattice does not model, a piece the census does not know
/// (<see cref="SupportGroups.AskGame"/>) and a mod's group with no rule registered go to the game's own check.</item>
/// </list>
/// All must hold; the supports are the union. A mod's rule that times out or is switched off counts as not
/// registered, with a warning. The game's check sees the built world only, so where the answer depends on ghosts
/// assumed built (near the piece, <see cref="Near"/>), the placement waits for those ghosts whatever the game says now.
/// </summary>
internal sealed class SupportJudge
{
    private readonly SupportRules _rules;
    private readonly Dictionary<RuleId, ICensusRule> _census;

    public SupportJudge(SupportRules rules, SupportGeometry geometry)
    {
        _rules = rules;
        Geometry = geometry;
        _census = new Dictionary<RuleId, ICensusRule>(8);
        Add(new FrameBelowRule(geometry));
        Add(new FrameOrGroundRule(geometry));
        Add(new WallMountRule(geometry));
        Add(new OnRunRule(geometry, SupportGroups.OnCable, Slot.Cable, "MustMountToCable", "CannotPlaceOnBrokenCable"));
        Add(new OnRunRule(geometry, SupportGroups.OnPipe, Slot.Pipe, "MustMountToPipe", "CannotPlaceOnBurstPipe"));
        Add(new OutsideRule(geometry));
        Add(new ElevatorShaftRule(geometry));
    }

    public SupportRules Rules => _rules;

    public SupportGeometry Geometry { get; }

    /// <summary>The census groups Lattice answers itself; every other group goes to the game's check.</summary>
    public IReadOnlyCollection<RuleId> Modelled => _census.Keys;

    /// <summary>
    /// Judges the placement. With <paramref name="report"/> false (a first look to find what waits on what), a mod's
    /// rule switched off now is reported by the next reporting judgement instead.
    /// </summary>
    public SupportAnswer Judge(SupportCase @case, bool report = true)
    {
        var warnings = new List<Reason>();
        var by = new List<StructureRef>();
        var replaced = false;
        Option<Reason> failed = Option<Reason>.None;
        foreach (var entry in _rules.Current)
        {
            var claim = _rules.Claims(entry, @case.At.Piece);
            if (claim == Claim.TimedOut)
            {
                warnings.Add(Reason.Of(ReasonCodes.RuleTimedOut, entry.Rule.Id));
                continue;
            }

            if (claim != Claim.Yes)
            {
                if (report && _rules.ReportSwitchOff(entry, @case.Ghost))
                    warnings.Add(Reason.Of(ReasonCodes.RuleSwitchedOff, entry.Rule.Id));
                continue;
            }

            if (!Ask(@case, entry).TryGet(out var answer))
                continue;
            switch (answer)
            {
                case Guarded<SupportVerdict>.Answered { Value: SupportVerdict.SupportedBy supported }:
                    replaced |= entry.Mode == SupportMode.Replaces;
                    AddAll(by, supported.Supports);
                    break;
                case Guarded<SupportVerdict>.Answered { Value: SupportVerdict.Unsupported unsupported }:
                    replaced |= entry.Mode == SupportMode.Replaces;
                    if (failed.IsNone)
                        failed = Option.Some(unsupported.Why);
                    break;
                case Guarded<SupportVerdict>.TimedOut:
                    warnings.Add(Reason.Of(ReasonCodes.RuleTimedOut, entry.Rule.Id));
                    break;
                case Guarded<SupportVerdict>.SwitchedOff:
                    if (report && _rules.ReportSwitchOff(entry, @case.Ghost))
                        warnings.Add(Reason.Of(ReasonCodes.RuleSwitchedOff, entry.Rule.Id));
                    break;
                default:
                    warnings.Add(Reason.Of(ReasonCodes.UnknownKind, entry.Rule.Id));
                    break;
            }
        }

        var waits = new List<GhostId>();
        var turnsOn = ValueList<GhostId>.Empty;
        if (!replaced)
        {
            switch (Census(@case))
            {
                case Judgement.Supported supported:
                    AddAll(by, supported.By);
                    break;
                case Judgement.Unsupported unsupported:
                    failed = Option.Some(unsupported.Why);
                    break;
                case Judgement.Uncertain uncertain:
                    turnsOn = uncertain.Until;
                    if (!AskGame(@case, turnsOn, waits, ref failed))
                        return new SupportAnswer(new SupportOutcome.Pending(@case.At), ValueList.From(warnings), turnsOn) { Replaced = replaced };
                    break;
                default:
                    turnsOn = Near(@case, Geometry);
                    if (!AskGame(@case, turnsOn, waits, ref failed))
                        return new SupportAnswer(new SupportOutcome.Pending(@case.At), ValueList.From(warnings), turnsOn) { Replaced = replaced };
                    break;
            }
        }

        if (failed.TryGet(out var why))
            return new SupportAnswer(new SupportOutcome.Fails(why), ValueList.From(warnings), turnsOn) { Replaced = replaced };
        foreach (var structure in by)
            if (@case.World.AssumedFrom(structure).TryGet(out var ghost) && !waits.Contains(ghost))
                waits.Add(ghost);
        return new SupportAnswer(new SupportOutcome.Holds(ValueList.From(by), ValueList.From(waits)), ValueList.From(warnings), turnsOn) { Replaced = replaced };
    }

    /// <summary>
    /// The ghosts assumed so far whose changes lie in the large cell holding the piece's centre or one of the 26 large
    /// cells around it, for a small piece as for a large one: everywhere its support can stand, so a frame ghost behind
    /// a device on its face counts. A heuristic for "could change the game's answer", not a game rule.
    /// </summary>
    public static ValueList<GhostId> Near(SupportCase @case, SupportGeometry geometry)
    {
        const int step = 20;
        var large = geometry.LargeCellAt(geometry.CentreOf(@case.At, @case.Piece));
        return @case.World.ChangesIn(
            new Cell(large.X - step, large.Y - step, large.Z - step),
            new Cell(large.X + step, large.Y + step, large.Z + step),
            @case.Ghost);
    }

    // A worker rule is called under the guard (its reads count only when it answers in time); a main-thread rule's
    // answer comes from the facts the main thread fills, none while it has not run.
    private Option<Guarded<SupportVerdict>> Ask(SupportCase @case, SupportRules.Entry entry)
    {
        if (entry.Rule.Thread == CallbackThread.Main)
            return @case.World.MainSupport(_rules, entry, @case.Ghost, new SupportInput(@case.At, @case.World));
        var call = @case.World.Fork();
        var answer = _rules.Check(entry, new SupportInput(@case.At, call));
        if (answer is Guarded<SupportVerdict>.Answered)
            @case.World.Log.Merge(call.Log);
        return Option.Some(answer);
    }

    private Judgement Census(SupportCase @case)
    {
        if (!@case.Piece.SupportGroup.TryGet(out var group))
            return Judgement.Nothing;
        return _census.TryGetValue(group, out var rule) ? rule.Judge(@case) : Judgement.Game;
    }

    // False when the game's answer is not there yet.
    private bool AskGame(SupportCase @case, ValueList<GhostId> until, List<GhostId> waits, ref Option<Reason> failed)
    {
        if (!@case.World.Game(@case.At).TryGet(out var answer))
            return false;
        foreach (var ghost in until)
            if (!waits.Contains(ghost))
                waits.Add(ghost);
        if (answer is GameAnswer.Refuses refuses && until.Count == 0 && failed.IsNone)
            failed = Option.Some(new Reason(
                ReasonCodes.GameRefused,
                ValueMap<string, string>.Of("group", Group(@case).Value),
                refuses.Message,
                Group(@case)));
        return true;
    }

    private static RuleId Group(SupportCase @case) => @case.Piece.SupportGroup.Reduce(SupportGroups.AskGame);

    private void Add(ICensusRule rule) => _census.Add(rule.Group, rule);

    private static void AddAll(List<StructureRef> into, ValueList<StructureRef> from)
    {
        foreach (var structure in from)
            if (!into.Contains(structure))
                into.Add(structure);
    }
}
