using System;
using Lattice.Api;
using Lattice.Core.Ports;

namespace Lattice.Core.Catalogue;

/// <summary>An item a build state names: its prefab name and the game's hash of it.</summary>
internal sealed record ItemRef(string Name, ItemKey Key);

/// <summary>
/// A spray can prefab: the item, the index of its colour in the game's colours (<c>GameManager.GetColorIndex</c> of
/// its <c>PaintMaterial</c>; -1 when none), the quantity one paint act takes (<c>Consumable.UseAmount</c>) and a full
/// can's quantity (<c>Consumable.MaxQuantity</c>).
/// </summary>
internal sealed record SprayCanData(ItemRef Can, int Colour, float UseAmount, float MaxQuantity);

/// <summary>
/// One entry item of a build state (<c>ToolEntry</c> or <c>ToolEntry2</c>): the item, the quantity the state asks,
/// and whether the item is a stack (the game uses up a stack's quantity; any other item is a tool held).
/// </summary>
internal sealed record EntryItem(ItemRef Item, int Quantity, bool IsStack);

/// <summary>
/// One entry of a structure prefab's <c>BuildStates</c>: what enters it, what leaves it, whether it seals, and whether
/// it blocks gravity (<c>BuildState.BlockGravity</c>).
/// </summary>
internal sealed record StateData(Option<EntryItem> Entry, Option<EntryItem> Entry2, Option<ItemRef> Exit, bool BlocksAir)
{
    public bool BlocksGravity { get; init; }
}

/// <summary>A point or direction in a prefab's own space, in metres.</summary>
internal readonly record struct Vector(double X, double Y, double Z);

/// <summary>The game's network kinds of a connection end (the values of its <c>NetworkType</c> flags).</summary>
[Flags]
internal enum NetworkKinds
{
    None = 0,
    Pipe = 1,
    Power = 2,
    Data = 4,
    Chute = 8,
    Elevator = 0x10,
    PipeLiquid = 0x20,
    LandingPad = 0x40,
    LaunchPad = 0x80,
    RoboticArmRail = 0x100,
}

/// <summary>
/// One of a prefab's <c>OpenEnds</c>: where it sits and which way it points in the prefab's space, its kinds (every
/// bit the game set, a modded network's included) and the game's own name for them.
/// </summary>
internal sealed record EndData(Vector Position, Vector Forward, NetworkKinds Kinds, string KindName)
{
    /// <summary>The game's <c>ConnectionRole</c> value: 0 none, 1 input, 2 input2, 3 output, 4 output2, 5 waste.</summary>
    public int Role { get; init; }

    /// <summary>Its index in the prefab's <c>OpenEnds</c> list; none when the source did not say, and then its place in the source's list stands in.</summary>
    public Option<int> Index { get; init; }
}

/// <summary>Which small-grid run a piece itself is part of (a cable, a pipe, a chute), from its class.</summary>
internal enum RunKind
{
    Other = 0,
    Cable = 1,
    Pipe = 2,
    Chute = 3,
}

/// <summary>
/// A type of a prefab's class chain or one of its interfaces, by full name, and whether that type itself declares
/// a placement check (overrides <c>CanConstruct</c>, <c>CanConstructCell</c>, <c>CanConstructCheckWalls</c> or
/// <c>CanMountOnWall</c>).
/// </summary>
internal sealed record TypeData(string FullName, bool DeclaresPlacementCheck)
{
    /// <summary>
    /// The type itself overrides the game's <c>Device.GetGeneratedPower</c>: its pieces feed power into a network
    /// (decompile <c>Assets.Scripts.Objects.Pipes/Device.cs</c>, overridden by generators, batteries and power controls).
    /// </summary>
    public bool DeclaresGeneratedPower { get; init; }
}

/// <summary>
/// Everything the catalogue reads from one structure prefab, as the game holds it: base-game or modded alike.
/// Types run from the prefab's own class down to its bases, then its interfaces. <c>RocketOnly</c> is the game's
/// <c>IRocketInternals.StrictlyInternal</c>: the piece is placed only inside a rocket.
/// </summary>
internal sealed record PrefabData(
    string Name,
    PrefabKey Key,
    double GridMetres,
    ValueList<TypeData> Types,
    RunKind Run,
    ValueList<StateData> States,
    ValueList<EndData> Ends,
    Option<double> CableWatts,
    Option<double> PipeKilopascals,
    double MaxPressureDelta,
    bool RocketOnly)
{
    /// <summary>What a pipe holds (<c>Pipe.ContentType</c>); none for anything else, or when the game says Unknown.</summary>
    public Option<PipeContent> PipeContent { get; init; }

    /// <summary>The small-cell slot a small-grid piece takes (<c>SmallCell.Add</c>); none for a large-grid piece.</summary>
    public Option<Slot> SmallSlot { get; init; }

    /// <summary>
    /// The small-grid bounds of a small-grid piece as the prefab holds them (the game's <c>GridBounds._gridsSmall</c>),
    /// in decimetres from its pivot, unturned and not yet snapped to small cells (the game snaps them when it
    /// registers the piece: <c>GridBounds.GetLocalSmallGrid</c>); empty when the prefab names none.
    /// </summary>
    public ValueList<CellOffset> SmallCells { get; init; } = ValueList<CellOffset>.Empty;

    /// <summary>The small-grid collision class bits (<c>SmallGrid.SmallCollisionType</c>); 0 for a large-grid piece.</summary>
    public int Collision { get; init; }

    /// <summary>
    /// The piece itself refuses a merge: a cable's <c>BlockMergeWithOtherCables</c> or a pipe's
    /// <c>DontAllowMergingWithWrench</c> (decompile <c>Assets.Scripts.Objects.Electrical/Cable.cs</c> <c>CanReplace</c>,
    /// <c>Assets.Scripts.Objects.Pipes/Piping.cs</c> <c>CanReplace</c>); false for anything else.
    /// </summary>
    public bool MergeBlocked { get; init; }

    /// <summary>
    /// Where the controls a player uses sit, in metres in the prefab's own space, unturned: one point per collider of
    /// the prefab's <c>Thing.Interactables</c>, indicators (<c>Powered</c>, <c>Error</c>) and paint (<c>Color</c>) left
    /// out; empty for a piece with none.
    /// </summary>
    public ValueList<Vector> Controls { get; init; } = ValueList<Vector>.Empty;

    /// <summary>What the prefab offers to draw it by; none when the source did not read it.</summary>
    public Option<PrefabLook> Look { get; init; }

    /// <summary>
    /// The game's <c>Structure.PlacementType</c> as its <c>PlacementSnap</c> value: 0 Grid, 1 Face, 2 FaceMount
    /// (decompile <c>Assets.Scripts.Objects/PlacementSnap.cs</c>); 0 when the source did not read it.
    /// </summary>
    public int Placement { get; init; }

    /// <summary>
    /// The prefab's <c>blockingGrids</c>, in metres in its own space, unturned; empty when it names none, and the game
    /// then registers the piece at its position alone (<c>Structure.OnAssignedReference</c>).
    /// </summary>
    public ValueList<Vector> BlockingPoints { get; init; } = ValueList<Vector>.Empty;

    /// <summary>
    /// The centre of the prefab's <c>Thing.Bounds</c> in its own space, in metres: the game's <c>CenterPosition</c> is the
    /// position plus this turned (<c>Thing.CenterPosition</c>, <c>Wall.CenterPosition</c>).
    /// </summary>
    public Vector BoundsCentre { get; init; }

    /// <summary>
    /// How the game snaps the prefab's position on each world axis (<c>Structure.GetWorldGrid</c>): its own
    /// <c>GridSize</c> and <c>GridOffset</c> on every axis, or <c>_gridSize3d</c> and <c>_gridOffset3d</c> when it sets
    /// <c>_useAxisIndependentGridSnapping</c>; none when the source did not read it, and the grid's own stands in.
    /// </summary>
    public Option<PieceSnap> Snap { get; init; }

    /// <summary>
    /// What limits the turns the game's builder gives the prefab (<c>Structure.RotationAxis</c>,
    /// <c>Structure.AllowedRotations</c>, <c>IMounted</c>); the game's defaults when the source did not read them.
    /// </summary>
    public TurnLimits Turning { get; init; } = TurnLimits.Free;
}

/// <summary>
/// What a structure prefab offers to draw it by (decompile <c>Assets.Scripts.Objects/Thing.cs</c> <c>Renderers</c>,
/// <c>CachePrefabBounds</c>; <c>Structure.cs</c> build states; <c>Assets.Scripts.UI/Wireframe.cs</c>).
/// <see cref="Bounds"/> is the prefab's bounds box, none when the game's is empty. <see cref="Meshes"/> counts the
/// meshes Lattice draws it with, <see cref="Readable"/> those with CPU-readable data, <see cref="ByStep"/> those shown
/// only at some build steps. <see cref="Blueprint"/> is some when the prefab's blueprint holds a mesh, with whether that
/// mesh is CPU-readable.
/// </summary>
internal sealed record PrefabLook(Option<PieceBox> Bounds, int Meshes, int Readable, int ByStep, Option<bool> Blueprint);

/// <summary>A kit prefab and the pieces it places, in the game's option order.</summary>
internal sealed record KitData(string Name, KitKey Key, ValueList<PrefabKey> Pieces)
{
    /// <summary>Which kit type it is; <see cref="KitKind.Multi"/> unless given.</summary>
    public KitKind Kind { get; init; } = KitKind.Multi;

    /// <summary>Display text in the local player's language; the prefab name unless given.</summary>
    public string DisplayName
    {
        get => _displayName ?? Name;
        init => _displayName = value;
    }

    private readonly string? _displayName;

    /// <summary>
    /// The tool in the other hand that makes this kit merge into the piece standing in its cell: a
    /// <c>MultiMergeConstructor</c>'s <c>ToolExit</c> (decompile <c>Assets.Scripts.Objects/MultiMergeConstructor.cs</c>,
    /// <c>Construct</c>); none for a kit that does not merge.
    /// </summary>
    public Option<ItemRef> MergeTool { get; init; }
}
