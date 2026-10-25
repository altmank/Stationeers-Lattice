using System;
using System.Collections.Generic;
using Lattice.Api;

namespace Lattice.Testing.Stubs;

/// <summary>
/// A base for a test fake of <see cref="IGhostSystem"/>: inherit it and override what your test uses. Every
/// member is virtual and throws <see cref="NotSupportedException"/> naming itself until overridden. A minor that
/// adds a member to <see cref="IGhostSystem"/> adds it here in the same release, so a fake that inherits this
/// keeps compiling and loading.
/// </summary>
public class StubGhostSystem : IGhostSystem
{
    /// <inheritdoc />
    public virtual IGhostBoard Board => throw Stub.NotSetUp("IGhostSystem", "Board", "StubGhostSystem");

    /// <inheritdoc />
    public virtual IWorldQuery World => throw Stub.NotSetUp("IGhostSystem", "World", "StubGhostSystem");

    /// <inheritdoc />
    public virtual IPlans Plans => throw Stub.NotSetUp("IGhostSystem", "Plans", "StubGhostSystem");

    /// <inheritdoc />
    public virtual ICommands Commands => throw Stub.NotSetUp("IGhostSystem", "Commands", "StubGhostSystem");

    /// <inheritdoc />
    public virtual IPreview Preview => throw Stub.NotSetUp("IGhostSystem", "Preview", "StubGhostSystem");

    /// <inheritdoc />
    public virtual IValidation Validation => throw Stub.NotSetUp("IGhostSystem", "Validation", "StubGhostSystem");

    /// <inheritdoc />
    public virtual ISteps Steps => throw Stub.NotSetUp("IGhostSystem", "Steps", "StubGhostSystem");

    /// <inheritdoc />
    public virtual IBills Bills => throw Stub.NotSetUp("IGhostSystem", "Bills", "StubGhostSystem");

    /// <inheritdoc />
    public virtual ISwaps Swaps => throw Stub.NotSetUp("IGhostSystem", "Swaps", "StubGhostSystem");

    /// <inheritdoc />
    public virtual IAnalysis Analysis => throw Stub.NotSetUp("IGhostSystem", "Analysis", "StubGhostSystem");

    /// <inheritdoc />
    public virtual IExecutors Executors => throw Stub.NotSetUp("IGhostSystem", "Executors", "StubGhostSystem");

    /// <inheritdoc />
    public virtual IRegistries Registries => throw Stub.NotSetUp("IGhostSystem", "Registries", "StubGhostSystem");

    /// <inheritdoc />
    public virtual IInspector Inspector => throw Stub.NotSetUp("IGhostSystem", "Inspector", "StubGhostSystem");

    /// <inheritdoc />
    public virtual IGhostEvents Events => throw Stub.NotSetUp("IGhostSystem", "Events", "StubGhostSystem");

    /// <inheritdoc />
    public virtual ISlices Slices => throw Stub.NotSetUp("IGhostSystem", "Slices", "StubGhostSystem");

    /// <inheritdoc />
    public virtual IGhostStyles Styles => throw Stub.NotSetUp("IGhostSystem", "Styles", "StubGhostSystem");

    /// <inheritdoc />
    public virtual IPieceCatalogue Catalogue => throw Stub.NotSetUp("IGhostSystem", "Catalogue", "StubGhostSystem");

    /// <inheritdoc />
    public virtual ApiVersion Version => throw Stub.NotSetUp("IGhostSystem", "Version", "StubGhostSystem");

    /// <inheritdoc />
    public virtual ValueList<ModuleStatus> Modules => throw Stub.NotSetUp("IGhostSystem", "Modules", "StubGhostSystem");

    /// <inheritdoc />
    public virtual bool Has(IntentKind kind) =>
        throw Stub.NotSetUp("IGhostSystem", "Has", "StubGhostSystem");
}
