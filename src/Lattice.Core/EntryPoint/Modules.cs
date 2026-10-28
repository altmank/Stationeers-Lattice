using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Executors;
using Lattice.Core.Modules;

namespace Lattice.Core.EntryPoint;

/// <summary>The game members a module's manifest names that the running game does not have.</summary>
internal interface IGameMembers
{
    /// <summary>The missing members of <paramref name="manifest"/>; empty when the game has all of them.</summary>
    ValueList<GameMember> Missing(ModuleManifest manifest);
}

/// <summary>
/// Where a module runs: on every seat, on the host only (it owns what only the host has), or only on a seat that draws
/// (not in batch mode: a dedicated server or a batch-mode client).
/// </summary>
internal enum ModuleSeats
{
    Both = 1,
    HostOnly = 2,
    Drawing = 3,
}

/// <summary>One of Lattice's modules: its manifest (name, the modules it needs, the game members it uses) and its seats.</summary>
internal sealed record ModuleEntry(ModuleManifest Manifest, ModuleSeats Seats)
{
    public ModuleName Name => Manifest.Name;
}

/// <summary>
/// Lattice's modules, in dependency order (a module comes after every module it needs). The catalogue is the
/// kernel's: when it is off, Lattice does not start for the world. Clients hold the world mirror and run previews
/// locally (architecture section 2); matching and completion, saving and the inspector are the host's, and reach
/// clients as change sets.
/// </summary>
internal static class LatticeModules
{
    public static ModuleName Catalogue => Name("catalogue");

    public static ModuleName World => Name("world");

    public static ModuleName Mirror => Name("mirror");

    public static ModuleName Matching => Name("matching");

    public static ModuleName Selectors => Name("selectors");

    public static ModuleName Validation => Name(global::Lattice.Core.Validation.ValidationCodes.Module);

    public static ModuleName Steps => Name("steps");

    public static ModuleName Safety => Name("safety");

    public static ModuleName Plans => Name("plans");

    public static ModuleName Runs => global::Lattice.Core.Runs.RunExpander.Module;

    public static ModuleName Routes => global::Lattice.Core.Routes.RouteExpander.Module;

    public static ModuleName Swaps => global::Lattice.Core.Swaps.SwapExpander.Module;

    public static ModuleName Transforms => global::Lattice.Core.Transforms.TransformExpander.Module;

    public static ModuleName Templates => global::Lattice.Core.Transforms.TemplateExpander.Module;

    public static ModuleName Saving => Name("saving");

    public static ModuleName FinishedPlans => global::Lattice.Core.FinishedPlans.FinishedCodes.Module;

    public static ModuleName Inspector => Name("inspector");

    public static ModuleName Executors => Name("executors");

    public static ModuleName Sync => Name("sync");

    public static ModuleName Preview => Name("preview");

    public static ModuleName Analysis => Name("analysis");

    /// <summary>Style resolution: layers, sets, drafts, the clip, picking and the story behind a look, on every seat.</summary>
    public static ModuleName Styles => Name("styles");

    /// <summary>Drawing: the draw plan and labels the renderer reads; without it styles keep no plan.</summary>
    public static ModuleName Render => Name("render");

    /// <summary>Every module, in dependency order.</summary>
    public static ValueList<ModuleEntry> All => ValueList.From(new[]
    {
        Entry(global::Lattice.Core.Catalogue.CatalogueManifest.Module.Uses, Catalogue, ModuleSeats.Both),
        Entry(global::Lattice.Core.World.WorldManifest.Module.Uses, World, ModuleSeats.Both),
        Entry(ValueList<GameMember>.Empty, Mirror, ModuleSeats.Both, World),
        Entry(ValueList<GameMember>.Empty, Matching, ModuleSeats.HostOnly, Mirror),
        Entry(ValueList<GameMember>.Empty, Selectors, ModuleSeats.Both, Mirror),
        Entry(ValueList<GameMember>.Empty, Validation, ModuleSeats.Both, Mirror),
        Entry(ValueList<GameMember>.Empty, Steps, ModuleSeats.Both, Mirror),
        Entry(ValueList<GameMember>.Empty, Safety, ModuleSeats.Both, World),
        Entry(ValueList<GameMember>.Empty, Plans, ModuleSeats.Both, Validation, Steps, Safety),
        Entry(ValueList<GameMember>.Empty, Runs, ModuleSeats.Both, World),
        Entry(ValueList<GameMember>.Empty, Routes, ModuleSeats.Both, Runs),
        Entry(ValueList<GameMember>.Empty, Swaps, ModuleSeats.Both, World),
        Entry(ValueList<GameMember>.Empty, Transforms, ModuleSeats.Both),
        Entry(ValueList<GameMember>.Empty, Templates, ModuleSeats.Both, Transforms),
        Entry(global::Lattice.Core.Saving.SavingManifest.Module.Uses, Saving, ModuleSeats.HostOnly),
        Entry(ValueList<GameMember>.Empty, FinishedPlans, ModuleSeats.HostOnly, Saving, Mirror),
        Entry(ValueList<GameMember>.Empty, Inspector, ModuleSeats.HostOnly, Saving),
        Entry(ValueList<GameMember>.Empty, Executors, ModuleSeats.Both, World),
        Entry(global::Lattice.Core.Sync.SyncManifest.Module.Uses, Sync, ModuleSeats.Both),
        Entry(ValueList<GameMember>.Empty, Preview, ModuleSeats.Both, Mirror, Steps, Safety),
        Entry(ValueList<GameMember>.Empty, Analysis, ModuleSeats.Both, Mirror),
        Entry(ValueList<GameMember>.Empty, Styles, ModuleSeats.Both),
        Entry(ValueList<GameMember>.Empty, Render, ModuleSeats.Drawing, Styles),
    });

    private static ModuleEntry Entry(ValueList<GameMember> uses, ModuleName name, ModuleSeats seats, params ModuleName[] needs) =>
        new(new ModuleManifest(name, ValueList.From(needs), uses), seats);

    internal static ModuleName Name(string value) =>
        ModuleName.Create(value).TryGet(out var name) ? name : throw new InvalidOperationException("module name " + value);
}

/// <summary>The modules of one world, each on or off with the reason, decided once when the world starts.</summary>
internal sealed class ModuleTable
{
    private readonly Dictionary<ModuleName, ModuleState> _states;

    public ModuleTable(ValueList<ModuleStatus> statuses)
    {
        Statuses = statuses;
        _states = new Dictionary<ModuleName, ModuleState>(statuses.Count);
        foreach (var status in statuses)
            _states[status.Name] = status.State;
    }

    /// <summary>Every module in dependency order.</summary>
    public ValueList<ModuleStatus> Statuses { get; }

    /// <summary>Whether the module runs; a module the table does not name does not.</summary>
    public bool IsOn(ModuleName module) => _states.TryGetValue(module, out var state) && state is ModuleState.On;

    /// <summary>Why a module is off; for a module that is on, nothing.</summary>
    public Option<Reason> Why(ModuleName module) => _states.TryGetValue(module, out var state)
        ? state is ModuleState.Off off ? Option.Some(off.Why) : Option<Reason>.None
        : Option.Some(ModuleSwitches.Unknown(module));

    /// <summary>The reason a call into an off module is refused with: its off reason, or module-missing for an unnamed one.</summary>
    public Reason Missing(ModuleName module) => Why(module).TryGet(out var why) ? why : ModuleSwitches.Unknown(module);
}

/// <summary>
/// Decides which modules run in a world (architecture section 12, "Game updates"): a module is off when the game lacks
/// a member its manifest names, when a module it needs is off, when it is host only and this seat is a client, or when
/// it draws and this seat runs in batch mode. The rest run. Off reasons come from <see cref="EntryCodes.Rule"/> with the
/// fact <c>module</c>: a host-only module on a client is <see cref="ReasonCodes.HostOnly"/> (<c>seat=client</c>); every
/// other is <see cref="ReasonCodes.ModuleMissing"/> with one of <c>missing</c> (up to eight member names, and
/// <c>missing-count</c>), <c>needs</c> (the module that is off) or <c>seat=batch-mode</c>.
/// Linear in the manifests and their members.
/// </summary>
internal static class ModuleSwitches
{
    private const int NamedMembers = 8;

    /// <param name="modules">Every module, in dependency order.</param>
    /// <param name="members">The game members the running game has.</param>
    /// <param name="seat">Host or client.</param>
    /// <param name="drawing">Whether the seat draws: false in batch mode (the game's <c>GameManager.IsBatchMode</c>).</param>
    public static ModuleTable Decide(ValueList<ModuleEntry> modules, IGameMembers members, WorldSeat seat, bool drawing = true)
    {
        var decided = new Dictionary<ModuleName, ModuleState>(modules.Count);
        var statuses = new List<ModuleStatus>(modules.Count);
        foreach (var module in modules)
        {
            var state = StateOf(module, members, seat, drawing, decided);
            decided[module.Name] = state;
            statuses.Add(new ModuleStatus(module.Name, state));
        }

        return new ModuleTable(ValueList.From(statuses));
    }

    private static ModuleState StateOf(ModuleEntry module, IGameMembers members, WorldSeat seat, bool drawing, Dictionary<ModuleName, ModuleState> decided)
    {
        if (module.Seats == ModuleSeats.HostOnly && seat != WorldSeat.Host)
            return Off(module.Name, ValueMap.Of("seat", "client"), ReasonCodes.HostOnly);
        if (module.Seats == ModuleSeats.Drawing && !drawing)
            return Off(module.Name, ValueMap.Of("seat", "batch-mode"));

        foreach (var need in module.Manifest.Needs)
        {
            if (!decided.TryGetValue(need, out var state))
                return Off(module.Name, ValueMap.Of("needs", need.Value).With("problem", "not-before"));
            if (state is not ModuleState.On)
                return Off(module.Name, ValueMap.Of("needs", need.Value));
        }

        if (module.Manifest.Uses.Count == 0)
            return new ModuleState.On();
        var missing = members.Missing(module.Manifest);
        return missing.Count == 0 ? new ModuleState.On() : Off(module.Name, MissingFacts(missing));
    }

    /// <summary>A member as the off reason names it: <c>Type.Member</c>, or the type's full name.</summary>
    public static string NameOf(GameMember member) =>
        member is GameMember.Type type ? type.FullName : member.TypeName + "." + member.MemberName;

    internal static Reason Unknown(ModuleName module) =>
        Reason.Of(ReasonCodes.ModuleMissing, EntryCodes.Rule, ValueMap.Of("module", module.Value).With("problem", "unknown-module"));

    private static ValueMap<string, string> MissingFacts(ValueList<GameMember> missing)
    {
        var names = new System.Text.StringBuilder();
        for (var i = 0; i < missing.Count && i < NamedMembers; i++)
        {
            if (i > 0)
                names.Append(',');
            names.Append(NameOf(missing[i]));
        }

        return ValueMap.Of("missing", names.ToString()).With("missing-count", missing.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static ModuleState Off(ModuleName module, ValueMap<string, string> facts) => Off(module, facts, ReasonCodes.ModuleMissing);

    private static ModuleState Off(ModuleName module, ValueMap<string, string> facts, ReasonCode code)
    {
        var all = ValueMap.Of("module", module.Value);
        foreach (var fact in facts)
            all = all.With(fact.Key, fact.Value);
        return new ModuleState.Off(Reason.Of(code, EntryCodes.Rule, all));
    }
}

/// <summary>The entry point's rule id, for the reasons it gives.</summary>
internal static class EntryCodes
{
    public static RuleId Rule => RuleId.Known("lattice.entry");
}

/// <summary>How the log names a module that is off.</summary>
internal static class ModuleLines
{
    /// <summary>
    /// The line for a module that is off: a host-only module on a client and a drawing module in batch mode say so in
    /// words (off by design, not a broken install), with the reason after; any other says why.
    /// </summary>
    public static string Off(ModuleName module, Reason why) =>
        why.Facts.TryGetValue("seat", out var seat) && seat == "client"
            ? $"Lattice: module {module.Value} is host-only; off on a client ({why})."
            : why.Facts.TryGetValue("seat", out var drawing) && drawing == "batch-mode"
                ? $"Lattice: module {module.Value} is drawing-only; off in batch mode ({why})."
                : $"Lattice: module {module.Value} is off: {why}";
}
