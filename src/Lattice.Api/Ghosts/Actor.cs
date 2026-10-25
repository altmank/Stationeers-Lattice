namespace Lattice.Api;

/// <summary>Who asked for a change; checked against the installed access policy.</summary>
public abstract record Actor
{
    private Actor() { }

    /// <summary>A player, by client id alone; the display name comes from the game when shown, so renaming changes nothing.</summary>
    /// <param name="Client">The player's client id.</param>
    public sealed record Player(ClientId Client) : Actor;

    /// <summary>An agent: a mod acting on its own, such as a builder that swaps a network.</summary>
    /// <param name="Id">The agent's id.</param>
    public sealed record Agent(AgentId Id) : Actor;

    /// <summary>Another mod, or Lattice itself for changes it makes (completion, target gone).</summary>
    /// <param name="Id">The mod's id.</param>
    public sealed record Mod(ModId Id) : Actor;
}

/// <summary>Who may see a ghost or plan. Enforced only when an installed access policy says so.</summary>
public abstract record Visibility
{
    private Visibility() { }

    /// <summary>Every actor.</summary>
    public sealed record Everyone : Visibility;

    /// <summary>Only the owner: for a ghost, the actor who placed it (<see cref="Ghost.PlacedBy"/>); for a plan, <see cref="Plan.Owner"/>.</summary>
    public sealed record OwnerOnly : Visibility;

    /// <summary>The actors the access policy counts in the named team.</summary>
    /// <param name="Name">The team.</param>
    public sealed record Team(TeamName Name) : Visibility;
}
