using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Assets.Scripts.Serialization;
using Cysharp.Threading.Tasks;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Grid;
using Lattice.Core.Ports;
using Lattice.Core.Saving;
using Lattice.Core.Slices;
using Lattice.Game.Catalogue;
using Lattice.Game.Saving;
using UnityEngine;

namespace Lattice.Game.Live;

/// <summary>
/// Saving in the game, over the game's own save and load. Modes:
/// <c>saving-new</c> a new world: ghost A, the first save (the head save, "Monday"), ghost B, an autosave ("Tuesday");
/// <c>saving-load-place</c> after loading a save: what opened, then ghost C written but not saved (as before a crash);
/// <c>saving-load</c> after loading a save: what opened. A board is made here over the game catalogue, as the entry
/// point will; ghosts are told apart by their cell's x (A 1010, B 1030, C 1050).
/// </summary>
internal static class SavingRun
{
    public const string Station = "lattice-saving";

    private static readonly Actor Tester = new Actor.Player(new ClientId(1));

    public static IEnumerator Run(string mode, Action<string> line)
    {
        line($"saving: hooks {(SaveHooks.Install().TryGetError(out var why) ? "refused " + why.Facts : "installed")}");
        var saving = GameSaving.Open(SaveHooks.TakeLoaded(), StationSaveUtils.GetSavePathSavesSubDir().FullName, line);
        SaveHooks.Current = saving;
        var report = saving.Opened.Report;
        line($"saving: opened {report.Basis} from {saving.Saving.Folder.Root}; problems {report.Problems.Count}");
        line($"saving: ghosts at x {Cells(saving.Opened.Seed)}");
        foreach (var offer in report.Offers)
            line($"saving: offer plan {offer.Plan.Value} changes {offer.Changes} from line {offer.Head} to {offer.Newest}");
        foreach (var plan in report.Hidden)
            line($"saving: hidden plan {plan.Value}");
        var board = new GhostBoard(
            new Shapes(GameCatalogue.Read(), new CubeGrid()),
            new Clock(),
            new AccessPolicies(),
            new PlanSizeLimit(),
            new BoardEvents(),
            Option.Some(saving.Opened.Seed));

        if (mode == "saving-new")
        {
            Place(board, saving, 1010, line);
            yield return Wait(SaveHelper.NewSave(Station, CancellationToken.None), "first save", line);
            Place(board, saving, 1030, line);
            yield return Wait(SaveHelper.AutoSave(Station, CancellationToken.None), "autosave", line);
            Records(saving, line);
        }
        else if (mode == "saving-load-place")
        {
            Place(board, saving, 1050, line);
            saving.Saving.Flush();
            line("saving: ghost C written, not saved");
        }

        SaveHooks.Current = null;
    }

    private static void Place(GhostBoard board, GameSaving saving, int x, Action<string> line)
    {
        var draft = new GhostDraft(
            new Intent.Build(new Placement(Scene.Frame, new Lattice.Api.Cell(x, 810, 1010), Turn.None), Option<Finish>.None),
            Option<PlanId>.None,
            Maker.Create(LatticeIdentity.Mod, "live").TryGet(out var maker) ? maker : default,
            new Lattice.Api.Visibility.Everyone(),
            Option<Label>.None,
            Option<Tint>.None,
            ValueSet<GroupName>.Empty,
            ValueMap<AttachmentKey, Attachment>.Empty);
        var outcome = board.Apply(new CommandId(Guid.NewGuid()), new Command.Place(ValueList.Of(draft)), Tester);
        if (outcome is Outcome.Applied applied)
        {
            saving.Saving.Record(applied.Changes);
            line($"saving: placed ghost {applied.Changes.Added[0].Id.Value} at x {x}");
        }
        else
        {
            line($"FAIL saving: place at x {x} refused: {outcome}");
        }
    }

    private static IEnumerator Wait(UniTask<SaveResult> save, string what, Action<string> line)
    {
        SaveResult? result = null;
        yield return save.ToCoroutine(r => result = r);
        line($"saving: {what}: {(result is { } r ? $"success {r.Success} '{r.Message}'" : "no result")}");
        yield return new WaitForSecondsRealtime(2f);
    }

    private static void Records(GameSaving saving, Action<string> line)
    {
        foreach (var record in saving.Saving.Folder.Records())
        {
            var heads = new List<string>();
            foreach (var (plan, head) in record.Heads)
                heads.Add($"{plan.Value}@{head}");
            line($"saving: record {record.Save.Path} fingerprint {record.Save.Fingerprint} heads {string.Join(",", heads)}");
        }
    }

    private static string Cells(BoardSeed seed)
    {
        var xs = new List<int>();
        foreach (var ghost in seed.Ghosts)
            if (ghost.Intent is Intent.Build build)
                xs.Add(build.Target.At.X);
        xs.Sort();
        return xs.Count == 0 ? "none" : string.Join(",", xs);
    }

    private sealed class Clock : ITimeSource
    {
        public DateTimeOffset Now => DateTimeOffset.Now;
    }
}
