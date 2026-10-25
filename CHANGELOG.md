# Changelog

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
