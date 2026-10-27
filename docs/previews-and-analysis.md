# Previews and analysis

Lattice tells you what a change would do before anyone makes it, and what a set of ghosts would do to the world once
built. Both run on clients too, against the local copy of the board and world, and never change anything.

## Preview a command

```csharp
var answer = lattice.Preview.Of(command, player);
if (answer is Answer<Result<Preview, ValueList<Refusal>>>.Ready ready)
{
    if (ready.Value.TryGet(out var preview))
        Show(preview);
    else if (ready.Value.TryGetError(out var refusals))
        ShowRefusals(refusals);
}
// Pending: a big change is worked out in time slices. Ask again after Events.AnswersReady.
```

A `Preview` holds:

| Field | What |
|---|---|
| `WouldChange` | The change set the command would make: ghosts added, changed and removed. |
| `Findings` | Validation of the new and changed ghosts in context. |
| `Bill` | Materials and tools for the new and changed ghosts. |
| `Effects` | What they would do to networks, rooms and gas (below). |
| `Run` | For `PlanRun` and `PlanRoute`: a `RunReport` with the cells, the pieces placed (longest first), the first cell that fails and why, the cost, and the cost if every cell were a single piece. `PortEnds` (contract 0.8) names, for each port end, the port and the piece that joins it; none when no piece of the kit can join it there, and then `FirstFailure` names that cell. For a route with branches, the cells and pieces of the main route and every branch. `LaidWith` (contract 0.9) names the kit the run was laid with when matching the grade chose another. `FirstBreak` (contract 0.14) names the first drawn cell that breaks the request's `Rules` (`rule-broken`, facts `cell` and `rule`); the run lays there all the same, and a route's report has none. |

The refusals sit inside the answer because a command's refusals are known only once it is expanded, which can take
several frames.

Use a preview for the live readout while a player drags (a run's length and cost, the first failing cell), for an
agent's dry run before it commits, and to draw the would-be result with `IDrafts.Show(preview)`
([styles](styles.md#drafts)).

## Analyse ghosts

```csharp
if (Must(lattice.Analysis.Of(new Selector.InPlan(plan))) is Answer<AnalysisReport>.Ready { Value: var report })
{
    foreach (var effect in report.Rooms)
        if (effect is RoomEffect.Opens opens)
            Warn(opens.Room);
}
```

An `AnalysisReport` lists:

| List | Cases |
|---|---|
| `Networks` | `Joins(a, b)`, `Splits(network)`, `Load(network, before, after, rating)` for power draw against the weakest cable. |
| `Rooms` | `Opens(room)`, `Seals(cells)`, `Resizes(room, before, after)`. |
| `Gas` | `Releases(at, into, gas)` from a pipe or device into a room or outside, `Pollutes(room, amount)` from painting. |
| `Facts` | Facts registered analyzers add (below). |

Each list is a union that may grow. Skip a case you do not know.

The refusal of `Analysis.Of` is outside the answer, because the selection is known at once.

## Add an analyzer

An analyzer adds facts about a set of ghosts, shown in previews: "heat added to this room", "this hydroponics bay
needs 2 kW". It never refuses anything.

```csharp
var heat = Must(RuleId.Create(mod, "heat"));
var added = Must(ReasonCode.Create(mod, "heat-added"));

var analyzer = new AnalyzerDescriptor(heat, input =>
{
    var watts = 0.0;
    foreach (var ghost in input.Ghosts)
        watts += HeatOf(ghost);
    return watts == 0
        ? ValueList<AnalyzerFact>.Empty
        : ValueList.Of(new AnalyzerFact(heat, added, ValueMap.Of("watts", watts.ToString("0"))));
});
Must(lattice.Registries.Register(analyzer));
```

- `Thread` defaults to `CallbackThread.Main`: the analyzer runs inside the preview's step in the frame slice, or
  inside `Analysis.Of`, with the world as it would be.
- `CallbackThread.Worker` runs it on a thread of its own with a snapshot world. Previews waiting for its facts stay
  pending while it runs. Everything else goes on.
- An analyzer is a plain call. Lattice does not time it, catch it or switch it off. What it throws leaves the step or
  call that ran it.

## Safety warnings

Build safety warnings come with validation and plan facts, not analysis: a swap that takes out a wall holding air,
removals that cut power to life support, a vent that lets a pipe's gas into a room, and others. They warn and never
refuse. See [validation](validation.md#what-lattice-checks).
