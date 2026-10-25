# Lattice docs

For mod authors building on Lattice. Read them in this order; each stands on the ones before it.

| Doc | What |
|---|---|
| [Setup](consumer-guide.md) | A complete minimal mod; reference Lattice, declare the dependency, get the system each world, versions, threads, multiplayer in short, testing. |
| [Concepts](concepts.md) | Ghosts, plans, the board, cells and spots, pieces, commands and change sets, answers, events, values, the frame. |
| [Building a layer](building-a-layer.md) | Worked recipes: the game side, a planner UI, a builder, an agent, an add-on, a test. |
| [Plans and ghosts](plans-and-ghosts.md) | Make, read, edit and finish ghosts and plans; runs, routes and swaps; done tracking. |
| [Validation and build order](validation.md) | Findings, what Lattice checks, build order, adding a rule or a support rule. |
| [Steps, materials and builders](steps-and-materials.md) | Steps per ghost, bills, executor claims and status. |
| [Previews and analysis](previews-and-analysis.md) | What a change would do; effects on networks, rooms and gas; adding an analyzer. |
| [Move, turn, mirror and copy](transforms-and-templates.md) | Transforms and templates. |
| [Saving and history](saving.md) | Plan files, older saves, crashes, finished plans, the inspector. |
| [Multiplayer](multiplayer.md) | Host and clients, client limits, the access policy, what never syncs. |
| [Pieces and the catalogue](catalogue.md) | Pieces, kits, families, modded pieces, cells and grids. |
| [Styles](styles.md) | How ghosts look: layers, matches, properties, palettes, the starter library, sets, drafts, the clip, picking. |
| [Drawing](drawing.md) | The Unity side: materials and their contract, fonts, icons, meshes, renderer claims, draw order, a faded world. |
| [Extension points](extension-points.md) | Every place Lattice calls your code: thread, time, and what happens if it throws. |

Every public type and member also carries XML docs in `Lattice.Api.xml` and `Lattice.Api.Unity.xml`, beside the dlls.
