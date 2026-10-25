using System;

namespace Lattice.Testing.Stubs;

/// <summary>What a stub member throws until a test fake overrides it.</summary>
internal static class Stub
{
    /// <summary>
    /// The failure for a member a test called without setting it up: names its interface, member and stub.
    /// </summary>
    public static NotSupportedException NotSetUp(string contract, string member, string stub) =>
        new($"{contract}.{member} is not set up in {stub}: override it");
}
