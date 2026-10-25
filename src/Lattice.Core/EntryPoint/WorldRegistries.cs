using System;
using Lattice.Api;
using Lattice.Core.Analysis;
using Lattice.Core.Board;
using Lattice.Core.Catalogue;
using Lattice.Core.Commands;
using Lattice.Core.Slices;
using Lattice.Core.Support;
using Lattice.Core.Validation;

namespace Lattice.Core.EntryPoint;

/// <summary>
/// One world's registries: each registration goes to the module that owns it, and is refused with that module's off
/// reason when it is off. Registrations end with the world. Main thread; each call costs what its registry's does.
/// </summary>
internal sealed class WorldRegistries(
    ValidationRules? rules,
    SupportRules? supportRules,
    Analyzers? analyzers,
    AttachmentLimits attachments,
    CatalogueAdditions additions,
    CommandExpanders expanders,
    AccessPolicies policies,
    PlanSizeLimit sizeLimit,
    Reason validationOff,
    Reason analysisOff) : IRegistries
{
    public Result<IDisposable, Reason> Register(RuleDescriptor rule) =>
        rules is null ? Result<IDisposable, Reason>.Error(validationOff) : rules.Register(rule);

    public Result<IDisposable, Reason> Register(SupportRuleDescriptor rule) =>
        supportRules is null ? Result<IDisposable, Reason>.Error(validationOff) : supportRules.Register(rule);

    public Result<IDisposable, Reason> Register(AnalyzerDescriptor analyzer) =>
        analyzers is null ? Result<IDisposable, Reason>.Error(analysisOff) : analyzers.Register(analyzer);

    public Result<IDisposable, Reason> Register(AttachmentDescriptor attachment) => attachments.Register(attachment);

    public Result<IDisposable, Reason> Register(CatalogueAddition addition) => additions.Register(addition);

    public Result<IDisposable, Reason> Register<TCommand>(CommandExpander<TCommand> expander) where TCommand : Command =>
        expanders.Register(expander);

    public Result<IPolicyHandle, Reason> Install(AccessPolicy policy) => policies.Install(policy);

    public Result<IDisposable, Reason> LimitPlanSize(int maxGhosts) => sizeLimit.Set(maxGhosts);
}
