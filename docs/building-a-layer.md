# Building a layer on Lattice

Lattice holds plans and answers questions about them. Everything a player sees or does with plans is a **layer** on
top: a planner UI, a builder, an agent, a small add-on. This guide shows each kind, the calls in order, and what to
watch for. Read [concepts](concepts.md) first and set up your project with the [setup guide](consumer-guide.md).

Every sketch here compiles against `Lattice.Api` alone; the game-side code also compiles against the game. The
sketches are not in the repository, but its tests show the same shapes:

- `tests/Lattice.Testing.Tests/ConsumerShapedFlows.cs` runs a planner's flow (a run dragged out, previewed, placed,
  validated and swapped) and an agent's flow (a network swap dry-run and submitted, with its own rule) against
  Lattice's real core in a `TestWorld`.
- `tests/Lattice.Consumer.Tests` writes the main flows (a client's cable run, an agent's swap, a builder's claims and
  steps), a sliced-overlay sketch and an add-on sketch against the contract alone, over a stub system.

The sketches use one helper, imported with `using static`. In files that also reference the game, alias the three
names the game shares with Lattice ([setup](consumer-guide.md#a-minimal-mod)).

```csharp
internal static class Results
{
    public static T Must<T>(Result<T, Reason> result) where T : notnull =>
        result.Match(static value => value, static why => throw new InvalidOperationException(why.Code.Value));
}
```

## What kind of layer

| You want | Build | Main calls |
|---|---|---|
| Players place, see and edit plans | [A planner UI](#a-planner-ui) | `Styles` (drafts, sets, picking), `Preview`, `Commands`, `Events` |
| Ghosts turn into structures without a player's hands | [A builder](#a-builder) | `Plans.Facts`, `Executors`, `Steps`, `Events.Completed` |
| A tool or AI reads and changes plans | [An agent](#an-agent) | `Board.As`, `Preview`, `Commands`, `Events.Answered` |
| A rule, a look or a fact on top of what is there | [An add-on](#an-add-on) | `Registries`, `Styles` |

Several layers live side by side in one game. A planner UI places ghosts, a builder builds them, an agent plans
upgrades, and each sees the others' work as change sets. None of them needs to know the others.

## The shape of every layer

One object per world: take the running system, check the contract, subscribe and register, and drop everything when
the world ends.

```csharp
public sealed class MyLayer
{
    private readonly ModId _mod = Must(ModId.Create("mymod"));
    private readonly List<IDisposable> _registrations = new();
    private IGhostSystem? _lattice;

    /// <summary>Call from your plugin's Awake.</summary>
    public void Start()
    {
        if (LatticeSystem.Current.TryGet(out var running))
            OnWorld(running);              // a world was already running when your mod loaded
        LatticeSystem.Started += OnWorld;  // every later world
    }

    private void OnWorld(IGhostSystem lattice)
    {
        if (lattice.Version.Major != ApiVersion.ContractMajor || lattice.Version.Minor < 3)
            return;                        // built for another contract: log it and stay out of this world
        _lattice = lattice;
        lattice.Events.Changed += OnChanged;
        lattice.Events.BoardReset += OnReset;
        // _registrations.Add(Must(lattice.Registries.Register(...)));
    }

    private void OnChanged(ChangeSet changes)
    {
        foreach (var ghost in changes.Added) { /* draw, index, react */ }
    }

    private void OnReset()
    {
        // Every answer you hold is stale. When the world has ended, Current is already none.
        if (LatticeSystem.Current.IsSome || _lattice is null)
            return;
        _lattice.Events.Changed -= OnChanged;
        _lattice.Events.BoardReset -= OnReset;
        _registrations.Clear();            // they ended with the world
        _lattice = null;
    }
}
```

- Ask `LatticeSystem.Current` when you need it, never at load, so load order between mods does not matter.
- Check `Version` before you use a member a later minor added (its XML doc says "Added in contract 0.x"), and
  `lattice.Modules` for a module you depend on.
- Everything you register ends with the world. After the world ends, drop your handles; do not dispose them.
- `BoardReset` is also raised when the board is replaced inside a world. `Current` is still set then: drop cached
  answers and keep going.

## The game side

Lattice speaks in its own values. Your mod turns the game's into them: the camera and mouse into a pick ray, the
local player into an actor, the construction cursor into a piece, a point and a turn. This code compiles against the
game's `Assembly-CSharp` and Unity:

```csharp
internal static class GameSide
{
    /// <summary>The local player as Lattice's actor.</summary>
    public static Actor LocalPlayer() => new Actor.Player(new ClientId(NetworkManager.LocalClientId));

    public static WorldPoint Point(Vector3 v) => new(v.x, v.y, v.z);

    /// <summary>A ray from the camera through the mouse, as a pick ray.</summary>
    public static Result<PickRay, Reason> MouseRay(Camera camera, float reach)
    {
        var ray = camera.ScreenPointToRay(Input.mousePosition);
        return PickRay.Create(Point(ray.origin), new WorldVector(ray.direction.x, ray.direction.y, ray.direction.z), reach);
    }

    /// <summary>The turn a game rotation makes: where it sends +X and +Y, snapped to the grid axes.</summary>
    public static Result<Turn, Reason> TurnOf(Quaternion rotation) =>
        Turn.Of(Snap(rotation * Vector3.right), Snap(rotation * Vector3.up));

    private static SignedAxis Snap(Vector3 v)
    {
        var (x, y, z) = (Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));
        if (x >= y && x >= z)
            return new SignedAxis(Axis.X, v.x > 0 ? AxisSign.Plus : AxisSign.Minus);
        if (y >= z)
            return new SignedAxis(Axis.Y, v.y > 0 ? AxisSign.Plus : AxisSign.Minus);
        return new SignedAxis(Axis.Z, v.z > 0 ? AxisSign.Plus : AxisSign.Minus);
    }

    /// <summary>What the player holds at the cursor: the piece, where and in which turn.</summary>
    public static bool Held(out PrefabKey piece, out WorldPoint at, out Turn turn)
    {
        piece = default;
        at = default;
        turn = default;
        var cursor = InventoryManager.ConstructionCursor;
        if (cursor == null || !TurnOf(InventoryManager.CurrentRotation).TryGet(out turn))
            return false;
        piece = new PrefabKey(cursor.PrefabHash);
        at = Point(cursor.transform.position);
        return true;
    }

    /// <summary>The kit in the active hand and the option it shows, for a kit with several pieces.</summary>
    public static bool HeldKit(out KitKey kit, out KitOption option)
    {
        kit = default;
        option = default;
        var held = InventoryManager.ActiveHandSlot?.Get<MultiConstructor>();
        if (held == null || !KitOption.Create(held.LastSelectedIndex).TryGet(out option))
            return false;
        kit = new KitKey(held.PrefabHash);
        return true;
    }
}
```

- **The camera.** `IGhostDrawing.Camera` (an `Option<Camera>`) is the camera Lattice draws for; Unity's `Camera.main`
  works too.
- **The local player.** `NetworkManager.LocalClientId` (namespace `Assets.Scripts.Networking`) is the local client's
  id, as Lattice itself reads it. A dedicated server (`GameManager.IsBatchMode`) has no local player.
- **The held piece.** `InventoryManager.ConstructionCursor` is the structure the game shows at the cursor, or null;
  its `PrefabHash` is the piece's `PrefabKey`. `InventoryManager.CurrentRotation` is its rotation. `Cell.FromWorld`
  snaps the point as the game does.
- **The held kit.** `InventoryManager.ActiveHandSlot` holds the item in the active hand. For a kit with several
  pieces (a `MultiConstructor`), `LastSelectedIndex` is the option it shows, the same index as `KitOption`.

## A planner UI

A planner shows the piece at the cursor, checks it live, places it on click and offers undo. Lattice draws
everything. Your mod handles input and says what to show.

```csharp
public sealed class Planner : IDisposable
{
    private readonly IGhostSystem _lattice;
    private readonly Actor _player;
    private readonly Maker _tool;
    private readonly IDrafts _cursor;
    private readonly IStyleSet _hovered;
    private readonly Stack<ChangeSetId> _undo = new();
    private Command? _held;

    public Planner(IGhostSystem lattice, Actor player, ModId mod)
    {
        _lattice = lattice;
        _player = player;
        _tool = Must(Maker.Create(mod, "planner"));
        _cursor = Must(lattice.Styles.Drafts(Must(DraftsId.Create(mod, "cursor"))));
        _hovered = Must(lattice.Styles.Set(LibrarySets.Hovered));
        lattice.Events.AnswersReady += OnAnswersReady;
        lattice.Events.Answered += OnAnswered;
    }

    /// <summary>Every frame the pointer moves.</summary>
    public void Aim(PickRay ray)
    {
        var options = PickOptions.Default with { Drafts = DraftPicks.Exclude };
        if (_lattice.Styles.TryPick(ray, options, out var hit) && hit.Kind == PickKind.Ghost)
            _hovered.Put(hit.Ghost);
        else
            _hovered.Clear();
    }

    /// <summary>The piece at the cursor moved or turned.</summary>
    public void Hold(PrefabKey piece, WorldPoint at, Turn turn)
    {
        if (!_lattice.Catalogue.Piece(piece).TryGet(out var entry))
            return;
        var draft = new GhostDraft(
            new Intent.Build(new Placement(piece, Cell.FromWorld(at, entry.Grid), turn), Option<Finish>.None),
            Option<PlanId>.None,
            _tool,
            new Visibility.Everyone(),
            Option<Label>.None,
            Option<Tint>.None,
            ValueSet<GroupName>.Empty,
            ValueMap<AttachmentKey, Attachment>.Empty);
        _held = new Command.Place(ValueList.Of(draft));
        _cursor.Show(ValueList.Of(draft));   // drawn at once, unchecked
        ShowPreview();                       // replaced by the checked preview when it is ready
    }

    /// <summary>The player clicks.</summary>
    public void Place()
    {
        if (_held is null)
            return;
        var submission = _lattice.Commands.Submit(CommandId.New(), _held, _player);
        _cursor.Hand(submission);            // no frame without either the drafts or the ghosts
        if (submission is Submission.Answered answered)
            OnAnswered(answered.Ticket, answered.Outcome);
        _held = null;
    }

    public void Undo()
    {
        if (_undo.Count > 0)
            _lattice.Commands.Submit(CommandId.New(), new Command.Revert(_undo.Pop()), _player);
    }

    private void ShowPreview()
    {
        if (_held is not null
            && _lattice.Preview.Of(_held, _player) is Answer<Result<Preview, ValueList<Refusal>>>.Ready ready
            && ready.Value.TryGet(out var preview))
            _cursor.Show(preview);
    }

    private void OnAnswersReady(Revision _) => ShowPreview();

    private void OnAnswered(Ticket _, Outcome outcome)
    {
        if (outcome is Outcome.Applied applied)
            _undo.Push(applied.Changes.Id);
    }

    public void Dispose()
    {
        _lattice.Events.AnswersReady -= OnAnswersReady;
        _lattice.Events.Answered -= OnAnswered;
        _hovered.Clear();
        _cursor.Dispose();
    }
}
```

The calls in order:

1. **Aim.** `Styles.TryPick` every frame the pointer moves; it allocates nothing. Put the hit in the library's
   `hovered` set and the starter library draws the glow.
2. **Hold.** Show the draft at once with `IDrafts.Show(drafts)`, and ask `Preview.Of` for the checked version. Snap
   with `Cell.FromWorld` and the piece's grid from the catalogue.
3. **Check.** When the preview is ready (now, or after `AnswersReady`), `IDrafts.Show(preview)` draws the drafts with
   their findings.
4. **Place.** `Submit`, then `IDrafts.Hand(submission)`, so no frame shows neither drafts nor ghosts.
5. **Undo.** Keep each applied change set's id. `Command.Revert` undoes it.

What to watch for:

- **The player's actor.** Lattice does not tell you who the local player is. Build `Actor.Player` from the game's
  client id for the local player.
- **Clients.** On a client `Submit` answers `Sent`. The outcome comes as `Events.Answered`, the ghosts as
  `Events.Changed`. The sketch handles both.
- **Looks.** Add your theme once as a layer in the `Theme` band and toggle it. Use sets for selection
  (`LibrarySets.Selected`) and for anything your logic decides.
- **Bigger tools.** A run tool submits `Command.PlanRun` and shows the preview's `RunReport` (cells, cost, first
  failing cell) while the player drags. A box select reads `Board.Select(new Selector.Box(a, b))`. Moving a selection
  is `Command.Transform`.
- **Your own overlays.** Long drawing work goes in `lattice.Slices`, so a large plan never stalls a frame. Fonts,
  icons, materials and claims are in [drawing](drawing.md).

## A builder

A builder takes ghosts that can be built now, claims them, carries out their steps in the game, and lets the world
say when each is done.

```csharp
public sealed class Builder : IDisposable
{
    private readonly IGhostSystem _lattice;
    private readonly IExecutorHandle _me;
    private readonly PlanId _plan;
    private readonly Lease _lease = Must(Lease.Create(30));
    private readonly Queue<Step> _work = new();
    private readonly RuleId _rule;
    private readonly ReasonCode _unknownStep;
    private Claim? _claim;

    private Builder(IGhostSystem lattice, IExecutorHandle me, PlanId plan, ModId mod)
    {
        _lattice = lattice;
        _me = me;
        _plan = plan;
        _rule = Must(RuleId.Create(mod, "drone"));
        _unknownStep = Must(ReasonCode.Create(mod, "unknown-step"));
        lattice.Events.Completed += OnCompleted;
    }

    /// <summary>On the host only: refused host-only on a client.</summary>
    public static Result<Builder, Reason> Start(IGhostSystem lattice, ModId mod, PlanId plan) =>
        ExecutorId.Create(mod, "drone-1")
            .Bind(id => DisplayName.Create("Drone 1").Map(name => new ExecutorInfo(id, name)))
            .Bind(info => lattice.Executors.Register(info))
            .Map(handle => new Builder(lattice, handle, plan, mod));

    /// <summary>A few times a second, from your own update.</summary>
    public void Tick()
    {
        if (_claim is not null)
        {
            _claim = _me.Renew(_claim).Match(static claim => claim, static _ => (Claim?)null);   // lapsed: let it go
            return;
        }

        if (!_lattice.Plans.Facts(_plan).TryGet(out var answer) || answer is not Answer<PlanFacts>.Ready { Value: var facts })
            return;                                    // unknown plan, or still being worked out
        foreach (var ghost in facts.BuildableNext)
        {
            if (!_me.Claim(ghost, _lease).TryGet(out var claim))
                continue;                              // another executor holds it
            if (!_lattice.Steps.For(ghost).TryGet(out var plan) || !Queue(plan))
            {
                _me.Release(claim);
                continue;
            }
            _claim = claim;
            _me.Report(ghost, new ExecutorStatus.Working(1, plan.Steps.Count));
            return;
        }
    }

    private bool Queue(StepPlan plan)
    {
        _work.Clear();
        foreach (var step in plan.Steps)
        {
            switch (step)
            {
                case Step.PlaceKit or Step.Advance or Step.TakeBack or Step.Merge or Step.Paint or Step.Rename or Step.SetValue:
                    _work.Enqueue(step);               // your drone carries these out in the game
                    break;
                default:
                    _me.Report(plan.Ghost, new ExecutorStatus.Waiting(Reason.Of(_unknownStep, _rule)));
                    return false;                      // a step kind from a newer Lattice: skip the ghost
            }
        }
        return true;
    }

    private void OnCompleted(GhostId ghost, Completion _)
    {
        if (_claim is not null && _claim.Ghost == ghost)
        {
            _me.Release(_claim);
            _claim = null;
        }
    }

    public void Dispose()
    {
        _lattice.Events.Completed -= OnCompleted;
        _me.Dispose();                                 // releases every claim it holds
    }
}
```

The calls in order:

1. **Register** an executor. Only on the host: a client is refused `host-only`.
2. **Find work.** `Plans.Facts(plan).BuildableNext` lists the ghosts whose support exists and that nothing blocks.
   Across plans, `Validation.Check` gives build order with `Finding.Passes`.
3. **Claim** one with a lease. A claim refused `claimed` means another executor has it: try the next.
4. **Read its steps** with `Steps.For`, and report `ExecutorStatus.Working` so players see what the drone does.
5. **Carry them out** in the game, in order, honouring each `Constraint`.
6. **Renew** the claim while you work. A claim you stop renewing lapses, and the ghost is free again.
7. **Done** is not yours to say. When the world matches the ghost, `Events.Completed` comes: release the claim then.

**Carrying out steps is the builder's job.** Lattice says what must happen; your mod makes it happen in the game,
as a player's hands would:

| Step | In the game |
|---|---|
| `PlaceKit(kit, option, uses, at)` | Place the kit item as the piece at that option, in that cell and turn, using that many items from the stack. For a kit with several pieces the game's own construct call (`MultiConstructor.Construct`) takes the option index. |
| `Advance(on, tool, material)` | Use the tool, with the material if one is named, on the structure: one build step, as a player's build action. |
| `TakeBack(on, tool, returns)` | Use the tool to take the structure one step apart; the items in `returns` come back. |
| `Merge(into, becomes, offhandTool, extra)` | Apply the kit, with the off-hand tool, to the standing structure so it becomes the new piece. |
| `Paint`, `Rename`, `SetValue` | Paint the structure, name it, or set its labeller value. |

A later step names a structure an earlier `PlaceKit` or `Merge` makes by `StepTarget.PlacedBy(index)`. Once that
step is done, find the new structure where it stands: `lattice.World.At(placement.At)` lists the structures in the
cell, and the one of the placed piece is it (`StructureInfo.Ref`). Use its `StructureRef` for the later steps.

What to watch for:

- **Unknown steps.** A later minor may add step kinds. Skip a ghost whose plan has one you do not know, and say why
  with `ExecutorStatus.Waiting`.
- **The world moves.** Steps are worked out from the world as it is now. Ask again after a change rather than keep an
  old plan.
- **Waiting.** Report `Waiting(reason)` with your own reason code when you lack materials or a path, so players and
  agents see why.
- **Bills.** `Bills.For(selector)` gives the materials for a set of ghosts, to gather them before you start.

## An agent

An agent is a tool or AI that reads plans and changes them as an `Actor.Agent`: a chat assistant that plans a base,
an upgrade tool, an admin script. It works through commands exactly as a player's UI does, so every change it makes
shows, saves and undoes like a player's.

```csharp
public sealed class CableAgent : IDisposable
{
    private readonly IGhostSystem _lattice;
    private readonly Actor _me;
    private readonly Maker _job;
    private Command? _pending;

    public CableAgent(IGhostSystem lattice, ModId mod)
    {
        _lattice = lattice;
        _me = new Actor.Agent(Must(AgentId.Create("cable-agent")));
        _job = Must(Maker.Create(mod, "heavy-cable"));
        lattice.Events.AnswersReady += OnAnswersReady;
        lattice.Events.Answered += OnAnswered;
    }

    /// <summary>The ghosts this agent made, as it may see them.</summary>
    public ValueList<Ghost> Mine() =>
        _lattice.Board.As(_me).Select(new Selector.MadeBy(_job)).Match(static g => g, static _ => ValueList<Ghost>.Empty);

    /// <summary>Plans a swap of every cable on a network to heavy cable, after a dry run.</summary>
    public void Upgrade(NetworkRef network, KitKey heavyCable)
    {
        _pending = new Command.PlanSwap(new StructureSelector.Network(network), new SwapTo.SameShapeFrom(heavyCable), Option<PlanId>.None, _job);
        TrySubmit();
    }

    private void TrySubmit()
    {
        if (_pending is null || _lattice.Preview.Of(_pending, _me) is not Answer<Result<Preview, ValueList<Refusal>>>.Ready ready)
            return;                                            // pending: AnswersReady brings us back
        var command = _pending;
        _pending = null;
        if (!ready.Value.TryGet(out var preview))
            return;                                            // refused: report ready.Value's refusals
        foreach (var finding in preview.Findings)
            if (finding is Finding.WillFail)
                return;                                        // report it; do not plan what cannot be built
        var submission = _lattice.Commands.Submit(CommandId.New(), command, _me);
        if (submission is Submission.Answered answered)
            OnAnswered(answered.Ticket, answered.Outcome);     // else Events.Answered brings the outcome
    }

    private void OnAnswersReady(Revision _) => TrySubmit();

    private void OnAnswered(Ticket ticket, Outcome outcome)
    {
        if (outcome is Outcome.Refused refused)
            foreach (var why in refused.Why)
                Console.WriteLine($"{ticket.Command.Value}: {why.Why.Code}");
    }

    public void Dispose()
    {
        _lattice.Events.AnswersReady -= OnAnswersReady;
        _lattice.Events.Answered -= OnAnswered;
    }
}
```

The calls in order:

1. **Read** through `Board.As(agent)`, so the access policy filters for the agent, not for the local player.
2. **Dry run.** `Preview.Of(command, agent)` gives what the command would add, change and remove, its findings, its
   bill and its effects on networks, rooms and gas.
3. **Decide** from the preview: skip what would fail, ask a player about what warns.
4. **Submit.** Large commands are worked out over several frames: `Submit` can answer `Sent` even on the host, and
   the outcome comes as `Events.Answered`.

What to watch for:

- **Makers.** Give each job its own `Maker` (`mymod/job-412`). `Selector.MadeBy` finds its ghosts, and
  `Events.Blocked` tells you when they cannot proceed.
- **Edits race players.** Pass `UnchangedSince` with the revision you read, so an edit is refused `changed-since`
  rather than overwrite a player's change.
- **Explain.** Every refusal and finding carries a `Reason` with a code and facts. An agent acts on the code and
  quotes the facts.
- **Kinds you do not know.** When you read the board, skip and report intents, findings and effects newer than you.

## An add-on

An add-on puts a rule, a look, a fact or a piece's data on top of what is there. This one warns about ghosts in no
group, and draws the warning with an icon and the reason as a label.

```csharp
public sealed class GroupLint : IDisposable
{
    private readonly IDisposable _rule;
    private readonly IStyleLayer _look;

    public GroupLint(IGhostSystem lattice, ModId mod)
    {
        var id = Must(RuleId.Create(mod, "ungrouped"));
        var ungrouped = Must(ReasonCode.Create(mod, "ungrouped"));

        _rule = Must(lattice.Registries.Register(new RuleDescriptor(id, input =>
            input.Ghost.Groups.Count == 0
                ? new RuleVerdict.Warn(Reason.Of(ungrouped, id))
                : new RuleVerdict.Pass())));

        _look = Must(lattice.Styles.Add(new StyleLayerDescriptor(
            Must(StyleLayerId.Create(mod, "ungrouped")),
            StyleBand.Meaning,
            0,
            ValueList.Of(new StyleRule(
                new StyleMatch.Finding(FindingKind.Warns, Option.Some(ungrouped)),
                new Style
                {
                    Icon = Option.Some<IconLook>(new IconLook.Shown(LibraryIcons.Warns)),
                    Label = Option.Some<LabelLook>(new LabelLook.Shown(
                        new LabelText.FindingReason(), LibraryText.Default, LabelAnchor.Above, 10, Must(Metres.Create(12f)))),
                })))));
    }

    public void Dispose()
    {
        _look.Dispose();
        _rule.Dispose();
    }
}
```

Building blocks:

| Add | With | See |
|---|---|---|
| A finding on ghosts | `RuleDescriptor` | [validation](validation.md#add-a-rule) |
| What holds a modded piece up | `SupportRuleDescriptor` | [validation](validation.md#add-a-support-rule) |
| A fact in previews | `AnalyzerDescriptor` | [previews](previews-and-analysis.md#add-an-analyzer) |
| A look | A `StyleLayerDescriptor` in the `Meaning` band | [styles](styles.md) |
| Data on ghosts and plans | `AttachmentDescriptor`, then `GhostEdit.Attach` or `PlanEdit.SetData` | [saving](saving.md) |
| Facts about a modded piece | `CatalogueAddition` | [catalogue](catalogue.md#modded-pieces) |
| A better mesh, an icon, a font | `IGhostDrawing.Register` | [drawing](drawing.md) |

What to watch for:

- **Rules run often:** once per ghost per check, on a worker thread by default. Keep them fast (2 ms per call is the
  limit), never touch Unity from a worker rule, and read only what `RuleInput` gives you. Work lookups out on the
  main thread first (from the catalogue, say) and capture them read-only.
- **A rule that throws is switched off** for the rest of the world. Every other callback of yours is a plain call:
  what it throws is your mod's to handle.
- **Your codes are yours.** Make reason codes, rule ids and layer ids from your `ModId`, so they never clash with
  another mod's.

## Test it

Run your layer against Lattice's real core in your own tests with `Lattice.Testing`. A `TestWorld` makes itself
`LatticeSystem.Current`, so the code above runs unchanged. Pump frames with `Settle()`, and use the world's hands
(`Build`, `Advance`, `Remove`) to see completion and staleness as in game. See
[testing](consumer-guide.md#9-testing-against-lattice).

The add-on's rule, tested (xunit here; any framework works):

```csharp
public sealed class GroupLintTests
{
    [Fact]
    public void A_ghost_in_no_group_warns()
    {
        var player = new Actor.Player(new ClientId(7));
        var mod = Must(ModId.Create("mymod"));
        using var world = Must(TestWorld.Start(TestScenario.Empty.As(player)));
        using var lint = new GroupLint(world.System, mod);
        var frame = TestCatalogue.Hand.Key("StructureFrame").Reduce(default(PrefabKey));
        var draft = new GhostDraft(
            new Intent.Build(new Placement(frame, new Cell(10, 10, 10), Turn.None), Option<Finish>.None),
            Option<PlanId>.None,
            Must(Maker.Create(mod, "test")),
            new Visibility.Everyone(),
            Option<Label>.None,
            Option<Tint>.None,
            ValueSet<GroupName>.Empty,
            ValueMap<AttachmentKey, Attachment>.Empty);

        world.System.Commands.Submit(CommandId.New(), new Command.Place(ValueList.Of(draft)), player);
        var all = new Selector.Everything();
        _ = Must(world.System.Validation.Check(all));   // pending: queues the check
        Must(world.Settle());

        var answer = Must(world.System.Validation.Check(all));
        var report = Assert.IsType<Answer<ValidationReport>.Ready>(answer).Value;
        Assert.Contains(report.Findings, finding => finding is Finding.Warns { Why.Code.Value: "mymod/ungrouped" });
    }
}
```
