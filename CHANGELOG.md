# Changelog

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
