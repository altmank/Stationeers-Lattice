using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Lattice.Api;
using Lattice.Core.Modules;
using Lattice.Core.Saving;
using Lattice.Core.World;
using Lattice.Game.World;

namespace Lattice.Game.Saving;

/// <summary>
/// The saving module's patches on the game's save and load code, installed once per process, only after every member
/// of <see cref="SavingManifest.Module"/> is found; a failing patch undoes every patch this module made. Each body runs
/// through <see cref="PatchGuard"/>: a fault turns saving off for the world (<see cref="Current"/> cleared), is logged
/// once, and never reaches the game.
/// <list type="table">
/// <item><term><c>LoadHelper.LoadGame(string, string)</c>, prefix</term><description>The save file a load reads
/// (every load goes through it: the load menu, the <c>load</c> and <c>loadlatest</c> commands, a server's
/// <c>-load</c>); the world folder comes from it, not from the station name.</description></item>
/// <item><term><c>SaveHelper.Save(DirectoryInfo, string, bool, CancellationToken)</c>, prefix</term><description>The
/// file a save writes, on the main thread before the game reads its world: Lattice flushes its lines and takes each
/// plan's line.</description></item>
/// <item><term><c>SaveHelper.SavingFinished()</c>, prefix</term><description>The save is written: the record of it,
/// keyed by its path and fingerprint.</description></item>
/// </list>
/// </summary>
internal static class SaveHooks
{
    private const string HarmonyId = "lattice.saving";

    private static Harmony? _harmony;

    private static readonly Action<string, Exception> OnFault = static (patch, e) =>
    {
        if (Current == null)
            return;
        Current = null;
        UnityEngine.Debug.LogError($"Lattice saving off for this world: patch {patch} threw {e}");
    };

    /// <summary>The running world's saving; set when a world runs, cleared when it ends.</summary>
    public static GameSaving? Current { get; set; }

    /// <summary>The save file the game is loading or last loaded; taken by <see cref="TakeLoaded"/>.</summary>
    public static string? LoadedFrom { get; private set; }

    /// <summary>The save the game loaded, once: none for a new world.</summary>
    public static Option<string> TakeLoaded()
    {
        var loaded = LoadedFrom;
        LoadedFrom = null;
        return loaded == null ? Option<string>.None : Option.Some(loaded);
    }

    /// <summary>Checks every member and patches; refused, with nothing patched, when a member is missing or a patch fails.</summary>
    public static Result<Unit, Reason> Install()
    {
        if (_harmony != null)
            return Result<Unit, Reason>.Ok(Unit.Value);
        var missing = GameMembers.Missing(SavingManifest.Module);
        if (missing.Count > 0)
            return Result<Unit, Reason>.Error(Reason.Of(ReasonCodes.ModuleMissing, SavingReasons.Saving,
                ValueMap<string, string>.Empty.With("module", SavingManifest.Module.Name.Value).With("missing", missing.Count.ToString())));

        var harmony = new Harmony(HarmonyId);
        try
        {
            harmony.Patch(Method(SavingManifest.LoadGame), prefix: Hook(nameof(BeforeLoadGame)));
            harmony.Patch(Method(SavingManifest.Save), prefix: Hook(nameof(BeforeSave)));
            harmony.Patch(Method(SavingManifest.SavingFinished), prefix: Hook(nameof(BeforeSavingFinished)));
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogError($"Lattice saving: patching failed, undoing every saving patch: {e}");
            try
            {
                harmony.UnpatchSelf();
            }
            catch (Exception undo)
            {
                UnityEngine.Debug.LogError($"Lattice saving: undoing the saving patches failed: {undo}");
            }

            return Result<Unit, Reason>.Error(Reason.Of(ReasonCodes.ModuleMissing, SavingReasons.Saving,
                ValueMap<string, string>.Empty.With("module", SavingManifest.Module.Name.Value).With("problem", e.Message)));
        }

        _harmony = harmony;
        return Result<Unit, Reason>.Ok(Unit.Value);
    }

    private static MethodBase Method(GameMember.Method method) =>
        GameMembers.Method(method) ?? throw new MissingMethodException(method.DeclaringType, method.Name);

    private static HarmonyMethod Hook(string name) =>
        new(typeof(SaveHooks).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static));

    private static void BeforeLoadGame(string path) =>
        PatchGuard.Run(nameof(BeforeLoadGame), path, static p => LoadedFrom = p, OnFault);

    private static void BeforeSave(DirectoryInfo saveDirectory, string saveFileName)
    {
        if (Current != null)
            PatchGuard.Run(nameof(BeforeSave), saveDirectory, saveFileName, static (dir, file) => Current?.OnSaveStarting(dir.FullName, file), OnFault);
    }

    private static void BeforeSavingFinished()
    {
        if (Current != null)
            PatchGuard.Run(nameof(BeforeSavingFinished), 0, static _ => Current?.OnSavingFinished(), OnFault);
    }
}
