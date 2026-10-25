using System;

namespace Lattice.Api;

/// <summary>
/// Where other mods extend Lattice. Every registration belongs to the current world: it returns an
/// <see cref="IDisposable"/> that ends it early, and it ends with the world. Register when
/// <see cref="LatticeSystem.Started"/> is raised. Status providers register through <see cref="IGhostSystem.Executors"/>.
/// Main thread. Lattice implements this; consumers call it. A minor may add members, so consumer fakes of it are not
/// covered by compatibility.
/// </summary>
public interface IRegistries
{
    /// <summary>Adds a validation rule; refused for an id already registered.</summary>
    Result<IDisposable, Reason> Register(RuleDescriptor rule);

    /// <summary>Adds a support rule; refused for an id already registered.</summary>
    Result<IDisposable, Reason> Register(SupportRuleDescriptor rule);

    /// <summary>Adds an analyzer; refused for an id already registered.</summary>
    Result<IDisposable, Reason> Register(AnalyzerDescriptor analyzer);

    /// <summary>Declares attached data under a key; refused for a key already declared.</summary>
    Result<IDisposable, Reason> Register(AttachmentDescriptor attachment);

    /// <summary>Adds a catalogue fact for a modded piece; refused when the piece already has that fact.</summary>
    Result<IDisposable, Reason> Register(CatalogueAddition addition);

    /// <summary>
    /// Adds the expander for a command kind; refused when that kind already has one. Lattice's own modules register
    /// theirs before <see cref="LatticeSystem.Started"/>, so a mod can fill only a kind whose module is off.
    /// </summary>
    Result<IDisposable, Reason> Register<TCommand>(CommandExpander<TCommand> expander) where TCommand : Command;

    /// <summary>
    /// Installs the access policy; refused while another is installed. Disposing the handle restores allow-everything;
    /// <see cref="IPolicyHandle.Changed"/> says the policy's answers changed without a board change.
    /// </summary>
    Result<IPolicyHandle, Reason> Install(AccessPolicy policy);

    /// <summary>Sets a limit on ghosts per plan that Lattice refuses past (none by default); refused when not positive or one is set.</summary>
    Result<IDisposable, Reason> LimitPlanSize(int maxGhosts);
}

/// <summary>
/// An installed access policy. Lattice implements this; consumers call it. A minor may add members, so consumer fakes of
/// it are not covered by compatibility.
/// </summary>
public interface IPolicyHandle : IDisposable
{
    /// <summary>
    /// Says the policy's answers changed with no board change (team membership, a server setting): Lattice drops what
    /// it cached from earlier answers, and the next reads ask the policy again. Main thread; constant cost.
    /// </summary>
    void Changed();
}
