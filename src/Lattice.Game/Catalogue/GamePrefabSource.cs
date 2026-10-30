using System;
using System.Collections.Generic;
using System.Reflection;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Items;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects.Pipes;
using Assets.Scripts.Util;
using Lattice.Api;
using Lattice.Core.Catalogue;
using Lattice.Core.Ports;
using Lattice.Core.World;
using Lattice.Game.Looks;
using Objects.Rockets;
using UnityEngine;
using Localization = Assets.Scripts.Localization;
using Vector = Lattice.Core.Catalogue.Vector;
using Slot = Lattice.Core.Ports.Slot;

namespace Lattice.Game.Catalogue;

/// <summary>
/// The game's prefabs as the catalogue reads them. Kits and structures are listed from <c>Prefab.AllPrefabs</c> when
/// it is built, so every prefab the game holds at that moment counts, modded ones included; each structure's data
/// comes from its own prefab fields (decompile Assets.Scripts.Objects/Prefab.cs, Structure.cs,
/// MultiConstructor.cs, MultiMergeConstructor.cs, Constructor.cs, SmallGrid.cs, Connection.cs). The broken tools are the
/// item prefabs that are an <c>IConstructor</c> (Assets.Scripts.Objects.Items/IConstructor.cs). The spray cans are the
/// <c>SprayCan</c> prefabs, each with its <c>PaintMaterial</c>'s colour index (Assets.Scripts/GameManager.cs
/// <c>GetColorIndex</c>) and its <c>UseAmount</c> and <c>MaxQuantity</c> (Objects.Items/Consumable.cs); the labellers are
/// the <c>Labeller</c> prefabs (Assets.Scripts.Objects.Items/Labeller.cs). Build it after the game's prefabs are loaded.
/// </summary>
internal sealed class GamePrefabSource : IPrefabSource
{
    private const BindingFlags Declared =
        BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public GamePrefabSource()
    {
        var prefabs = Prefab.AllPrefabs;
        var kits = new List<KitData>(256);
        var structures = new List<PrefabKey>(prefabs.Count / 2);
        _structures = new List<Structure>(prefabs.Count / 2);
        var brokenTools = new List<ItemRef>(8);
        var sprayCans = new List<SprayCanData>(24);
        var labellers = new List<ItemRef>(2);
        _items = new List<Item>(prefabs.Count / 2);
        foreach (var thing in prefabs)
        {
            if (thing is Item item)
                _items.Add(item);
            if (thing is Item tool && tool is IConstructor)
                brokenTools.Add(Item(tool));
            if (thing is SprayCan can)
                sprayCans.Add(new SprayCanData(Item(can), ColourOf(can.PaintMaterial), can.UseAmount, can.MaxQuantity));
            if (thing is Labeller labeller)
                labellers.Add(Item(labeller));
            if (thing is MultiMergeConstructor merging)
                kits.Add(Kit(thing, merging.Constructables, KitKind.Merge) with
                {
                    MergeTool = merging.ToolExit == null ? Option<ItemRef>.None : Option.Some(Item(merging.ToolExit)),
                });
            else if (thing is MultiConstructor multi)
                kits.Add(Kit(thing, multi.Constructables, KitKind.Multi));
            else if (thing is Constructor single && single.BuildStructure != null)
                kits.Add(Kit(thing, new List<Structure>(1) { single.BuildStructure }, KitKind.Single));
            else if (thing is Structure structure)
            {
                structures.Add(new PrefabKey(thing.PrefabHash));
                _structures.Add(structure);
            }
        }

        Kits = kits;
        Structures = structures;
        BrokenTools = brokenTools;
        SprayCans = sprayCans;
        Labellers = labellers;
        _listed = new Lazy<IReadOnlyList<ListedPiece>>(ListAll);
        _listedItems = new Lazy<IReadOnlyList<ListedItem>>(ListItems);
    }

    private readonly List<Structure> _structures;
    private readonly List<Item> _items;
    private readonly Lazy<IReadOnlyList<ListedPiece>> _listed;
    private readonly Lazy<IReadOnlyList<ListedItem>> _listedItems;

    /// <inheritdoc />
    public IReadOnlyList<ListedItem> Items => _listedItems.Value;

    /// <inheritdoc />
    public IReadOnlyList<ListedPiece> Listed => _listed.Value;

    /// <inheritdoc />
    public IReadOnlyList<KitData> Kits { get; }

    /// <inheritdoc />
    public IReadOnlyList<PrefabKey> Structures { get; }

    /// <inheritdoc />
    public IReadOnlyList<ItemRef> BrokenTools { get; }

    /// <inheritdoc />
    public IReadOnlyList<SprayCanData> SprayCans { get; }

    /// <inheritdoc />
    public IReadOnlyList<ItemRef> Labellers { get; }

    /// <inheritdoc />
    public Option<PrefabData> Read(PrefabKey piece)
    {
        var structure = Prefab.Find(piece.Hash) as Structure;
        if (structure == null)
            return Option<PrefabData>.None;
        return Option.Some(new PrefabData(
            structure.PrefabName,
            piece,
            structure.GridSize,
            Types(structure.GetType()),
            Run(structure),
            States(structure),
            Ends(structure),
            structure is Cable cable ? Option.Some((double)cable.MaxVoltage) : Option<double>.None,
            structure is Pipe pipe ? Option.Some(pipe.MaxPressure.ToDouble()) : Option<double>.None,
            structure.maxPressureDelta,
            structure is IRocketInternals { StrictlyInternal: true })
        {
            PipeContent = structure is Pipe held ? ContentOf(held) : Option<PipeContent>.None,
            SmallSlot = SlotOf(structure),
            Collision = structure is SmallGrid small ? (int)small.SmallCollisionType : 0,
            SmallCells = structure is SmallGrid ? SmallCellsOf(structure) : ValueList<CellOffset>.Empty,
            MergeBlocked = structure is Cable { BlockMergeWithOtherCables: true } or Piping { DontAllowMergingWithWrench: true },
            Controls = ControlsOf(structure),
            Look = Option.Some(PieceMeshes.Summary(structure)),
            Placement = (int)structure.PlacementType,
            BlockingPoints = BlockingPointsOf(structure),
            BoundsCentre = BoundsCentreOf(structure),
            Snap = Option.Some(SnapOf(structure)),
            Turning = TurningOf(structure),
        });
    }

    // How the game snaps the prefab's position (decompile Assets.Scripts.Objects/Structure.cs GetWorldGrid, the fields
    // GridSize, GridOffset, _useAxisIndependentGridSnapping, _gridSize3d, _gridOffset3d): the axis-independent size and
    // offset when the prefab sets them, else its own GridSize and GridOffset on every axis. The private fields are read
    // on Structure, so a modded subclass gives its own values.
    private static PieceSnap SnapOf(Structure structure)
    {
        if (AxisIndependentField?.GetValue(structure) is true &&
            GridSize3dField?.GetValue(structure) is Vector3 size && GridOffset3dField?.GetValue(structure) is Vector3 offset)
            return new PieceSnap(new AxisSnap(size.x, offset.x), new AxisSnap(size.y, offset.y), new AxisSnap(size.z, offset.z));
        var axis = new AxisSnap(structure.GridSize, structure.GridOffset);
        return new PieceSnap(axis, axis, axis);
    }

    // What limits the builder's turns (decompile Assets.Scripts.Objects/Structure.cs RotationAxis, AllowedRotations;
    // Assets.Scripts.Inventory/InventoryManager.cs PlacementMode, UpdatePlacement): prefab fields with the game's flag
    // values, so a modded prefab gives its own. The game tests one bit at a time, so bits past the three it names (a
    // prefab set to Unity's "Everything") are dropped. A smart-rotated piece also steps through its connection type's
    // rotations (Assets.Scripts.Util/SmartRotate.cs RotationsList, _GetNext), read here from the game's own table.
    private static TurnLimits TurningOf(Structure structure) =>
        new(
            (PlacedBy)(int)structure.PlacementType,
            (RotationAxes)((int)structure.RotationAxis & (int)RotationAxes.All),
            (StartFaces)((int)structure.AllowedRotations & (int)StartFaces.All),
            structure is IMounted)
        {
            Smart = structure is ISmartRotatable smart ? SmartTurns(smart) : TurnSet.None,
        };

    private static TurnSet SmartTurns(ISmartRotatable smart)
    {
        if (!SmartRotate.RotationsList.TryGetValue(smart.GetConnectionType(), out var steps) || steps == null)
            return TurnSet.None;
        var turns = TurnSet.None;
        foreach (var step in steps)
        {
            var q = step.Rotation;
            if (TurnReading.FromGame(new GameRotation(q.x, q.y, q.z, q.w)).TryGet(out var turn))
                turns = turns.With(turn);
        }

        return turns;
    }

    private static readonly FieldInfo? AxisIndependentField =
        typeof(Structure).GetField("_useAxisIndependentGridSnapping", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo? GridSize3dField =
        typeof(Structure).GetField("_gridSize3d", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo? GridOffset3dField =
        typeof(Structure).GetField("_gridOffset3d", BindingFlags.Instance | BindingFlags.NonPublic);

    // The prefab's blockingGrids, protected on Structure: the points a piece placed on a face registers by, in its own
    // space (decompile Assets.Scripts.Objects/Structure.cs, OnAssignedReference). Read as data, so a modded subclass
    // gives its own.
    private static ValueList<Vector> BlockingPointsOf(Structure structure)
    {
        if (BlockingGridsField?.GetValue(structure) is not Vector3[] points || points.Length == 0)
            return ValueList<Vector>.Empty;
        var read = new Vector[points.Length];
        for (var i = 0; i < points.Length; i++)
            read[i] = new Vector(points[i].x, points[i].y, points[i].z);
        return ValueList.From(read);
    }

    private static readonly FieldInfo? BlockingGridsField =
        typeof(Structure).GetField("blockingGrids", BindingFlags.Instance | BindingFlags.NonPublic);

    // The centre of the prefab's bounds in its own space, cached by Thing.CheckBounds when empty: CenterPosition adds it
    // turned to the position (decompile Assets.Scripts.Objects/Thing.cs CenterPosition, Wall.cs CenterPosition).
    private static Vector BoundsCentreOf(Structure structure)
    {
        structure.CheckBounds();
        var centre = structure.Bounds.center;
        return new Vector(centre.x, centre.y, centre.z);
    }

    // The prefab's Interactables a player uses (decompile Assets.Scripts.Objects/Thing.cs, Interactable.cs,
    // InteractableType.cs): the centre of each one's collider (Interactable.Bounds, in the collider's own space, from
    // CacheBounds) in the prefab's space; the Powered and Error lights and the paint slot are not controls. The
    // collider is read as the Component it is, so the reader needs no physics assembly.
    private static ValueList<Vector> ControlsOf(Structure structure)
    {
        var interactables = structure.Interactables;
        if (interactables == null || interactables.Count == 0 || ColliderField == null)
            return ValueList<Vector>.Empty;
        var root = structure.transform;
        var points = new List<Vector>(interactables.Count);
        foreach (var interactable in interactables)
        {
            if (interactable == null || interactable.Action is InteractableType.Powered or InteractableType.Error or InteractableType.Color)
                continue;
            if (ColliderField.GetValue(interactable) is not Component collider || collider == null)
                continue;
            var local = root.InverseTransformPoint(collider.transform.TransformPoint(interactable.Bounds.center));
            points.Add(new Vector(local.x, local.y, local.z));
        }

        return points.Count == 0 ? ValueList<Vector>.Empty : ValueList.From(points);
    }

    private static readonly FieldInfo? ColliderField =
        typeof(Interactable).GetField("Collider", BindingFlags.Instance | BindingFlags.Public);

    // The prefab's small cells as the game registers them (GridBounds, built from the virtual
    // GetLocalSmallGridBounds when the prefab caches its bounds, so a modded override counts).
    private static ValueList<CellOffset> SmallCellsOf(Structure structure)
    {
        var cells = structure.GridBounds?._gridsSmall;
        if (cells == null || cells.Length == 0)
            return ValueList<CellOffset>.Empty;
        var offsets = new List<CellOffset>(cells.Length);
        foreach (var cell in cells)
            offsets.Add(new CellOffset(cell.x, cell.y, cell.z));
        return ValueList.From(offsets);
    }

    // Constructables in list order: the option index the game places and the build panel selects by
    // (Assets.Scripts.Objects/MultiConstructor.cs:15, 36-51; Assets.Scripts.UI/ConstructionPanel.cs:37, 65, 86). Null
    // entries are gone once the prefab has loaded (MultiConstructor.OnPrefabLoad, MultiConstructor.cs:20-34).
    private static KitData Kit(Thing kit, List<Structure> constructables, KitKind kind)
    {
        var pieces = new List<PrefabKey>(constructables.Count);
        foreach (var structure in constructables)
            if (structure != null)
                pieces.Add(new PrefabKey(structure.PrefabHash));
        return new KitData(kit.PrefabName, new KitKey(kit.PrefabHash), ValueList.From(pieces)) { Kind = kind, DisplayName = NameOf(kit) };
    }

    private IReadOnlyList<ListedPiece> ListAll()
    {
        var listed = new List<ListedPiece>(_structures.Count);
        foreach (var structure in _structures)
            if (KitHomes.GridOf(structure.GridSize).TryGet(out var grid))
                listed.Add(new ListedPiece(new PrefabKey(structure.PrefabHash), structure.PrefabName, NameOf(structure), grid, FirstStep(structure))
                {
                    Snap = SnapOf(structure),
                    Turning = TurningOf(structure),
                });
        return listed;
    }

    private IReadOnlyList<ListedItem> ListItems()
    {
        var listed = new List<ListedItem>(_items.Count);
        foreach (var item in _items)
            listed.Add(new ListedItem(new ItemKey(item.PrefabHash), item.PrefabName, NameOf(item)));
        return listed;
    }

    // The game's display name (Thing.DisplayName: the custom name, else Localization.GetThingName, Thing.cs:989-999).
    // Without a localised name the game gives "<N:language:prefab name>" (Localization.ErrorName, Localization.cs:680,
    // 1682-1695); the prefab name stands in for it.
    private static string NameOf(Thing thing)
    {
        var name = thing.DisplayName;
        if (string.IsNullOrEmpty(name) || name == "<N:" + Localization.CurrentLanguage + ":" + thing.PrefabName + ">")
            return thing.PrefabName;
        return name;
    }

    // The items the first build state takes (Structure.cs:1278 reads BuildStates[0].Tool.ToolEntry, then ToolEntry2).
    private static ValueList<ItemKey> FirstStep(Structure structure)
    {
        var states = structure.BuildStates;
        var tool = states != null && states.Count > 0 ? states[0]?.Tool : null;
        if (tool == null)
            return ValueList<ItemKey>.Empty;
        var items = new List<ItemKey>(2);
        if (tool.ToolEntry != null)
            items.Add(new ItemKey(tool.ToolEntry.PrefabHash));
        if (tool.ToolEntry2 != null)
            items.Add(new ItemKey(tool.ToolEntry2.PrefabHash));
        return ValueList.From(items);
    }

    private static ValueList<TypeData> Types(Type type)
    {
        var types = new List<TypeData>(16);
        for (var t = type; t != null && t != typeof(MonoBehaviour); t = t.BaseType)
            types.Add(new TypeData(t.FullName ?? t.Name, DeclaresPlacementCheck(t)) { DeclaresGeneratedPower = OverridesGeneratedPower(t) });
        foreach (var face in type.GetInterfaces())
            types.Add(new TypeData(face.FullName ?? face.Name, false));
        return ValueList.From(types);
    }

    // Device.GetGeneratedPower is virtual (decompile Assets.Scripts.Objects.Pipes/Device.cs); a type that
    // overrides it feeds a network: generators, batteries, power controls, transformers, modded ones alike.
    private static bool OverridesGeneratedPower(Type type)
    {
        var method = type.GetMethod(nameof(Device.GetGeneratedPower), Declared, null, new[] { typeof(CableNetwork) }, null);
        return method != null && method.GetBaseDefinition().DeclaringType != type;
    }


    private static bool DeclaresPlacementCheck(Type type)
    {
        foreach (var method in type.GetMethods(Declared))
            if (IsPlacementCheck(method.Name))
                return true;
        return false;
    }

    // The game's placement checks: Structure.CanConstruct, Structure.CanConstructCell, Airlock.CanConstructCheckWalls,
    // Structure.CanMountOnWall.
    private static bool IsPlacementCheck(string name) =>
        name is "CanConstruct" or "CanConstructCell" or "CanConstructCheckWalls" or "CanMountOnWall";

    private static RunKind Run(Structure structure) => structure switch
    {
        Cable => RunKind.Cable,
        Pipe => RunKind.Pipe,
        Chute => RunKind.Chute,
        _ => RunKind.Other,
    };

    private static ValueList<StateData> States(Structure structure)
    {
        var states = structure.BuildStates;
        if (states == null)
            return ValueList<StateData>.Empty;
        var data = new List<StateData>(states.Count);
        foreach (var state in states)
        {
            var tool = state?.Tool;
            data.Add(new StateData(
                tool == null ? Option<EntryItem>.None : Entry(tool.ToolEntry, tool.EntryQuantity),
                tool == null ? Option<EntryItem>.None : Entry(tool.ToolEntry2, tool.EntryQuantity2),
                tool == null || tool.ToolExit == null ? Option<ItemRef>.None : Option.Some(Item(tool.ToolExit)),
                state != null && state.BlockAir)
            {
                BlocksGravity = state != null && state.BlockGravity,
            });
        }

        return ValueList.From(data);
    }

    // SmallCell.Add (decompile Assets.Scripts.GridSystem/SmallCell.cs): cable, chute, pipe, then a device
    // that does not only cover, then a robotic arm rail, else the other slot.
    private static Option<Slot> SlotOf(Structure structure) => structure switch
    {
        Cable => Option.Some(Slot.Cable),
        Chute => Option.Some(Slot.Chute),
        Pipe => Option.Some(Slot.Pipe),
        Device device when device.SmallCollisionType != SmallGridBlock.Covers => Option.Some(Slot.Device),
        Objects.RoboticArm.IRoboticArmRail => Option.Some(Slot.Rail),
        SmallGrid => Option.Some(Slot.Other),
        _ => Option<Slot>.None,
    };

    // Pipe.pipeContentType is a private serialized field (decompile Assets.Scripts.Objects.Pipes/Pipe.cs):
    // Unknown, Gas, Liquid, All.
    private static readonly FieldInfo? PipeContentField =
        typeof(Pipe).GetField("pipeContentType", BindingFlags.Instance | BindingFlags.NonPublic);

    private static Option<PipeContent> ContentOf(Pipe pipe) =>
        PipeContentField?.GetValue(pipe) is Pipe.ContentType content
            ? content switch
            {
                Pipe.ContentType.Gas => Option.Some(PipeContent.Gas),
                Pipe.ContentType.Liquid => Option.Some(PipeContent.Liquid),
                Pipe.ContentType.All => Option.Some(PipeContent.Any),
                _ => Option<PipeContent>.None,
            }
            : Option<PipeContent>.None;

    private static Option<EntryItem> Entry(Item? item, int quantity) =>
        item == null ? Option<EntryItem>.None : Option.Some(new EntryItem(Item(item), quantity, item is Stackable));

    private static ItemRef Item(Item item) => new(item.PrefabName, new ItemKey(item.PrefabHash));

    // GameManager.GetColorIndex reads the game manager's colours; -1 before it is there or for a material it does not hold.
    private static int ColourOf(Material material)
    {
        var manager = Assets.Scripts.Util.Singleton<Assets.Scripts.GameManager>.Instance;
        if (material == null || manager == null || manager.CustomColors == null)
            return -1;
        return Assets.Scripts.GameManager.GetColorIndex(material);
    }

    private static ValueList<EndData> Ends(Structure structure)
    {
        if (structure is not SmallGrid grid || grid.OpenEnds == null)
            return ValueList<EndData>.Empty;
        var root = structure.transform;
        var ends = new List<EndData>(grid.OpenEnds.Count);
        for (var index = 0; index < grid.OpenEnds.Count; index++)
        {
            var end = grid.OpenEnds[index];
            if (end?.Transform == null)
                continue;
            var position = root.InverseTransformPoint(end.Transform.position);
            var forward = root.InverseTransformDirection(end.Transform.forward);
            ends.Add(new EndData(
                new Vector(position.x, position.y, position.z),
                new Vector(forward.x, forward.y, forward.z),
                (NetworkKinds)(int)end.ConnectionType,
                end.ConnectionType.ToString())
            {
                Role = (int)end.ConnectionRole,
                Index = Option.Some(index),
            });
        }

        return ValueList.From(ends);
    }
}
