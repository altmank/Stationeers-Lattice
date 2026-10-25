using System.IO;
using Lattice.Api;
using Lattice.Core.Catalogue;
using Lattice.TestKit;
using Lattice.TestKit.Catalogue;

namespace Lattice.Testing;

/// <summary>
/// The pieces a test world knows: the test kit's hand catalogue, or a catalogue the game wrote on the consumer's own
/// machine (<c>-lattice-write-catalogue &lt;file&gt;</c>). Game data never ships with Lattice.
/// </summary>
public sealed class TestCatalogue
{
    private TestCatalogue(FakeCatalogue pieces, string source)
    {
        Pieces = pieces;
        Source = source;
        View = new PieceCatalogue(pieces.Listing, new CatalogueAdditions(pieces), PiecePorts.Over(pieces), Lattice.Core.Styles.Facings.Over(pieces));
    }

    /// <summary>
    /// The catalogue as Lattice's <see cref="IGhostSystem.Catalogue"/> reads it, families by type (no mod has
    /// registered one here). A world's own view adds its scenario's pieces.
    /// </summary>
    public IPieceCatalogue View { get; }

    /// <summary>
    /// The hand catalogue: about twenty base-game pieces (frames, walls, cables, pipes, chutes, a few devices) with
    /// their build steps, ends and footprints, written by hand from the game's facts.
    /// </summary>
    public static TestCatalogue Hand { get; } = new(FakeCatalogue.Hand(), "hand");

    /// <summary>Where the pieces came from: <c>hand</c> or the file's path.</summary>
    public string Source { get; }

    /// <summary>How many pieces it holds.</summary>
    public int Count => Pieces.Pieces.Count;

    internal FakeCatalogue Pieces { get; }

    /// <summary>
    /// A catalogue data file the game wrote. Refused with <see cref="ReasonCodes.NotFound"/> when there is no file and
    /// <see cref="ReasonCodes.InvalidValue"/> (with the file, line and problem) when it does not read, a file of an older
    /// format included (write it again with the game), or when a kit's options leave a gap.
    /// </summary>
    public static Result<TestCatalogue, Reason> FromFile(string path)
    {
        if (!File.Exists(path))
            return Result<TestCatalogue, Reason>.Error(TestCodes.Refuse(ReasonCodes.NotFound, ValueMap.Of("file", path)));
        using var reader = new StreamReader(path);
        var read = CatalogueFile.Read(reader, path);
        if (read.TryGet(out var pieces))
            return pieces.Gap.TryGet(out var gap)
                ? Result<TestCatalogue, Reason>.Error(Unreadable(new FileError(path, 0, gap)))
                : Result<TestCatalogue, Reason>.Ok(new TestCatalogue(pieces, path));
        read.TryGetError(out var error);
        return Result<TestCatalogue, Reason>.Error(Unreadable(error));
    }

    internal static Reason Unreadable(FileError error) => TestCodes.Refuse(ReasonCodes.InvalidValue, ValueMap
        .Of("problem", "catalogue-file")
        .With("file", error.File)
        .With("line", error.Line.ToString(System.Globalization.CultureInfo.InvariantCulture))
        .With("detail", error.Problem));

    /// <summary>The key of a piece by its prefab name; none when this catalogue does not hold it.</summary>
    public Option<PrefabKey> Key(string prefabName)
    {
        var key = PrefabHash.Piece(prefabName);
        return Pieces.Piece(key).IsOk ? Option.Some(key) : Option<PrefabKey>.None;
    }

    /// <summary>The key of a kit by its item name; none when no piece here is placed with it.</summary>
    public Option<KitKey> Kit(string kitName)
    {
        var key = PrefabHash.Kit(kitName);
        return Pieces.Kit(key).IsOk ? Option.Some(key) : Option<KitKey>.None;
    }
}
