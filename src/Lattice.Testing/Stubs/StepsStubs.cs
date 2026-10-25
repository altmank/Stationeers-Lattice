using System;
using System.Collections.Generic;
using Lattice.Api;

namespace Lattice.Testing.Stubs;

/// <summary>
/// A base for a test fake of <see cref="ISteps"/>: inherit it and override what your test uses. Every member is
/// virtual and throws <see cref="NotSupportedException"/> naming itself until overridden. A minor that adds a
/// member to <see cref="ISteps"/> adds it here in the same release, so a fake that inherits this keeps compiling
/// and loading.
/// </summary>
public class StubSteps : ISteps
{
    /// <inheritdoc />
    public virtual Result<StepPlan, Reason> For(GhostId ghost) =>
        throw Stub.NotSetUp("ISteps", "For", "StubSteps");
}

/// <summary>
/// A base for a test fake of <see cref="IBills"/>: inherit it and override what your test uses. Every member is
/// virtual and throws <see cref="NotSupportedException"/> naming itself until overridden. A minor that adds a
/// member to <see cref="IBills"/> adds it here in the same release, so a fake that inherits this keeps compiling
/// and loading.
/// </summary>
public class StubBills : IBills
{
    /// <inheritdoc />
    public virtual Result<Bill, Reason> For(Selector which) =>
        throw Stub.NotSetUp("IBills", "For", "StubBills");
}
