using System;
using System.IO;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.BuiltWorld;
using Lattice.Core.Commands;
using Lattice.Core.FinishedPlans;
using Lattice.Core.Matching;
using Lattice.Core.Saving;
using Lattice.Core.Slices;
using Lattice.TestKit;
using Lattice.TestKit.Catalogue;
using Lattice.TestKit.World;
using Lattice.Tests.Board;
using Lattice.Tests.Matching;

namespace Lattice.Tests.FinishedPlans;

/// <summary>
/// A world whose plans are saved, built and finished: a temp folder with Saving, a board started from what Saving
/// opened (saving installed, every change set recorded), a fake world with its mirror and the completion watcher, and
/// the finished-plans expanders. Every piece is modded: a wall panel and a window pane on a large cell's x+ face, a
/// truss in its centre. A game load (<see cref="Reload"/>) makes a new Saving, board, mirror and watcher over the same
/// folder and world.
/// </summary>
internal sealed class FinishedBench : IDisposable
{
    /// <summary>The maker the bench's repair and take-down commands name.</summary>
    public static readonly Maker Asker = TestKitIds.Must(Maker.Create(TestKitIds.Mod, "asker"));

    public const string Text = """
        lattice-catalogue 3
        piece ModPanel kit=ItemModPanelKit option=0 grid=large
          step material=ItemModPanelKit*1 exit=ItemWrench air=yes gravity=yes
          cell 0 0 0 face-x+

        piece ModPane kit=ItemModPaneKit option=0 grid=large opening=window
          step material=ItemModPaneKit*1 exit=ItemWrench air=yes gravity=yes
          cell 0 0 0 face-x+

        piece ModTruss kit=ItemModTrussKit option=0 grid=large
          step material=ItemModTrussKit*1 exit=ItemWrench air=no
          cell 0 0 0 centre
        """;

    private static readonly NamedPiece[] Modded =
        TestKitIds.MustRead(CatalogueFile.Read(new StringReader(Text), "finished.lcat")).Pieces.ToArray();

    private long _nextStructure = 5000;
    private int _commands;
    private int _saves;

    public FinishedBench()
    {
        Root = Path.Combine(Path.GetTempPath(), "lattice-finished-" + Guid.NewGuid().ToString("N"));
        Folder = new WorldFolder(Root);
        World = new FakeWorld(new TestBoard(default, Modded).Catalogue);
        Reload(Option<SaveKey>.None);
    }

    public string Root { get; }

    public WorldFolder Folder { get; }

    public FakeWorld World { get; }

    public PlanSaving Saving { get; private set; } = null!;

    public TestBoard T { get; private set; } = null!;

    public GhostBoard Board => T.Board;

    public WorldMirror Mirror { get; private set; } = null!;

    public FinishedPlansExpander Expanders { get; private set; } = null!;

    public static Cell Large(int i) => new(10 + 20 * i, 10, 10);

    /// <summary>The game loads <paramref name="save"/> (none: a new world).</summary>
    public void Reload(Option<SaveKey> save)
    {
        Mirror?.End();
        Saving = new PlanSaving(Folder);
        var opened = Saving.Open(save);
        T = new TestBoard(Option.Some(opened.Seed), Modded);
        Board.InstallSaving(Saving);
        T.Events.Changed += Saving.Record;
        var shapes = new Shapes(T.Catalogue, T.Grid);
        var reads = new CountingWorld(World);
        Mirror = new WorldMirror(shapes, reads);
        var watcher = new CompletionWatcher(Board, Mirror, new TurnFreePieces(T.Catalogue, T.Grid, shapes), T.Time);
        Mirror.Start(World, reads);
        watcher.Load();
        Expanders = new FinishedPlansExpander(Board, Mirror, shapes);
        Flush();
    }

    /// <summary>The game saves; returns the save's key.</summary>
    public SaveKey Save()
    {
        var key = new SaveKey(Path.Combine(Root, "game", "world.save"), $"save-{++_saves}");
        Saving.OnGameSave(key, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(_saves));
        return key;
    }

    /// <summary>Raises the board's queued events (Saving records them) and writes Saving's queue.</summary>
    public void Flush()
    {
        T.Events.Flush();
        Saving.Flush();
    }

    public Outcome Apply(Command command, Actor? actor = null)
    {
        var outcome = Board.Apply(new CommandId(new Guid(++_commands, 7, 7, new byte[8])), command, actor ?? TestBoard.Alice);
        Flush();
        return outcome;
    }

    public PlanId NewPlan(string name, Visibility? visibility = null) =>
        TestBoard.Applied(Apply(new Command.CreatePlan(new PlanDraft(
            TestKitIds.Must(PlanName.Create(name)), TestBoard.TestMaker, visibility ?? new Visibility.Everyone(),
            ValueMap<AttachmentKey, Attachment>.Empty)))).PlansOpened[0].Id;

    public PrefabKey Key(string piece) => T.Catalogue.Named(piece).Key;

    public Placement At(string piece, Cell cell) => new(Key(piece), cell, default);

    public Ghost Plan(PlanId plan, Intent intent) =>
        Assert.Single(TestBoard.Applied(Apply(new Command.Place(ValueList.Of(TestBoard.Draft(intent, Option.Some(plan)))))).Added);

    public Ghost Plan(PlanId plan, string piece, Cell cell, Option<Finish> finish = default) =>
        Plan(plan, new Intent.Build(At(piece, cell), finish));

    /// <summary>A player builds the piece in the world, fully, as the ghost asks (finish included).</summary>
    public StructureRef Construct(Ghost ghost)
    {
        var build = (Intent.Build)ghost.Intent;
        var finish = build.Finish.TryGet(out var f) ? f : null;
        var info = new StructureInfo(
            new StructureRef(new ReferenceId(++_nextStructure), build.Target.Piece, build.Target.At), build.Target.Turn,
            new BuildState.Built(), finish?.Colour ?? Option<PaintColour>.None, finish?.Name ?? Option<ThingName>.None,
            finish?.Setting ?? Option<Setting>.None);
        World.Register(info);
        Flush();
        return info.Ref;
    }

    /// <summary>A structure that was never planned.</summary>
    public StructureRef Stray(string piece, Cell cell)
    {
        var info = new StructureInfo(new StructureRef(new ReferenceId(++_nextStructure), Key(piece), cell), default,
            new BuildState.Built(), Option<PaintColour>.None, Option<ThingName>.None, Option<Setting>.None);
        World.Register(info);
        Flush();
        return info.Ref;
    }

    public void TakeApart(StructureRef structure)
    {
        World.Remove(structure.Id);
        Flush();
    }

    public void Finish(PlanId plan) => TestBoard.Applied(Apply(new Command.FinishPlan(plan)));

    /// <summary>Runs a repair expansion to its end; returns what it found and how many steps it took.</summary>
    public (Result<Command, Reason> Found, int Steps) Repair(PlanId plan, Actor? actor = null) =>
        Run(Expanders.Repair.Start(Input(new Command.Repair(plan, Asker), actor)));

    public (Result<Command, Reason> Found, int Steps) TakeDown(PlanId plan, Actor? actor = null) =>
        Run(Expanders.TakeDown.Start(Input(new Command.TakeDown(plan, Asker), actor)));

    /// <summary>The ghosts of the plan an expansion creates.</summary>
    public static ValueList<GhostDraft> Drafts(Result<Command, Reason> found) =>
        Assert.IsType<Command.CreatePlan>(TestKitIds.Must(found)).Ghosts;

    public static Reason Refusal(Result<Command, Reason> found) =>
        found.TryGetError(out var why) ? why : throw new InvalidOperationException("expanded");

    public string PlanText(PlanId plan) =>
        File.Exists(Folder.PlanPath(plan)) ? Folder.ReadText(Folder.PlanPath(plan)) : Folder.ReadText(Folder.ArchivedPath(plan));

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private ExpansionInput<TCommand> Input<TCommand>(TCommand command, Actor? actor)
        where TCommand : Command =>
        new(command, new CommandId(Guid.NewGuid()), actor ?? TestBoard.Alice, Board.ViewFor(actor ?? TestBoard.Alice), null!);

    private static (Result<Command, Reason> Found, int Steps) Run(IExpansionWork work)
    {
        var steps = 1;
        while (work.Step() == StepOutcome.More)
            steps++;
        return (work.Found, steps);
    }
}
