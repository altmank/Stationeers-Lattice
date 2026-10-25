using System;
using System.Collections.Generic;
using Lattice.Api;

namespace Lattice.Testing.Stubs;

/// <summary>
/// A base for a test fake of <see cref="IRegistries"/>: inherit it and override what your test uses. Every member
/// is virtual and throws <see cref="NotSupportedException"/> naming itself until overridden. A minor that adds a
/// member to <see cref="IRegistries"/> adds it here in the same release, so a fake that inherits this keeps
/// compiling and loading.
/// </summary>
public class StubRegistries : IRegistries
{
    /// <inheritdoc />
    public virtual Result<IDisposable, Reason> Register(RuleDescriptor rule) =>
        throw Stub.NotSetUp("IRegistries", "Register", "StubRegistries");

    /// <inheritdoc />
    public virtual Result<IDisposable, Reason> Register(SupportRuleDescriptor rule) =>
        throw Stub.NotSetUp("IRegistries", "Register", "StubRegistries");

    /// <inheritdoc />
    public virtual Result<IDisposable, Reason> Register(AnalyzerDescriptor analyzer) =>
        throw Stub.NotSetUp("IRegistries", "Register", "StubRegistries");

    /// <inheritdoc />
    public virtual Result<IDisposable, Reason> Register(AttachmentDescriptor attachment) =>
        throw Stub.NotSetUp("IRegistries", "Register", "StubRegistries");

    /// <inheritdoc />
    public virtual Result<IDisposable, Reason> Register(CatalogueAddition addition) =>
        throw Stub.NotSetUp("IRegistries", "Register", "StubRegistries");

    /// <inheritdoc />
    public virtual Result<IDisposable, Reason> Register<TCommand>(CommandExpander<TCommand> expander)
        where TCommand : Command =>
        throw Stub.NotSetUp("IRegistries", "Register", "StubRegistries");

    /// <inheritdoc />
    public virtual Result<IPolicyHandle, Reason> Install(AccessPolicy policy) =>
        throw Stub.NotSetUp("IRegistries", "Install", "StubRegistries");

    /// <inheritdoc />
    public virtual Result<IDisposable, Reason> LimitPlanSize(int maxGhosts) =>
        throw Stub.NotSetUp("IRegistries", "LimitPlanSize", "StubRegistries");
}

/// <summary>
/// A base for a test fake of <see cref="IPolicyHandle"/>: inherit it and override what your test uses. Every
/// member is virtual and throws <see cref="NotSupportedException"/> naming itself until overridden;
/// <see cref="IDisposable.Dispose"/> does nothing. A minor that adds a member to
/// <see cref="IPolicyHandle"/> adds it here in the same release, so a fake that inherits this keeps compiling and
/// loading.
/// </summary>
public class StubPolicyHandle : IPolicyHandle
{
    /// <inheritdoc />
    public virtual void Changed() =>
        throw Stub.NotSetUp("IPolicyHandle", "Changed", "StubPolicyHandle");

    /// <summary>Does nothing until overridden.</summary>
    public virtual void Dispose()
    {
    }
}
