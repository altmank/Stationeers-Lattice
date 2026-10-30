# Changelog

## 0.19.1

Fixed:
- **A joining player sees the host's progress.** A client's board came without each plan's done count, so a player
  who joined after work was built saw fewer done ghosts in `PlanFacts.Progress`, and a plan built to the end not as
  complete. The join now carries each plan's done count, the default plan included, so every seat gives the same
  facts and states from the first frame; ghosts done after the join count once, as before.
- **A small preview no longer waits behind a big plan.** Every planned build ghost asked two of Lattice's own
  validation rules on the main thread (the snap rule, and the swap face rule, which only ever answers swaps), a few
  dozen a frame, and a preview was not answered until all of them were: after a 5,000-ghost plan a run's preview
  waited hundreds of frames. The snap rule now runs as each ghost is judged on Lattice's worker, and the face rule is
  asked about swaps only, with the same verdicts and reasons. A preview's validation is judged on its own ghosts and
  the ghosts whose changes they read, its questions answered ahead of the board's, so it no longer waits for an
  unrelated plan's checks (a mod's main-thread rule over every ghost, the game's placement check). A run's preview
  over 5,000 unrelated planned ghosts answers in as many frames as over an empty board, settled or right after the
  plan is placed.
- **A route goes around planned devices.** The route search read a planned piece only in its own cell, so it walked
  through the other cells of a planned device (a stacker's upper cell, where its chute ports are) and the laid run
  was then refused there. A planned piece now counts in every cell it will fill, as a built one does, for the route
  search and for a drawn run's cell rules alike: a route between two planned stackers goes around both, as it does
  between built ones, and a cable, pipe or chute route goes around a planned device in its way.

## 0.19.0

Added:
- **A cell's world point.** `Cell.ToWorld()` gives the point in metres a piece placed at the cell stands at, the
  inverse of `Cell.FromWorld` for a snapped point.
- **A turn's rotation.** `Turn.ToRotation()` in `Lattice.Api.Unity` gives the Unity rotation the game gives a piece
  placed with the turn.
- **Builder reason codes.** `needs-items`, `out-of-reach`, `no-power` and `blocked`, with `Reason.NeedsItems`,
  `Reason.OutOfReach`, `Reason.NoPower`, `Reason.Blocked` and `Reason.BlockedByPlayer` to make them, so every builder
  says why it waits in the same words.
- **Item names.** `IPieceCatalogue.ItemName(item)` gives any item's display name in the player's language: kits, tools,
  ingots and everything a bill names.
- Contract 0.19, which only adds: a mod written for 0.18 sees the same answers.

## 0.18.0

Added:
- **Each piece's snap.** `PieceEntry.Snap` holds how the game snaps a piece on each world axis, read from its prefab:
  the grid's own for most pieces, and for the gas and liquid rocket engines and the umbilicals x and z on frame
  centres only.
  `PieceSnap.Holds(cell)` tells a planner whether the game will build the piece there.
- **Off-snap ghosts fail.** A ghost placed off its piece's snap fails `off-snap` and is never built there; it stays
  where it was placed. A run, route or transform that would put a piece off its snap says so with the same code.
- **The cells a piece fills.** `IPieceCatalogue.CellsAt(placement)` gives the cells a piece fills at a cell and turn,
  so a planner sees how far a piece such as the small insulated in-line tank hangs below its cell.
- The catalogue reads each prefab's own grid offset, where it assumed the small grid's for every small piece.
- Contract 0.18, which only adds: a mod written for 0.17 sees the same answers, except that a ghost off its piece's
  snap now fails `off-snap`, and a run, route or transform that would put a piece off its snap says so.

## 0.17.1

Fixed:
- **A drawn run through a built device fails there.** A run judged built pieces only through the game's placement
  check, so where that check accepts everything (a `Lattice.Testing` world), a cable run straight through a built
  battery fit. A run now judges the built pieces in its cells by the small-cell clash rule, as it judges ghosts. The
  first cell that clashes is the run's `FirstFailure`, `cannot-share` or `ends-collide`, naming the built piece in
  `with` and its id in `structure`. Routes already went round them; a run ending on a device's port still joins it.
- `Lattice.Testing`: `TestPiece.WithCollision` and the consumer guide give the game's collision bits. A test device
  with only the devices bit (4) lets a cable through its cell; the game's small devices are 23.

## 0.17.0

Added:
- **Reasons print their facts.** `Reason.ToString()` gives one line, for example
  `invalid-value from lattice.sync: problem=not-the-host-board revision=12`, with the facts sorted by key and escaped
  as the written forms escape them, and the game's message last. Before, a logged reason showed only how many facts it
  had.
- `ReasonCodes.OutcomeLost` (`outcome-lost`): a client's command whose outcome the host no longer has after a resync
  (fact `command`).

Fixed:
- **A client resyncs after a change set it cannot take.** One refused change set left a client's board behind the
  host's for the rest of the session: it refused every later set, so pieces built on the host kept their ghosts on
  the client. Now the client logs the drop once, with its facts, and asks the host for the whole board again.
  Commands still waiting get their answers after the new board, an applied one with its full change set, so every
  ticket gets exactly one `Answered`.
- **One event handler that throws no longer skips the others.** A throwing `Changed` handler skipped every handler
  after it for that change set, sync's included, so the change never reached the clients. Each handler now runs on
  its own; one that throws is logged with its exception and skipped.
- A change set that removes a ghost and adds it again under the same id now reaches a client with both changes.
- A host-only module on a client is off with the reason `host-only` and logs `host-only; off on a client`, instead
  of reading as a missing module. A drawing module on a dedicated server logs `drawing-only; off in batch mode`.
- Contract 0.17, which only adds: a mod written for 0.16 sees the same answers, except that a host-only module's
  commands on a client are refused `host-only` instead of `module-missing`.

## 0.16.0

- **A batch's preview carries its run reports.** A planner that sends a run with another step in one `Command.Batch`
  (a run and a paint step, say) got no `Preview.Run`, so it had no counts, no mark for the first failing or
  rule-breaking cell, and no cells. Now `Preview.Run` is the report of the batch's first run or route step, and the
  new `Preview.Runs` holds one report per run or route step in step order, nested batches flattened. Each is the
  report that step gives as the batch lays it, against the earlier steps' ghosts, with its costs for its own pieces.
- Contract 0.16, which only adds: a mod written for 0.15 sees the same answers for everything but a batch, whose
  `Run` was none before.

## 0.15.0

- **A planned network from one of its pieces.** `Selector.Joined(cell)` picks the build and swap ghosts of cable, pipe
  and chute pieces joined end to end to the planned run piece at the cell, through junctions, by that piece's network
  kinds. It stops at devices and at built pieces, and reads ghosts only. A planner can widen a planned run to its whole
  network before any of it is built, where `Selector.Network` needs a built piece and `Selector.RunThrough` stops at
  junctions. Written form `s.joined`; Lattice 0.14 refuses a command holding it as an unknown kind.
- Contract 0.15, which only adds: a mod written for 0.14 sees the same answers.

## 0.14.1

- **A run that leaves a line's end turns it.** Starting or ending a run on the last piece of a cable, pipe or chute
  line and leaving it side-on now makes that piece a corner. Before, the line's open end was kept, so cable and pipe
  got a tee with an open stub, and a chute run failed `no-piece` there because chutes have no tee. A face that joins
  a neighbour is never dropped: leaving mid-line is still a tee.
- No API change.

## 0.14.0

- **Routes can keep off lines.** With `RouteToggles.KeepOffLines`, a route joins existing cables, pipes and chutes
  only at its ends: it never runs along or across a line between them, so its start no longer slides along a line
  beside it. A route that cannot keep off answers `no-route` with the new cause `along-line` in `stopped`.
- **Drawn runs can be judged.** `RunRequest.Rules` judges a drawn run's cells by a route's rules (doors and windows
  clear, ports free, no unsafe joins, keep off lines), its two ends exempt. The run is still laid as drawn; the first
  cell that breaks a rule is the new `RunReport.FirstBreak`, reason `rule-broken` with facts `cell` and `rule`, beside
  `FirstFailure`, which keeps meaning that the run cannot be laid there.
- Contract 0.14, which only adds: a mod written for 0.13 sees the same answers.

## 0.13.1

- **Lattice enabled twice runs once.** With two copies in the mod list (the Workshop item and a local copy), both
  started Lattice: two plugins, and the second one's log wrongly said sync was off. Now the first copy to start runs
  Lattice, and any later copy does nothing and logs one line naming both copies, their versions and which one runs.
  Keep one copy of Lattice enabled.

## 0.13.0

- **Planned pieces swap in place.** `GhostEdit.Swap`, sent with `Command.Change`, changes the piece of planned
  ghosts as `PlanSwap` changes built ones: a planned cable run dragged with heavy cable becomes heavy cable, each ghost
  keeping its id, plan, label, tint, groups, attachments and maker. What it waits for, its bill, steps and looks follow
  the new piece. Ghosts that cannot swap are left as they are; the edit is refused only when none could.
- **One drag over built and planned pieces** is a `Batch` of a `PlanSwap` and a `Change` with `GhostEdit.Swap`: one
  change set, one undo.
- **Changed: `PlanSwap` keeps a structure's existing swap ghost.** A picked structure that already had a planned swap
  got a new swap ghost in its place, with a new id, in the command's plan, with its label, tint, groups and attachments
  lost. Now that ghost is changed in place and stays in its plan; send `MoveToPlan` to move it.
- Undoing a change that changed a ghost's piece puts its shape back too.
- `GhostEdit` says that a later minor may add cases; an edit Lattice does not know is refused `unknown-kind`, as before.
- Contract 0.13, which only adds: a mod written for 0.12 sees the same answers, but for the `PlanSwap` change above.

## 0.12.1

- **A route that finds no route says why it stopped.** The new fact `stopped` names, for each side of the nearest cell
  the route could not enter, what kept it out: what stands there, no frame, a door or window, a free port, another
  network, an earlier leg, or the edge of the search box. It is set for routes and for each leg of a pinned route.

## 0.12.0

- **Runs and routes can end on a built device's port.** Name the port with `PortEnd.OnStructure`, as a ghost's port
  is named. The run reports the piece that joins it, and it is refused before anything is laid when the device has
  moved or turned, the kit cannot join the port, or the device is gone. A device taken down while the route is being
  found refuses the route, so nothing is laid to nothing.
- **Pin a route through cells.** `RouteRequest.Via` lists cells the route passes through, in order. Each leg is found
  in turn, never crosses an earlier one, and the whole route is laid as one run. A leg with no route says which leg.
- **One highlight over ghosts and built structures.** `HighlightTargets.PickedAndBuilt` lights the ghosts a selector
  picks and the built structures a structure selector picks with one call, one handle and one timer: a whole network
  is `new PickedAndBuilt(new Selector.Network(n), new StructureSelector.Network(n))`. A refusal of either selector
  refuses the highlight.
- A highlight target case this Lattice does not know is refused `unknown-kind`; it lit nothing before.
- Contract 0.12, which only adds: a mod written for 0.11 sees the same answers.

## 0.11.0

- **See-through strength now reaches the full look.** Above 0.5, faces seen square-on show more strongly, and at 1
  the hidden part shows like the visible ghost; 0.5 and below are unchanged.
- **`SeeThroughLook.Through.Opacity`** sets the hidden part's own opacity, in place of the fill's, so x-ray can be
  stronger than the visible ghost and a look with no fill can show through.
- The see-through material's `_LatticeSeeThrough` carries a third value, the share of the strength a face seen
  square-on shows, for override shaders.

## 0.10.5

- **A device on a planned wall or frame waits for it.** A wall-mounted piece planned on the face of a frame or wall
  ghost now waits for that ghost instead of failing; on a built frame it passes. One placed on no face is refused by
  Lattice itself, with the game's own reason, without asking the game.
- Support checks around small pieces reach the large cells next to them, so support from a neighbouring large cell
  is seen.
- The in-game test harness gains its render, look and timing modes, with scenes for ports, flow and waits.

## 0.10.4

- **Ports, open ends, flow arrows and waits links are drawn.** The style properties `Ports`, `OpenEnds`, `FlowArrows`
  and `WaitsLinks` were resolved but not drawn; now they are. Ports show as a bar from the cell out to its face, open
  ends (ports nothing joins) as a block, flow as arrows into a device's inputs and out of its outputs and along its
  runs, and waits as a link to each ghost a ghost waits for. They draw in the look's own colours and allocate nothing
  in a steady frame.
- **Hiding ghosts beyond the clip works in game.** A look set to hide what lies past the clip plane now does so; two
  values reached the shaders the wrong way and were ignored.
- **Templates of active plans on clients** are documented and tested: a client builds them from its own board, the
  same as the host's.

## 0.10.3

- **Box selections are fast on large bases.** Selecting built structures or ghosts in a box looks only at the part of
  the base the box covers. On a 36,000-structure base a 10 m box answers in 0.1-0.3 ms instead of 5 ms, and a 20 m
  box in 0.6 ms instead of 34 ms. The answers are the same as before.

## 0.10.2

- **Test fakes that keep compiling.** `Lattice.Testing.Stubs` has a base stub for every contract interface, every
  member virtual. A mod's fake that inherits one keeps compiling when a later minor adds a member. A minor adds to
  Lattice's interfaces for callers; it promises nothing to code that implements them, so fake them through the stubs.
- **The finished-plans list follows the access policy.** With a policy installed, `Plans.Finished` lists only the
  plans the viewer may see.

## 0.10.1

- **Clients see who works on which ghost.** Executors still run on the host; a client now reads their claims and
  status through the same `IExecutors`, raises the same `StatusChanged` and `Blocked` events, and shows status
  looks, a message after the host. A claim's lease is shown on the client's own game clock; the host alone decides
  when it lapses. A client that joins gets every live claim with the board. Host and clients must run the same
  Lattice version, as before: the game refuses a join between two versions.

## 0.10.0

- **Templates can be saved and shared.** A template has a public written form: write one to text and read it back on
  any seat or world. A damaged or unknown form is refused, saying which line and why.
- **Place a template into a plan you already have.** The placement is one change in that plan and undoes like any
  other.
- **Access policies see the plan.** A request to change a plan's ghosts, reopening a finished plan included, carries
  the plan's record, so a mod's policy can apply that plan's own access setting.
- **Finished plans are the host's.** The docs now say so: a client lists none, and its requests to repair, take down
  or reopen one are applied by the host.
- Contract 0.10, which only adds: a mod written for 0.9 sees the same answers.

## 0.9.0

- **Route choices.** A route can go up or down first, or across first, and can keep to frames and walls instead of
  open air. A route can carry branches that join it with tees. Runs and routes can lay single pieces only, or match
  the grade of what they join.
- **Split a long straight on purpose.** A new command replans a long straight as single pieces, for when one piece
  must be swapped or removed on its own.
- **A face piece's plane.** The catalogue answers which way a placed wall, window or floor faces, so a planner can lay
  a line or a rectangle of them in that plane.
- Contract 0.9, which only adds: a mod written for 0.8 sees the same answers.

## 0.8.1

- **A refused move, turn or mirror names exactly the ghosts in the way.** A mirror whose selection holds pieces with
  no mirror image is still refused as a whole, and now each problem comes as its own refusal listing exactly its
  ghosts, so a mod can resubmit without them in one round trip.

## 0.8.0

- **Every placed piece's ports.** The catalogue answers the ports of any piece at a cell and turn: a ghost, a draft, a
  preview's ghost or a built structure. Each port has the game's own index, its networks, its role, the cell it
  leaves from and the cell a joining piece takes. A tool that plans pipe or cable runs between ghosts no longer needs
  a real build to find them. Contract 0.8, which only adds.
- **Runs and routes can end at a ghost's port.** The run's last piece joins that port, and the run's report says
  which port ends joined. A port that does not match the run's network is refused with its own reason.
- **A batch's steps see the earlier steps of the same batch.** A trunk and its branches in one batch now meet in tees,
  as one change that undoes together; before, a branch replaced the trunk's pieces where they met. Batches whose
  steps do not touch are unchanged.
- The window family's description now says what decides it: a see-through face that seals air once built.

## 0.7.1

- **Validation stops asking the game about ghosts that are already gone.** A ghost built, removed or finished while
  its placement check waited is passed over, and asked again if it comes back. On a large base a plan's completion
  settles in 6 frames instead of 17, with no frame over 3 ms.

## 0.7.0

- **Windows are their own family, under walls.** A see-through face that seals air once built (the game's windows,
  the shuttered window included) is in the new `window` family, which sits under `wall`: a style rule for walls
  still covers windows, and a rule for windows alone is now possible. Floor gratings stay walls. Contract 0.7, which
  only adds: a mod written for 0.6 sees the same answers.
- **Match a face by how it lies.** A style rule can match face pieces laid flat (floors and ceilings) or upright,
  whatever their family.
- **The catalogue answers a piece's family chain** (a window: window, then wall) and a family's parent.
- Public docs for mod authors: what Lattice does, how to build on it, and a guide per feature.

## 0.6.0

- **First release of Lattice, the ghost core library other mods build on.** It holds planned structures (ghosts) and
  answers everything about them: whether they can be built, in what order, the steps and materials each needs, and
  what a change would look like before it is made. It does nothing on its own: install it when a mod you use asks
  for it.
- **Plans are saved with the world.** They are kept in a `lattice` folder beside the game's saves, never inside a
  save, and each save loads the plans as they were when it was written.
- **Multiplayer.** The host and every player need Lattice at the same version. The host decides; players see the
  host's plans as they change.
- **Ghosts are drawn in game**, with a styling system other mods can extend with looks of their own.
