using System;
using System.Collections.Generic;
using Lattice.Api;

namespace Lattice.Testing.Stubs;

/// <summary>
/// A base for a test fake of <see cref="ISlices"/>: inherit it and override what your test uses. Every member is
/// virtual and throws <see cref="NotSupportedException"/> naming itself until overridden. A minor that adds a
/// member to <see cref="ISlices"/> adds it here in the same release, so a fake that inherits this keeps compiling
/// and loading.
/// </summary>
public class StubSlices : ISlices
{
    /// <inheritdoc />
    public virtual Result<IDisposable, Reason> Schedule(SlicedWorkDescriptor work) =>
        throw Stub.NotSetUp("ISlices", "Schedule", "StubSlices");
}
