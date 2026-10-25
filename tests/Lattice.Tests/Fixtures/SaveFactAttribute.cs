using Lattice.TestKit.Fixtures;

namespace Lattice.Tests;

/// <summary>A fact that needs a generated save; skipped, saying how to make it, when the save is not there.</summary>
public sealed class SaveFactAttribute : FactAttribute
{
    public SaveFactAttribute(string save)
    {
        if (Fixtures.Save(save).IsNone)
            Skip = $"{save} is not in tests/fixtures/generated: run tests/fixtures/make-fixtures.ps1.";
    }
}
