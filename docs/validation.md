# Validation and build order

Lattice checks every ghost against the game's own placement rules and its own, in build order, with the ghosts before
it assumed built. It says why a ghost cannot be built, what it waits for, and what it warns about. Other mods add
rules of their own.

## Ask for findings

```csharp
var which = new Selector.InPlan(plan);
if (Must(lattice.Validation.Check(which)) is Answer<ValidationReport>.Ready { Value: var report })
    foreach (var finding in report.Findings)
        Show(finding);
// Pending: wait for Events.AnswersReady, then ask again.
```

- `Check` is refused only for a selector kind this Lattice does not know. Otherwise it is ready or pending.
- A ready `ValidationReport` holds one or more findings per ghost, **in build order**, for one board revision.
- The answer is cached per revision. After a change, Lattice re-checks the ghosts near the change and those that
  depend on them, on its worker thread.
- Plans are checked in the background as well, so plan state and the drawn look of a failing ghost appear without
  anyone asking.

## Findings

| Finding | Means |
|---|---|
| `Finding.Passes(Ghost)` | It can be built as things stand, with earlier ghosts assumed built. |
| `Finding.WaitsFor(Ghost, Others)` | It can be built once those ghosts are, in any plan. |
| `Finding.WillFail(Ghost, Why)` | It cannot be built. |
| `Finding.Warns(Ghost, Why)` | It can be built, with a warning. |

A later minor may add finding kinds. Skip and report one you do not know.

## What Lattice checks

The reason codes are in `ReasonCodes`. The main ones:

| Code | When |
|---|---|
| `game-refused` | The game's own placement check refused. Its message is in `Reason.GameMessage`. |
| `needs-support` | Nothing holds the piece up. When its support is a ghost earlier in the order, it waits instead. |
| `face-over-rating` | A swap's new face piece would sit under a pressure difference above its rating (a warning). |
| `different` | Something other than the ghost's piece stands in its spot (the ghost turns stale). |
| `target-gone` | The structure the ghost points at is gone. |
| `cycle` | Ghosts wait for each other round a loop, so none can be built first. Fact `ghosts`: the ids. |
| `no-removable-chain` | A loop stays: every way round it runs through something that may not go. |
| `would-bridge`, `would-split`, `contents-would-move`, `would-overload-when-on` | What a change would do to networks. |
| `rule-timed-out`, `rule-switched-off` | A mod's rule did not answer in time, or threw (below). |

Where the game's own check answers for a piece (an edge, a group Lattice does not model), it sees only what is
built: a ghost earlier in the order in the large cells around the piece makes the piece wait for it, whatever the
game says now.

Two clashes are refused when the ghost is placed, not found later: `cannot-share` (two pieces whose kinds never
share a small cell, a cable with a chute for example) and `ends-collide` (ends within 0.1 m of each other in the same
small cells).

**Build safety** warnings never refuse anything: `swap-on-air-room`, `cuts-power`, `gas-indoors`,
`oxidiser-outside`, `hot-gas`, `controls-unreachable`, `run-through-door`, `on-window`, `paint-indoors` and
`terrain-in-the-way`. Lattice warns and never digs terrain.

## Build order

The report's order is a build order that holds together: supports before what they hold, removals in an order that
keeps loops removable. Use it three ways:

- **For one plan:** `Plans.Facts(plan)` gives `BuildableNext`, the ghosts whose support exists and that nothing
  blocks. A builder starts there.
- **Across plans:** `WaitsFor` names the ghosts a ghost waits for, in any plan.
- **Inside one ghost:** its `StepPlan` carries `Constraint.After(ghost)` where a step must wait for another ghost
  ([steps and materials](steps-and-materials.md)).

A loop of ghosts waiting for each other fails with `cycle`, and its `ghosts` fact lists the ids.

## Add a rule

A rule judges one ghost at a time and answers pass, fail or warn. A layout lint, a server's house rules, an add-on's
own constraints: they all show up as findings, for players and agents alike.

```csharp
var mod = Must(ModId.Create("mymod"));
var id = Must(RuleId.Create(mod, "too-high"));
var tooHigh = Must(ReasonCode.Create(mod, "too-high"));

var rule = new RuleDescriptor(id, input =>
    input.Ghost.Intent is Intent.Build { Target.At.Y: > 1000 }   // decimetres: above 100 m
        ? new RuleVerdict.Warn(Reason.Of(tooHigh, id, ValueMap.Of("limit-m", "100")))
        : new RuleVerdict.Pass());

var handle = Must(lattice.Registries.Register(rule));   // refused already-registered for a second rule with this id
```

What Lattice promises a rule, and asks of it:

- **Thread.** `Thread` defaults to `CallbackThread.Worker`: Lattice's worker thread, with `input.World` a snapshot of
  the world with the ghosts before this one assumed built. Never touch Unity or the game from it. Set
  `Thread = CallbackThread.Main` for a rule that must read the game; it runs inside Lattice's frame slices.
- **Time.** Every rule gets the same time limit per call (2 ms in this version). A rule that has not answered by then
  gives a `rule-timed-out` finding, and validation completes without it.
- **Failure.** A rule that throws is switched off for the rest of the world and reported once (`rule-switched-off`).
- **Order.** Rules run in registration order, once per ghost validation checks.
- **Lifetime.** The registration ends with the world, or when you dispose the handle.

This is the one place Lattice judges other mods' code: a rule's verdict decides what players see, so a broken rule
must not hold validation up. Lattice does not time, catch or switch off any other callback.

`input.World.AssumedFrom(structure)` tells you whether a structure the rule sees is a ghost assumed built.

## Add a support rule

A support rule says what holds a group of pieces up: a modded launch mount, a piece that hangs from a ceiling.

```csharp
var mount = Must(RuleId.Create(mod, "on-launch-mount"));
var noMount = Must(ReasonCode.Create(mod, "needs-launch-mount"));

var support = new SupportRuleDescriptor(mount, IsRocketPart, input =>
{
    var below = input.World.At(input.At.At with { Y = input.At.At.Y - 20 });   // the large cell below
    foreach (var structure in below)
        if (structure.Ref.Piece == launchMount)
            return new SupportVerdict.SupportedBy(ValueList.Of(structure.Ref));
    return new SupportVerdict.Unsupported(Reason.Of(noMount, mount));
})
{
    Mode = SupportMode.Adds,
};
Must(lattice.Registries.Register(support));
```

- `AppliesTo` binds pieces to the rule. Lattice calls it once per piece per world.
- `SupportMode.Adds` (the default) checks the rule as well as the piece's own support. `SupportMode.Replaces`
  answers alone for the pieces it claims, for a mod that changes the game's own placement rules for them.
- A support that is only assumed (a ghost before this one) makes the ghost wait for it.
- Time limit, thread and failure are as for a rule.

## Analyzers

An analyzer adds facts to previews, not findings ("heat added to this room"). See
[previews and analysis](previews-and-analysis.md).
