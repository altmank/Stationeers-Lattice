using System;
using System.Collections.Generic;
using Lattice.Api;

namespace Lattice.Testing.Stubs;

/// <summary>
/// A base for a test fake of <see cref="IExecutors"/>: inherit it and override what your test uses. Every member
/// is virtual and throws <see cref="NotSupportedException"/> naming itself until overridden. A minor that adds a
/// member to <see cref="IExecutors"/> adds it here in the same release, so a fake that inherits this keeps
/// compiling and loading.
/// </summary>
public class StubExecutors : IExecutors
{
    /// <inheritdoc />
    public virtual Result<IExecutorHandle, Reason> Register(ExecutorInfo info) =>
        throw Stub.NotSetUp("IExecutors", "Register", "StubExecutors");

    /// <inheritdoc />
    public virtual Option<Claim> ClaimOn(GhostId ghost) =>
        throw Stub.NotSetUp("IExecutors", "ClaimOn", "StubExecutors");

    /// <inheritdoc />
    public virtual ValueList<ExecutorReport> StatusOf(GhostId ghost) =>
        throw Stub.NotSetUp("IExecutors", "StatusOf", "StubExecutors");

    /// <inheritdoc />
    public virtual ValueList<Builder> All =>
        throw Stub.NotSetUp("IExecutors", "All", "StubExecutors");

    /// <inheritdoc />
    public virtual Option<Builder> Find(ExecutorId id) =>
        throw Stub.NotSetUp("IExecutors", "Find", "StubExecutors");

    /// <inheritdoc />
    public virtual ValueList<Assignment> AssignedTo(PlanId plan) =>
        throw Stub.NotSetUp("IExecutors", "AssignedTo", "StubExecutors");

    /// <inheritdoc />
    public virtual ValueList<PlanId> PlansOf(ExecutorId id) =>
        throw Stub.NotSetUp("IExecutors", "PlansOf", "StubExecutors");

    /// <inheritdoc />
    public virtual BuilderProgress ProgressOf(PlanId plan, ExecutorId id) =>
        throw Stub.NotSetUp("IExecutors", "ProgressOf", "StubExecutors");

    /// <inheritdoc />
    public virtual BuilderProgress ProgressOf(PlanId plan) =>
        throw Stub.NotSetUp("IExecutors", "ProgressOf", "StubExecutors");

    /// <inheritdoc />
    public virtual ValueList<ItemQuantity> SupplyToward(PlanId plan) =>
        throw Stub.NotSetUp("IExecutors", "SupplyToward", "StubExecutors");

    /// <inheritdoc />
    public virtual int UnclaimedIn(PlanId plan) =>
        throw Stub.NotSetUp("IExecutors", "UnclaimedIn", "StubExecutors");
}

/// <summary>
/// A base for a test fake of <see cref="IExecutorHandle"/>: inherit it and override what your test uses. Every
/// member is virtual and throws <see cref="NotSupportedException"/> naming itself until overridden;
/// <see cref="IDisposable.Dispose"/> does nothing. A minor that adds a member to
/// <see cref="IExecutorHandle"/> adds it here in the same release, so a fake that inherits this keeps compiling
/// and loading.
/// </summary>
public class StubExecutorHandle : IExecutorHandle
{
    /// <inheritdoc />
    public virtual Result<Claim, ClaimRefused> Claim(GhostId ghost, Lease lease) =>
        throw Stub.NotSetUp("IExecutorHandle", "Claim", "StubExecutorHandle");

    /// <inheritdoc />
    public virtual Result<Claim, ClaimRefused> Renew(Claim claim) =>
        throw Stub.NotSetUp("IExecutorHandle", "Renew", "StubExecutorHandle");

    /// <inheritdoc />
    public virtual void Release(Claim claim) =>
        throw Stub.NotSetUp("IExecutorHandle", "Release", "StubExecutorHandle");

    /// <inheritdoc />
    public virtual Result<Unit, Reason> Report(GhostId ghost, ExecutorStatus status) =>
        throw Stub.NotSetUp("IExecutorHandle", "Report", "StubExecutorHandle");

    /// <inheritdoc />
    public virtual void Locate(Option<BuilderArea> area) =>
        throw Stub.NotSetUp("IExecutorHandle", "Locate", "StubExecutorHandle");

    /// <inheritdoc />
    public virtual void Say(BuilderWord word) =>
        throw Stub.NotSetUp("IExecutorHandle", "Say", "StubExecutorHandle");

    /// <inheritdoc />
    public virtual Result<Unit, Reason> Offer(SupplyReport supply) =>
        throw Stub.NotSetUp("IExecutorHandle", "Offer", "StubExecutorHandle");

    /// <inheritdoc />
    public virtual Result<Unit, Reason> Answer(PlanId plan, AssignmentAnswer answer) =>
        throw Stub.NotSetUp("IExecutorHandle", "Answer", "StubExecutorHandle");

    /// <summary>Does nothing until overridden.</summary>
    public virtual void Dispose()
    {
    }
}
