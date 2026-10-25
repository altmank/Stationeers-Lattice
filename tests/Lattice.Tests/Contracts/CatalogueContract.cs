using Lattice.Api;
using Lattice.Core.Ports;

namespace Lattice.Tests.Contracts;

/// <summary>
/// What every <see cref="ICatalogue"/> promises, fake or read from the game: every kit and its pieces resolve both
/// ways, steps are numbered from 0, and unknown keys are refused with <see cref="ReasonCodes.UnknownPiece"/>.
/// Derive it and give the subject.
/// </summary>
public abstract class CatalogueContract
{
    internal abstract ICatalogue Subject { get; }

    /// <summary>A piece key the subject does not hold.</summary>
    internal virtual PrefabKey AbsentPiece => default;

    [Fact]
    public void Has_kits() => Assert.NotEmpty(Subject.Kits);

    [Fact]
    public void Every_kit_resolves_to_itself()
    {
        foreach (var kit in Subject.Kits)
            Assert.Equal(kit, Ok(Subject.Kit(kit)).Key);
    }

    [Fact]
    public void Every_piece_of_a_kit_names_that_kit_and_its_option()
    {
        foreach (var kit in Subject.Kits)
            foreach (var key in Ok(Subject.Kit(kit)).Pieces)
            {
                var piece = Ok(Subject.Piece(key));
                Assert.Equal(key, piece.Key);
                var place = Assert.Single(piece.Kits, k => k.Kit == kit);
                Assert.Equal(key, Ok(Subject.PieceOf(kit, place.Option)));
            }
    }

    [Fact]
    public void Every_piece_has_steps_numbered_from_zero()
    {
        foreach (var kit in Subject.Kits)
            foreach (var key in Ok(Subject.Kit(kit)).Pieces)
            {
                var steps = Ok(Subject.Piece(key)).Steps;
                Assert.NotEmpty(steps);
                for (var i = 0; i < steps.Count; i++)
                    Assert.Equal(i, steps[i].Index.Value);
            }
    }

    [Fact]
    public void An_unknown_piece_is_refused() =>
        Assert.Equal(ReasonCodes.UnknownPiece, Error(Subject.Piece(AbsentPiece)).Code);

    [Fact]
    public void An_unknown_kit_is_refused() => Assert.True(Subject.Kit(default).IsError);

    [Fact]
    public void An_option_past_the_kit_is_refused()
    {
        foreach (var kit in Subject.Kits)
        {
            var past = Ok(KitOption.Create(Ok(Subject.Kit(kit)).Pieces.Count + 1000));
            Assert.True(Subject.PieceOf(kit, past).IsError);
        }
    }

    private static T Ok<T>(Result<T, Reason> result) where T : notnull =>
        result.TryGet(out var value) ? value : throw new Xunit.Sdk.XunitException($"Expected Ok, got {result}.");

    private static Reason Error<T>(Result<T, Reason> result) where T : notnull =>
        result.TryGetError(out var error) ? error : throw new Xunit.Sdk.XunitException($"Expected an error, got {result}.");
}
