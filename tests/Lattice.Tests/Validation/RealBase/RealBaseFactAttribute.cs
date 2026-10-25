using System;
using Lattice.TestKit.Fixtures;

namespace Lattice.Tests.Validation.RealBase;

/// <summary>
/// A measurement over a real save: minutes long, so it runs only when asked (<c>LATTICE_REALSAVE=1</c>) and when the
/// save fixture is there. Its numbers go to the 51 report.
/// </summary>
public sealed class RealBaseFactAttribute : FactAttribute
{
    public const string Switch = "LATTICE_REALSAVE";

    public RealBaseFactAttribute(string save)
    {
        if (Environment.GetEnvironmentVariable(Switch) != "1")
            Skip = $"A real-save measurement: set {Switch}=1 to run it.";
        else if (Fixtures.Save(save).IsNone)
            Skip = $"{save} is not in tests/fixtures/generated: run tests/fixtures/make-fixtures.ps1.";
    }
}
