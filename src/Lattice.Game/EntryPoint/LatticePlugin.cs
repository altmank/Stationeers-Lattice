using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BepInEx;
using LaunchPadBooster;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.EntryPoint;
using Lattice.Core.Modules;
using Lattice.Game.Saving;
using Lattice.Game.Sync;
using Lattice.Game.World;
using UnityEngine;

namespace Lattice.Game.EntryPoint;

/// <summary>
/// Lattice's plugin, loaded once per process by LaunchPad (it adds every <see cref="BaseUnityPlugin"/> subclass of a
/// mod's assemblies; the type stays internal). It logs Lattice's loaded copies, checks the entry point's game
/// members, installs the save hooks before any world loads, registers Lattice's networking as required on every
/// peer (decided), and leaves a <see cref="WorldWatcher"/> that starts and ends Lattice with each world.
/// </summary>
[BepInPlugin(LatticeIdentity.ModIdText, "Lattice", LatticeVersion.Text)]
internal sealed class LatticePlugin : BaseUnityPlugin
{
    private void Awake()
    {
        Action<string> log = Logger.LogInfo;
        foreach (var line in AssemblyCopies.Report(Loaded(), Own()))
            log(line);

        var members = new CachedGameMembers();
        var missing = members.Missing(EntryManifest.Module);
        if (missing.Count > 0)
        {
            log($"Lattice is off for this game: the entry point's game members are missing: {Names(missing)}.");
            return;
        }

        if (SaveHooks.Install().TryGetError(out var noHooks))
            log($"Lattice saving hooks not installed: {noHooks}");
        var networking = Networking.Register(log);

        var watcher = new GameObject("Lattice");
        DontDestroyOnLoad(watcher);
        watcher.AddComponent<WorldWatcher>().Prepare(members, networking, log);
    }

    private static ValueList<LoadedAssembly> Loaded()
    {
        var loaded = new List<LoadedAssembly>();
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var name = assembly.GetName();
            if (name.Name is { } simple && simple.StartsWith("Lattice.", StringComparison.Ordinal))
                loaded.Add(new LoadedAssembly(simple, VersionOf(assembly), assembly.IsDynamic ? "" : assembly.Location));
        }

        return ValueList.From(loaded);
    }

    private static string VersionOf(System.Reflection.Assembly assembly) =>
        System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(assembly)?.InformationalVersion
        ?? assembly.GetName().Version?.ToString()
        ?? "";

    private static ValueMap<string, string> Own() =>
        ValueMap.Of("Lattice.Api", typeof(LatticeSystem).Assembly.Location)
            .With("Lattice.Api.Unity", typeof(LatticeDrawing).Assembly.Location)
            .With("Lattice.Core", typeof(LatticeWorld).Assembly.Location)
            .With("Lattice.Render", typeof(Lattice.Render.Unity.RenderHost).Assembly.Location)
            .With("Lattice.Game", typeof(LatticePlugin).Assembly.Location);

    private static string Names(ValueList<GameMember> members)
    {
        var names = new System.Text.StringBuilder();
        foreach (var member in members)
        {
            if (names.Length > 0)
                names.Append(", ");
            names.Append(ModuleSwitches.NameOf(member));
        }

        return names.ToString();
    }
}

/// <summary>
/// Lattice's own LaunchPadBooster mod: required on every peer at the same version (decided), carrying the sync
/// messages. Without LaunchPadBooster's networking the sync module is off and the log says why.
/// </summary>
internal static class Networking
{
    public static bool Register(Action<string> log)
    {
        try
        {
            RegisterWithBooster();
            return true;
        }
        catch (Exception error)
        {
            log($"Lattice sync is off: LaunchPadBooster has no mod networking ({error.GetType().Name}: {error.Message}).");
            return false;
        }
    }

    // Its own method, never inlined, so a missing LaunchPadBooster fails inside Register's try.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RegisterWithBooster()
    {
        var mod = new Mod("Lattice", LatticeVersion.Text);
        mod.Networking.Required = true;
        GameSync.Register(mod.Networking);
    }
}

/// <summary>
/// <see cref="IGameMembers"/> over the running game, each manifest checked once per process: game members cannot change
/// inside one.
/// </summary>
internal sealed class CachedGameMembers : IGameMembers
{
    private readonly Dictionary<ModuleName, ValueList<GameMember>> _missing = new();

    public ValueList<GameMember> Missing(ModuleManifest manifest)
    {
        if (!_missing.TryGetValue(manifest.Name, out var missing))
            _missing[manifest.Name] = missing = GameMembers.Missing(manifest);
        return missing;
    }
}
