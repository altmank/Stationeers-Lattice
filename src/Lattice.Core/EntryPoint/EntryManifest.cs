using System;
using Lattice.Api;
using Lattice.Core.Modules;

namespace Lattice.Core.EntryPoint;

/// <summary>
/// Every game member the entry point uses (the world watcher, the seat and the local player), checked once when the
/// plugin loads: without them Lattice cannot tell when a world starts or ends, so it stays off for the process and the
/// log names the members. A test reads the entry point's compiled references and fails when one is not listed here.
/// </summary>
internal static class EntryManifest
{
    /// <summary>The entry point: its name, no other module, and every game member it uses.</summary>
    public static readonly ModuleManifest Module = new(
        ModuleName.Create("entry").TryGet(out var name) ? name : throw new InvalidOperationException("module name"),
        ValueList<ModuleName>.Empty,
        ValueList.From(new GameMember[]
        {
            new GameMember.Method("StationSaveUtils", "GetSavePathSavesSubDir", Params()),
            new GameMember.Method("UnityEngine.GameObject", ".ctor", Params("System.String")),
            new GameMember.Method("UnityEngine.GameObject", "AddComponent", Params()),
            new GameMember.Method("UnityEngine.MonoBehaviour", ".ctor", Params()),
            new GameMember.Method("UnityEngine.Object", "DontDestroyOnLoad", Params("UnityEngine.Object")),
            new GameMember.Property("Assets.Scripts.GameManager", "GameState"),
            new GameMember.Property("Assets.Scripts.GameManager", "IsBatchMode"),
            new GameMember.Property("Assets.Scripts.Networking.NetworkManager", "IsClient"),
            new GameMember.Property("Assets.Scripts.Networking.NetworkManager", "LocalClientId"),
            new GameMember.Type("UnityEngine.Object"),
        }));

    private static ValueList<string> Params(params string[] types) => ValueList.From(types);
}
