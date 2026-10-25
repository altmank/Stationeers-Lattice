using System;
using Lattice.Api;
using Lattice.Core.Modules;

namespace Lattice.Core.Saving;

/// <summary>
/// Every game member the saving module uses, checked at load: the module is off, with the missing members named, when
/// the game lacks any. A test reads the module's compiled references and fails when one is not listed here.
/// </summary>
internal static class SavingManifest
{
    private const string SaveHelper = "Assets.Scripts.Serialization.SaveHelper";

    /// <summary>The patch target that names the save file a load reads.</summary>
    public static readonly GameMember.Method LoadGame = new("Assets.Scripts.Serialization.LoadHelper", "LoadGame", Params("System.String", "System.String"));

    /// <summary>The patch target every save starts through (main thread, before the file is written).</summary>
    public static readonly GameMember.Method Save = new(SaveHelper, "Save", Params("System.IO.DirectoryInfo", "System.String", "System.Boolean", "System.Threading.CancellationToken"));

    /// <summary>The patch target every save ends through (the file is written).</summary>
    public static readonly GameMember.Method SavingFinished = new(SaveHelper, "SavingFinished", Params());

    /// <summary>The saving module: its name, no other module, and every game member it uses.</summary>
    public static readonly ModuleManifest Module = new(
        ModuleName.Create("saving").TryGet(out var name) ? name : throw new InvalidOperationException("module name"),
        ValueList<ModuleName>.Empty,
        ValueList.From(new GameMember[]
        {
            LoadGame, Save, SavingFinished,
            new GameMember.Method("UnityEngine.Debug", "LogError", Params("System.Object")),
        }));

    private static ValueList<string> Params(params string[] types) => ValueList.From(types);
}
