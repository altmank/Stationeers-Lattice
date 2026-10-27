# Changelog

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
