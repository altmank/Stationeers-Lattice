# Steps, materials and builders

Lattice says exactly what must happen in the game to reach a ghost's target, and what it costs. It never does it: a
player's hands, a builder mod or an agent does, and the world tells Lattice when the ghost is done.

## Steps for one ghost

```csharp
if (lattice.Steps.For(ghost).TryGet(out var plan))
    for (var i = 0; i < plan.Steps.Count; i++)
        Do(plan.Steps[i], plan);
```

`Steps.For` works from the world as it is now. It is refused when the ghost is unknown or cannot be built. A
`StepPlan` holds the steps in order and the `Constraint`s on carrying them out.

| Step | The builder |
|---|---|
| `Step.PlaceKit(kit, option, uses, at)` | Places a kit as the piece at that option, using that many kit items, at a placement. |
| `Step.Advance(on, tool, material)` | Does one build step on a structure with a tool, using a material if one is named. |
| `Step.TakeBack(on, tool, returns)` | Does one take-apart step and gets those items back. |
| `Step.Merge(into, becomes, offhandTool, extra)` | Merges a kit into a standing structure so it becomes another piece (a straight becoming a T). |
| `Step.Paint(on, colour)`, `Step.Rename(on, to)`, `Step.SetValue(on, to)` | Finishes a structure. |

`Advance`, `Paint`, `Rename` and `SetValue` name their structure with a `StepTarget`:

- `StepTarget.Standing(structure)`: a structure standing now.
- `StepTarget.PlacedBy(step)`: the structure an earlier `PlaceKit` or `Merge` of the same plan places. The builder
  learns its id when that step is done.

`TakeBack.On` and `Merge.Into` are always a `StructureRef` standing now.

A swap's plan, for example, starts with the take-apart steps of the old structure and goes on with the kit and build
steps of the new one.

| Constraint | Means |
|---|---|
| `Constraint.OnePipeJoinPerTick` | At most one join of pipe networks per game tick. |
| `Constraint.NetworkOff(network)` | The network must be off while these steps run. |
| `Constraint.After(ghost)` | Only after another ghost is done. |

A later minor may add step, target and constraint kinds. A builder skips and reports what it does not know, and
leaves that ghost alone.

## Materials and tools

```csharp
var bill = Must(lattice.Bills.For(new Selector.InPlan(plan)));
```

A `Bill` has the items `Needed`, the items `GivenBack` by take-apart steps, the `Net` per item (positive needed,
negative given back) and the `Tools` used. It is linear in the ghosts and cached per revision. `Plans.Facts(plan)`
carries the same bill for what is still to build, as `Cost`.

Items are `ItemKey`s (the game's prefab hash), with counts in `ItemQuantity` and changes in `ItemDelta`.

## Builders: claims and status

An executor is anything that carries out steps: a drone, an automated builder, an agent. Executors live on the host.

```csharp
var builder = Must(lattice.Executors.Register(new ExecutorInfo(
    Must(ExecutorId.Create(mod, "drone-1")), Must(DisplayName.Create("Drone 1")))));

if (builder.Claim(ghost, Must(Lease.Create(30))).TryGet(out var claim))
{
    builder.Report(ghost, new ExecutorStatus.Working(1, plan.Steps.Count));
    // ... carry out the steps; renew before the lease runs out:
    claim = Must(builder.Renew(claim).MapError(refused => refused.Why));
}
```

- **One executor per ghost.** `Claim` is refused `claimed` (with the holder) while another executor holds the ghost.
- **Leases** run in game time. A claim that is not renewed lapses, and the ghost is free again. `Renew` is refused
  `lease-expired` once it has lapsed.
- **Status** is what the executor says about a ghost it holds: `Working(step, of)`, `Waiting(why)` or `Paused`.
  Players and agents read it with `Executors.StatusOf(ghost)`; `Events.StatusChanged` says when it changes; styles can
  draw it.
- **Never "done".** An executor does not report completion. When the world matches the ghost, Lattice removes it and
  raises `Events.Completed`. Release your claim then.
- **Release** a claim with `Release(claim)`. Disposing the handle releases every claim it holds. The handle ends with
  the world.
- `Executors.Register` is refused on a client (`host-only`).

See the builder recipe in [building a layer](building-a-layer.md#a-builder).
