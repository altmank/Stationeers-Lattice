using System.Collections.Generic;
using Lattice.Api;

namespace Lattice.TestKit.World;

/// <summary>
/// The consumers' <see cref="IWorldQuery"/> over a <see cref="FakeWorld"/>, for rules and analyzers under test.
/// Selects <see cref="StructureSelector.Refs"/> (those still standing), <see cref="StructureSelector.OfPiece"/> and
/// <see cref="StructureSelector.Network"/>; refuses other kinds with <see cref="ReasonCodes.UnknownKind"/>. Nothing is
/// assumed built.
/// </summary>
internal sealed class FakeWorldQuery(FakeWorld world) : IWorldQuery
{
    public Option<StructureInfo> Structure(ReferenceId id) => world.Structure(id);

    public ValueList<StructureInfo> At(Cell cell) => world.At(cell);

    public Result<ValueList<StructureRef>, Reason> Select(StructureSelector selector)
    {
        var picked = new List<StructureRef>();
        switch (selector)
        {
            case StructureSelector.Refs refs:
                foreach (var r in refs.Values)
                    if (world.Structure(r.Id).IsSome)
                        picked.Add(r);
                break;
            case StructureSelector.OfPiece ofPiece:
                foreach (var s in world.All)
                    if (s.Ref.Piece == ofPiece.Piece)
                        picked.Add(s.Ref);
                break;
            case StructureSelector.Network network:
                if (world.Network(network.Of).TryGet(out var info))
                    foreach (var id in info.Pieces)
                        if (world.Structure(id).TryGet(out var s))
                            picked.Add(s.Ref);
                break;
            default:
                return Result<ValueList<StructureRef>, Reason>.Error(
                    Reason.Of(ReasonCodes.UnknownKind, TestKitIds.Catalogue, ValueMap<string, string>.Of("kind", selector.GetType().Name)));
        }

        return Result<ValueList<StructureRef>, Reason>.Ok(ValueList.From(picked));
    }

    public Option<NetworkRef> NetworkOf(ReferenceId id) => world.NetworkOf(id);

    public Option<RoomRef> RoomAt(Cell cell) => world.RoomAt(cell);

    public Option<GhostId> AssumedFrom(StructureRef structure) => Option<GhostId>.None;
}
