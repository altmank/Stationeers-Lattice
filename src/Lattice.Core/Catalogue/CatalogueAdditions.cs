using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Ports;

namespace Lattice.Core.Catalogue;

/// <summary>The mirror partners mods registered: the piece another piece's mirror image is.</summary>
internal interface IMirrorPartners
{
    /// <summary>The piece's registered mirror partner; none when no mod named one. Main thread; constant cost.</summary>
    Option<PrefabKey> PartnerOf(PrefabKey piece);
}

/// <summary>
/// The family each piece is drawn and filtered by: the one a mod registered (<see cref="CatalogueAddition.InFamily"/>),
/// else the one the catalogue found by type, else <see cref="PieceFamilies.Other"/> for a piece the catalogue lacks.
/// </summary>
internal interface IPieceFamilies
{
    /// <summary>The piece's family. Main thread; constant cost.</summary>
    PieceFamily FamilyOf(PrefabKey piece);
}

/// <summary>
/// The catalogue facts mods add for pieces the bridge cannot read them from (architecture 6.8, "Catalogue additions";
/// <see cref="IRegistries.Register(CatalogueAddition)"/>), one registry per world. A mirror partner is held both ways.
/// Refused: <see cref="ReasonCodes.UnknownPiece"/> for a piece the catalogue lacks; <see cref="ReasonCodes.InvalidValue"/>
/// for a piece named as its own mirror (the catalogue finds own mirror images from ends) or a pair on different grids;
/// <see cref="ReasonCodes.AlreadyRegistered"/> while either piece has a partner; <see cref="ReasonCodes.UnknownKind"/>
/// for an addition this version does not know. A family is held per piece: refused with
/// <see cref="ReasonCodes.UnknownPiece"/> for a piece the catalogue lacks, <see cref="ReasonCodes.InvalidValue"/> for a
/// <c>default</c> family, <see cref="ReasonCodes.AlreadyRegistered"/> while the piece has one. Main thread; registering
/// costs at most two catalogue reads, disposing is constant, and a disposed handle does nothing more.
/// </summary>
internal sealed class CatalogueAdditions(ICatalogue catalogue) : IMirrorPartners, IPieceFamilies
{
    private readonly Dictionary<PrefabKey, Partnership> _partners = new();
    private readonly Dictionary<PrefabKey, Membership> _families = new();

    public Result<IDisposable, Reason> Register(CatalogueAddition addition) => addition switch
    {
        CatalogueAddition.MirrorPartner pair => Partner(pair),
        CatalogueAddition.InFamily member => Join(member),
        _ => Refuse(ReasonCodes.UnknownKind, ValueMap.Of("kind", addition.GetType().Name)),
    };

    public Option<PrefabKey> PartnerOf(PrefabKey piece) =>
        _partners.TryGetValue(piece, out var partnership)
            ? Option.Some(partnership.Pair.Piece == piece ? partnership.Pair.Mirror : partnership.Pair.Piece)
            : Option<PrefabKey>.None;

    public PieceFamily FamilyOf(PrefabKey piece)
    {
        if (_families.TryGetValue(piece, out var membership))
            return membership.Member.Family;
        return catalogue.Piece(piece).TryGet(out var facts) ? facts.Family : PieceFamilies.Other;
    }

    private Result<IDisposable, Reason> Join(CatalogueAddition.InFamily member)
    {
        if (!catalogue.Piece(member.Piece).IsOk)
            return Refuse(ReasonCodes.UnknownPiece, Fact(member.Piece));
        if (member.Family == default)
            return Refuse(ReasonCodes.InvalidValue, Fact(member.Piece).With("problem", "no-family"));
        if (_families.TryGetValue(member.Piece, out var held))
            return Refuse(ReasonCodes.AlreadyRegistered, Fact(member.Piece).With("family", held.Member.Family.Value));

        var membership = new Membership(this, member);
        _families.Add(member.Piece, membership);
        return Result<IDisposable, Reason>.Ok(membership);
    }

    private void Leave(Membership membership)
    {
        if (_families.TryGetValue(membership.Member.Piece, out var current) && ReferenceEquals(current, membership))
            _families.Remove(membership.Member.Piece);
    }

    private Result<IDisposable, Reason> Partner(CatalogueAddition.MirrorPartner pair)
    {
        if (!catalogue.Piece(pair.Piece).TryGet(out var piece))
            return Refuse(ReasonCodes.UnknownPiece, Fact(pair.Piece));
        if (!catalogue.Piece(pair.Mirror).TryGet(out var mirror))
            return Refuse(ReasonCodes.UnknownPiece, Fact(pair.Mirror));
        if (pair.Piece == pair.Mirror)
            return Refuse(ReasonCodes.InvalidValue, Fact(pair.Piece).With("problem", "own-mirror"));
        if (piece.Grid != mirror.Grid)
            return Refuse(ReasonCodes.InvalidValue, Fact(pair.Piece).With("mirror", Hash(pair.Mirror)).With("problem", "grid-size"));
        if (_partners.ContainsKey(pair.Piece))
            return Refuse(ReasonCodes.AlreadyRegistered, Fact(pair.Piece));
        if (_partners.ContainsKey(pair.Mirror))
            return Refuse(ReasonCodes.AlreadyRegistered, Fact(pair.Mirror));

        var partnership = new Partnership(this, pair);
        _partners.Add(pair.Piece, partnership);
        _partners.Add(pair.Mirror, partnership);
        return Result<IDisposable, Reason>.Ok(partnership);
    }

    private void Remove(Partnership partnership)
    {
        Drop(partnership.Pair.Piece, partnership);
        Drop(partnership.Pair.Mirror, partnership);
    }

    private void Drop(PrefabKey piece, Partnership partnership)
    {
        if (_partners.TryGetValue(piece, out var current) && ReferenceEquals(current, partnership))
            _partners.Remove(piece);
    }

    private static ValueMap<string, string> Fact(PrefabKey piece) => ValueMap.Of("piece", Hash(piece));

    private static string Hash(PrefabKey piece) => piece.Hash.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static Result<IDisposable, Reason> Refuse(ReasonCode code, ValueMap<string, string> facts) =>
        Result<IDisposable, Reason>.Error(Reason.Of(code, CatalogueReasons.Catalogue, facts));

    private sealed class Partnership(CatalogueAdditions owner, CatalogueAddition.MirrorPartner pair) : IDisposable
    {
        public CatalogueAddition.MirrorPartner Pair { get; } = pair;

        public void Dispose() => owner.Remove(this);
    }

    private sealed class Membership(CatalogueAdditions owner, CatalogueAddition.InFamily member) : IDisposable
    {
        public CatalogueAddition.InFamily Member { get; } = member;

        public void Dispose() => owner.Leave(this);
    }
}
