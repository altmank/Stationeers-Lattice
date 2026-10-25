using System;
using System.Collections.Generic;
using Lattice.Api;

namespace Lattice.Testing.Stubs;

/// <summary>
/// A base for a test fake of <see cref="IValidation"/>: inherit it and override what your test uses. Every member
/// is virtual and throws <see cref="NotSupportedException"/> naming itself until overridden. A minor that adds a
/// member to <see cref="IValidation"/> adds it here in the same release, so a fake that inherits this keeps
/// compiling and loading.
/// </summary>
public class StubValidation : IValidation
{
    /// <inheritdoc />
    public virtual Result<Answer<ValidationReport>, Reason> Check(Selector which) =>
        throw Stub.NotSetUp("IValidation", "Check", "StubValidation");
}
