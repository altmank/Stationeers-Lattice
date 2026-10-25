using System;
using Lattice.Api;
using Lattice.Core.Modules;

namespace Lattice.Core.World;

/// <summary>
/// Every game member the world bridge uses, checked at load: the bridge is off, with the missing members
/// named, when the game lacks any of them. A test reads the bridge's compiled references and fails when one is not
/// listed here, and checks each entry against the game's assemblies.
/// </summary>
internal static class WorldManifest
{
    private const string Structure = "Assets.Scripts.Objects.Structure";
    private const string Thing = "Assets.Scripts.Objects.Thing";
    private const string GridController = "Assets.Scripts.GridController";

    /// <summary>The patch target for structures joining the grid.</summary>
    public static readonly GameMember.Method Register = new(GridController, "Register", Params(Structure));

    /// <summary>The patch target for structures leaving the grid.</summary>
    public static readonly GameMember.Method Deregister = new(GridController, "Deregister", Params(Structure));

    /// <summary>The patch target for structures the game takes off the grid to move.</summary>
    public static readonly GameMember.Method Detatch = new(GridController, "Detatch", Params(Structure));

    /// <summary>The patch target for moved structures put back on the grid.</summary>
    public static readonly GameMember.Method Attach = new(GridController, "Attach", Params(Structure));

    /// <summary>The patch target for build state changes (its setter).</summary>
    public static readonly GameMember.Property BuildStateIndex = new(Structure, "CurrentBuildStateIndex");

    /// <summary>The patch target for paint.</summary>
    public static readonly GameMember.Method SetCustomColor = new(Thing, "SetCustomColor", Params("System.Int32", "System.Boolean"));

    /// <summary>The patch target for names (its setter).</summary>
    public static readonly GameMember.Property CustomName = new(Thing, "CustomName");

    /// <summary>The world bridge module: its name, no other module, and every game member it uses.</summary>
    public static readonly ModuleManifest Module = new(
        ModuleName.Create("world").TryGet(out var name) ? name : throw new InvalidOperationException("module name"),
        ValueList<ModuleName>.Empty,
        ValueList.From(new GameMember[]
        {
            Register, Deregister, Detatch, Attach, BuildStateIndex, SetCustomColor, CustomName,
            new GameMember.Method("UnityEngine.Debug", "LogError", Params("System.Object")),
            new GameMember.Field("Assets.Scripts.GameManager", "GameTime"),
            new GameMember.Field("Assets.Scripts.GridController", "AllStructuresPool"),
            new GameMember.Type("Assets.Scripts.Util.DensePool`1"),
            new GameMember.Type("Assets.Scripts.Objects.Items.ISetable"),
            new GameMember.Property("Assets.Scripts.Objects.Items.ISetable", "Setting"),
            new GameMember.Field("Assets.Scripts.Atmospherics.Atmosphere", "GasMixture"),
            new GameMember.Field("Assets.Scripts.GridController", "AtmosphericsController"),
            new GameMember.Field("Assets.Scripts.GridController", "World"),
            new GameMember.Field("Assets.Scripts.GridSystem.Cell", "AllStructures"),
            new GameMember.Field("Assets.Scripts.GridSystem.Grid3", "x"),
            new GameMember.Field("Assets.Scripts.GridSystem.Grid3", "y"),
            new GameMember.Field("Assets.Scripts.GridSystem.Grid3", "z"),
            new GameMember.Field("Assets.Scripts.GridSystem.Room", "GasMixture"),
            new GameMember.Field("Assets.Scripts.GridSystem.Room", "Grids"),
            new GameMember.Field("Assets.Scripts.GridSystem.Room", "RoomId"),
            new GameMember.Field("Assets.Scripts.GridSystem.SmallCell", "Cable"),
            new GameMember.Field("Assets.Scripts.GridSystem.SmallCell", "Chute"),
            new GameMember.Field("Assets.Scripts.GridSystem.SmallCell", "Device"),
            new GameMember.Field("Assets.Scripts.GridSystem.SmallCell", "Other"),
            new GameMember.Field("Assets.Scripts.GridSystem.SmallCell", "Pipe"),
            new GameMember.Field("Assets.Scripts.GridSystem.SmallCell", "Rail"),
            new GameMember.Field("Assets.Scripts.GridSystem.WorldGrid", "Value"),
            new GameMember.Field("Assets.Scripts.Inventory.InventoryManager", "_constructionCursors"),
            new GameMember.Field("Assets.Scripts.Networks.CableNetwork", "CableList"),
            new GameMember.Field("Assets.Scripts.Networks.CableNetwork", "CurrentLoad"),
            new GameMember.Field("Assets.Scripts.Networks.CableNetwork", "DeviceList"),
            new GameMember.Field("Assets.Scripts.Networks.PipeNetwork", "DeviceList"),
            new GameMember.Field("Assets.Scripts.Objects.ColorSwatch", "Normal"),
            new GameMember.Field("Assets.Scripts.Objects.ColorSwatch", "Color"),
            new GameMember.Field("Assets.Scripts.GameManager", "CustomColors"),
            new GameMember.Field("UnityEngine.Color", "r"),
            new GameMember.Field("UnityEngine.Color", "g"),
            new GameMember.Field("UnityEngine.Color", "b"),
            new GameMember.Field("UnityEngine.Color", "a"),
            new GameMember.Field("Assets.Scripts.Objects.Structure", "BuildStates"),
            new GameMember.Field("Assets.Scripts.Objects.Structure", "PlacementType"),
            new GameMember.Field("Assets.Scripts.Objects.Thing", "CustomColor"),
            new GameMember.Field("Assets.Scripts.Objects.Thing", "IsCursor"),
            new GameMember.Field("Assets.Scripts.Objects.Thing", "PaintableMaterial"),
            new GameMember.Field("Assets.Scripts.Objects.Thing", "PrefabHash"),
            new GameMember.Field("Assets.Scripts.Objects.Thing", "Rotation"),
            new GameMember.Field("Assets.Scripts.Objects.Thing", "ThingTransform"),
            new GameMember.Field("Assets.Scripts.RoomController", "World"),
            new GameMember.Field("UnityEngine.Quaternion", "w"),
            new GameMember.Field("UnityEngine.Quaternion", "x"),
            new GameMember.Field("UnityEngine.Quaternion", "y"),
            new GameMember.Field("UnityEngine.Quaternion", "z"),
            new GameMember.Field("UnityEngine.Vector3", "x"),
            new GameMember.Field("UnityEngine.Vector3", "y"),
            new GameMember.Field("UnityEngine.Vector3", "z"),
            new GameMember.Method("Assets.Scripts.Atmospherics.GasMixture", "GetMoleValue", Params("Assets.Scripts.Atmospherics.Chemistry+GasType")),
            new GameMember.Method("Assets.Scripts.Atmospherics.MoleEnergy", "ToDouble", Params()),
            new GameMember.Method("Assets.Scripts.Atmospherics.MoleQuantity", "ToDouble", Params()),
            new GameMember.Method("Assets.Scripts.Atmospherics.PressurekPa", "ToDouble", Params()),
            new GameMember.Method("Assets.Scripts.Atmospherics.VolumeLitres", "ToDouble", Params()),
            new GameMember.Method("Assets.Scripts.Atmospherics.TemperatureKelvin", "ToDouble", Params()),
            new GameMember.Property("Assets.Scripts.Atmospherics.GasMixture", "Temperature"),
            new GameMember.Method("TerrainSystem.VoxelTerrain", "GetDensityWorldSpace", Params("UnityEngine.Vector3")),
            new GameMember.Method("Assets.Scripts.AtmosphericsController", "SampleGlobalAtmosphere", Params("Assets.Scripts.GridSystem.WorldGrid")),
            new GameMember.Method("Assets.Scripts.GameManager", "GetColorIndex", Params("Assets.Scripts.Objects.ColorSwatch")),
            new GameMember.Method("Assets.Scripts.GridController", "GetCell", Params("Assets.Scripts.GridSystem.Grid3")),
            new GameMember.Method("Assets.Scripts.GridController", "GetFaceStructures", Params("Assets.Scripts.GridSystem.Grid3")),
            new GameMember.Method("Assets.Scripts.GridController", "GetSmallCell", Params("Assets.Scripts.GridSystem.Grid3")),
            new GameMember.Method("Assets.Scripts.GridSystem.Grid3", ".ctor", Params("System.Int32", "System.Int32", "System.Int32")),
            new GameMember.Method("Assets.Scripts.GridSystem.WorldGrid", ".ctor", Params("System.Int32", "System.Int32", "System.Int32")),
            new GameMember.Method("Assets.Scripts.Objects.CanMountResult", "ResultMessage", Params()),
            new GameMember.Method("Assets.Scripts.Objects.CanMountResult", "op_Implicit", Params("Assets.Scripts.Objects.CanMountResult")),
            new GameMember.Method("Assets.Scripts.Objects.Structure", "CanConstruct", Params()),
            new GameMember.Method("Assets.Scripts.Objects.Structure", "CanMountOnWall", Params()),
            new GameMember.Method("Assets.Scripts.RoomController", "Get", Params("System.Int64")),
            new GameMember.Method("Assets.Scripts.RoomController", "GetRoom", Params("Assets.Scripts.GridSystem.Grid3")),
            new GameMember.Method("Referencable", "Find", Params("System.Int64")),
            new GameMember.Method("UnityEngine.GameObject", ".ctor", Params("System.String")),
            new GameMember.Method("UnityEngine.GameObject", "AddComponent", Params()),
            new GameMember.Method("UnityEngine.MonoBehaviour", ".ctor", Params()),
            new GameMember.Method("UnityEngine.Object", "Destroy", Params("UnityEngine.Object")),
            new GameMember.Method("UnityEngine.Object", "DontDestroyOnLoad", Params("UnityEngine.Object")),
            new GameMember.Method("UnityEngine.Object", "op_Equality", Params("UnityEngine.Object", "UnityEngine.Object")),
            new GameMember.Method("UnityEngine.Object", "op_Inequality", Params("UnityEngine.Object", "UnityEngine.Object")),
            new GameMember.Method("UnityEngine.Quaternion", ".ctor", Params("System.Single", "System.Single", "System.Single", "System.Single")),
            new GameMember.Method("UnityEngine.Transform", "SetPositionAndRotation", Params("UnityEngine.Vector3", "UnityEngine.Quaternion")),
            new GameMember.Method("UnityEngine.Vector3", ".ctor", Params("System.Single", "System.Single", "System.Single")),
            new GameMember.Property("Assets.Scripts.Atmospherics.Atmosphere", "PressureGassesAndLiquids"),
            new GameMember.Property("Assets.Scripts.Atmospherics.Atmosphere", "Volume"),
            new GameMember.Property("Assets.Scripts.Atmospherics.Mole", "Energy"),
            new GameMember.Property("Assets.Scripts.Atmospherics.Mole", "Quantity"),
            new GameMember.Property("Assets.Scripts.Networks.CableNetwork", "ReferenceId"),
            new GameMember.Property("Assets.Scripts.Objects.CanConstructInfo", "CanConstruct"),
            new GameMember.Property("Assets.Scripts.Objects.CanConstructInfo", "ErrorMessage"),
            new GameMember.Property("Assets.Scripts.Objects.Electrical.Cable", "CableNetwork"),
            new GameMember.Property("Assets.Scripts.Objects.Pipes.Device", "DataCableNetwork"),
            new GameMember.Property("Assets.Scripts.Objects.Pipes.Device", "PowerCableNetwork"),
            new GameMember.Property("Assets.Scripts.Objects.Thing", "BeingDestroyed"),
            new GameMember.Property("Assets.Scripts.Objects.Thing", "Position"),
            new GameMember.Property("Assets.Scripts.Objects.Thing", "ReferenceId"),
            new GameMember.Property("Assets.Scripts.Objects.Thing", "ThingTransformPosition"),
            new GameMember.Property("Assets.Scripts.Objects.Thing", "ThingTransformRotation"),
            new GameMember.Property("Networks.AtmosphericsNetwork", "Atmosphere"),
            new GameMember.Property("Networks.AtmosphericsNetwork", "NetworkContentType"),
            new GameMember.Property("Networks.INetworkedStructure", "StructureNetwork"),
            new GameMember.Property("Networks.ReferencableNetwork", "ReferenceId"),
            new GameMember.Property("Networks.StructureNetwork", "NetworkType"),
            new GameMember.Property("Networks.StructureNetwork", "StructureList"),
            new GameMember.Property("UnityEngine.Transform", "position"),
            new GameMember.Property("UnityEngine.Transform", "rotation"),
            new GameMember.Type("Assets.Scripts.Atmospherics.Chemistry+GasType"),
            new GameMember.Type("Assets.Scripts.GridSystem.Grid3"),
            new GameMember.Type("Assets.Scripts.GridSystem.WorldGrid"),
            new GameMember.Type("Assets.Scripts.Inventory.InventoryManager"),
            new GameMember.Type("Assets.Scripts.Networks.CableNetwork"),
            new GameMember.Type("Assets.Scripts.Networks.ChuteNetwork"),
            new GameMember.Type("Assets.Scripts.Networks.PipeNetwork"),
            new GameMember.Type("Assets.Scripts.Objects.BuildState"),
            new GameMember.Type("Assets.Scripts.Objects.CanMountResult"),
            new GameMember.Type("Assets.Scripts.Objects.ColorSwatch"),
            new GameMember.Type("Assets.Scripts.Util.Singleton`1"),
            new GameMember.Type("UnityEngine.Color"),
            new GameMember.Type("Assets.Scripts.Objects.Electrical.Cable"),
            new GameMember.Type("Assets.Scripts.Objects.Pipes.Device"),
            new GameMember.Type("Networks.AtmosphericsNetwork"),
            new GameMember.Type("Networks.INetworkedStructure"),
            new GameMember.Type("Networks.StructureNetwork"),
            new GameMember.Type("UnityEngine.GameObject"),
            new GameMember.Type("UnityEngine.Object"),
            new GameMember.Type("UnityEngine.Quaternion"),
            new GameMember.Type("UnityEngine.Vector3"),
        }));

    private static ValueList<string> Params(params string[] types) => ValueList.From(types);
}
