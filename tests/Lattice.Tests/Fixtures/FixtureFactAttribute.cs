using System;
using System.Collections.Generic;
using System.Linq;
using Lattice.TestKit.Fixtures;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Lattice.Tests;

/// <summary>
/// A fact that needs generated fixtures. Without <c>LATTICE_REALSAVE=1</c> a missing fixture skips it (a fresh
/// checkout has no saves). With the switch set, as the release gate always runs, a missing fixture fails it, naming
/// the file, so a gate with nothing to measure is never green.
/// </summary>
[XunitTestCaseDiscoverer("Lattice.Tests.FixtureFactDiscoverer", "Lattice.Tests")]
public abstract class FixtureFactAttribute : FactAttribute
{
    public const string Switch = "LATTICE_REALSAVE";

    private protected FixtureFactAttribute(string? missing, bool onlyWithSwitch = false)
    {
        if (!Required)
            Skip = onlyWithSwitch ? $"A real-save measurement: set {Switch}=1 to run it." : missing;
        else if (missing is not null)
            Missing = $"{Switch}=1 needs this test's fixtures: {missing}";
    }

    public static bool Required => Environment.GetEnvironmentVariable(Switch) == "1";

    /// <summary>Why the test fails before it runs: a fixture it needs is missing while the switch is set.</summary>
    public string? Missing { get; }

    private protected static string? MissingSave(IEnumerable<string> saves) =>
        saves.Where(static s => Fixtures.Save(s).IsNone)
            .Select(static s => $"{s} is not in tests/fixtures/generated: run tests/fixtures/make-fixtures.ps1.")
            .FirstOrDefault();
}

/// <summary>Discovers a <see cref="FixtureFactAttribute"/> fact as a failing case when its fixtures are missing.</summary>
public sealed class FixtureFactDiscoverer(IMessageSink diagnosticMessageSink) : FactDiscoverer(diagnosticMessageSink)
{
    public override IEnumerable<IXunitTestCase> Discover(ITestFrameworkDiscoveryOptions discoveryOptions, ITestMethod testMethod, IAttributeInfo factAttribute) =>
        factAttribute.GetNamedArgument<string?>(nameof(FixtureFactAttribute.Missing)) is { } missing
            ? [new ExecutionErrorTestCase(DiagnosticMessageSink, discoveryOptions.MethodDisplayOrDefault(), discoveryOptions.MethodDisplayOptionsOrDefault(), testMethod, missing)]
            : base.Discover(discoveryOptions, testMethod, factAttribute);
}
