namespace Lattice.Api;

/// <summary>
/// A builder's mode, as it says it and as <see cref="Command.SetBuildMode"/> asks for it. What each mode does is the
/// builder's; Lattice carries it. Lattice's own code refuses a case it does not know with
/// <see cref="ReasonCodes.UnknownKind"/>. Added in 0.22.8.
/// </summary>
public abstract record BuildMode
{
    private BuildMode() { }

    /// <summary>It works its plans, ghosts added later included.</summary>
    public sealed record On : BuildMode;

    /// <summary>It is stopped.</summary>
    public sealed record Off : BuildMode;

    /// <summary>It builds the ghosts of its batch (<see cref="IExecutorHandle.Batch"/>), then stops by itself; ghosts added later wait.</summary>
    public sealed record Batch : BuildMode;
}

/// <summary>
/// Who a builder says may set its mode. Lattice publishes it and never judges an actor against it: the builder does,
/// in its mode handler (<see cref="IExecutorHandle.AcceptModes"/>). Added in 0.22.8.
/// </summary>
public abstract record BuildModeAccess
{
    private BuildModeAccess() { }

    /// <summary>No one: it takes no mode requests. Every builder starts here.</summary>
    public sealed record None : BuildModeAccess;

    /// <summary>Its owner (<see cref="ExecutorInfo.Owner"/>) and the host.</summary>
    public sealed record OwnerAndHost : BuildModeAccess;

    /// <summary>Any player.</summary>
    public sealed record Anyone : BuildModeAccess;
}

/// <summary>A ghost a builder's last batch could not build, and why. Added in 0.22.8.</summary>
/// <param name="Ghost">The ghost.</param>
/// <param name="Why">The builder's reason.</param>
public sealed record SkippedGhost(GhostId Ghost, Reason Why);
