using System;
using System.Collections.Generic;
using Lattice.Api;

namespace Lattice.Testing.Stubs;

/// <summary>
/// A base for a test fake of <see cref="IPreview"/>: inherit it and override what your test uses. Every member is
/// virtual and throws <see cref="NotSupportedException"/> naming itself until overridden. A minor that adds a
/// member to <see cref="IPreview"/> adds it here in the same release, so a fake that inherits this keeps
/// compiling and loading.
/// </summary>
public class StubPreview : IPreview
{
    /// <inheritdoc />
    public virtual Answer<Result<Preview, ValueList<Refusal>>> Of(Command command, Actor actor) =>
        throw Stub.NotSetUp("IPreview", "Of", "StubPreview");
}
