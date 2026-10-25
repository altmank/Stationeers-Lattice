# Changelog

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
