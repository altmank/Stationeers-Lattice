using System;
using System.Collections.Generic;
using System.IO;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Saving;
using Lattice.Tests.Board;

namespace Lattice.Tests.Saving;

/// <summary>
/// A world as Saving sees it across game loads: a temp folder for Lattice's files, a board started from what Saving
/// opened, and every change set the board makes recorded. Each load makes a new board and a new Saving, as a game
/// load does.
/// </summary>
internal sealed class SavedWorld : IDisposable
{
    private int _saves;

    public SavedWorld(int checkpointEvery = 2000)
    {
        Root = Path.Combine(Path.GetTempPath(), "lattice-saving-" + Guid.NewGuid().ToString("N"));
        Folder = new WorldFolder(Root);
        CheckpointEvery = checkpointEvery;
        Load(Option<SaveKey>.None);
    }

    public string Root { get; }

    public WorldFolder Folder { get; }

    public int CheckpointEvery { get; }

    public PlanSaving Saving { get; private set; } = null!;

    public TestBoard Board { get; private set; } = null!;

    public OpenReport Report { get; private set; } = null!;

    /// <summary>The ghosts the board holds now.</summary>
    public HashSet<GhostId> Ghosts { get; } = new();

    /// <summary>The game loads a save (none: a new world).</summary>
    public OpenReport Load(Option<SaveKey> save)
    {
        Saving = new PlanSaving(Folder, CheckpointEvery);
        var opened = Saving.Open(save);
        Start(opened.Seed);
        Report = opened.Report;
        return Report;
    }

    /// <summary>The board restarts from a seed (after a recovery move).</summary>
    public void Start(BoardSeed seed)
    {
        Board = new TestBoard(Option.Some(seed));
        Ghosts.Clear();
        foreach (var ghost in seed.Ghosts)
            Ghosts.Add(ghost.Id);
    }

    /// <summary>Places a frame ghost and records the change; returns its id.</summary>
    public GhostId Place(int x, Option<PlanId> plan = default)
    {
        var set = Board.Placed(Board.Draft("StructureFrame", new Cell(x, 10, 10), plan: plan));
        Saving.Record(set);
        Ghosts.Add(set.Added[0].Id);
        return set.Added[0].Id;
    }

    public ChangeSet Apply(Command command)
    {
        var set = TestBoard.Applied(Board.Apply(command));
        Saving.Record(set);
        foreach (var ghost in set.Added)
            Ghosts.Add(ghost.Id);
        foreach (var removal in set.Removed)
            Ghosts.Remove(removal.Ghost.Id);
        return set;
    }

    /// <summary>The game saves to a file named <paramref name="name"/>; each save has its own fingerprint.</summary>
    public SaveKey Save(string name)
    {
        var key = new SaveKey(Path.Combine(Root, "game", name + ".save"), $"{name}-{++_saves}");
        Saving.OnGameSave(key, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(_saves));
        return key;
    }

    /// <summary>Writes every queued line, as the frame's saving slices would.</summary>
    public void WriteQueued()
    {
        while (Saving.Step() == Lattice.Core.Slices.StepOutcome.More)
        {
        }
    }

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
}
