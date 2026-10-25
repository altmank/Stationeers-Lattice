# Multiplayer

The host decides what happens to a plan. Every player sees the host's plans as they change. Your mod writes the same
code for single player, a listen host, a client and a dedicated server.

## Who does what

| Seat | Board | Commands | Drawing |
|---|---|---|---|
| Single player, listen host | The local player's view | Applied at once | Yes |
| Client | A copy, kept in step by the host's change sets | Sent to the host | Yes |
| Dedicated server | Lattice's own actor, which sees everything | Applied at once | No: `LatticeDrawing.Current` is none |

Every player and the host need Lattice at the same version.

## Commands from a client

1. `Commands.Submit` sends the command to the host and returns `Submission.Sent` with a `Ticket`.
2. The host checks it against its limits and the access policy, expands it and applies it, or refuses it.
3. `Events.Answered(ticket, outcome)` arrives on the client with the outcome.
4. The change set arrives as `Events.Changed`, on every seat.

Write handlers that work from events, not from the `Submit` return value: the same code then works on every seat. The
host's answer wins over a client's preview.

The same `CommandId` sent twice applies once, so a client can resend after a dropped connection.

## What runs where

- **Every seat:** the board, the catalogue, validation, steps, bills, previews, analysis, selectors and styles.
  Previews and validation on a client run against its local copy of the board and world.
- **Host only:** applying commands, matching built pieces to ghosts, saving, finished plans, the inspector and
  executors. Clients see their results as change sets and status events.
- **Drawing seats only:** the `render` module. On a dedicated server or a headless client, style registrations are
  accepted and held, and `Pick` and `Explain` still answer, but nothing is drawn.

The catalogue reads the same on every seat, because every peer holds the same prefabs. Only display names differ, in
each player's language. Use `PrefabKey` or `PrefabName` as an identity, never `Name`.

## Limits on client commands

The host refuses a command from a client that is past these limits:

| Limit | Value | Refused |
|---|---|---|
| Ghosts placed or named in one command | 10,000 | `too-many-ghosts` |
| Selector nesting | 8 deep | `selector-too-deep` |
| Batch nesting | 4 deep | `batch-too-deep` |
| Cells | within ±1,000,000 dm on each axis | `out-of-range` |
| Attached text | the key's `AttachmentDescriptor.MaxLength`, 1024 characters by default | `text-too-long` |

A host can also cap ghosts per plan with `Registries.LimitPlanSize`.

## Who may see and change what

Without an access policy, everyone sees and changes everything. A server mod that wants rules installs one policy:

```csharp
var policy = new AccessPolicy(
    MaySeeGhost: (actor, ghost) => ghost.Visibility is not Visibility.OwnerOnly || ghost.PlacedBy == actor,
    MaySeePlan: (actor, plan) => plan.Visibility is not Visibility.OwnerOnly || plan.Owner == actor,
    MayChange: request => IsAdmin(request.Actor) || request.Ghost.Match(g => g.PlacedBy == request.Actor, () => true)
        ? Result<Unit, Reason>.Ok(Unit.Value)
        : Result<Unit, Reason>.Error(Reason.Of(ReasonCodes.NotAllowed, Must(RuleId.Create(mod, "owners")))));

var handle = Must(lattice.Registries.Install(policy));   // refused while another policy is installed
```

- The host asks `MayChange` for every primitive command, after expansion, with the plan it lands in and the ghost it
  changes.
- Reads filter by `MaySeeGhost` and `MaySeePlan`, per ghost per read. They run on the main thread and must not
  allocate.
- `Visibility` (`Everyone`, `OwnerOnly`, `Team(name)`) is data on each ghost and plan. It means something only when
  the installed policy reads it. The policy decides who is in a team.
- When the policy's answers change with no board change (a player joins a team, a server setting changes), call
  `handle.Changed()`. Lattice drops what it cached and asks again.
- Disposing the handle restores allow-everything.
- `Events.Changed` carries every change the process holds, whatever the policy lets a viewer see. To show a viewer
  what it may see, read through `lattice.Board` or `Board.As(viewer)`.

## What never syncs

Style state is the local viewer's: layers, sets, highlights, drafts and the clip never sync and never save. What every
player should see alike belongs in ghost data a style reads: tint, label, groups and attachments.
