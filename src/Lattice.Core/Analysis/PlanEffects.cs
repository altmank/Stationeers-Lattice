using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Consequences;
using Lattice.Core.Ports;
using Lattice.Core.Safety;
using Lattice.Core.Support;
using Paint = Lattice.Core.Safety.Paint;

namespace Lattice.Core.Analysis;

/// <summary>
/// What a set of ghosts would do to networks, rooms and gas if they were built (the consequence
/// prediction extended to whole plans): the built world against the world with only those ghosts done
/// (<see cref="PlannedWorld"/> restricted to the set). Every fact comes from the catalogue (ends, end kinds, air
/// blocking, vents) and the world view, never from a prefab name, so a modded piece counts like any piece of its kind.
/// Main thread, as the board is. Cost: linear in the ghosts, the pieces of each network they take from, up to
/// <see cref="Vents.MostFollowed"/> pieces behind each vent, and up to the game's room size (<see cref="GameRooms"/>)
/// in large cells per region filled.
/// </summary>
/// <param name="catalogue">The catalogue.</param>
/// <param name="grid">The grid.</param>
/// <param name="rooms">The game's room rule: the bridge reads it from the game, <see cref="GameRooms.Default"/> elsewhere.</param>
internal sealed class PlanEffects(ICatalogue catalogue, IGrid grid, GameRooms rooms)
{
    public PlanEffects(ICatalogue catalogue, IGrid grid)
        : this(catalogue, grid, GameRooms.Default)
    {
    }

    /// <summary>The effects of the ghosts on the board's world; analyzers' facts are added by the caller.</summary>
    public AnalysisReport Of(IReadOnlyList<Ghost> ghosts, IGhostBoard board, IWorldView world, ValueList<AnalyzerFact> facts)
    {
        var scan = new SafetyScan(ghosts, board, world, catalogue, grid, new NoSafetyFacts(), SafetyLimits.Default, onlyThese: true);
        return new AnalysisReport(NetworkEffects.Of(scan), RoomEffects.Of(scan, rooms), GasEffects.Of(scan), facts);
    }
}

/// <summary>
/// The game's rule for what makes a room. The game fills large cells from a changed cell through open faces and stops
/// after <see cref="FillLimit"/> cells; a fill that reaches the limit makes no room, and every room in the cells it
/// filled is deleted (<c>RoomFloodFiller.Fill</c> returns <c>IterationLimit</c> when its count reaches
/// <c>MAX_ITERATIONS</c>, decompile Rooms/RoomFloodFiller.cs; <c>RoomEvaluator.ThreadedWork</c> makes a room only on
/// <c>Success</c>, decompile Rooms/RoomEvaluator.cs). So a room has fewer than <see cref="FillLimit"/> cells. A game
/// fact, not a Lattice choice: the bridge reads the number from the game's constant.
/// </summary>
/// <param name="FillLimit">The game's fill limit in large cells (1200 in the game read for this code).</param>
internal sealed record GameRooms(int FillLimit)
{
    /// <summary>The game's limit as read from the decompile (<c>RoomFloodFiller.MAX_ITERATIONS</c> = 1200).</summary>
    public static GameRooms Default => new(1200);

    /// <summary>Whether a fill of this many cells makes no room in the game.</summary>
    public bool NoRoom(int cells) => cells >= FillLimit;
}

/// <summary>
/// Network joins, splits and load. The ghosts' removals take pieces out of their networks; the pieces left on each such
/// network are linked again by the game's connection rule (<see cref="Connectivity"/>); each new piece links to what its
/// ends meet in the planned world (other new pieces, pieces left, or a whole network the ghosts take nothing from); then
/// <see cref="NetworkForecaster"/> gives the networks after. A merge gives one <see cref="NetworkEffect.Joins"/> per
/// network it adds to the first (lowest id); a network falling apart gives <see cref="NetworkEffect.Splits"/>. A merge
/// whose networks all have a load read gives <see cref="NetworkEffect.Load"/> on the first: its load before, the sum
/// after, the lowest rating read. Device ports are not followed (a device bridging two networks is not a join here),
/// and a new piece meets only pieces whose own cell its end reaches.
/// </summary>
internal static class NetworkEffects
{
    public static ValueList<NetworkEffect> Of(SafetyScan scan)
    {
        var edit = new NetworkEdit();
        var taken = new HashSet<ReferenceId>();
        var cut = new List<NetworkRef>();
        foreach (var ghost in scan.Ghosts)
            if (SafetyScan.Taken(ghost).TryGet(out var target) && scan.World.NetworkOf(target.Id).TryGet(out var network))
            {
                edit.Remove(target.Id, network);
                taken.Add(target.Id);
                if (!cut.Contains(network))
                    cut.Add(network);
            }

        Remaining(scan, edit, taken, cut);
        var added = Added(scan, edit);
        foreach (var piece in added)
            LinkNew(scan, edit, piece, cut);
        if (added.Count == 0 && cut.Count == 0)
            return ValueList<NetworkEffect>.Empty;

        var forecast = NetworkForecaster.Of(edit, out _);
        var effects = new List<NetworkEffect>(forecast.Merges.Count + forecast.Splits.Count);
        foreach (var merge in forecast.Merges)
        {
            var first = merge.NetworksBefore[0];
            for (var i = 1; i < merge.NetworksBefore.Count; i++)
                effects.Add(new NetworkEffect.Joins(first, merge.NetworksBefore[i]));
            if (LoadOf(scan, merge.NetworksBefore).TryGet(out var load))
                effects.Add(load);
        }

        foreach (var split in forecast.Splits)
            effects.Add(new NetworkEffect.Splits(split.Network));
        return ValueList.From(effects);
    }

    private static void Remaining(SafetyScan scan, NetworkEdit edit, HashSet<ReferenceId> taken, List<NetworkRef> cut)
    {
        foreach (var network in cut)
        {
            if (!scan.World.Network(network).TryGet(out var info))
                continue;
            var models = new List<PieceModel>(info.Pieces.Count);
            var ids = new HashSet<ReferenceId>();
            foreach (var id in info.Pieces)
                if (!taken.Contains(id) && ids.Add(id) && scan.Models.Piece(id).TryGet(out var model))
                {
                    models.Add(model);
                    edit.AddPiece(id, network);
                }

            foreach (var link in Connectivity.LinksTouching(models, ids))
                edit.Link(ForecastNode.Piece(link.From), ForecastNode.Piece(link.To));
        }
    }

    private static List<PieceModel> Added(SafetyScan scan, NetworkEdit edit)
    {
        var added = new List<PieceModel>();
        foreach (var ghost in scan.Ghosts)
        {
            if (!SafetyScan.Put(ghost).TryGet(out var put) || !scan.Piece(put.Piece).TryGet(out var piece) || piece.Ends.Count == 0)
                continue;
            var model = scan.Models.Piece(SupportWorld.AssumedInfo(ghost.Id, put), piece);
            edit.AddNewPiece(model.Id);
            added.Add(model);
        }

        return added;
    }

    private static void LinkNew(SafetyScan scan, NetworkEdit edit, PieceModel piece, List<NetworkRef> cut)
    {
        var node = ForecastNode.Piece(piece.Id);
        foreach (var end in piece.Ends)
            foreach (var info in scan.Planned.At(end.Into))
            {
                if (info.Ref.Id == piece.Id || !scan.Piece(info.Ref.Piece).TryGet(out var facts))
                    continue;
                var other = scan.Models.Piece(info, facts);
                if (!Connectivity.Links(piece, other) && !Connectivity.Links(other, piece))
                    continue;
                if (scan.Planned.AssumedFrom(info.Ref).IsSome)
                    edit.Link(node, ForecastNode.Piece(info.Ref.Id));
                else if (scan.World.NetworkOf(info.Ref.Id).TryGet(out var network))
                {
                    if (cut.Contains(network))
                        edit.Link(node, ForecastNode.Piece(info.Ref.Id));
                    else
                    {
                        edit.AddNetwork(network);
                        edit.Link(node, ForecastNode.Whole(network));
                    }
                }
            }
    }

    private static Option<NetworkEffect> LoadOf(SafetyScan scan, List<NetworkRef> networks)
    {
        var first = 0.0;
        var sum = 0.0;
        var rating = Option<Watts>.None;
        for (var i = 0; i < networks.Count; i++)
        {
            if (!scan.World.Network(networks[i]).TryGet(out var info) || !info.Load.TryGet(out var load))
                return Option<NetworkEffect>.None;
            if (i == 0)
                first = load.Value;
            sum += load.Value;
            if (info.Rating.TryGet(out var rated) && (!rating.TryGet(out var lowest) || rated.Value < lowest.Value))
                rating = Option.Some(rated);
        }

        return Option.Some<NetworkEffect>(new NetworkEffect.Load(networks[0], new Watts(first), new Watts(sum), rating));
    }
}

/// <summary>
/// Rooms opened, sealed and resized. Air passes between two large cells when nothing on the face between them blocks
/// air and neither cell's own structure does (<see cref="FaceSeal"/>, <see cref="WorldModels.AirBlockerOf"/>). From both
/// sides of every face, and around every cell, whose air-blocking piece the ghosts take out or put in, the planned world
/// is filled through faces that let air pass, as the game fills (<see cref="GameRooms"/>): a fill that reaches the
/// game's limit makes no room, so it counts as the outside. A filled region holding a room of the built world and
/// reaching the outside or another room opens it; a closed region with no room is sealed; a closed region with one room
/// of another size resizes it. Only large-grid pieces are read for blocking; terrain filling a large cell (which the
/// game also counts as blocked, <c>GridController.LargeGridIsFullOfVoxels</c>) is not read.
/// </summary>
internal static class RoomEffects
{
    public static ValueList<RoomEffect> Of(SafetyScan scan, GameRooms rooms)
    {
        var air = new AirCells(scan);
        var seeds = new List<Cell>();
        foreach (var ghost in scan.Ghosts)
        {
            if (SafetyScan.Taken(ghost).TryGet(out var target) && scan.World.Structure(target.Id).TryGet(out var info))
                Seeds(scan, air, info, seeds);
            if (SafetyScan.Put(ghost).TryGet(out var put))
                Seeds(scan, air, SupportWorld.AssumedInfo(ghost.Id, put), seeds);
        }

        var effects = new List<RoomEffect>();
        var filled = new HashSet<Cell>();
        foreach (var seed in seeds)
        {
            if (filled.Contains(seed) || air.Blocked(seed))
                continue;
            var region = Fill(scan, air, seed, rooms, out var outside);
            foreach (var cell in region)
                filled.Add(cell);
            Classify(scan, region, outside, effects);
        }

        return ValueList.From(effects);
    }

    private static void Seeds(SafetyScan scan, AirCells air, StructureInfo info, List<Cell> seeds)
    {
        if (!air.Blocker(info).TryGet(out var spots))
            return;
        foreach (var spot in spots)
            if (SafetyScan.IsFace(spot.Slot))
            {
                var (negative, positive) = scan.SidesOf(spot);
                seeds.Add(negative);
                seeds.Add(positive);
            }
            else if (spot.Slot == Slot.Centre)
            {
                seeds.Add(spot.Cell);
                for (var d = Direction.XPlus; d <= Direction.ZMinus; d++)
                    seeds.Add(scan.Grid.Neighbour(spot.Cell, d, GridSize.Large));
            }
    }

    // Breadth first like the game's fill; stops as soon as the region is too big to be a room.
    private static List<Cell> Fill(SafetyScan scan, AirCells air, Cell start, GameRooms rooms, out bool outside)
    {
        var seen = new HashSet<Cell> { start };
        var region = new List<Cell> { start };
        outside = rooms.NoRoom(region.Count);
        for (var next = 0; next < region.Count && !outside; next++)
        {
            var here = region[next];
            for (var d = Direction.XPlus; d <= Direction.ZMinus && !outside; d++)
            {
                var there = scan.Grid.Neighbour(here, d, GridSize.Large);
                if (seen.Contains(there) || air.Blocked(there) || air.FaceBlocked(here, d))
                    continue;
                seen.Add(there);
                region.Add(there);
                outside = rooms.NoRoom(region.Count);
            }
        }

        return region;
    }

    private static void Classify(SafetyScan scan, List<Cell> region, bool outside, List<RoomEffect> effects)
    {
        var rooms = new List<RoomRef>(1);
        foreach (var cell in region)
            if (scan.World.RoomAt(cell).TryGet(out var room) && !rooms.Contains(room))
                rooms.Add(room);
        if (outside || rooms.Count > 1)
        {
            foreach (var room in rooms)
                Add(effects, new RoomEffect.Opens(room));
            return;
        }

        if (rooms.Count == 0)
        {
            Add(effects, new RoomEffect.Seals(region.Count));
            return;
        }

        if (scan.World.Room(rooms[0]).TryGet(out var info) && info.Cells.Count != region.Count)
            Add(effects, new RoomEffect.Resizes(rooms[0], info.Cells.Count, region.Count));
    }

    private static void Add(List<RoomEffect> effects, RoomEffect effect)
    {
        if (!effects.Contains(effect))
            effects.Add(effect);
    }

    /// <summary>Air blocking in the planned world, per large cell and face; each structure's spots read once.</summary>
    private sealed class AirCells(SafetyScan scan)
    {
        private readonly Dictionary<ReferenceId, Option<IReadOnlyList<SpotKey>>> _blockers = [];
        private readonly Dictionary<Cell, bool> _blocked = [];

        /// <summary>The spots of a large-grid structure that blocks air; none for one that does not.</summary>
        public Option<IReadOnlyList<SpotKey>> Blocker(StructureInfo info)
        {
            if (_blockers.TryGetValue(info.Ref.Id, out var known))
                return known;
            var spots = Option<IReadOnlyList<SpotKey>>.None;
            if (scan.Piece(info.Ref.Piece).TryGet(out var piece) && piece.Grid == GridSize.Large
                && WorldModels.AirBlockerOf(info, piece).BlocksAir && scan.Shape(info).TryGet(out var shape))
                spots = Option.Some(shape.Spots);
            return _blockers[info.Ref.Id] = spots;
        }

        /// <summary>Whether the cell's own structure blocks air, so air never enters it.</summary>
        public bool Blocked(Cell cell)
        {
            if (_blocked.TryGetValue(cell, out var known))
                return known;
            var blocked = false;
            foreach (var info in scan.Planned.At(cell))
                if (Blocker(info).TryGet(out var spots))
                    foreach (var spot in spots)
                        blocked |= spot.Slot == Slot.Centre && spot.Cell == cell;
            return _blocked[cell] = blocked;
        }

        /// <summary>Whether a piece on the face between the cell and its neighbour that way blocks air.</summary>
        public bool FaceBlocked(Cell cell, Direction toward)
        {
            var face = scan.FaceBetween(cell, toward);
            return Holds(cell, face) || Holds(scan.Grid.Neighbour(cell, toward, GridSize.Large), face);
        }

        private bool Holds(Cell cell, Boundary face)
        {
            foreach (var info in scan.Planned.At(cell))
                if (Blocker(info).TryGet(out var spots))
                    foreach (var spot in spots)
                        if (face.Holds(spot))
                            return true;
            return false;
        }
    }
}

/// <summary>
/// Gas let out. A planned vent (<see cref="PieceFacts.Vents"/>) followed through the planned pieces to the built
/// networks it joins (<see cref="Vents.Joined"/>) releases each one's gas where it stands, into the room of its large
/// cell or the outside, when the network's contents and temperature are read. Taking pipes out releases nothing: the
/// game divides the network's gas over the pipes left (<c>Pipe.OnDestroy</c>, decompile
/// Assets.Scripts.Objects.Pipes/Pipe.cs). Painting adds the spray can's pollutant to the room the piece is in, one
/// <see cref="PerSpray"/> per painted ghost (<c>SprayCan.PollutionMixture</c> added in <c>OnUseItem</c>, decompile
/// Assets.Scripts.Objects.Items/SprayCan.cs); a face piece counts in the room on either side.
/// </summary>
internal static class GasEffects
{
    /// <summary>The pollutant one spray adds: 0.01 mol (<c>SprayCan.PollutionMixture</c>).</summary>
    public static Moles PerSpray => new(0.01);

    public static ValueList<GasEffect> Of(SafetyScan scan)
    {
        var effects = new List<GasEffect>();
        Released(scan, effects);
        Painted(scan, effects);
        return ValueList.From(effects);
    }

    private static void Released(SafetyScan scan, List<GasEffect> effects)
    {
        foreach (var ghost in scan.Ghosts)
        {
            if (!SafetyScan.Put(ghost).TryGet(out var put) || !scan.Piece(put.Piece).TryGet(out var piece) || !piece.Vents)
                continue;
            var vent = scan.Models.Piece(SupportWorld.AssumedInfo(ghost.Id, put), piece);
            var room = scan.World.RoomAt(scan.LargeOf(put.At, piece.Grid));
            foreach (var network in Vents.Joined(scan, vent))
                if (scan.World.Network(network).TryGet(out var info) && info.Contents.TryGet(out var contents)
                    && info.Temperature.TryGet(out var kelvin) && Oxidisers.Total(contents) > 0.0)
                    effects.Add(new GasEffect.Releases(put.At, room, new GasMix(Amounts(contents), kelvin)));
        }
    }

    private static ValueList<GasAmount> Amounts(ValueList<GasHeld> contents)
    {
        var amounts = new List<GasAmount>(contents.Count);
        foreach (var gas in contents)
            amounts.Add(new GasAmount(gas.Gas, gas.Amount));
        return ValueList.From(amounts);
    }

    private static void Painted(SafetyScan scan, List<GasEffect> effects)
    {
        var sprays = new Dictionary<RoomRef, int>();
        var order = new List<RoomRef>();
        foreach (var ghost in scan.Ghosts)
        {
            if (!Paint.ColourOf(ghost) || !Paint.Where(scan, ghost).TryGet(out var placement) || !scan.Shape(placement).TryGet(out var shape))
                continue;
            var rooms = new List<RoomRef>(2);
            foreach (var spot in shape.Spots)
                if (SafetyScan.IsFace(spot.Slot))
                {
                    var (negative, positive) = scan.SidesOf(spot);
                    InRoom(scan, negative, rooms);
                    InRoom(scan, positive, rooms);
                }
                else
                {
                    InRoom(scan, scan.LargeOf(spot.Cell, shape.Piece.Grid), rooms);
                }

            foreach (var room in rooms)
            {
                if (!sprays.ContainsKey(room))
                    order.Add(room);
                sprays[room] = (sprays.TryGetValue(room, out var count) ? count : 0) + 1;
            }
        }

        foreach (var room in order)
            effects.Add(new GasEffect.Pollutes(room, new Moles(PerSpray.Value * sprays[room])));
    }

    private static void InRoom(SafetyScan scan, Cell large, List<RoomRef> rooms)
    {
        if (scan.World.RoomAt(large).TryGet(out var room) && !rooms.Contains(room))
            rooms.Add(room);
    }
}
