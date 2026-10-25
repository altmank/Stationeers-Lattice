using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Assets.Scripts.Objects;
using Lattice.Api;
using Lattice.Core.Catalogue;
using UnityEngine;

namespace Lattice.Game.Catalogue;

/// <summary>The catalogue read from the game's prefabs: a new one at every world load.</summary>
internal static class GameCatalogue
{
    /// <summary>A catalogue over the prefabs the game holds now; pieces are read as they are asked for.</summary>
    public static PrefabCatalogue Read() => new(new GamePrefabSource());
}

/// <summary>What an export wrote: the file, what the read found, and how long reading every prefab took.</summary>
internal sealed record ExportResult(string Path, ReadReport Report, TimeSpan LoadTime);

/// <summary>
/// The headless run that writes the catalogue data file for the test kit: start the game with
/// <c>-lattice-write-catalogue &lt;file&gt;</c>; once its prefabs are loaded Lattice reads every structure prefab,
/// writes the file and quits the game.
/// </summary>
internal static class CatalogueExport
{
    /// <summary>Reads every structure prefab now and writes the data file; a file that cannot be written is a reason.</summary>
    public static Result<ExportResult, Reason> Write(string path)
    {
        var watch = Stopwatch.StartNew();
        var catalogue = GameCatalogue.Read();
        var report = catalogue.ReadAll();
        watch.Stop();
        try
        {
            using var writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            CatalogueDataFile.Write(catalogue, report, watch.Elapsed, writer);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Result<ExportResult, Reason>.Error(Reason.Of(
                ReasonCodes.InvalidValue,
                CatalogueReasons.Catalogue,
                ValueMap<string, string>.Empty.With("file", path).With("problem", e.Message)));
        }

        return Result<ExportResult, Reason>.Ok(new ExportResult(path, report, watch.Elapsed));
    }

    /// <summary>
    /// When the game's command line carries the switch, writes the file once the prefabs are loaded (now, if they
    /// already are), logs the outcome and quits the game. Does nothing without the switch.
    /// </summary>
    public static void RunIfAsked(Action<string> log)
    {
        if (!ExportSwitch.Parse(Environment.GetCommandLineArgs()).TryGet(out var asked))
        {
            log($"{ExportSwitch.Name} needs a file after it; no catalogue written.");
            return;
        }

        if (!asked.TryGet(out var path))
            return;
        if (Prefab.AllPrefabs.Count > 0)
        {
            Run(path, log);
            return;
        }

        void OnLoaded()
        {
            Prefab.OnPrefabsLoaded -= OnLoaded;
            Run(path, log);
        }

        Prefab.OnPrefabsLoaded += OnLoaded;
    }

    private static void Run(string path, Action<string> log)
    {
        var result = Write(path);
        log(result.Match(
            static ok => $"Catalogue written to {ok.Path}: {ok.Report.Read} pieces, {ok.Report.Refused.Count} refused, " +
                         $"{ok.Report.WithoutKit.Count} without a kit, read in {(long)ok.LoadTime.TotalMilliseconds} ms.",
            static error => $"Catalogue not written: {error.Facts["problem"]} ({error.Facts["file"]})."));
        Application.Quit();
    }
}
