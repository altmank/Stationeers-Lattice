using System.Collections.Generic;
using Lattice.Api;

namespace Lattice.Core.Catalogue;

/// <summary>
/// The placement rule groups of the game's placement checks: what a piece needs from other pieces or its surroundings. A piece takes the group of
/// the first type in its chain the census names, so a modded subclass takes its base-game parent's group (the same
/// test as <c>IsAssignableFrom</c> on the census classes). A class the census does not know that declares its own
/// placement check takes <see cref="AskGame"/>: only the game's own check can answer for it.
/// </summary>
internal static class SupportGroups
{
    /// <summary>A frame (a structure with <c>AllowMounting</c>) half a grid below.</summary>
    public static readonly RuleId FrameBelow = RuleId.Known("lattice.support.frame-below");

    /// <summary>A frame or solid ground below (<c>Flag</c>).</summary>
    public static readonly RuleId FrameOrGround = RuleId.Known("lattice.support.frame-or-ground");

    /// <summary>On terrain only (<c>DeepMiner</c>).</summary>
    public static readonly RuleId Ground = RuleId.Known("lattice.support.ground");

    /// <summary>A frame or another tower below (<c>RocketTower</c>).</summary>
    public static readonly RuleId FrameOrTower = RuleId.Known("lattice.support.frame-or-tower");

    /// <summary>Several pillars below (<c>LaunchMount</c>).</summary>
    public static readonly RuleId Pillars = RuleId.Known("lattice.support.pillars");

    /// <summary>A blocked face behind the mount point.</summary>
    public static readonly RuleId WallMount = RuleId.Known("lattice.support.wall-mount");

    /// <summary>On a cable that is not broken.</summary>
    public static readonly RuleId OnCable = RuleId.Known("lattice.support.on-cable");

    /// <summary>On a pipe of the right content that is not burst.</summary>
    public static readonly RuleId OnPipe = RuleId.Known("lattice.support.on-pipe");

    /// <summary>On a pipe valve (organ pipe).</summary>
    public static readonly RuleId OnPipeValve = RuleId.Known("lattice.support.on-pipe-valve");

    /// <summary>On a hydroponics tray (harvester).</summary>
    public static readonly RuleId OnTray = RuleId.Known("lattice.support.on-tray");

    /// <summary>On a structure it extends.</summary>
    public static readonly RuleId OnExtendable = RuleId.Known("lattice.support.on-extendable");

    /// <summary>Inside a rocket tower (rocket umbilicals).</summary>
    public static readonly RuleId InRocketTower = RuleId.Known("lattice.support.in-rocket-tower");

    /// <summary>Outside every room (Industrial Filtration): ghost walls that close a room change the answer.</summary>
    public static readonly RuleId Outside = RuleId.Known("lattice.support.outside");

    /// <summary>A rocket part with its own rules (fuselage, nose cone, bulkhead, engines).</summary>
    public static readonly RuleId Rocket = RuleId.Known("lattice.support.rocket");

    /// <summary>Elevator shaft merge and rotation rules.</summary>
    public static readonly RuleId ElevatorShaft = RuleId.Known("lattice.support.elevator-shaft");

    /// <summary>Only inside a rocket (a strictly internal rocket piece).</summary>
    public static readonly RuleId InRocket = RuleId.Known("lattice.support.in-rocket");

    /// <summary>A placement check the census does not know: ask the game's own check.</summary>
    public static readonly RuleId AskGame = RuleId.Known("lattice.support.ask-game");

    /// <summary>Every group above.</summary>
    public static readonly ValueList<RuleId> All = ValueList.From(new[]
    {
        FrameBelow, FrameOrGround, Ground, FrameOrTower, Pillars, WallMount, OnCable, OnPipe, OnPipeValve, OnTray,
        OnExtendable, InRocketTower, Outside, Rocket, ElevatorShaft, InRocket, AskGame,
    });

    private static readonly ValueMap<string, RuleId> ByType = ValueMap<string, RuleId>.From(Table());

    // Census classes whose own check only looks at what already holds the spot (slots, collisions, ports): they
    // need no other piece, so they add no group.
    private static readonly ValueSet<string> SpotOnly = ValueSet.From(new[]
    {
        "Assets.Scripts.Objects.Structure",
        "Assets.Scripts.Objects.SmallGrid",
        "Assets.Scripts.Objects.Pipes.Device",
        "Assets.Scripts.Objects.Electrical.Cable",
        "Assets.Scripts.Objects.Pipes.Pipe",
        "Assets.Scripts.Objects.Pipes.Chute",
        "Assets.Scripts.Objects.Wall",
        "Assets.Scripts.Objects.Structures.Door",
        "Assets.Scripts.Objects.Structures.Airlock",
        "Assets.Scripts.Objects.Structures.RoboticArmDoor",
        "Assets.Scripts.Objects.WindowShutterController",
        "Objects.Pipes.AccessBridge",
    });

    /// <summary>
    /// The group of a piece from its types (own class first, then its bases, then its interfaces) and whether it is
    /// a strictly internal rocket piece; none when it needs nothing beyond its own spot.
    /// </summary>
    public static Option<RuleId> Of(ValueList<TypeData> types, bool rocketOnly)
    {
        foreach (var type in types)
        {
            if (ByType.TryGetValue(type.FullName, out var group))
                return Option.Some(group);
            if (type.DeclaresPlacementCheck && !SpotOnly.Contains(type.FullName))
                return Option.Some(AskGame);
        }

        return rocketOnly ? Option.Some(InRocket) : Option<RuleId>.None;
    }

    /// <summary>Reads a group as written: one of Lattice's above, or a mod's own rule id (<c>owner/name</c>).</summary>
    public static Result<RuleId, Reason> Parse(string text)
    {
        foreach (var group in All)
            if (group.Value == text)
                return Result<RuleId, Reason>.Ok(group);
        var slash = text.IndexOf('/');
        if (slash <= 0)
            return Result<RuleId, Reason>.Error(CatalogueReasons.Piece(text, "unknown-support-group"));
        return ModId.Create(text.Substring(0, slash)).Bind(owner => RuleId.Create(owner, text.Substring(slash + 1)));
    }

    private static Dictionary<string, RuleId> Table()
    {
        var table = new Dictionary<string, RuleId>(64);
        Add(table, FrameBelow,
            "Assets.Scripts.LargeElectrical",
            "Assets.Scripts.Objects.MountedSmallGrid",
            "Assets.Scripts.Objects.Radiator",
            "Assets.Scripts.Objects.RadiatorRotatable",
            "Assets.Scripts.Objects.Electrical.Battery",
            "Assets.Scripts.Objects.Electrical.FabricatorBase",
            "Assets.Scripts.Objects.Electrical.HydroponicsAutomated",
            "Assets.Scripts.Objects.Electrical.HydroponicsStation",
            "Assets.Scripts.Objects.Electrical.Sleeper",
            "Assets.Scripts.Objects.Electrical.SolidFuelGenerator",
            "Assets.Scripts.Objects.Electrical.StirlingEngine",
            "Assets.Scripts.Objects.Pipes.AdvancedFurnace",
            "Assets.Scripts.Objects.Pipes.ArcFurnace",
            "Assets.Scripts.Objects.Pipes.CombustorMachine",
            "Assets.Scripts.Objects.Pipes.DeviceInputOutputCircuit",
            "Assets.Scripts.Objects.Pipes.DeviceInputOutputImportExportCircuit",
            "Assets.Scripts.Objects.Pipes.ElectrolysisMachine",
            "Assets.Scripts.Objects.Pipes.H2CombustorMachine",
            "Assets.Scripts.Objects.Pipes.HeatExchangerBase",
            "Assets.Scripts.Objects.Pipes.IceCrusher",
            "Assets.Scripts.Objects.Pipes.IndustrialCombustor",
            "Assets.Scripts.Objects.Pipes.RocketDataUpLink",
            "Assets.Scripts.Objects.Pipes.WaterPurifier",
            "Assets.Scripts.Objects.Structures.CrateMount",
            "Assets.Scripts.Objects.Structures.Stairs",
            "Objects.WindTurbineGenerator",
            "Objects.Electrical.LandingPadModular",
            "Objects.Pipes.DeviceInputOutputImportCircuit");
        Add(table, FrameOrGround, "Assets.Scripts.Objects.Flag");
        Add(table, Ground, "Assets.Scripts.Objects.Pipes.DeepMiner");
        Add(table, FrameOrTower, "Objects.Structures.RocketTower");
        Add(table, Pillars, "Objects.Rockets.LaunchMount");
        Add(table, WallMount,
            "Assets.Scripts.Objects.Electrical.AreaPowerControl",
            "Assets.Scripts.Objects.Electrical.BatteryCellCharger",
            "Assets.Scripts.Objects.Electrical.Console",
            "Assets.Scripts.Objects.Electrical.Speaker",
            "Assets.Scripts.Objects.Pipes.ActiveVent",
            "Assets.Scripts.Objects.Pipes.PoweredVent",
            "Assets.Scripts.Objects.Structures.WallLight",
            "Assets.Scripts.Objects.Structures.CrewModuleWallLight",
            "Objects.Rockets.CrewModuleActiveVent");
        Add(table, OnCable, "Assets.Scripts.Objects.Electrical.DeviceCableMounted");
        Add(table, OnPipe, "Assets.Scripts.Objects.Pipes.DevicePipeMounted");
        Add(table, OnPipeValve, "Objects.Pipes.OrganPipe");
        Add(table, OnTray, "Assets.Scripts.Objects.Chutes.Harvester");
        Add(table, OnExtendable, "Assets.Scripts.Objects.Pipes.StructureExtension");
        Add(table, InRocketTower,
            "Objects.Rockets.RocketChuteUmbilicalMale",
            "Objects.Rockets.RocketCrewUmbilical",
            "Objects.Rockets.RocketGasUmbilicalMale",
            "Objects.Rockets.RocketPowerUmbilicalMale");
        Add(table, Outside, "Assets.Scripts.Objects.Pipes.IndustrialFiltration");
        Add(table, Rocket,
            "Objects.Rockets.CrewBulkhead",
            "Objects.Rockets.EngineFuselage",
            "Objects.Rockets.Fuselage",
            "Objects.Rockets.NoseCone",
            "Objects.Rockets.StructureFuselage",
            "Assets.Scripts.Objects.Pipes.RocketEngineBase");
        Add(table, ElevatorShaft, "Assets.Scripts.Objects.Electrical.ElevatorShaft");
        return table;
    }

    private static void Add(Dictionary<string, RuleId> table, RuleId group, params string[] types)
    {
        foreach (var type in types)
            table.Add(type, group);
    }
}
