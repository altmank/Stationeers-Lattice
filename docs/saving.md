# Saving, plan files and history

Lattice saves plans itself. Your mod saves nothing of the board: what you attach to ghosts and plans is saved with
them.

## Where plans live

- A `lattice` folder beside the game's saves holds a folder per world and a file per plan. Nothing is written inside
  a game save, so saves load the same with or without Lattice.
- A plan file is a log: each applied change set adds one line to every plan it touches. Lines are only appended,
  never rewritten. They are written in the frame's saving slice, a few at a time, each flushed to disk as it is
  written, and the rest at once when the game saves.
- When the game saves, Lattice checkpoints every active plan and records, for that save, the line each plan is at.
- Saving runs on the host only (module `saving`). Clients write no plan files.
- Ids are never reused within a world, across every file and branch.

## Loading an older save

Each save loads the plans as they were when it was written: every plan opens at the line the save remembers. Lines
after it stay in the file. A change made after loading an older save starts a **branch** that follows that line, so
no history is lost.

When a world finishes loading, `Events.Loaded` is raised once with a `LoadReport`, and `Plans.LastLoad` keeps it:

| Field | What |
|---|---|
| `Record` | `SaveRecord.Found`: the save had a record and every plan was rebuilt to it. `SaveRecord.Missing`: the save was made before Lattice was installed, was copied in, or lost its record, so every plan opened at its newest line. |
| `Ahead` | The plans whose files hold changes beyond the save, with how many. |

## After a crash

When the game crashes between saves, the newest save is behind its plan files. Lattice never decides for the player:

1. `LoadReport.Ahead` (or `Plans.AheadOfSave(plan)`) says which plans have newer changes, and how many.
2. A UI offers to catch up.
3. `Command.CatchUp(plan)` moves the plan forward to those changes.

## Finished plans

A finished plan's file moves to an archive with an entry in an index. `Plans.Finished` reads the index once per world,
without opening the files. Loading a save from before a plan finished reopens it. See
[plans and ghosts](plans-and-ghosts.md#finished-plans).

## History: the inspector

`lattice.Inspector` reads plan files, so its cost is linear in the file's lines. It runs on the host (module
`inspector`).

| Call | Gives |
|---|---|
| `Story(ghostId)` | A `GhostStory`: every line that touched the ghost, across branches and plans, ghosts no longer on the board included. |
| `Story(planId)` | A `PlanStory`: the plan's lines up to where it is loaded, and every `Branch` of its file with the saves on it. |
| `Export(planId)` | The plan's change log as written lines, to attach to a bug report. |

Each `HistoryEntry` names its line, the line it follows, the command and actor, the time and the line as written.

## What is saved and what is not

| Saved | Not saved |
|---|---|
| Ghosts, plans and their history | Style layers, sets, drafts, highlights and the clip: they are the local viewer's, and your mod sets them up again each world |
| `Ghost.Attached` and `Plan.HigherLayerData` | Registrations (rules, analyzers, executors, policies, claims): they end with the world |
| Labels, tints and groups | Executor claims and status |

Declare each attachment key you use with `Registries.Register(new AttachmentDescriptor(key, description))`. The
inspector then names it, and the host checks its length on commands from clients.

## Removing Lattice

Removing Lattice leaves saves loading the same, without ghosts. Put it back and the plans come back. Deleting the
`lattice` folder drops them for good.
