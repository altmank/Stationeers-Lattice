using Lattice.Api;
using Lattice.Core.Ports;

namespace Lattice.Tests.Contracts;

/// <summary>
/// What every <see cref="IPlacementCheck"/> promises, fake or the game's cursor: it accepts what the game accepts,
/// refuses with a reason that has a code, and asking twice gives the same answer. Derive it and give the subject plus a
/// placement it accepts and one it refuses.
/// </summary>
public abstract class PlacementCheckContract
{
    internal abstract IPlacementCheck Subject { get; }

    internal abstract Placement Accepted { get; }

    internal abstract Placement Refused { get; }

    [Fact]
    public void Accepts_an_accepted_placement() => Assert.True(Subject.Check(Accepted).IsOk);

    [Fact]
    public void Refuses_with_a_coded_reason()
    {
        Assert.True(Subject.Check(Refused).TryGetError(out var why));
        Assert.NotEqual(default, why.Code);
    }

    [Fact]
    public void Asking_twice_gives_the_same_answer()
    {
        Assert.Equal(Subject.Check(Accepted), Subject.Check(Accepted));
        Assert.Equal(Subject.Check(Refused), Subject.Check(Refused));
    }
}
