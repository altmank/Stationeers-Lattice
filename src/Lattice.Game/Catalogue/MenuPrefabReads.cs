using System;
using Assets.Scripts.Objects;
using Lattice.Core.Catalogue;
using Lattice.Core.Slices;

namespace Lattice.Game.Catalogue;

/// <summary>
/// Reads the structure prefabs into <see cref="Reads"/> a slice a frame outside a world, from the time the game has
/// loaded its prefabs until every one is read, so a world's catalogue finds them there at start. Main thread.
/// </summary>
internal sealed class MenuPrefabReads
{
    /// <summary>The time a frame spends reading.</summary>
    public static readonly TimeSpan Slice = TimeSpan.FromMilliseconds(2);

    private static readonly StopwatchClock Clock = new();

    private GamePrefabSource? _source;
    private bool _loaded;
    private bool _done;

    private MenuPrefabReads()
    {
    }

    /// <summary>The structure prefabs' data read so far, for every world of the process.</summary>
    public static PrefabReads Reads { get; } = new();

    /// <summary>Starts reading once the game's prefabs are loaded (now, if they are).</summary>
    public static MenuPrefabReads Start()
    {
        var reads = new MenuPrefabReads { _loaded = Prefab.AllPrefabs.Count > 0 };
        if (!reads._loaded)
            Prefab.OnPrefabsLoaded += reads.OnLoaded;
        return reads;
    }

    /// <summary>One frame's slice; nothing before the prefabs are loaded or once all are read.</summary>
    public void Step()
    {
        if (_done || !_loaded)
            return;
        _source ??= new GamePrefabSource();
        if (!Reads.ReadSome(_source, Clock, Slice))
            return;
        _done = true;
        _source = null;
    }

    /// <summary>Stops listening for the prefabs.</summary>
    public void End() => Prefab.OnPrefabsLoaded -= OnLoaded;

    private void OnLoaded()
    {
        Prefab.OnPrefabsLoaded -= OnLoaded;
        _loaded = true;
    }
}
