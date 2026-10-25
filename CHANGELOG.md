# Changelog

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
