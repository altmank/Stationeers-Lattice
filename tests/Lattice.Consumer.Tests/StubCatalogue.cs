using System.Collections.Generic;
using Lattice.Api;

namespace Lattice.Consumer.Tests;

/// <summary>
/// A consumer's stand-in for Lattice's catalogue, written against the contract alone: the kits given, each option a
/// piece, and the pieces they name; no kit places a piece more than once.
/// </summary>
internal sealed class StubCatalogue : IPieceCatalogue
{
    private readonly Dictionary<PrefabKey, PieceEntry> _pieces = new();
    private readonly Dictionary<KitKey, KitEntry> _kits = new();

    public StubCatalogue(params KitEntry[] kits)
    {
        var pieces = new List<PieceEntry>();
        foreach (var kit in kits)
        {
            _kits.Add(kit.Key, kit);
            for (var i = 0; i < kit.Options.Count; i++)
            {
                var place = new KitPlace(kit.Key, KitOption.Create(i).Match(static o => o, static _ => default));
                var key = kit.Options[i];
                var entry = _pieces.TryGetValue(key, out var held)
                    ? held with { Kits = ValueList.From(new List<KitPlace>(held.Kits) { place }) }
                    : new PieceEntry(key, "Piece" + key.Hash, "Piece " + key.Hash, PieceGrid.Small, ValueList.Of(place));
                _pieces[key] = entry;
            }
        }

        foreach (var entry in _pieces.Values)
            pieces.Add(entry);
        Pieces = ValueList.From(pieces);
        Kits = ValueList.From(kits);
    }

    public ValueList<PieceEntry> Pieces { get; }

    public ValueList<KitEntry> Kits { get; }

    public Result<PieceEntry, Reason> Piece(PrefabKey piece) =>
        _pieces.TryGetValue(piece, out var entry) ? Result<PieceEntry, Reason>.Ok(entry) : Result<PieceEntry, Reason>.Error(Unknown());

    public Result<KitEntry, Reason> Kit(KitKey kit) =>
        _kits.TryGetValue(kit, out var entry) ? Result<KitEntry, Reason>.Ok(entry) : Result<KitEntry, Reason>.Error(Unknown());

    public Result<PrefabKey, Reason> PieceOf(KitKey kit, KitOption option) =>
        Kit(kit).Bind(entry => option.Index < entry.Options.Count
            ? Result<PrefabKey, Reason>.Ok(entry.Options[option.Index])
            : Result<PrefabKey, Reason>.Error(Reason.Of(ReasonCodes.InvalidValue, RuleId.Contract)));

    public Result<KitPlace, Reason> KitOf(PrefabKey piece) =>
        Piece(piece).Bind(entry => entry.Kits.Count > 0
            ? Result<KitPlace, Reason>.Ok(entry.Kits[0])
            : Result<KitPlace, Reason>.Error(Reason.Of(ReasonCodes.NotFound, RuleId.Contract)));

    public Result<PieceFamily, Reason> FamilyOf(PrefabKey piece) => Piece(piece).Map(static _ => PieceFamilies.Other);

    public Result<ValueList<PieceFamily>, Reason> FamiliesOf(PrefabKey piece) => Piece(piece).Map(static _ => ValueList.Of(PieceFamilies.Other));

    public Option<PieceFamily> ParentOf(PieceFamily family) => family == PieceFamilies.Window ? Option.Some(PieceFamilies.Wall) : Option<PieceFamily>.None;

    /// <summary>One power port toward +X from the piece's own cell, whatever its turn: enough for a consumer's flow.</summary>
    public Result<ValueList<Port>, Reason> PortsAt(Placement placement) =>
        Piece(placement.Piece).Bind(_ => PortIndex.Create(0).Map(index => ValueList.Of(new Port(
            index,
            ValueSet.Of(PortKinds.Power),
            "Power",
            Option.Some(PortRole.Input),
            placement.At,
            Face.PlusX,
            placement.At.Neighbour(Face.PlusX, PieceGrid.Small)))));

    /// <summary>No piece of this stub lies on a face.</summary>
    public Result<Option<Axis>, Reason> FacingOf(Placement placement) => Piece(placement.Piece).Map(static _ => Option<Axis>.None);

    private static Reason Unknown() => Reason.Of(ReasonCodes.UnknownPiece, RuleId.Contract);
}
