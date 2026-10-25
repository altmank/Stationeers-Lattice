using System;

namespace Lattice.TestKit;

/// <summary>A kit check that failed: a golden file that differs, a call that allocates. Any test framework reports it.</summary>
internal sealed class KitCheckFailed(string message) : Exception(message);
