using System;
using System.Collections.Generic;
using Lattice.Api;

namespace Lattice.Testing.Stubs;

/// <summary>
/// A base for a test fake of <see cref="IGhostBoard"/>: inherit it and override what your test uses. Every member
/// is virtual and throws <see cref="NotSupportedException"/> naming itself until overridden. A minor that adds a
/// member to <see cref="IGhostBoard"/> adds it here in the same release, so a fake that inherits this keeps
/// compiling and loading.
/// </summary>
public class StubGhostBoard : IGhostBoard
{
    /// <inheritdoc />
    public virtual Revision Revision => throw Stub.NotSetUp("IGhostBoard", "Revision", "StubGhostBoard");

    /// <inheritdoc />
    public virtual ValueList<GroupCount> Groups => throw Stub.NotSetUp("IGhostBoard", "Groups", "StubGhostBoard");

    /// <inheritdoc />
    public virtual Option<Ghost> Find(GhostId id) =>
        throw Stub.NotSetUp("IGhostBoard", "Find", "StubGhostBoard");

    /// <inheritdoc />
    public virtual Result<ValueList<Ghost>, Reason> Select(Selector selector) =>
        throw Stub.NotSetUp("IGhostBoard", "Select", "StubGhostBoard");

    /// <inheritdoc />
    public virtual ValueList<Ghost> At(Cell cell) =>
        throw Stub.NotSetUp("IGhostBoard", "At", "StubGhostBoard");

    /// <inheritdoc />
    public virtual ValueList<Ghost> On(StructureRef structure) =>
        throw Stub.NotSetUp("IGhostBoard", "On", "StubGhostBoard");

    /// <inheritdoc />
    public virtual IGhostBoard As(Actor viewer) =>
        throw Stub.NotSetUp("IGhostBoard", "As", "StubGhostBoard");
}

/// <summary>
/// A base for a test fake of <see cref="IWorldQuery"/>: inherit it and override what your test uses. Every member
/// is virtual and throws <see cref="NotSupportedException"/> naming itself until overridden. A minor that adds a
/// member to <see cref="IWorldQuery"/> adds it here in the same release, so a fake that inherits this keeps
/// compiling and loading.
/// </summary>
public class StubWorldQuery : IWorldQuery
{
    /// <inheritdoc />
    public virtual Option<StructureInfo> Structure(ReferenceId id) =>
        throw Stub.NotSetUp("IWorldQuery", "Structure", "StubWorldQuery");

    /// <inheritdoc />
    public virtual ValueList<StructureInfo> At(Cell cell) =>
        throw Stub.NotSetUp("IWorldQuery", "At", "StubWorldQuery");

    /// <inheritdoc />
    public virtual Result<ValueList<StructureRef>, Reason> Select(StructureSelector selector) =>
        throw Stub.NotSetUp("IWorldQuery", "Select", "StubWorldQuery");

    /// <inheritdoc />
    public virtual Option<NetworkRef> NetworkOf(ReferenceId id) =>
        throw Stub.NotSetUp("IWorldQuery", "NetworkOf", "StubWorldQuery");

    /// <inheritdoc />
    public virtual Option<RoomRef> RoomAt(Cell cell) =>
        throw Stub.NotSetUp("IWorldQuery", "RoomAt", "StubWorldQuery");

    /// <inheritdoc />
    public virtual Option<GhostId> AssumedFrom(StructureRef structure) =>
        throw Stub.NotSetUp("IWorldQuery", "AssumedFrom", "StubWorldQuery");
}
