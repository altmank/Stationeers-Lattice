using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Ports;
using Lattice.Core.Transforms;
using static Lattice.Tests.Board.TestBoard;
using static Lattice.Tests.Transforms.TransformBench;

namespace Lattice.Tests.Transforms;

/// <summary>
/// Mirroring a piece a mod registered a mirror partner for (architecture 6.8, "Catalogue additions"): the image is the
/// partner piece, laid so its ends, roles included, are the original's reflected.
/// </summary>
public sealed class MirrorPartnerTests
{
    private static readonly Cell Origin = new(5, 0, 0);

    public static TheoryData<Axis, int> Planes() => new()
    {
        { Axis.X, 0 }, { Axis.X, 7 }, { Axis.Y, 0 }, { Axis.Y, 13 }, { Axis.Z, 0 }, { Axis.Z, 22 },
    };

    [Theory]
    [MemberData(nameof(Planes))]
    public void A_piece_with_a_partner_mirrors_into_the_partner_with_every_end_and_role_reflected(Axis normal, int turnIndex)
    {
        var b = new TransformBench();
        using var _ = b.Partners("ModHandLeft", "ModHandRight");
        var plane = new MirrorPlane(normal, 20);
        var original = new Placement(b.T.Catalogue.Named("ModHandLeft").Key, Origin, TurnAt(turnIndex));

        var image = Must(b.Placements.Apply(new Transformation.Mirror(plane), original));

        Assert.Equal(b.T.Catalogue.Named("ModHandRight").Key, image.Piece);
        Assert.Equal(b.T.Grid.Reflect(Origin, plane), image.At);
        Assert.Equal(Reflected(b, original, plane), EndsByRole(b, image));
    }

    [Fact]
    public void Mirroring_the_partner_back_gives_the_original_placement()
    {
        var b = new TransformBench();
        using var _ = b.Partners("ModHandLeft", "ModHandRight");
        var mirror = new Transformation.Mirror(new MirrorPlane(Axis.Z, 20));
        var original = new Placement(b.T.Catalogue.Named("ModHandLeft").Key, Origin, TurnAt(9));

        var back = Must(b.Placements.Apply(mirror, Must(b.Placements.Apply(mirror, original))));

        Assert.Equal(original, back);
    }

    [Fact]
    public void Without_a_partner_or_once_the_registration_ends_the_piece_has_no_mirror_image()
    {
        var b = new TransformBench();
        var mirror = new Transformation.Mirror(new MirrorPlane(Axis.X, 20));
        var left = new Placement(b.T.Catalogue.Named("ModHandLeft").Key, Origin, Turn.None);

        var before = b.Placements.Apply(mirror, left);
        var registration = b.Partners("ModHandLeft", "ModHandRight");
        var during = b.Placements.Apply(mirror, left);
        registration.Dispose();
        var after = b.Placements.Apply(mirror, left);

        Assert.True(before.TryGetError(out var first) && first == TransformProblem.NoMirrorImage);
        Assert.True(during.IsOk);
        Assert.True(after.TryGetError(out var last) && last == TransformProblem.NoMirrorImage);
    }

    [Fact]
    public void A_mirrored_copy_on_the_board_places_the_partner_piece()
    {
        var b = new TransformBench();
        using var _ = b.Partners("ModHandLeft", "ModHandRight");
        b.Build("ModHandLeft", Origin);

        var copy = Applied(b.Transform(All, new Transformation.Mirror(new MirrorPlane(Axis.X, 20)), Copying));

        var image = Assert.Single(copy.Added);
        Assert.Equal(b.T.Catalogue.Named("ModHandRight").Key, ((Intent.Build)image.Intent).Target.Piece);
    }

    private static Turn TurnAt(int index) => Lattice.TestKit.TestKitIds.Must(Turn.Create(index));

    private static Placement Must(Result<Placement, TransformProblem> result) =>
        result.TryGet(out var placement) ? placement : throw new Xunit.Sdk.XunitException($"refused: {(result.TryGetError(out var why) ? why : default)}");

    private static Dictionary<ConnectionRole, (Cell, Direction)> EndsByRole(TransformBench b, Placement placement)
    {
        var shape = Lattice.TestKit.TestKitIds.Must(b.Shapes.Of(placement));
        var facts = Lattice.TestKit.TestKitIds.Must(b.T.Catalogue.Piece(placement.Piece));
        var byRole = new Dictionary<ConnectionRole, (Cell, Direction)>();
        for (var i = 0; i < shape.Ends.Count; i++)
            byRole[facts.Ends[i].Role.TryGet(out var role) ? role : default] = (shape.Ends[i].Cell, shape.Ends[i].Facing);
        return byRole;
    }

    private static Dictionary<ConnectionRole, (Cell, Direction)> Reflected(TransformBench b, Placement placement, MirrorPlane plane)
    {
        var reflected = new Dictionary<ConnectionRole, (Cell, Direction)>();
        foreach (var (role, (cell, facing)) in EndsByRole(b, placement))
            reflected[role] = (b.T.Grid.Reflect(cell, plane), b.T.Grid.Reflect(facing, plane.Normal));
        return reflected;
    }
}
