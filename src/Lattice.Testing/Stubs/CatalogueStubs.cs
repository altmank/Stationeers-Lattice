using System;
using System.Collections.Generic;
using Lattice.Api;

namespace Lattice.Testing.Stubs;

/// <summary>
/// A base for a test fake of <see cref="IPieceCatalogue"/>: inherit it and override what your test uses. Every
/// member is virtual and throws <see cref="NotSupportedException"/> naming itself until overridden. A minor that
/// adds a member to <see cref="IPieceCatalogue"/> adds it here in the same release, so a fake that inherits this
/// keeps compiling and loading.
/// </summary>
public class StubPieceCatalogue : IPieceCatalogue
{
    /// <inheritdoc />
    public virtual ValueList<PieceEntry> Pieces =>
        throw Stub.NotSetUp("IPieceCatalogue", "Pieces", "StubPieceCatalogue");

    /// <inheritdoc />
    public virtual ValueList<KitEntry> Kits => throw Stub.NotSetUp("IPieceCatalogue", "Kits", "StubPieceCatalogue");

    /// <inheritdoc />
    public virtual Result<PieceEntry, Reason> Piece(PrefabKey piece) =>
        throw Stub.NotSetUp("IPieceCatalogue", "Piece", "StubPieceCatalogue");

    /// <inheritdoc />
    public virtual Result<KitEntry, Reason> Kit(KitKey kit) =>
        throw Stub.NotSetUp("IPieceCatalogue", "Kit", "StubPieceCatalogue");

    /// <inheritdoc />
    public virtual Result<PrefabKey, Reason> PieceOf(KitKey kit, KitOption option) =>
        throw Stub.NotSetUp("IPieceCatalogue", "PieceOf", "StubPieceCatalogue");

    /// <inheritdoc />
    public virtual Result<KitPlace, Reason> KitOf(PrefabKey piece) =>
        throw Stub.NotSetUp("IPieceCatalogue", "KitOf", "StubPieceCatalogue");

    /// <inheritdoc />
    public virtual Result<PieceFamily, Reason> FamilyOf(PrefabKey piece) =>
        throw Stub.NotSetUp("IPieceCatalogue", "FamilyOf", "StubPieceCatalogue");

    /// <inheritdoc />
    public virtual Result<ValueList<PieceFamily>, Reason> FamiliesOf(PrefabKey piece) =>
        throw Stub.NotSetUp("IPieceCatalogue", "FamiliesOf", "StubPieceCatalogue");

    /// <inheritdoc />
    public virtual Option<PieceFamily> ParentOf(PieceFamily family) =>
        throw Stub.NotSetUp("IPieceCatalogue", "ParentOf", "StubPieceCatalogue");

    /// <inheritdoc />
    public virtual Result<ValueList<Port>, Reason> PortsAt(Placement placement) =>
        throw Stub.NotSetUp("IPieceCatalogue", "PortsAt", "StubPieceCatalogue");

    /// <inheritdoc />
    public virtual Result<Option<Axis>, Reason> FacingOf(Placement placement) =>
        throw Stub.NotSetUp("IPieceCatalogue", "FacingOf", "StubPieceCatalogue");

    /// <inheritdoc />
    public virtual Result<ValueList<Cell>, Reason> CellsAt(Placement placement) =>
        throw Stub.NotSetUp("IPieceCatalogue", "CellsAt", "StubPieceCatalogue");

    /// <inheritdoc />
    public virtual Result<string, Reason> ItemName(ItemKey item) =>
        throw Stub.NotSetUp("IPieceCatalogue", "ItemName", "StubPieceCatalogue");
}
