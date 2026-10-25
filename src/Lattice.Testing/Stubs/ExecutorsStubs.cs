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

    /// <summary>Does nothing until overridden.</summary>
    public virtual void Dispose()
    {
    }
}
