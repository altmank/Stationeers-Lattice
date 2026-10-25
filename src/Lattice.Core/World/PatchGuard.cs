using System;

namespace Lattice.Core.World;

/// <summary>
/// Runs the body of a patch on game code so that nothing Lattice throws reaches the game (architecture section 12:
/// fail safe to vanilla). An exception in the body is a Lattice bug: it goes to <c>fault</c> with the patch's name and
/// the patch returns as if it had nothing to do, so the game's own call always completes. The bodies are static
/// lambdas, so a guarded call allocates nothing.
/// </summary>
internal static class PatchGuard
{
    /// <summary>Runs a body that returns nothing.</summary>
    public static void Run<T>(string patch, T argument, Action<T> body, Action<string, Exception> fault)
    {
        try
        {
            body(argument);
        }
        catch (Exception e)
        {
            fault(patch, e);
        }
    }

    /// <summary>Runs a body with two arguments that returns nothing.</summary>
    public static void Run<T1, T2>(string patch, T1 first, T2 second, Action<T1, T2> body, Action<string, Exception> fault)
    {
        try
        {
            body(first, second);
        }
        catch (Exception e)
        {
            fault(patch, e);
        }
    }

    /// <summary>
    /// Runs a prefix body that returns the state for its postfix; on a fault returns <paramref name="skip"/>, which
    /// the postfix takes as "do nothing".
    /// </summary>
    public static TState Run<T, TState>(string patch, T argument, Func<T, TState> body, TState skip, Action<string, Exception> fault)
    {
        try
        {
            return body(argument);
        }
        catch (Exception e)
        {
            fault(patch, e);
            return skip;
        }
    }
}
