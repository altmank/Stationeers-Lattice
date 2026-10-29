using Lattice.Api;

namespace Lattice.Core.Catalogue;

/// <summary>
/// The reasons the catalogue gives: a refusal is <see cref="ReasonCodes.UnknownPiece"/> with what it is about, except
/// the catalogue view's option past a kit's last (<see cref="ReasonCodes.InvalidValue"/>) and piece no kit places
/// (<see cref="ReasonCodes.NotFound"/>).
/// </summary>
internal static class CatalogueReasons
{
    public static readonly RuleId Catalogue = RuleId.Known("lattice.catalogue");

    public static Reason Piece(string piece, string problem) =>
        Reason.Of(ReasonCodes.UnknownPiece, Catalogue, ValueMap<string, string>.Empty.With("piece", piece).With("problem", problem));

    public static Reason Kit(int hash) =>
        Reason.Of(ReasonCodes.UnknownPiece, Catalogue, ValueMap<string, string>.Empty.With("kit", hash.ToString()).With("problem", "no-kit-prefab"));

    public static Reason Option(int kitHash, int option) =>
        Reason.Of(
            ReasonCodes.UnknownPiece,
            Catalogue,
            ValueMap<string, string>.Empty.With("kit", kitHash.ToString()).With("option", option.ToString()).With("problem", "no-such-option"));

    public static Reason PastLastOption(int kitHash, int option, int count) =>
        Reason.Of(
            ReasonCodes.InvalidValue,
            Catalogue,
            ValueMap<string, string>.Empty.With("kit", kitHash.ToString()).With("option", option.ToString())
                .With("options", count.ToString()).With("problem", "past-last-option"));

    public static Reason Item(int hash) =>
        Reason.Of(ReasonCodes.NotFound, Catalogue, ValueMap<string, string>.Empty.With("item", hash.ToString()).With("problem", "no-item-prefab"));

    public static Reason NoKit(string piece) =>
        Reason.Of(ReasonCodes.NotFound, Catalogue, ValueMap<string, string>.Empty.With("piece", piece).With("problem", "no-kit"));
}
