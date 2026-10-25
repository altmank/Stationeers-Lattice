using System;
using System.Collections.Generic;
using Lattice.Api;

namespace Lattice.Testing.Stubs;

/// <summary>
/// A base for a test fake of <see cref="IInspector"/>: inherit it and override what your test uses. Every member
/// is virtual and throws <see cref="NotSupportedException"/> naming itself until overridden. A minor that adds a
/// member to <see cref="IInspector"/> adds it here in the same release, so a fake that inherits this keeps
/// compiling and loading.
/// </summary>
public class StubInspector : IInspector
{
    /// <inheritdoc />
    public virtual Result<GhostStory, Reason> Story(GhostId ghost) =>
        throw Stub.NotSetUp("IInspector", "Story", "StubInspector");

    /// <inheritdoc />
    public virtual Result<PlanStory, Reason> Story(PlanId plan) =>
        throw Stub.NotSetUp("IInspector", "Story", "StubInspector");

    /// <inheritdoc />
    public virtual Result<ValueList<string>, Reason> Export(PlanId plan) =>
        throw Stub.NotSetUp("IInspector", "Export", "StubInspector");
}
