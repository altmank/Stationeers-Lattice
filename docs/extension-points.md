# Extension points

Every place Lattice calls your code, and what it promises and asks there. Lattice sits under your mod and does not
police it: what your code throws leaves the call that ran it, and Lattice does not time it or switch it off. The one
exception is validation rules (below), because a rule's verdict decides what everyone sees.

Every registration belongs to one world. It returns an `IDisposable` (or a handle) that ends it early, and it ends
with the world. Register when `LatticeSystem.Started` is raised. Registering an id twice is refused
`already-registered`.

## Code Lattice calls

| You give | Registered with | Called | Thread | Time | If it throws |
|---|---|---|---|---|---|
| `RuleDescriptor.Check` | `Registries.Register` | Once per ghost validation checks, in registration order | Worker by default (a snapshot world), or main | 2 ms per call; past it: a `rule-timed-out` finding, validation completes without it | Switched off for the world, reported once (`rule-switched-off`) |
| `SupportRuleDescriptor.AppliesTo` | `Registries.Register` | Once per piece per world | As `Thread` | As a rule | As a rule |
| `SupportRuleDescriptor.Check` | `Registries.Register` | For every placement of a piece it applies to, in registration order | Worker by default, or main | As a rule | As a rule |
| `AnalyzerDescriptor.Analyze` | `Registries.Register` | Once per preview or analysis | Main by default (in the preview's slice or in `Analysis.Of`), or a thread of its own | Not timed | Leaves the preview step or call; on its own thread, the analysis stays pending |
| `AccessPolicy.MaySeeGhost`, `MaySeePlan` | `Registries.Install` | Per ghost or plan, per read | Main | Must not allocate | Leaves the read |
| `AccessPolicy.MayChange` | `Registries.Install` | For every primitive command the host applies, after expansion | Main | In the apply | Leaves `Submit` |
| `CommandExpander.Expand` | `Registries.Register` | Once per command, by the host's apply and by previews | Main | In the call | Leaves `Submit` or the preview |
| `SlicedWorkDescriptor.Step` | `Slices.Schedule` | As often as the frame's drawing budget allows, high priority first | Main | Keep each step small and even; a slice stops when the next step would pass the budget, and the first step of a frame always runs | Skips the rest of this frame's consumer work; Lattice's own work is unaffected; your work steps again next frame |
| Event handlers (`Events.*`) | `+=` | Once per event, at most one burst per frame, in revision order | Main | In Lattice's frame | Leaves the raise: your mod's to handle |
| `LatticeSystem.Started`, `LatticeDrawing.Started` | `+=` | Once per world, in subscription order | Main | At world start | Your mod's to handle |
| `IGhostStyles.ClipChanged` | `+=` | At most once a frame | Main | | Your mod's to handle |
| `RendererClaimDescriptor.Draw` | `IGhostDrawing.Claim` | Once a frame, after Lattice's own submission | Main | In the frame | Leaves the callback; later claims skipped that frame; the claim stays |

Rules for every callback:

- **No nesting.** A command you submit from inside a callback or handler is queued and applied after the current
  one.
- **Worker means worker.** Code on `CallbackThread.Worker` never touches Unity or the game. It gets a snapshot world.
- **Values only.** Lattice hands you immutable values. Holding them is safe; they do not change under you.

## Data Lattice reads

These registrations hold no code. Lattice evaluates them itself, so they cost what Lattice says and cannot fail at
run time.

| You give | Registered with | For |
|---|---|---|
| `AttachmentDescriptor` | `Registries.Register` | Declares a key for attached data, so the inspector names it and the host checks its length. |
| `CatalogueAddition.MirrorPartner`, `InFamily` | `Registries.Register` | Facts Lattice cannot read from a modded piece. |
| `LimitPlanSize(max)` | `Registries` | A cap on ghosts per plan. |
| `ExecutorInfo` | `Executors.Register` | A builder; you then call its handle. |
| `StyleLayerDescriptor`, sets, highlights, drafts, clips | `Styles` | How things look to the local viewer. |
| Materials, text styles, icons, decorations, piece looks | `IGhostDrawing` | Unity assets Lattice draws with. |

## Calls back into Lattice

A few handles are calls you make to tell Lattice something changed:

- `IPolicyHandle.Changed()`: your policy's answers changed with no board change.
- `IExecutorHandle.Report`, `Renew`, `Release`: a builder's status and claims.
- `IStyleSet`, `IStructureSet`: the sets your logic fills.

## Command expanders

Lattice's own modules register an expander for each command kind they own, before `LatticeSystem.Started`. A mod
can register one only for a kind whose module is off, as a stand-in. An expander turns its command into primitive
commands, usually a `Command.Batch`, or refuses with a reason.
