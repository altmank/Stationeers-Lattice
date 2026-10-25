using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Render.Gpu;

namespace Lattice.Render.Materials;

/// <summary>Render's rule id, for the reasons it gives.</summary>
internal static class RenderCodes
{
    public static readonly RuleId Rule = RuleId.Known("lattice.render");
}

/// <summary>
/// One material key: Lattice's own material at the key's draw order and a second instance at the overlays' order with
/// the depth offset (an existing part draws exactly like a placement, from that second instance), and
/// the overrides installed on it, last on top. Every material here is Lattice's copy except an override's own
/// material, which is drawn as given only when it sets its own queue. Lattice's own shaders also get procedural copies
/// too: drawn from a draw group's instance buffer while no override is installed.
/// </summary>
internal sealed class MaterialEntry
{
    private Override[] _overrides = Array.Empty<Override>();

    public MaterialEntry(MaterialKey key, int queue, int overlayQueue)
    {
        Key = key;
        Queue = queue;
        OverlayQueue = overlayQueue;
    }

    public MaterialKey Key { get; }

    public int Queue { get; }

    public int OverlayQueue { get; }

    public IGpuMaterial? Own { get; private set; }

    public IGpuMaterial? OwnOverlay { get; private set; }

    private IGpuMaterial? OwnProcedural { get; set; }

    private IGpuMaterial? OwnOverlayProcedural { get; set; }

    /// <summary>What a placement draws with procedurally: Lattice's own procedural copy while no override is installed.</summary>
    public IGpuMaterial? ProceduralPlacement => _overrides.Length == 0 ? OwnProcedural : null;

    /// <summary>What an existing part draws with procedurally, likewise.</summary>
    public IGpuMaterial? ProceduralOverlay => _overrides.Length == 0 ? OwnOverlayProcedural : null;

    /// <summary>What a placement draws with: the top override, else Lattice's own; null when the key has neither.</summary>
    public IGpuMaterial? Placement => _overrides.Length > 0 ? _overrides[_overrides.Length - 1].Placement : Own;

    /// <summary>What an existing part (a built-structure overlay) draws with.</summary>
    public IGpuMaterial? Overlay => _overrides.Length > 0 ? _overrides[_overrides.Length - 1].Overlay : OwnOverlay;

    /// <summary>What a consumer reads: the top override as given, else Lattice's own.</summary>
    public IGpuMaterial? Exposed => _overrides.Length > 0 ? _overrides[_overrides.Length - 1].Consumer : Own;

    public int Overrides => _overrides.Length;

    public void Load(IGpuMaterial loaded)
    {
        Own = loaded.Copy(Queue, overlay: false);
        OwnOverlay = loaded.Copy(OverlayQueue, overlay: true);
        if (!loaded.HasProcedural)
            return;
        OwnProcedural = loaded.Copy(Queue, overlay: false, procedural: true);
        OwnOverlayProcedural = loaded.Copy(OverlayQueue, overlay: true, procedural: true);
    }

    /// <summary>Installs an override on top. Copy-on-change: a draw or a callback reading the stack mid-change is safe.</summary>
    public Override Push(IGpuMaterial consumer)
    {
        var placement = consumer.OwnQueue.IsSome ? consumer : consumer.Copy(Queue, overlay: false);
        var entry = new Override(this, consumer, placement, consumer.Copy(OverlayQueue, overlay: true));
        var next = new Override[_overrides.Length + 1];
        Array.Copy(_overrides, next, _overrides.Length);
        next[_overrides.Length] = entry;
        _overrides = next;
        return entry;
    }

    public bool Pop(Override entry)
    {
        var at = Array.IndexOf(_overrides, entry);
        if (at < 0)
            return false;
        var next = new Override[_overrides.Length - 1];
        Array.Copy(_overrides, 0, next, 0, at);
        Array.Copy(_overrides, at + 1, next, at, _overrides.Length - at - 1);
        _overrides = next;
        entry.DestroyCopies();
        return true;
    }

    /// <summary>Destroys every material Lattice made for this key: its own copies and every override's.</summary>
    public void End()
    {
        foreach (var entry in _overrides)
            entry.DestroyCopies();
        _overrides = Array.Empty<Override>();
        Own?.Destroy();
        OwnOverlay?.Destroy();
        OwnProcedural?.Destroy();
        OwnOverlayProcedural?.Destroy();
        Own = null;
        OwnOverlay = null;
        OwnProcedural = null;
        OwnOverlayProcedural = null;
    }
}

/// <summary>An installed override: the consumer's material and Lattice's copies of it.</summary>
internal sealed class Override(MaterialEntry entry, IGpuMaterial consumer, IGpuMaterial placement, IGpuMaterial overlay)
{
    public MaterialEntry Entry { get; } = entry;

    public IGpuMaterial Consumer { get; } = consumer;

    public IGpuMaterial Placement { get; } = placement;

    public IGpuMaterial Overlay { get; } = overlay;

    public void DestroyCopies()
    {
        if (!ReferenceEquals(Placement, Consumer))
            Placement.Destroy();
        Overlay.Destroy();
    }
}

/// <summary>
/// Lattice's materials by key, for one world. Loaded once at render
/// start; <see cref="Revision"/> rises with every override change so drawing re-reads a batch's materials only then.
/// <see cref="End"/> destroys every material Render made.
/// </summary>
internal sealed class MaterialTable
{
    private readonly Dictionary<MaterialKey, MaterialEntry> _entries = new(16);
    private readonly DrawOrder _order;

    public MaterialTable(DrawOrder order)
    {
        _order = order;
        foreach (var key in LatticeKeys)
            _entries.Add(key, NewEntry(key));
        var ghost = _entries[MaterialKeys.Ghost];
        FillAndEdge = new MaterialEntry(FillAndEdgeKey, ghost.Queue, ghost.OverlayQueue);
    }

    /// <summary>Lattice's own keys, in the order the bundle holds them.</summary>
    public static IReadOnlyList<MaterialKey> LatticeKeys { get; } = new[]
    {
        MaterialKeys.Ghost, MaterialKeys.Solid, MaterialKeys.Wire, MaterialKeys.Edge,
        MaterialKeys.SeeThrough, MaterialKeys.Glow, MaterialKeys.Label, MaterialKeys.Faded,
    };

    /// <summary>
    /// Lattice's internal material that draws a ghost's fill and edge in one call (the fill pass, then the edge's three
    /// passes; edge colour <c>_LatticeEdgeColor</c>). Not a contract key: used only while neither <c>ghost</c> nor
    /// <c>edge</c> is overridden.
    /// </summary>
    public static MaterialKey FillAndEdgeKey { get; } = MaterialKey.Known("ghost-edged");

    public long Revision { get; private set; } = 1;

    /// <summary>The fill-and-edge material, when the bundle has one.</summary>
    public MaterialEntry FillAndEdge { get; }

    /// <summary>Loads Lattice's own material for each key the source has one for (the bundle, or the stand-in).</summary>
    public void Load(Func<MaterialKey, Option<IGpuMaterial>> source)
    {
        foreach (var key in LatticeKeys)
            if (source(key).TryGet(out var loaded))
                _entries[key].Load(loaded);
        if (source(FillAndEdgeKey).TryGet(out var both))
            FillAndEdge.Load(both);
        Revision++;
    }

    /// <summary>
    /// The one procedural material for a ghost's fill and edge, for a placement or an existing part: only while the
    /// fill draws with <c>ghost</c> and neither it nor <c>edge</c> has an override.
    /// </summary>
    public IGpuMaterial? Combined(MaterialKey fill, bool existing)
    {
        var edge = _entries[MaterialKeys.Edge];
        if (fill != MaterialKeys.Ghost || _entries[MaterialKeys.Ghost].Overrides > 0 || edge.Overrides > 0 || edge.Own is null)
            return null;
        return existing ? FillAndEdge.ProceduralOverlay : FillAndEdge.ProceduralPlacement;
    }

    /// <summary>The key's entry; a consumer's own key gets one on first ask (drawn once an override supplies it).</summary>
    public MaterialEntry Entry(MaterialKey key)
    {
        if (!_entries.TryGetValue(key, out var entry))
        {
            entry = NewEntry(key);
            _entries.Add(key, entry);
        }

        return entry;
    }

    /// <summary>Whether any key has a material to draw with.</summary>
    public bool Any
    {
        get
        {
            foreach (var entry in _entries.Values)
                if (entry.Placement is not null)
                    return true;
            return false;
        }
    }

    public Result<IGpuMaterial, Reason> Exposed(MaterialKey key) =>
        _entries.TryGetValue(key, out var entry) && entry.Exposed is { } material
            ? Result<IGpuMaterial, Reason>.Ok(material)
            : Result<IGpuMaterial, Reason>.Error(Reason.Of(ReasonCodes.NotFound, RenderCodes.Rule, ValueMap.Of("material", key.Value)));

    /// <summary>
    /// Installs an override; refused (<c>invalid-value</c>) for a blank key or a material without instancing, an
    /// argument check at the call, never a per-frame guard.
    /// </summary>
    public Result<IDisposable, Reason> Override(MaterialKey key, IGpuMaterial material)
    {
        if (key.Value.Length == 0)
            return Result<IDisposable, Reason>.Error(Reason.Of(ReasonCodes.InvalidValue, RenderCodes.Rule, ValueMap.Of("material", "").With("problem", "no-key")));
        if (!material.Instanced)
            return Result<IDisposable, Reason>.Error(Reason.Of(ReasonCodes.InvalidValue, RenderCodes.Rule, ValueMap.Of("material", key.Value).With("problem", "instancing-off")));
        var installed = Entry(key).Push(material);
        Revision++;
        return Result<IDisposable, Reason>.Ok(new Installed(this, installed));
    }

    public void End()
    {
        foreach (var entry in _entries.Values)
            entry.End();
        FillAndEdge.End();
        Revision++;
    }

    private void Remove(Override installed)
    {
        if (installed.Entry.Pop(installed))
            Revision++;
    }

    private MaterialEntry NewEntry(MaterialKey key)
    {
        var queue = QueueOf(key);
        return new MaterialEntry(key, queue, queue == _order.Ghosts ? _order.Overlays : queue);
    }

    private int QueueOf(MaterialKey key) =>
        key == MaterialKeys.Faded ? _order.FadedWorld
        : key == MaterialKeys.SeeThrough ? _order.SeeThrough
        : key == MaterialKeys.Label ? _order.Labels
        : _order.Ghosts;

    private sealed class Installed(MaterialTable table, Override installed) : IDisposable
    {
        public void Dispose() => table.Remove(installed);
    }
}
