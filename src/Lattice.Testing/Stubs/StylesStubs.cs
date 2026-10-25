using System;
using System.Collections.Generic;
using Lattice.Api;

namespace Lattice.Testing.Stubs;

/// <summary>
/// A base for a test fake of <see cref="IGhostStyles"/>: inherit it and override what your test uses. Every
/// member is virtual and throws <see cref="NotSupportedException"/> naming itself until overridden; its events
/// keep their subscribers, and each has a <c>Raise</c> method a test fires it with. A minor that adds a member to
/// <see cref="IGhostStyles"/> adds it here in the same release, so a fake that inherits this keeps compiling and
/// loading.
/// </summary>
public class StubGhostStyles : IGhostStyles
{
    /// <inheritdoc />
    public virtual Option<ClipShape> InForce => throw Stub.NotSetUp("IGhostStyles", "InForce", "StubGhostStyles");

    /// <inheritdoc />
    public virtual Result<IStyleLayer, Reason> Add(StyleLayerDescriptor layer) =>
        throw Stub.NotSetUp("IGhostStyles", "Add", "StubGhostStyles");

    /// <inheritdoc />
    public virtual Result<IStyleSet, Reason> Set(StyleSetId id) =>
        throw Stub.NotSetUp("IGhostStyles", "Set", "StubGhostStyles");

    /// <inheritdoc />
    public virtual Result<IStructureSet, Reason> StructureSet(StyleSetId id) =>
        throw Stub.NotSetUp("IGhostStyles", "StructureSet", "StubGhostStyles");

    /// <inheritdoc />
    public virtual Result<IDisposable, Reason> Highlight(HighlightRequest request) =>
        throw Stub.NotSetUp("IGhostStyles", "Highlight", "StubGhostStyles");

    /// <inheritdoc />
    public virtual Result<IDrafts, Reason> Drafts(DraftsId id) =>
        throw Stub.NotSetUp("IGhostStyles", "Drafts", "StubGhostStyles");

    /// <inheritdoc />
    public virtual Result<IClip, Reason> Clip(ClipShape shape) =>
        throw Stub.NotSetUp("IGhostStyles", "Clip", "StubGhostStyles");

    /// <inheritdoc />
    public virtual Option<PickHit> Pick(PickRay ray, PickOptions options) =>
        throw Stub.NotSetUp("IGhostStyles", "Pick", "StubGhostStyles");

    /// <inheritdoc />
    public virtual bool TryPick(PickRay ray, PickOptions options, out PickResult hit)
        => throw Stub.NotSetUp("IGhostStyles", "TryPick", "StubGhostStyles");

    /// <inheritdoc />
    public virtual Result<StyleStory, Reason> Explain(GhostId ghost) =>
        throw Stub.NotSetUp("IGhostStyles", "Explain", "StubGhostStyles");

    /// <inheritdoc />
    public virtual bool Matches(StyleMatch match, StructureRef structure) =>
        throw Stub.NotSetUp("IGhostStyles", "Matches", "StubGhostStyles");

    /// <inheritdoc />
    public virtual event Action<Option<ClipShape>> ClipChanged = delegate { };

    /// <summary>Raises <see cref="ClipChanged"/> to its subscribers, as Lattice would.</summary>
    public void RaiseClipChanged(Option<ClipShape> shape) => ClipChanged.Invoke(shape);
}

/// <summary>
/// A base for a test fake of <see cref="IStyleLayer"/>: inherit it and override what your test uses. Every member
/// is virtual and throws <see cref="NotSupportedException"/> naming itself until overridden;
/// <see cref="IDisposable.Dispose"/> does nothing. A minor that adds a member to <see cref="IStyleLayer"/>
/// adds it here in the same release, so a fake that inherits this keeps compiling and loading.
/// </summary>
public class StubStyleLayer : IStyleLayer
{
    /// <inheritdoc />
    public virtual StyleLayerId Id => throw Stub.NotSetUp("IStyleLayer", "Id", "StubStyleLayer");

    /// <inheritdoc />
    public virtual bool Enabled => throw Stub.NotSetUp("IStyleLayer", "Enabled", "StubStyleLayer");

    /// <inheritdoc />
    public virtual void Enable() =>
        throw Stub.NotSetUp("IStyleLayer", "Enable", "StubStyleLayer");

    /// <inheritdoc />
    public virtual void Disable() =>
        throw Stub.NotSetUp("IStyleLayer", "Disable", "StubStyleLayer");

    /// <summary>Does nothing until overridden.</summary>
    public virtual void Dispose()
    {
    }
}

/// <summary>
/// A base for a test fake of <see cref="IStyleSet"/>: inherit it and override what your test uses. Every member
/// is virtual and throws <see cref="NotSupportedException"/> naming itself until overridden. A minor that adds a
/// member to <see cref="IStyleSet"/> adds it here in the same release, so a fake that inherits this keeps
/// compiling and loading.
/// </summary>
public class StubStyleSet : IStyleSet
{
    /// <inheritdoc />
    public virtual StyleSetId Id => throw Stub.NotSetUp("IStyleSet", "Id", "StubStyleSet");

    /// <inheritdoc />
    public virtual int Count => throw Stub.NotSetUp("IStyleSet", "Count", "StubStyleSet");

    /// <inheritdoc />
    public virtual bool Contains(GhostId ghost) =>
        throw Stub.NotSetUp("IStyleSet", "Contains", "StubStyleSet");

    /// <inheritdoc />
    public virtual void Put(GhostId ghost) =>
        throw Stub.NotSetUp("IStyleSet", "Put", "StubStyleSet");

    /// <inheritdoc />
    public virtual void Replace(ValueList<GhostId> ghosts) =>
        throw Stub.NotSetUp("IStyleSet", "Replace", "StubStyleSet");

    /// <inheritdoc />
    public virtual void Add(ValueList<GhostId> ghosts) =>
        throw Stub.NotSetUp("IStyleSet", "Add", "StubStyleSet");

    /// <inheritdoc />
    public virtual void Remove(ValueList<GhostId> ghosts) =>
        throw Stub.NotSetUp("IStyleSet", "Remove", "StubStyleSet");

    /// <inheritdoc />
    public virtual void Clear() =>
        throw Stub.NotSetUp("IStyleSet", "Clear", "StubStyleSet");
}

/// <summary>
/// A base for a test fake of <see cref="IStructureSet"/>: inherit it and override what your test uses. Every
/// member is virtual and throws <see cref="NotSupportedException"/> naming itself until overridden. A minor that
/// adds a member to <see cref="IStructureSet"/> adds it here in the same release, so a fake that inherits this
/// keeps compiling and loading.
/// </summary>
public class StubStructureSet : IStructureSet
{
    /// <inheritdoc />
    public virtual StyleSetId Id => throw Stub.NotSetUp("IStructureSet", "Id", "StubStructureSet");

    /// <inheritdoc />
    public virtual int Count => throw Stub.NotSetUp("IStructureSet", "Count", "StubStructureSet");

    /// <inheritdoc />
    public virtual bool Contains(StructureRef structure) =>
        throw Stub.NotSetUp("IStructureSet", "Contains", "StubStructureSet");

    /// <inheritdoc />
    public virtual void Put(StructureRef structure) =>
        throw Stub.NotSetUp("IStructureSet", "Put", "StubStructureSet");

    /// <inheritdoc />
    public virtual void Replace(ValueList<StructureRef> structures) =>
        throw Stub.NotSetUp("IStructureSet", "Replace", "StubStructureSet");

    /// <inheritdoc />
    public virtual void Add(ValueList<StructureRef> structures) =>
        throw Stub.NotSetUp("IStructureSet", "Add", "StubStructureSet");

    /// <inheritdoc />
    public virtual void Remove(ValueList<StructureRef> structures) =>
        throw Stub.NotSetUp("IStructureSet", "Remove", "StubStructureSet");

    /// <inheritdoc />
    public virtual void Clear() =>
        throw Stub.NotSetUp("IStructureSet", "Clear", "StubStructureSet");
}

/// <summary>
/// A base for a test fake of <see cref="IDrafts"/>: inherit it and override what your test uses. Every member is
/// virtual and throws <see cref="NotSupportedException"/> naming itself until overridden;
/// <see cref="IDisposable.Dispose"/> does nothing. A minor that adds a member to <see cref="IDrafts"/> adds
/// it here in the same release, so a fake that inherits this keeps compiling and loading.
/// </summary>
public class StubDrafts : IDrafts
{
    /// <inheritdoc />
    public virtual DraftsId Id => throw Stub.NotSetUp("IDrafts", "Id", "StubDrafts");

    /// <inheritdoc />
    public virtual void Show(ValueList<GhostDraft> drafts) =>
        throw Stub.NotSetUp("IDrafts", "Show", "StubDrafts");

    /// <inheritdoc />
    public virtual void Show(Preview preview) =>
        throw Stub.NotSetUp("IDrafts", "Show", "StubDrafts");

    /// <inheritdoc />
    public virtual void Clear() =>
        throw Stub.NotSetUp("IDrafts", "Clear", "StubDrafts");

    /// <inheritdoc />
    public virtual void Hand(Submission submission) =>
        throw Stub.NotSetUp("IDrafts", "Hand", "StubDrafts");

    /// <summary>Does nothing until overridden.</summary>
    public virtual void Dispose()
    {
    }
}

/// <summary>
/// A base for a test fake of <see cref="IClip"/>: inherit it and override what your test uses. Every member is
/// virtual and throws <see cref="NotSupportedException"/> naming itself until overridden;
/// <see cref="IDisposable.Dispose"/> does nothing. A minor that adds a member to <see cref="IClip"/> adds
/// it here in the same release, so a fake that inherits this keeps compiling and loading.
/// </summary>
public class StubClip : IClip
{
    /// <inheritdoc />
    public virtual ClipShape Shape => throw Stub.NotSetUp("IClip", "Shape", "StubClip");

    /// <inheritdoc />
    public virtual void Move(ClipShape to) =>
        throw Stub.NotSetUp("IClip", "Move", "StubClip");

    /// <summary>Does nothing until overridden.</summary>
    public virtual void Dispose()
    {
    }
}
