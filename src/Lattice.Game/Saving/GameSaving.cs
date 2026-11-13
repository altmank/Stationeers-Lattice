using System;
using System.IO;
using Lattice.Api;
using Lattice.Core.Saving;

namespace Lattice.Game.Saving;

/// <summary>
/// Saving for one running world over the game's save files. <see cref="Open"/> runs when the world runs: from the save
/// the game loaded (<see cref="SaveHooks.LoadedFrom"/>), or for a new world from a folder of its own until its first
/// save names the world. The hooks call <see cref="OnSaveStarting"/> when the game starts a save (main thread, before
/// it writes) and <see cref="OnSavingFinished"/> when the file is written (the game's save thread or the main thread:
/// it only writes the record file). Lattice's files never go inside a game save.
/// </summary>
internal sealed class GameSaving
{
    private readonly Action<string> _log;
    private bool _newWorld;
    private SaveCapture? _capture;
    private string? _savingTo;
    private Option<string> _before = Option<string>.None;

    private GameSaving(PlanSaving saving, OpenedPlans opened, bool newWorld, Action<string> log)
    {
        Saving = saving;
        Opened = opened;
        _newWorld = newWorld;
        _log = log;
    }

    public PlanSaving Saving { get; }

    /// <summary>The board to start the world with, and what the load found.</summary>
    public OpenedPlans Opened { get; }

    /// <summary>Brings the board up to the world before a save takes its heads: the world's <c>CatchUp</c> once it runs.</summary>
    public Action CatchUp { get; set; } = static () => { };

    /// <summary>
    /// Opens the world's plans: from <paramref name="loadedSave"/> when the game loaded one, else as a new world under
    /// <paramref name="savesRoot"/> (the game's <c>saves</c> folder). A loaded save with no fingerprint opens as a save
    /// with no record, reported.
    /// </summary>
    public static GameSaving Open(Option<string> loadedSave, string savesRoot, Action<string> log)
    {
        if (!loadedSave.TryGet(out var path))
        {
            var fresh = new PlanSaving(new WorldFolder(SaveFiles.NewWorldFolder(savesRoot, Guid.NewGuid())));
            return new GameSaving(fresh, fresh.Open(Option<SaveKey>.None), newWorld: true, log);
        }

        var saving = new PlanSaving(new WorldFolder(SaveFiles.LatticeFolderFor(SaveFiles.WorldFolderOf(path))));
        var key = SaveFiles.Fingerprint(path).Match(
            fingerprint => Option.Some(new SaveKey(Path.GetFullPath(path), fingerprint)),
            why =>
            {
                log($"Lattice can't tell which save this is ({why}); each plan opens with its newest changes.");
                return Option.Some(new SaveKey(Path.GetFullPath(path), ""));
            });
        return new GameSaving(saving, saving.Open(key), newWorld: false, log);
    }

    /// <summary>The game starts writing <paramref name="file"/> in <paramref name="folder"/>: the board caught up, lines flushed, heads taken.</summary>
    public void OnSaveStarting(string folder, string file)
    {
        CatchUp();
        var path = Path.GetFullPath(Path.Combine(folder, file));
        if (Saving.FollowSave(path, _newWorld).TryGetError(out var why))
            _log($"Lattice saving: the plans stay in {Saving.Folder.Root}: {why}");
        else
            _newWorld = false;

        _capture = Saving.BeforeGameSave();
        _savingTo = path;
        _before = SaveFiles.Fingerprint(path).Match(static was => Option.Some(was), static _ => Option<string>.None);
    }

    /// <summary>The game finished a save: the record of the save just written, keyed by its path and fingerprint.</summary>
    public void OnSavingFinished()
    {
        var capture = _capture;
        var path = _savingTo;
        _capture = null;
        _savingTo = null;
        var before = _before;
        _before = Option<string>.None;
        if (capture == null || path == null || !File.Exists(path))
            return;
        var after = SaveFiles.Fingerprint(path);
        if (!after.TryGet(out var fingerprint))
            _log($"Lattice can't tell which save this is, so it kept no plans for {path}: the save has no fingerprint.");
        else if (!SaveFiles.WasWritten(before, after))
            _log($"Lattice kept no plans for {path}: the game did not write that save.");
        else
            Saving.AfterGameSave(capture, new SaveKey(path, fingerprint), DateTimeOffset.Now);
    }
}
