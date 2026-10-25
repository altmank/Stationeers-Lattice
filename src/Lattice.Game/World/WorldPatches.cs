using System;
using System.Collections.Generic;
using System.Reflection;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Items;
using HarmonyLib;
using Lattice.Api;
using Lattice.Core.Modules;
using Lattice.Core.World;

namespace Lattice.Game.World;

/// <summary>
/// The world bridge's patches on the game: the game raises no events for these changes
/// (<c>Structure.OnAnyConstructed</c> is declared and never raised: <c>Assets.Scripts.Objects/Structure.cs:436</c>).
/// Installed once per process, only after every member of <see cref="WorldManifest.Module"/> is found; if any patch
/// fails, every patch this module's Harmony id made is undone. Every patch body runs through <see cref="PatchGuard"/>:
/// an exception turns the bridge off for the world, is logged once, and never reaches the game. Each patch reports to <see cref="Current"/>, the running world's relay, and
/// returns at once, allocating nothing, when no world runs or the thing is not a structure.
/// <list type="table">
/// <item><term><c>GridController.Register(Structure)</c>, postfix</term><description>Registered: the one
/// non-virtual point every structure joins the grid through, modded ones included. Reads the whole
/// structure.</description></item>
/// <item><term><c>GridController.Deregister(Structure)</c>, postfix</term><description>Removed: called from
/// <c>Structure.OnDestroy</c>. Reads id, prefab, position.</description></item>
/// <item><term><c>Structure.CurrentBuildStateIndex</c> setter, prefix and postfix</term><description>StateChanged when the
/// index changed: reads the index before and after and the number of build states. Fires for every structure, during
/// loads too (no world then: returns at once).</description></item>
/// <item><term><c>Thing.SetCustomColor(int, bool)</c>, prefix and postfix</term><description>Painted when the paint
/// index changed; the <c>ColorSwatch</c> overload calls this one, <c>SetCustomColor(bool)</c> only refreshes
/// materials.</description></item>
/// <item><term><c>Thing.CustomName</c> setter, postfix</term><description>Renamed when the new name is a valid
/// <see cref="ThingName"/>. A fuselage's override writes the rocket's name without calling the base setter
/// (<c>Objects.Rockets/StructureFuselage.cs:42</c>), so fuselage renames are not
/// signalled.</description></item>
/// <item><term>every <c>Setting</c> setter of a structure type that implements <c>ISetable</c>, prefix and
/// postfix</term><description>SettingChanged when the value changed: the labeller and logic write it
/// (<c>Labeller.InputSetting</c> -> <c>SetLogicValue(LogicType.Setting)</c>). Each class implements the property
/// itself, so the setters are found when the patches go in, over every loaded assembly: modded devices
/// included.</description></item>
/// <item><term><c>GridController.Detatch(Structure)</c>, prefix</term><description>Detached, read before the game
/// takes it off the grid: a rocket part at launch (<c>Rocket.cs:1637-1656</c>).</description></item>
/// <item><term><c>GridController.Attach(Structure)</c>, postfix</term><description>Attached: the new cell and turn,
/// from the structure's transform, become a move.</description></item>
/// </list>
/// Main thread only, like the game code they patch. On a client the same setters run while the host's state arrives,
/// so the signals work there too (checked by the two-player tests).
/// </summary>
internal static class WorldPatches
{
    private const string HarmonyId = "lattice.world";

    private static Harmony? _harmony;
    private static List<MethodBase>? _patched;

    /// <summary>The running world's relay; set when a world runs, cleared when it ends.</summary>
    public static WorldChangeRelay? Current { get; set; }

    /// <summary>Whether the patches are in.</summary>
    public static bool Installed => _patched != null;

    /// <summary>
    /// Checks every member of the module and patches the game. Refused, with nothing patched, when a member is missing
    /// or a patch fails; installing again after success does nothing.
    /// </summary>
    public static Result<Unit, Reason> Install()
    {
        if (_patched != null)
            return Result<Unit, Reason>.Ok(Unit.Value);
        var missing = GameMembers.Missing(WorldManifest.Module);
        if (missing.Count > 0)
            return Result<Unit, Reason>.Error(MissingMembers(missing));

        var harmony = new Harmony(HarmonyId);
        var patched = new List<MethodBase>(8);
        try
        {
            Patch(harmony, patched, Method(WorldManifest.Register), postfix: nameof(AfterRegister));
            Patch(harmony, patched, Method(WorldManifest.Deregister), postfix: nameof(AfterDeregister));
            Patch(harmony, patched, Setter(WorldManifest.BuildStateIndex), nameof(BeforeBuildState), nameof(AfterBuildState));
            Patch(harmony, patched, Method(WorldManifest.SetCustomColor), nameof(BeforePaint), nameof(AfterPaint));
            Patch(harmony, patched, Setter(WorldManifest.CustomName), postfix: nameof(AfterRename));
            Patch(harmony, patched, Method(WorldManifest.Detatch), nameof(BeforeDetatch));
            Patch(harmony, patched, Method(WorldManifest.Attach), postfix: nameof(AfterAttach));
            foreach (var setter in SettingSetters())
                Patch(harmony, patched, setter, nameof(BeforeSetting), nameof(AfterSetting));
        }
        catch (Exception e)
        {
            Log($"Lattice world bridge: patching {(patched.Count < 1 ? "the first target" : "after " + patched[patched.Count - 1].Name)} failed, undoing every world patch: {e}");
            try
            {
                harmony.UnpatchSelf();
            }
            catch (Exception undo)
            {
                Log($"Lattice world bridge: undoing the world patches failed: {undo}");
            }

            return Result<Unit, Reason>.Error(Reason.Of(
                ReasonCodes.ModuleMissing,
                WorldReasons.World,
                ValueMap<string, string>.Empty.With("module", WorldManifest.Module.Name.Value).With("problem", e.Message)));
        }

        _harmony = harmony;
        _patched = patched;
        return Result<Unit, Reason>.Ok(Unit.Value);
    }

    /// <summary>Undoes every patch made here (process end, tests).</summary>
    public static void Uninstall()
    {
        if (_harmony == null || _patched == null)
            return;
        _harmony.UnpatchSelf();
        _patched = null;
        _harmony = null;
    }

    /// <summary>The declared <c>Setting</c> setters of every structure type that implements <c>ISetable</c>.</summary>
    private static List<MethodBase> SettingSetters()
    {
        var setters = new List<MethodBase>();
        var interfaceName = typeof(ISetable).FullName + ".Setting";
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            foreach (var type in TypesOf(assembly))
            {
                if (type == null || type.IsInterface || !typeof(Structure).IsAssignableFrom(type) || !typeof(ISetable).IsAssignableFrom(type))
                    continue;
                foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if ((property.Name == "Setting" || property.Name == interfaceName)
                        && property.PropertyType == typeof(double)
                        && property.GetSetMethod(true) is { IsAbstract: false } setter
                        && !setters.Contains(setter))
                        setters.Add(setter);
            }

        return setters;
    }

    private static Type?[] TypesOf(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            return e.Types;
        }
    }

    private static Reason MissingMembers(ValueList<GameMember> missing)
    {
        var facts = ValueMap<string, string>.Empty.With("module", WorldManifest.Module.Name.Value);
        var i = 0;
        foreach (var member in missing)
            facts = facts.With($"missing-{i++}", $"{member.TypeName}.{member.MemberName}");
        return Reason.Of(ReasonCodes.ModuleMissing, WorldReasons.World, facts);
    }

    private static MethodBase Method(GameMember.Method method) =>
        GameMembers.Method(method) ?? throw new MissingMethodException(method.DeclaringType, method.Name);

    private static MethodBase Setter(GameMember.Property property) =>
        GameMembers.Property(property)?.GetSetMethod(true) ?? throw new MissingMethodException(property.DeclaringType, "set_" + property.Name);

    private static void Patch(Harmony harmony, List<MethodBase> patched, MethodBase original, string? prefix = null, string? postfix = null)
    {
        harmony.Patch(original, prefix: Hook(prefix), postfix: Hook(postfix));
        patched.Add(original);
    }

    private static HarmonyMethod? Hook(string? name) =>
        name == null ? null : new HarmonyMethod(typeof(WorldPatches).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static));

    /// <summary>
    /// A fault in a patch body is a Lattice bug: the bridge is off for this world (no more reports), the fault is logged
    /// once, and the game's call goes on.
    /// </summary>
    private static readonly Action<string, Exception> OnFault = static (patch, e) =>
    {
        if (Current == null)
            return;
        Current = null;
        Log($"Lattice world bridge off for this world: patch {patch} threw {e}");
    };

    private static void Log(string text) => UnityEngine.Debug.LogError(text);

    private static void AfterRegister(Structure structure)
    {
        if (Current != null)
            PatchGuard.Run(nameof(AfterRegister), structure, static s =>
            {
                if (s != null && !s.IsCursor && GameReads.Info(s).TryGet(out var info))
                    Current?.Report(new WorldChange.Registered(info));
            }, OnFault);
    }

    private static void AfterDeregister(Structure structure)
    {
        if (Current != null)
            PatchGuard.Run(nameof(AfterDeregister), structure, static s =>
            {
                if (s != null && !s.IsCursor)
                    Current?.Report(new WorldChange.Deregistered(GameReads.Ref(s)));
            }, OnFault);
    }

    private static void BeforeBuildState(Structure __instance, out int __state) =>
        __state = Current == null
            ? int.MinValue
            : PatchGuard.Run(nameof(BeforeBuildState), __instance, static s => s.CurrentBuildStateIndex, int.MinValue, OnFault);

    private static void AfterBuildState(Structure __instance, int __state)
    {
        if (Current != null && __state != int.MinValue)
            PatchGuard.Run(nameof(AfterBuildState), __instance, __state, static (s, before) =>
            {
                if (!s.IsCursor && s.CurrentBuildStateIndex != before)
                    Current?.Report(new WorldChange.StateSet(GameReads.Ref(s), GameReads.State(s)));
            }, OnFault);
    }

    private static void BeforePaint(Thing __instance, out int __state) =>
        __state = Current == null || __instance is not Structure
            ? int.MinValue
            : PatchGuard.Run(nameof(BeforePaint), __instance, static t => GameReads.ColourIndex(t), int.MinValue, OnFault);

    private static void AfterPaint(Thing __instance, int __state)
    {
        if (Current != null && __state != int.MinValue)
            PatchGuard.Run(nameof(AfterPaint), __instance, __state, static (t, before) =>
            {
                if (t is not Structure structure || structure.IsCursor)
                    return;
                var index = GameReads.ColourIndex(structure);
                if (index != before && PaintColour.Create(index).TryGet(out var colour))
                    Current?.Report(new WorldChange.Painted(GameReads.Ref(structure), colour));
            }, OnFault);
    }

    private static void AfterRename(Thing __instance, string value)
    {
        if (Current != null)
            PatchGuard.Run(nameof(AfterRename), __instance, value, static (t, name) =>
            {
                if (t is Structure structure && !structure.IsCursor && !string.IsNullOrEmpty(name) && ThingName.Create(name).TryGet(out var valid))
                    Current?.Report(new WorldChange.Renamed(GameReads.Ref(structure), valid));
            }, OnFault);
    }

    private static void BeforeSetting(Structure __instance, out double __state) =>
        __state = Current == null || __instance is not ISetable
            ? double.NaN
            : PatchGuard.Run(nameof(BeforeSetting), __instance, static s => ((ISetable)s).Setting, double.NaN, OnFault);

    private static void AfterSetting(Structure __instance, double __state)
    {
        if (Current != null && !double.IsNaN(__state))
            PatchGuard.Run(nameof(AfterSetting), __instance, __state, static (s, before) =>
            {
                if (s.IsCursor || s is not ISetable setable)
                    return;
                var value = setable.Setting;
                if (!value.Equals(before) && Setting.Create(value).TryGet(out var setting))
                    Current?.Report(new WorldChange.SettingSet(GameReads.Ref(s), setting));
            }, OnFault);
    }

    private static void BeforeDetatch(Structure structure)
    {
        if (Current != null)
            PatchGuard.Run(nameof(BeforeDetatch), structure, static s =>
            {
                if (s != null && !s.IsCursor)
                    Current?.Report(new WorldChange.Detached(GameReads.Ref(s)));
            }, OnFault);
    }

    private static void AfterAttach(Structure structure)
    {
        if (Current != null)
            PatchGuard.Run(nameof(AfterAttach), structure, static s =>
            {
                if (s == null || s.IsCursor)
                    return;
                var at = GameReads.Ref(s).At;
                if (TurnReading.FromGame(GameReads.Rotation(s.ThingTransformRotation)).TryGet(out var turn))
                    Current?.Report(new WorldChange.Attached(new ReferenceId(s.ReferenceId), at, turn));
            }, OnFault);
    }
}
