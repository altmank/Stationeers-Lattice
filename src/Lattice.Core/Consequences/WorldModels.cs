using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Ports;
using Port = Lattice.Core.Ports;

namespace Lattice.Core.Consequences;

/// <summary>
/// The consequence models built from the world view and the catalogue. Every fact comes from the piece's catalogue
/// entry (ends, end kinds, grid, build steps, ratings), never from its prefab name, hash or class, so a modded pipe,
/// cable or wall is modelled like any piece of its kind. Constant cost per structure, linear in its ends; a network's
/// gas reading is linear in its pieces and gases.
/// </summary>
internal sealed class WorldModels(IWorldView world, ICatalogue catalogue, IGrid grid)
{
    /// <summary>
    /// The game's network type bits of an end's kinds (Assets.Scripts.Objects/NetworkType.cs); a modded kind keeps the
    /// bits the game gave it.
    /// </summary>
    public static NetworkTypes TypesOf(ValueSet<NetworkType> kinds)
    {
        var mask = 0;
        foreach (var kind in kinds)
            mask |= kind switch
            {
                NetworkType.GasPipe => 0x1,
                NetworkType.Power => 0x2,
                NetworkType.Data => 0x4,
                NetworkType.Chute => 0x8,
                NetworkType.Elevator => 0x10,
                NetworkType.LiquidPipe => 0x20,
                NetworkType.LandingPad => 0x40,
                NetworkType.LaunchPad => 0x80,
                NetworkType.Rail => 0x100,
                NetworkType.Unknown unknown => unknown.Bits,
                _ => 0,
            };
        return new NetworkTypes(mask);
    }

    /// <summary>An end's device role in the game's numbering (input 1, input2 2, output 3, output2 4, waste 5); none for a run end.</summary>
    public static ConnectionRole RoleOf(Option<Port.ConnectionRole> role) =>
        role.TryGet(out var given) ? new ConnectionRole((int)given) : ConnectionRole.None;

    /// <summary>A pipe's content in the game's numbering (gas 1, liquid 2); a pipe that holds any takes every content.</summary>
    public static Option<PipeContent> ContentOf(Option<Port.PipeContent> content) => content.Map(static c => c switch
    {
        Port.PipeContent.Gas => new PipeContent(1, TakesAny: false),
        Port.PipeContent.Liquid => new PipeContent(2, TakesAny: false),
        _ => new PipeContent(3, TakesAny: true),
    });

    /// <summary>
    /// The structure as connectivity sees it. Its cells are the cells of its catalogue footprint, turned; with no
    /// footprint given, its own cell and the cell of each of its ends.
    /// </summary>
    public PieceModel Piece(StructureInfo structure, PieceFacts facts)
    {
        var cells = new List<Cell>(facts.Footprint.Count + facts.Ends.Count + 1);
        foreach (var cell in facts.Footprint)
        {
            var at = structure.Ref.At.Plus(grid.Apply(structure.Turn, cell.At));
            if (!cells.Contains(at))
                cells.Add(at);
        }

        if (cells.Count == 0)
            cells.Add(structure.Ref.At);
        var ends = new List<PieceEnd>(facts.Ends.Count);
        foreach (var end in facts.Ends)
        {
            var from = structure.Ref.At.Plus(grid.Apply(structure.Turn, end.At));
            var into = grid.Neighbour(from, grid.Apply(structure.Turn, end.Facing), facts.Grid);
            if (facts.Footprint.Count == 0 && !cells.Contains(from))
                cells.Add(from);
            ends.Add(new PieceEnd(into, from, TypesOf(end.Kinds), RoleOf(end.Role)));
        }

        return new PieceModel(structure.Ref.Id, cells, ends, ContentOf(facts.Pipe));
    }

    /// <summary>The structure with the id as connectivity sees it; refused when the world or the catalogue does not know it.</summary>
    public Result<PieceModel, Reason> Piece(ReferenceId id)
    {
        if (!world.Structure(id).TryGet(out var structure))
            return Result<PieceModel, Reason>.Error(Reason.Of(ReasonCodes.NotFound, ConsequenceCodes.Rule, ValueMap<string, string>.Of("structure", IdOrder.Text([id]))));
        return catalogue.Piece(structure.Ref.Piece).Map(facts => Piece(structure, facts));
    }

    /// <summary>The structures with the ids as connectivity sees them, skipping any the world or the catalogue does not know.</summary>
    public List<PieceModel> Pieces(IEnumerable<ReferenceId> ids)
    {
        var models = new List<PieceModel>();
        foreach (var id in ids)
            if (Piece(id).TryGet(out var model))
                models.Add(model);
        return models;
    }

    /// <summary>
    /// Whether the structure blocks air at its build step: that step's blocks-air fact; a built structure's last step.
    /// The catalogue holds no fact for a broken structure's state, so a broken one is taken as not blocking.
    /// </summary>
    public static AirBlocker AirBlockerOf(StructureInfo structure, PieceFacts facts)
    {
        var step = structure.State switch
        {
            BuildState.Building building => building.At.Value,
            BuildState.Built => facts.Steps.Count - 1,
            _ => -1,
        };
        var blocks = step >= 0 && step < facts.Steps.Count && facts.Steps[step].BlocksAir;
        return new AirBlocker(structure.Ref.Id, blocks);
    }

    /// <summary>A wall's face rating (<c>MaxPressureDelta</c>) in kPa; 0 when the catalogue gives none, which is never stressed.</summary>
    public static double FaceRatingKpa(PieceFacts facts) => facts.Ratings.Face.TryGet(out var face) ? face.Value : 0.0;

    /// <summary>The cable rating of each new piece that has one, for <see cref="PowerAfter.Of"/>.</summary>
    public static Dictionary<ReferenceId, double> CableRatings(IEnumerable<(ReferenceId Piece, PieceFacts Facts)> pieces)
    {
        var ratings = new Dictionary<ReferenceId, double>();
        foreach (var (piece, facts) in pieces)
            if (facts.Ratings.Cable.TryGet(out var watts))
                ratings[piece] = watts.Value;
        return ratings;
    }

    /// <summary>
    /// The pipe network's reading for the gas check: its contents in the order, volume, pieces, devices, cells and the
    /// lowest pipe rating among its pieces (none when none is rated). None when the world did not read its volume or
    /// contents (a cable or chute network has neither).
    /// </summary>
    public Option<NetworkGas> Gas(NetworkInfo network, GasOrder order)
    {
        if (!network.Volume.TryGet(out var volume) || !network.Contents.TryGet(out var contents))
            return Option<NetworkGas>.None;
        var rating = Option<double>.None;
        foreach (var piece in network.Pieces)
            if (world.Structure(piece).TryGet(out var structure) &&
                catalogue.Piece(structure.Ref.Piece).TryGet(out var facts) &&
                facts.Ratings.Pipe.TryGet(out var kpa) &&
                (!rating.TryGet(out var lowest) || kpa.Value < lowest))
                rating = Option.Some(kpa.Value);
        return Option.Some(new NetworkGas(
            network.Ref, order.Contents(contents), volume.Value, network.Pieces, network.Devices, network.Cells, rating));
    }

    /// <summary>The room as the room check records it: its cells and its air summed; none when its air was not read.</summary>
    public static Option<RoomRecord> Room(RoomInfo room)
    {
        if (!room.Air.TryGet(out var air))
            return Option<RoomRecord>.None;
        var moles = 0.0;
        var energy = 0.0;
        foreach (var gas in air)
        {
            moles += gas.Amount.Value;
            energy += gas.Energy.Value;
        }

        return Option.Some(new RoomRecord(room.Ref, room.Cells, moles, energy));
    }
}
