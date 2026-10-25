using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Ports;

namespace Lattice.Core.Catalogue;

/// <summary>
/// Turns one structure prefab's data into the facts Lattice plans with. Pure: the same data gives the same facts.
/// A prefab Lattice cannot describe is refused with <see cref="ReasonCodes.UnknownPiece"/> and a <c>problem</c>.
/// </summary>
internal static class PieceMapper
{
    // Assets.Scripts.Objects/SmallGrid.cs: SmallGridSize 0.5, SmallGridOffset 0.25.
    private const float SmallGridSize = 0.5f;
    private const float SmallGridHalf = 0.5f;

    // Assets.Scripts.Util/IGridMergeable.cs: the game's mergeable pieces (Cable, Piping and their subclasses).
    private const string GridMergeable = "Assets.Scripts.Util.IGridMergeable";

    // Assets.Scripts.Objects.Pipes/PassiveVent.cs: a pipe that mixes its network with its cell's air (GasPipeVent too).
    private const string PassiveVent = "Assets.Scripts.Objects.Pipes.PassiveVent";

    // A control point closer than this to the pivot along its furthest axis, in metres, sits on no side.
    private const double ControlDeadZone = 0.05;

    // Assets.Scripts.Objects/PlacementSnap.cs: Grid is 0; Face and FaceMount follow.
    private const int GridPlacement = 0;

    /// <summary>The piece's facts when its first kit does not merge (<see cref="Map(PrefabData, ValueList{KitPlace}, Option{ItemKey})"/>).</summary>
    public static Result<PieceFacts, Reason> Map(PrefabData prefab, ValueList<KitPlace> kits) =>
        Map(prefab, kits, Option<ItemKey>.None);

    /// <summary>
    /// The piece's facts, with every kit that places it (the kit its first build step uses first; none for a piece no
    /// kit places). Every end with a network kind is kept, a modded kind as <see cref="NetworkType.Unknown"/>, with its
    /// index in the prefab's <c>OpenEnds</c> and the game's name for its kinds.
    /// <paramref name="firstKitMergeTool"/> is the merge tool of the piece's first kit: the piece merges with it when
    /// its type is the game's <c>IGridMergeable</c> (a modded subclass too) and the piece does not refuse merges.
    /// </summary>
    public static Result<PieceFacts, Reason> Map(PrefabData prefab, ValueList<KitPlace> kits, Option<ItemKey> firstKitMergeTool)
    {
        GridSize? grid = prefab.GridMetres switch
        {
            2.0 => GridSize.Large,
            0.5 => GridSize.Small,
            _ => null,
        };
        if (grid is null)
            return Refuse(prefab, "grid-size");
        if (prefab.Placement is < GridPlacement or > (int)FacePlacement.FaceMount)
            return Refuse(prefab, "placement-type");
        if (prefab.States.Count == 0)
            return Refuse(prefab, "no-build-state");

        var steps = new BuildStep[prefab.States.Count];
        for (var i = 0; i < steps.Length; i++)
        {
            var step = Step(prefab.States[i], i);
            if (step is null)
                return Refuse(prefab, "two-materials-or-two-tools in state " + i);
            steps[i] = step;
        }

        var ends = new System.Collections.Generic.List<ConnectionEnd>(prefab.Ends.Count);
        for (var i = 0; i < prefab.Ends.Count; i++)
        {
            var data = prefab.Ends[i];
            if (data.Kinds == NetworkKinds.None)
                continue;
            var end = End(data, data.Index.Reduce(i));
            if (end is null)
                return Refuse(prefab, "end-off-axis");
            ends.Add(end);
        }

        return Result<PieceFacts, Reason>.Ok(new PieceFacts(
            prefab.Key,
            kits,
            grid.Value,
            ValueList.From(steps),
            ValueList.From(ends),
            new Ratings(
                prefab.CableWatts.Map(static w => new Watts(w)),
                prefab.PipeKilopascals.Map(static p => new Kilopascals(p)),
                prefab.MaxPressureDelta > 0 ? Option.Some(new Kilopascals(prefab.MaxPressureDelta)) : Option<Kilopascals>.None),
            SupportGroups.Of(prefab.Types, prefab.RocketOnly))
        {
            Pipe = prefab.Run == RunKind.Pipe ? prefab.PipeContent : Option<PipeContent>.None,
            Footprint = grid == GridSize.Small && prefab.SmallSlot.TryGet(out var slot)
                ? SmallFootprint(prefab.SmallCells, slot)
                : ValueList<FootprintCell>.Empty,
            Face = grid == GridSize.Large ? FaceOf(prefab) : Option<FaceFacts>.None,
            Collision = grid == GridSize.Small ? (SmallCollision)(prefab.Collision & (int)SmallCollision.Everything) : SmallCollision.None,
            Opening = OpeningOf(prefab.Types),
            Merge = MergeOf(prefab, firstKitMergeTool),
            Keeps = KeepsOf(prefab.Types),
            Supplies = SuppliesPower(prefab.Types),
            Vents = Has(prefab.Types, PassiveVent),
            ControlFace = ControlFaceOf(prefab.Controls),
            Family = TypeFamilies.Of(prefab.Types, prefab.States),
            Look = prefab.Look.Map(look => LookOf(look, prefab.GridMetres)),
        });
    }

    // A large piece the game puts on a face registers each blocking point from the side of its bounds centre
    // (Structure.OnAssignedReference, GridController.AddGridStructure); a point is keyed to the decimetre as Grid3 keys it.
    private static Option<FaceFacts> FaceOf(PrefabData prefab)
    {
        if (prefab.Placement == GridPlacement)
            return Option<FaceFacts>.None;
        var points = new CellOffset[prefab.BlockingPoints.Count];
        for (var i = 0; i < points.Length; i++)
        {
            var point = prefab.BlockingPoints[i];
            points[i] = new CellOffset(Decimetres(point.X), Decimetres(point.Y), Decimetres(point.Z));
        }

        return Option.Some(new FaceFacts((FacePlacement)prefab.Placement, ValueList.From(points), prefab.BoundsCentre));
    }

    private static int Decimetres(double metres) => (int)Math.Round(metres * 10.0, MidpointRounding.AwayFromZero);

    // A piece's look: the prefab's meshes, else its blueprint's, else its bounds box, else one cell.
    private static LookFacts LookOf(PrefabLook look, double gridMetres)
    {
        var bounds = look.Bounds.Reduce(PieceBox.Cell(gridMetres));
        LookSource source = look.Meshes > 0 ? new LookSource.Meshes(look.Meshes, look.Readable, look.ByStep)
            : look.Blueprint.TryGet(out var readable) ? new LookSource.Blueprint(readable)
            : look.Bounds.IsSome ? new LookSource.Box()
            : new LookSource.Cell();
        return new LookFacts(bounds, source);
    }

    private static Option<MergeFacts> MergeOf(PrefabData prefab, Option<ItemKey> tool)
    {
        if (prefab.MergeBlocked || !tool.TryGet(out var offhand))
            return Option<MergeFacts>.None;
        foreach (var type in prefab.Types)
            if (type.FullName == GridMergeable)
                return Option.Some(new MergeFacts(offhand));
        return Option<MergeFacts>.None;
    }

    // The first opening type along the class chain, the prefab's own class first, so a modded subclass inherits its
    // base's kind and a WindowShutter (a Door over a window) is a window.
    private static Option<OpeningKind> OpeningOf(ValueList<TypeData> types)
    {
        foreach (var type in types)
            switch (type.FullName)
            {
                case "Assets.Scripts.Objects.Structures.WindowShutter":
                case "Assets.Scripts.Objects.WallTransparent":
                    return Option.Some(OpeningKind.Window);
                case "Assets.Scripts.Objects.Structures.Door":
                case "Assets.Scripts.Objects.Structures.RoboticArmDoor":
                    return Option.Some(OpeningKind.Door);
            }

        return Option<OpeningKind>.None;
    }

    // What a device keeps going, from every type of its chain and its interfaces, so a modded subclass or a modded
    // class that implements IPoweredVent inherits it: powered vents and filtration keep the air, heaters, coolers and
    // air conditioners the temperature (decompile Assets.Scripts.Objects.Pipes/IPoweredVent.cs, FiltrationMachineBase.cs,
    // IndustrialFiltration.cs; Assets.Scripts.Objects.Electrical/WallHeater.cs, WallCooler.cs, AirConditioner.cs).
    private static Keeps KeepsOf(ValueList<TypeData> types)
    {
        var keeps = Keeps.None;
        foreach (var type in types)
            keeps |= type.FullName switch
            {
                "Assets.Scripts.Objects.Pipes.IPoweredVent" => Keeps.Air,
                "Assets.Scripts.Objects.Pipes.FiltrationMachineBase" => Keeps.Air,
                "Assets.Scripts.Objects.Pipes.IndustrialFiltration" => Keeps.Air,
                "Assets.Scripts.Objects.Electrical.WallHeater" => Keeps.Heat,
                "Assets.Scripts.Objects.Electrical.WallCooler" => Keeps.Heat,
                "Assets.Scripts.Objects.Electrical.AirConditioner" => Keeps.Heat,
                _ => Keeps.None,
            };
        return keeps;
    }

    private static bool SuppliesPower(ValueList<TypeData> types)
    {
        foreach (var type in types)
            if (type.DeclaresGeneratedPower)
                return true;
        return false;
    }

    private static bool Has(ValueList<TypeData> types, string fullName)
    {
        foreach (var type in types)
            if (type.FullName == fullName)
                return true;
        return false;
    }

    // Each control votes for the side its point lies on, along the axis it lies furthest out on; a point within
    // ControlDeadZone of the pivot on that axis, or a tie between two sides, names no side.
    private static Option<Direction> ControlFaceOf(ValueList<Vector> controls)
    {
        if (controls.Count == 0)
            return Option<Direction>.None;
        Span<int> votes = stackalloc int[7];
        foreach (var point in controls)
            if (SideOf(point) is { } side)
                votes[(int)side]++;

        var best = 0;
        var tied = false;
        for (var side = 1; side < votes.Length; side++)
            if (votes[side] > votes[best])
            {
                best = side;
                tied = false;
            }
            else if (votes[side] == votes[best] && votes[side] > 0)
                tied = true;

        return best == 0 || tied ? Option<Direction>.None : Option.Some((Direction)best);
    }

    private static Direction? SideOf(Vector point)
    {
        var (x, y, z) = (Math.Abs(point.X), Math.Abs(point.Y), Math.Abs(point.Z));
        if (x > y && x > z && x > ControlDeadZone)
            return point.X > 0 ? Direction.XPlus : Direction.XMinus;
        if (y > x && y > z && y > ControlDeadZone)
            return point.Y > 0 ? Direction.YPlus : Direction.YMinus;
        if (z > x && z > y && z > ControlDeadZone)
            return point.Z > 0 ? Direction.ZPlus : Direction.ZMinus;
        return null;
    }

    // Every small cell the piece fills takes its slot, its own cell first: each grid-bounds offset snapped to the
    // small grid as the game registers it (GridBounds.GetLocalSmallGrid -> Grid3(pos, 0.5, 0.25)), so a long
    // straight's raw offsets 0, -1, -6, -11 dm fill the cells 0, -5, -10.
    private static ValueList<FootprintCell> SmallFootprint(ValueList<CellOffset> bounds, Slot slot)
    {
        var footprint = new List<FootprintCell>(bounds.Count + 1) { new(default, slot) };
        foreach (var raw in bounds)
        {
            var cell = new CellOffset(SmallCell(raw.X), SmallCell(raw.Y), SmallCell(raw.Z));
            var known = false;
            foreach (var each in footprint)
                known |= each.At == cell;
            if (!known)
                footprint.Add(new FootprintCell(cell, slot));
        }

        return ValueList.From(footprint);
    }

    // The game's small-cell key of an offset in decimetres: ExtensionMethods.GridCenter(0.5, 0.25) with Unity's
    // Mathf.Round (halves to even), then Grid3's key (decimetres, rounded away from zero).
    internal static int SmallCell(int decimetres)
    {
        var metres = decimetres / 10.0;
        var centre = Math.Round((metres - 0.5) / 0.5, MidpointRounding.ToEven) * 0.5 + 0.5;
        return (int)Math.Round(centre * 10, MidpointRounding.AwayFromZero);
    }

    // The material is the entry the game uses up (Structure.HandleToolUse): a stack with a quantity, or an item in the
    // other hand (ToolEntry2) with a quantity, which the game destroys whole. The other entry is the tool held.
    private static BuildStep? Step(StateData state, int index)
    {
        var material = Option<ItemQuantity>.None;
        var tool = Option<ItemKey>.None;
        for (var slot = 0; slot < 2; slot++)
        {
            if (!(slot == 0 ? state.Entry : state.Entry2).TryGet(out var item))
                continue;
            var usedUp = item.Quantity > 0 && (item.IsStack || slot == 1);
            if (usedUp)
            {
                var count = item.IsStack ? item.Quantity : 1;
                if (material.IsSome || !ItemQuantity.Create(item.Item.Key, count).TryGet(out var quantity))
                    return null;
                material = Option.Some(quantity);
            }
            else
            {
                if (tool.IsSome)
                    return null;
                tool = Option.Some(item.Item.Key);
            }
        }

        return StepIndex.Create(index).TryGet(out var stepIndex)
            ? new BuildStep(stepIndex, tool, material, state.Exit.Map(static e => e.Key), state.BlocksAir) { BlocksGravity = state.BlocksGravity }
            : null;
    }

    // Connection.SetGrids: the end's own cell (LocalGrid) is where it sits; the cell 0.5 m along its forward
    // (FacingGrid) is the piece cell it leaves from. Outward is LocalGrid - FacingGrid. The reach is how far the end's
    // point lies from the centre of the cell it leaves from, along the outward direction.
    private static ConnectionEnd? End(EndData end, int index)
    {
        var at = SmallCell(end.Position, default);
        var from = SmallCell(end.Position, end.Forward);
        var facing = (at.X - from.X, at.Y - from.Y, at.Z - from.Z) switch
        {
            (5, 0, 0) => Direction.XPlus,
            (-5, 0, 0) => Direction.XMinus,
            (0, 5, 0) => Direction.YPlus,
            (0, -5, 0) => Direction.YMinus,
            (0, 0, 5) => Direction.ZPlus,
            (0, 0, -5) => Direction.ZMinus,
            _ => (Direction?)null,
        };
        if (facing is null)
            return null;
        var (ux, uy, uz) = ((at.X - from.X) / 5, (at.Y - from.Y) / 5, (at.Z - from.Z) / 5);
        var reachMetres = ux * (end.Position.X - from.X / 10.0) + uy * (end.Position.Y - from.Y / 10.0) +
                          uz * (end.Position.Z - from.Z / 10.0);
        var reach = (int)Math.Round(reachMetres * 1000);
        return new ConnectionEnd(from, facing.Value, Kinds(end))
        {
            Reach = reach >= 0 ? Option.Some(new Millimetres(reach)) : Option<Millimetres>.None,
            Role = end.Role is >= 1 and <= 5 ? Option.Some((ConnectionRole)end.Role) : Option<ConnectionRole>.None,
            GameIndex = Option.Some(index),
            GameName = Option.Some(end.KindName),
        };
    }

    private static ValueSet<NetworkType> Kinds(EndData end)
    {
        var bits = (int)end.Kinds;
        var kinds = new System.Collections.Generic.List<NetworkType>(2);
        void Take(NetworkKinds bit, NetworkType type)
        {
            if ((bits & (int)bit) == 0)
                return;
            kinds.Add(type);
            bits &= ~(int)bit;
        }

        Take(NetworkKinds.Pipe, new NetworkType.GasPipe());
        Take(NetworkKinds.Power, new NetworkType.Power());
        Take(NetworkKinds.Data, new NetworkType.Data());
        Take(NetworkKinds.Chute, new NetworkType.Chute());
        Take(NetworkKinds.Elevator, new NetworkType.Elevator());
        Take(NetworkKinds.PipeLiquid, new NetworkType.LiquidPipe());
        Take(NetworkKinds.LandingPad, new NetworkType.LandingPad());
        Take(NetworkKinds.LaunchPad, new NetworkType.LaunchPad());
        Take(NetworkKinds.RoboticArmRail, new NetworkType.Rail());

        if (bits != 0)
            kinds.Add(new NetworkType.Unknown(end.KindName, bits));
        return ValueSet.From(kinds);
    }

    // ExtensionMethods.GridCenter(0.5, 0.25) then Grid3's round(metres * 10), in the game's float arithmetic.
    private static CellOffset SmallCell(Vector position, Vector step) =>
        new(Axis(position.X, step.X), Axis(position.Y, step.Y), Axis(position.Z, step.Z));

    private static int Axis(double position, double forward)
    {
        var metres = (float)position + (float)forward * SmallGridSize;
        var centre = (float)Math.Round((metres - SmallGridHalf) / SmallGridSize) * SmallGridSize + SmallGridHalf;
        var decimetres = centre * 10f;
        return decimetres >= 0f ? (int)(decimetres + 0.5f) : (int)(decimetres - 0.5f);
    }

    private static Result<PieceFacts, Reason> Refuse(PrefabData prefab, string problem) =>
        Result<PieceFacts, Reason>.Error(CatalogueReasons.Piece(prefab.Name, problem));
}
