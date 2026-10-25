using System;
using System.Collections.Generic;
using Lattice.Api;

namespace Lattice.Testing.Stubs;

/// <summary>
/// A base for a test fake of <see cref="IPlans"/>: inherit it and override what your test uses. Every member is
/// virtual and throws <see cref="NotSupportedException"/> naming itself until overridden. A minor that adds a
/// member to <see cref="IPlans"/> adds it here in the same release, so a fake that inherits this keeps compiling
/// and loading.
/// </summary>
public class StubPlans : IPlans
{
    /// <inheritdoc />
    public virtual ValueList<Plan> All => throw Stub.NotSetUp("IPlans", "All", "StubPlans");

    /// <inheritdoc />
    public virtual PlanId Default => throw Stub.NotSetUp("IPlans", "Default", "StubPlans");

    /// <inheritdoc />
    public virtual ValueList<FinishedPlan> Finished => throw Stub.NotSetUp("IPlans", "Finished", "StubPlans");

    /// <inheritdoc />
    public virtual Option<LoadReport> LastLoad => throw Stub.NotSetUp("IPlans", "LastLoad", "StubPlans");

    /// <inheritdoc />
    public virtual Option<Plan> Find(PlanId plan) =>
        throw Stub.NotSetUp("IPlans", "Find", "StubPlans");

    /// <inheritdoc />
    public virtual Result<Answer<PlanFacts>, Reason> Facts(PlanId plan) =>
        throw Stub.NotSetUp("IPlans", "Facts", "StubPlans");

    /// <inheritdoc />
    public virtual Result<Answer<PlanState>, Reason> State(PlanId plan) =>
        throw Stub.NotSetUp("IPlans", "State", "StubPlans");

    /// <inheritdoc />
    public virtual Result<int, Reason> AheadOfSave(PlanId plan) =>
        throw Stub.NotSetUp("IPlans", "AheadOfSave", "StubPlans");

    /// <inheritdoc />
    public virtual Result<Template, Reason> AsTemplate(PlanId plan, TemplateName name) =>
        throw Stub.NotSetUp("IPlans", "AsTemplate", "StubPlans");

    /// <inheritdoc />
    public virtual string WriteTemplate(Template template) =>
        throw Stub.NotSetUp("IPlans", "WriteTemplate", "StubPlans");

    /// <inheritdoc />
    public virtual Result<Template, Reason> ReadTemplate(string text) =>
        throw Stub.NotSetUp("IPlans", "ReadTemplate", "StubPlans");
}

/// <summary>
/// A base for a test fake of <see cref="ISwaps"/>: inherit it and override what your test uses. Every member is
/// virtual and throws <see cref="NotSupportedException"/> naming itself until overridden. A minor that adds a
/// member to <see cref="ISwaps"/> adds it here in the same release, so a fake that inherits this keeps compiling
/// and loading.
/// </summary>
public class StubSwaps : ISwaps
{
    /// <inheritdoc />
    public virtual Result<ValueList<SwapOption>, Reason> OptionsFor(StructureRef structure) =>
        throw Stub.NotSetUp("ISwaps", "OptionsFor", "StubSwaps");
}
