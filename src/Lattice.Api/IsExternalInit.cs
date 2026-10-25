namespace System.Runtime.CompilerServices;

/// <summary>
/// Lets netstandard2.1 compile <c>init</c> accessors and records. The one copy in the solution:
/// Core and Game see it through InternalsVisibleTo; consumers need none.
/// </summary>
internal static class IsExternalInit;
