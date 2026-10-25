using System;

namespace Lattice.Api;

/// <summary>An executor (a builder, a drone, an agent) as players and agents see it.</summary>
/// <param name="Id">Its id.</param>
/// <param name="Name">Its name, for display.</param>
public sealed record ExecutorInfo(ExecutorId Id, DisplayName Name);

/// <summary>How long a claim holds without renewal, in game time. Positive. <c>default</c> is no time; claims refuse it.</summary>
public readonly record struct Lease
{
    private Lease(double seconds) => Seconds = seconds;

    /// <summary>The length in game seconds.</summary>
    public double Seconds { get; }

    /// <summary>Refuses a length that is not a positive, finite number of seconds.</summary>
    public static Result<Lease, Reason> Create(double seconds) =>
        seconds > 0 && !double.IsInfinity(seconds)
            ? Result<Lease, Reason>.Ok(new Lease(seconds))
            : Result<Lease, Reason>.Error(Reason.InvalidValue(nameof(Lease), seconds.ToString(), "positive"));
}

/// <summary>One executor working on one ghost until a point in game time, unless renewed.</summary>
/// <param name="Ghost">The ghost.</param>
/// <param name="By">The executor.</param>
/// <param name="Until">
/// When it lapses. On a client, the host's lease converted to this seat's game time when it was last told; the host
/// decides when the claim lapses.
/// </param>
public sealed record Claim(GhostId Ghost, ExecutorId By, GameTime Until);

/// <summary>Why a claim was refused, and who holds the ghost if someone does.</summary>
/// <param name="Why">The reason.</param>
/// <param name="HeldBy">The executor holding it, if any.</param>
public sealed record ClaimRefused(Reason Why, Option<ExecutorId> HeldBy);

/// <summary>What an executor says about a ghost it works on. Never "done": matching decides that from the world.</summary>
public abstract record ExecutorStatus
{
    private ExecutorStatus() { }

    /// <summary>Waiting (needs materials, blocked, holds gas).</summary>
    /// <param name="Why">The reason.</param>
    public sealed record Waiting(Reason Why) : ExecutorStatus;

    /// <summary>Working on a step.</summary>
    /// <param name="Step">The step, from 1.</param>
    /// <param name="Of">How many steps.</param>
    public sealed record Working(int Step, int Of) : ExecutorStatus;

    /// <summary>Paused by the executor.</summary>
    public sealed record Paused : ExecutorStatus;
}

/// <summary>One executor's status on a ghost.</summary>
/// <param name="By">The executor.</param>
/// <param name="Status">What it says.</param>
public sealed record ExecutorReport(ExecutorInfo By, ExecutorStatus Status);

/// <summary>
/// Executors: who works on which ghost (claims with leases) and what each says (status providers). Main thread. Lattice
/// implements this; consumers call it. A minor may add members: to fake it in a test, inherit
/// <c>Lattice.Testing.Stubs.StubExecutors</c>, which gains every added member in the same release.
/// </summary>
public interface IExecutors
{
    /// <summary>Registers an executor for this world; refused on a client or for an id already registered. The handle ends with the world.</summary>
    Result<IExecutorHandle, Reason> Register(ExecutorInfo info);

    /// <summary>The claim on a ghost, if any. Constant cost.</summary>
    Option<Claim> ClaimOn(GhostId ghost);

    /// <summary>What every executor says about a ghost. Constant cost; allocates nothing on an unchanged board.</summary>
    ValueList<ExecutorReport> StatusOf(GhostId ghost);
}

/// <summary>
/// A registered executor. Disposing it releases every claim it holds. Main thread. Lattice implements this; consumers
/// call it. A minor may add members: to fake it in a test, inherit <c>Lattice.Testing.Stubs.StubExecutorHandle</c>,
/// which gains every added member in the same release.
/// </summary>
public interface IExecutorHandle : IDisposable
{
    /// <summary>Claims a ghost: one executor per ghost at a time; refused when another holds it or the lease is <c>default</c>.</summary>
    Result<Claim, ClaimRefused> Claim(GhostId ghost, Lease lease);

    /// <summary>Extends a claim by its lease again; refused when it has lapsed.</summary>
    Result<Claim, ClaimRefused> Renew(Claim claim);

    /// <summary>Gives a claim up. Releasing a lapsed claim does nothing.</summary>
    void Release(Claim claim);

    /// <summary>Reports status on a claimed ghost, shown to players and agents; refused when this executor does not hold it.</summary>
    Result<Unit, Reason> Report(GhostId ghost, ExecutorStatus status);
}
