namespace Lattice.Core.Consequences;

/// <summary>Which of two pipe networks a merge after building over an old piece keeps.</summary>
internal enum NetworkUnion
{
    /// <summary>The replaced piece's network takes the new pieces' network in and keeps its id.</summary>
    IntoKept = 1,

    /// <summary>The new pieces' network takes the replaced piece's network in.</summary>
    IntoTheirs = 2,
}

/// <summary>
/// When new pieces are built over an old one (a changed run cell, a split long straight's singles) and the new pieces
/// stand in another network than the old piece, the two are merged on the host. No client is told of that merge: a
/// client puts each new piece into the network its host id names, unless it has connected neighbours there, in which
/// case it takes their network (<c>Pipe.DeserializeOnJoin</c>: <c>StructureNetwork.Merge</c> of
/// <c>ConnectedNetworks</c>). Keeping the old network is safe only when the new pieces' network holds nothing but the
/// new pieces: the client then puts them where the host did. When it also holds pieces that stood before, the client
/// leaves those where they were and puts the new pieces with them, so the host must keep that network too; otherwise
/// the client loses the old network with its last piece while the host goes on sending its state.
/// </summary>
internal static class NetworkUnionRule
{
    /// <summary>The network to keep, from whether the new pieces' network holds only new pieces.</summary>
    public static NetworkUnion For(bool theirsHoldsOnlyNewPieces) =>
        theirsHoldsOnlyNewPieces ? NetworkUnion.IntoKept : NetworkUnion.IntoTheirs;
}

/// <summary>What a network piece's own <c>OnDestroy</c> does to the networks of the pieces next to it.</summary>
internal enum NeighbourRebuild
{
    /// <summary>
    /// Only when the destroyed piece still had a network: <c>Cable.OnDestroy</c> and <c>Pipe.OnDestroy</c> keep the
    /// network they read before <c>CableNetwork</c>/<c>PipeNetwork.Remove</c> and rebuild the neighbours from it only
    /// when it was not null.
    /// </summary>
    WhenItHadANetwork = 1,

    /// <summary>
    /// Always: <c>Chute.OnDestroy</c> calls <c>ChuteNetwork.RebuildNetworkServer</c> on every connected chute whether
    /// or not it had a network itself, and runs <c>base.OnDestroy</c> (<c>Thing.OnDestroy</c>,
    /// <c>GridController.Deregister</c>) only after that loop.
    /// </summary>
    Always = 2,
}

/// <summary>
/// Whether a piece a removal takes away leaves its network before it is destroyed. Leaving first keeps the network's
/// id and, for pipes, every mole in what is left; it is only safe where the piece's <c>OnDestroy</c> then leaves its
/// neighbours alone. Where <c>OnDestroy</c> rebuilds every neighbour's network unconditionally (chutes), two
/// neighbouring pieces removed in one frame would each find the other already out of its network: the rebuild
/// dereferences that null network and throws, the rest of <c>OnDestroy</c> never runs, and the destroyed piece stays
/// registered in its grid cell, so its live neighbours keep a link to it. Such a piece is destroyed as a player's
/// deconstruction destroys it, in its network.
/// </summary>
internal static class NetworkLeaveRule
{
    /// <summary>Whether a removed piece leaves its network first.</summary>
    public static bool LeavesFirst(NeighbourRebuild rebuild, bool networkKeptWhole) =>
        networkKeptWhole && rebuild == NeighbourRebuild.WhenItHadANetwork;

    /// <summary>
    /// Whether a swap's old piece leaves its network before it is destroyed. With replacements it always does:
    /// leaving hands each device registered through it over to them. With none (a plain removal) it leaves only where
    /// <c>OnDestroy</c> leaves the neighbours alone, as for the removal of a network kept whole.
    /// </summary>
    public static bool SwapLeavesFirst(NeighbourRebuild rebuild, bool hasReplacements) =>
        hasReplacements || LeavesFirst(rebuild, networkKeptWhole: true);
}
