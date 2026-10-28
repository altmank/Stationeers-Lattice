using System.Collections.Generic;
using Lattice.Api;

namespace Lattice.Core.Ports;

/// <summary>The two grids pieces sit on: large (2 m) and small (0.5 m).</summary>
internal enum GridSize
{
    Large = 1,
    Small = 2,
}

/// <summary>One of the six directions along the grid axes.</summary>
internal enum Direction
{
    XPlus = 1,
    XMinus = 2,
    YPlus = 3,
    YMinus = 4,
    ZPlus = 5,
    ZMinus = 6,
}

/// <summary>
/// A network a connection end can join: one case per game network kind Lattice models (the game's <c>NetworkType</c>
/// flags, decompile Assets.Scripts.Objects/NetworkType.cs), and <see cref="Unknown"/> for a kind a mod
/// adds, kept with the game's name and bits so a modded piece's ends are never dropped. Two ends join only through a
/// kind they share: a gas pipe never joins a liquid pipe.
/// </summary>
internal abstract record NetworkType
{
    private NetworkType() { }

    public sealed record Power : NetworkType;

    public sealed record Data : NetworkType;

    public sealed record GasPipe : NetworkType;

    public sealed record LiquidPipe : NetworkType;

    public sealed record Chute : NetworkType;

    public sealed record Elevator : NetworkType;

    public sealed record LandingPad : NetworkType;

    public sealed record LaunchPad : NetworkType;

    public sealed record Rail : NetworkType;

    /// <summary>A network kind Lattice does not model, as the game names it, with its bits beyond the kinds above.</summary>
    public sealed record Unknown(string GameTypeName, int Bits) : NetworkType;
}

/// <summary>The kind sets most ends carry.</summary>
internal static class EndKinds
{
    /// <summary>A cable end: power and data (the game's <c>PowerAndData</c>).</summary>
    public static readonly ValueSet<NetworkType> Cable = ValueSet.From(new NetworkType[] { new NetworkType.Power(), new NetworkType.Data() });

    public static readonly ValueSet<NetworkType> Power = ValueSet.Of<NetworkType>(new NetworkType.Power());

    public static readonly ValueSet<NetworkType> Data = ValueSet.Of<NetworkType>(new NetworkType.Data());

    public static readonly ValueSet<NetworkType> GasPipe = ValueSet.Of<NetworkType>(new NetworkType.GasPipe());

    public static readonly ValueSet<NetworkType> LiquidPipe = ValueSet.Of<NetworkType>(new NetworkType.LiquidPipe());

    public static readonly ValueSet<NetworkType> Chute = ValueSet.Of<NetworkType>(new NetworkType.Chute());
}

/// <summary>What an end does for its device (the game's <c>ConnectionRole</c>, Assets.Scripts.Objects/ConnectionRole.cs).</summary>
internal enum ConnectionRole
{
    Input = 1,
    Input2 = 2,
    Output = 3,
    Output2 = 4,
    Waste = 5,
}

/// <summary>What a pipe holds (the game's <c>Pipe.ContentType</c>, Assets.Scripts.Objects.Pipes/Pipe.cs).</summary>
internal enum PipeContent
{
    Gas = 1,
    Liquid = 2,
    Any = 3,
}

/// <summary>A length in millimetres.</summary>
internal readonly record struct Millimetres(int Value);

/// <summary>
/// A connection end of a piece: the cell it leaves from (decimetres from the piece's own cell, unturned), facing which
/// way, and the networks it joins; <see cref="Role"/> is its device role, none for a plain run end. <see cref="Reach"/> is how far the end point lies from that cell's centre along
/// <see cref="Facing"/> (cable ends 0.34-0.40 m, pipe ends 0.34-0.37 m);
/// none when the catalogue does not know it, and then two ends collide only when they leave
/// one cell facing the same way.
/// </summary>
internal sealed record ConnectionEnd(CellOffset At, Direction Facing, ValueSet<NetworkType> Kinds)
{
    public Option<Millimetres> Reach { get; init; }

    public Option<ConnectionRole> Role { get; init; }

    /// <summary>
    /// Its index in the prefab's <c>OpenEnds</c> list, entries the catalogue does not keep counted; none when the
    /// source did not say, and then its place among the piece's ends stands in.
    /// </summary>
    public Option<int> GameIndex { get; init; }

    /// <summary>The game's name for its kinds (<c>ConnectionType.ToString()</c>); none when the source did not say.</summary>
    public Option<string> GameName { get; init; }

    /// <summary>Whether this end and another share a network kind.</summary>
    public bool SharesAKindWith(ConnectionEnd other)
    {
        foreach (var kind in Kinds)
            if (other.Kinds.Contains(kind))
                return true;
        return false;
    }
}

/// <summary>
/// What a piece takes in a cell. Large grid: the cell's centre or one of its six faces (a frame takes the centre, a
/// wall one face). Small grid: one slot per kind
/// (<c>SmallCell.Add</c>).
/// </summary>
internal enum Slot
{
    Centre = 1,
    FaceXPlus = 2,
    FaceXMinus = 3,
    FaceYPlus = 4,
    FaceYMinus = 5,
    FaceZPlus = 6,
    FaceZMinus = 7,
    Cable = 8,
    Pipe = 9,
    Chute = 10,
    Device = 11,
    Rail = 12,
    Other = 13,
}

/// <summary>How the game puts a piece on a face rather than in a cell (<c>PlacementSnap</c> past <c>Grid</c>).</summary>
internal enum FacePlacement
{
    /// <summary><c>PlacementSnap.Face</c>: walls, windows, floors and their kin.</summary>
    Face = 1,

    /// <summary><c>PlacementSnap.FaceMount</c>: pieces mounted on a wall.</summary>
    FaceMount = 2,
}

/// <summary>
/// How a piece the game places on a face takes its spots. Each point, turned and added to the position, lies in the
/// large cell whose centre is nearest it on the side of the piece's centre (the position plus <paramref name="Centre"/>
/// turned), and takes the slot it makes there: the centre or one of the six faces; a point that makes neither takes
/// nothing (decompile <c>Assets.Scripts/GridController.cs</c> <c>AddGridStructure</c>, <c>Grid3.GridCenter</c>,
/// <c>Assets.Scripts.GridSystem/Cell.cs</c> <c>AddStructural</c>, <c>StructuralArray.Get</c>).
/// Two walls back to back on one boundary lie in two cells.
/// </summary>
/// <param name="Kind">Face or face mount.</param>
/// <param name="Points">The prefab's blocking points in decimetres from its position, unturned; empty is the position alone.</param>
/// <param name="Centre">The centre of its bounds in metres from its position, unturned.</param>
internal sealed record FaceFacts(FacePlacement Kind, ValueList<CellOffset> Points, Lattice.Core.Catalogue.Vector Centre);

/// <summary>One cell a piece takes, as an offset in decimetres from its own cell (unturned), and the slot it takes there.</summary>
internal readonly record struct FootprintCell(CellOffset At, Slot Slot);

/// <summary>
/// A small-grid piece's collision class (<c>SmallCollisionType</c>): two pieces sharing a small cell clash when their
/// classes share a bit; otherwise only their ends can.
/// </summary>
[System.Flags]
internal enum SmallCollision
{
    None = 0,
    Pipes = 1,
    Cables = 2,
    Devices = 4,
    Covers = 8,
    Rails = 16,
    Everything = Pipes | Cables | Devices | Covers | Rails,
}

/// <summary>One build step of a piece: the tool that does it, the material it uses, the tool that undoes it, whether it seals.</summary>
internal sealed record BuildStep(
    StepIndex Index,
    Option<ItemKey> EntryTool,
    Option<ItemQuantity> Material,
    Option<ItemKey> ExitTool,
    bool BlocksAir)
{
    /// <summary>Whether the piece at this step blocks gravity: what walls a room (<c>BuildState.BlockGravity</c>, <c>CanGravityPass</c>).</summary>
    public bool BlocksGravity { get; init; }
}

/// <summary>A piece's ratings: cable power, pipe pressure, face pressure difference, where it has them.</summary>
internal sealed record Ratings(Option<Watts> Cable, Option<Kilopascals> Pipe, Option<Kilopascals> Face);

/// <summary>What a piece offers other pieces that mount on it, found by type so a modded subclass offers the same.</summary>
internal enum HostRole
{
    /// <summary>A pipe valve an organ pipe sits on (<c>OrganPipe</c>).</summary>
    PipeValve = 1,

    /// <summary>A tray a harvester sits on (<c>IHarvestable</c>).</summary>
    Tray = 2,

    /// <summary>A structure an extension fits (<c>IExtendableStructure</c>).</summary>
    Extendable = 3,
}

/// <summary>
/// A piece's facts that support rules read beyond its catalogue entry. <see cref="AllowsMounting"/> is the game's
/// <c>Structure.AllowMounting</c>; <see cref="BlocksGrid"/> is <c>StructureCollisionType == BlockGrid</c> (with the
/// step blocking gravity, the cell counts as blocked: <c>GridController.IsBlockedGrid</c>); <see cref="RequiresFrame"/>
/// is a wall-mounted piece that refuses a wall as its holder (<c>SmallGrid.RequiresFrame</c>).
/// </summary>
internal sealed record SupportFacts(bool AllowsMounting, bool BlocksGrid, bool RequiresFrame, ValueSet<HostRole> Offers)
{
    /// <summary>A piece that offers nothing: no mounting, no blocked cell, no host role.</summary>
    public static SupportFacts Nothing => new(false, false, false, ValueSet<HostRole>.Empty);
}

/// <summary>
/// How a piece merges: a kit of its own placed on a piece of that kit in its cell, with <see cref="OffhandTool"/> in the
/// other hand, replaces the standing piece with the one whose ends cover both, charging the kit cost difference
/// (<c>MultiMergeConstructor.Construct</c>, <c>Cable.CanReplace</c>, <c>Piping.CanReplace</c>).
/// </summary>
internal sealed record MergeFacts(ItemKey OffhandTool);

/// <summary>
/// Everything Lattice knows about a piece, read from its prefab at load. <see cref="Footprint"/> is the cells and slots it
/// takes; empty means its own cell only, in the centre (large grid) or the <see cref="Slot.Other"/> slot (small grid).
/// <see cref="Face"/>, when some, takes the place of the footprint: the piece is placed on a face and takes the slots
/// its points make (<see cref="FaceFacts"/>).
/// <see cref="Collision"/> is its small-grid collision class; <see cref="SmallCollision.None"/> on the large grid.
/// <see cref="Kits"/> lists every kit that places it, the kit its first build step uses first (the game's choice,
/// Structure.cs:1278); empty for a piece no kit places (burnt cables, extensions, pieces a mod spawns by code).
/// <see cref="Merge"/> is how a kit merges into it; none for a piece no kit merges into.
/// <see cref="Pipe"/> is what a pipe holds; none for anything else. <see cref="Support"/> is what support rules read
/// beyond the entry; none when the catalogue did not read it, and the rules that need it then ask the game.
/// </summary>
internal sealed record PieceFacts(
    PrefabKey Key,
    ValueList<KitPlace> Kits,
    GridSize Grid,
    ValueList<BuildStep> Steps,
    ValueList<ConnectionEnd> Ends,
    Ratings Ratings,
    Option<RuleId> SupportGroup)
{
    public Option<MergeFacts> Merge { get; init; }

    public ValueList<FootprintCell> Footprint { get; init; } = ValueList<FootprintCell>.Empty;

    /// <summary>How the piece takes its spots when the game places it on a face; none for a piece placed in a cell.</summary>
    public Option<FaceFacts> Face { get; init; }

    public SmallCollision Collision { get; init; }

    public Option<PipeContent> Pipe { get; init; }

    public Option<SupportFacts> Support { get; init; }

    /// <summary>A door or a window, found by the piece's type so a modded subclass is the same; none for anything else.</summary>
    public Option<OpeningKind> Opening { get; init; }

    /// <summary>What the device keeps going for the people in a base, found by its type; none for anything else.</summary>
    public Keeps Keeps { get; init; }

    /// <summary>Whether the piece feeds power into the networks it sits on (generators, batteries, power controls), by type.</summary>
    public bool Supplies { get; init; }

    /// <summary>The side the controls a player uses sit on, unturned; none for a piece with no such control.</summary>
    public Option<Direction> ControlFace { get; init; }

    /// <summary>Whether the piece opens its pipe network to the air of its cell (a passive vent and its subclasses).</summary>
    public bool Vents { get; init; }

    /// <summary>
    /// The piece's narrowest family, found by its type and build states, so a modded subclass inherits its base's; <see cref="PieceFamilies.Other"/>
    /// when no type says. A mod's <see cref="CatalogueAddition.InFamily"/> comes before this (<c>IPieceFamilies</c>).
    /// </summary>
    public PieceFamily Family { get; init; } = PieceFamilies.Other;

    /// <summary>How the game snaps the piece's position on each world axis; the grid's own unless the source read another.</summary>
    public PieceSnap Snap { get; init; } = PieceSnap.Of(Grid == GridSize.Large ? PieceGrid.Large : PieceGrid.Small);

    /// <summary>How the piece is drawn as a ghost; none when the catalogue did not read it (a file or fake without looks).</summary>
    public Option<LookFacts> Look { get; init; }
}

/// <summary>What a device keeps going for the people in a base, found by the device's type.</summary>
[System.Flags]
internal enum Keeps
{
    /// <summary>Nothing a base lives on.</summary>
    None = 0,

    /// <summary>Breathable air: powered vents, air conditioners, filtration.</summary>
    Air = 1,

    /// <summary>Warmth or cooling: wall heaters and coolers.</summary>
    Heat = 2,

    /// <summary>Any other life support.</summary>
    Life = 4,
}

/// <summary>What a face piece is to a layout: something people walk through, or something they look through.</summary>
internal enum OpeningKind
{
    /// <summary>
    /// The game's <c>Door</c> and its subclasses (<c>Airlock</c>, <c>Dock</c>, <c>UnPoweredDoor</c>), and
    /// <c>RoboticArmDoor</c> (decompile <c>Assets.Scripts.Objects.Structures/Door.cs</c>, <c>Airlock.cs</c>,
    /// <c>Dock.cs</c>, <c>UnPoweredDoor.cs</c>).
    /// </summary>
    Door = 1,

    /// <summary>
    /// <c>WallTransparent</c> and its subclasses (<c>ShutteredWindow</c>), and <c>WindowShutter</c>, a <c>Door</c> that
    /// sits over a window (decompile <c>Assets.Scripts.Objects/WallTransparent.cs</c>, <c>ShutteredWindow.cs</c>,
    /// <c>Assets.Scripts.Objects.Structures/WindowShutter.cs</c>).
    /// </summary>
    Window = 2,
}

/// <summary>A kit and the pieces it places, in option order.</summary>
internal sealed record KitFacts(KitKey Key, ValueList<PrefabKey> Pieces);

/// <summary>Piece and kit facts from the game's prefabs. Each prefab is read once per load.</summary>
internal interface ICatalogue
{
    IReadOnlyList<KitKey> Kits { get; }

    Result<PieceFacts, Reason> Piece(PrefabKey piece);

    Result<KitFacts, Reason> Kit(KitKey kit);

    Result<PrefabKey, Reason> PieceOf(KitKey kit, KitOption option);

    /// <summary>
    /// The tools that take apart a broken structure, found by type so a modded tool counts: the game takes any
    /// <c>IConstructor</c> tool there and gives nothing back.
    /// Read once per load; empty when the catalogue did not read them.
    /// </summary>
    ValueList<ItemKey> BrokenTools { get; }

    /// <summary>
    /// Every piece and kit by name, grid and option order, for the catalogue view (<see cref="IPieceCatalogue"/>);
    /// built without reading each piece's facts. Built once, on first use.
    /// </summary>
    Lattice.Core.Catalogue.CatalogueListing Listing { get; }
}
