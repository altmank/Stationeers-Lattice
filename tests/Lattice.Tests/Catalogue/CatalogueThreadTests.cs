using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Lattice.Api;
using Lattice.Core.Catalogue;
using Lattice.Core.Ports;
using Lattice.Core.Slices;
using Lattice.TestKit.Catalogue;
using Xunit.Abstractions;
using static Lattice.Tests.Catalogue.Prefabs;

namespace Lattice.Tests.Catalogue;

/// <summary>
/// A game of <paramref name="count"/> structure prefabs, each a renamed copy of a sample piece, whose reads say which
/// thread made them (the game's reads are main-thread only).
/// </summary>
internal sealed class ThreadedSource : IPrefabSource
{
    private readonly Dictionary<PrefabKey, PrefabData> _pieces = [];
    private readonly object _gate = new();
    private readonly List<int> _readers = [];

    public ThreadedSource(int count)
    {
        var shapes = new[] { SampleGame.Frame, SampleGame.Straight, SampleGame.Corner, SampleGame.Furnace, SampleGame.Sensor };
        var keys = new List<PrefabKey>(count);
        var kitPieces = new List<PrefabKey>(count);
        for (var i = 0; i < count; i++)
        {
            var name = "StructureThreaded" + i;
            var piece = shapes[i % shapes.Length] with { Name = name, Key = PrefabHash.Piece(name) };
            _pieces.Add(piece.Key, piece);
            keys.Add(piece.Key);
            kitPieces.Add(piece.Key);
        }

        Structures = keys;
        Kits = [new KitData("ItemKitThreaded", PrefabHash.Kit("ItemKitThreaded"), ValueList.From(kitPieces))];
    }

    public IReadOnlyList<KitData> Kits { get; }

    public IReadOnlyList<PrefabKey> Structures { get; }

    public IReadOnlyList<ListedPiece> Listed { get; } = [];

    public IReadOnlyList<ItemRef> BrokenTools { get; } = [];

    public IReadOnlyList<SprayCanData> SprayCans { get; } = [];

    public IReadOnlyList<ItemRef> Labellers { get; } = [];

    public IReadOnlyList<ListedItem> Items { get; } = [];

    /// <summary>The managed thread of every read, in order.</summary>
    public IReadOnlyList<int> Readers
    {
        get
        {
            lock (_gate)
                return [.. _readers];
        }
    }

    public Option<PrefabData> Read(PrefabKey piece)
    {
        lock (_gate)
            _readers.Add(Environment.CurrentManagedThreadId);
        return _pieces.TryGetValue(piece, out var data) ? Option.Some(data) : Option<PrefabData>.None;
    }
}

/// <summary>A clock that moves a fixed step each time it is read.</summary>
internal sealed class TickClock(TimeSpan tick) : IClock
{
    private TimeSpan _now;

    public TimeSpan Now => _now += tick;
}

/// <summary>
/// The prefab catalogue is read on the main thread and the validation worker. Reading a piece calls the game, so every
/// structure is read on the main thread before the world starts (<see cref="PrefabCatalogue.Complete"/>, mostly from
/// <see cref="PrefabReads"/> filled a slice a frame before the first world), and from then on the catalogue is
/// read-only: any thread may ask, and none of them calls the game.
/// </summary>
public sealed class CatalogueThreadTests
{
    private static readonly PrefabKey Unknown = PrefabHash.Piece("StructureNobodyMade");

    [Fact]
    public void Main_and_worker_threads_asking_the_same_pieces_at_once_get_the_same_answers_and_never_call_the_game()
    {
        var source = new ThreadedSource(400);
        var catalogue = new PrefabCatalogue(source);
        var main = Environment.CurrentManagedThreadId;
        catalogue.Complete();
        var expected = new Dictionary<PrefabKey, Result<PieceFacts, Reason>>();
        foreach (var key in source.Structures)
            expected[key] = catalogue.Piece(key);
        var unknown = catalogue.Piece(Unknown);

        var failures = new List<string>();
        using var start = new Barrier(2);
        void Ask()
        {
            start.SignalAndWait();
            for (var round = 0; round < 200; round++)
            {
                foreach (var key in source.Structures)
                    if (!catalogue.Piece(key).Equals(expected[key]))
                        lock (failures)
                            failures.Add($"{key.Hash} differs on thread {Environment.CurrentManagedThreadId}");
                if (!catalogue.Piece(Unknown).Equals(unknown))
                    lock (failures)
                        failures.Add("the unknown piece differs");
            }
        }

        Exception? thrown = null;
        var worker = new Thread(() =>
        {
            try
            {
                Ask();
            }
            catch (Exception error)
            {
                thrown = error;
            }
        });
        worker.Start();
        Ask();
        worker.Join();

        Assert.Null(thrown);
        Assert.Empty(failures);
        Assert.True(unknown.TryGetError(out var why));
        Assert.Equal("no-prefab", why.Facts["problem"]);
        Assert.Equal(source.Structures.Count, source.Readers.Count);
        Assert.All(source.Readers, reader => Assert.Equal(main, reader));
    }

    [Fact]
    public void A_complete_catalogue_refuses_a_piece_it_does_not_hold_as_the_game_would_without_asking_it()
    {
        var source = new ThreadedSource(10);
        var lazy = new PrefabCatalogue(source);
        var refusedLazily = lazy.Piece(Unknown);
        var complete = new PrefabCatalogue(source);
        complete.Complete();
        var reads = source.Readers.Count;

        Assert.Equal(refusedLazily, complete.Piece(Unknown));
        Assert.Equal(reads, source.Readers.Count);
        Assert.True(complete.IsComplete);
    }

    [Fact]
    public void Reading_in_slices_stops_at_the_budget_and_reads_each_piece_once()
    {
        var source = new ThreadedSource(50);
        var catalogue = new PrefabCatalogue(source);
        var clock = new TickClock(TimeSpan.FromMilliseconds(1));

        var slices = 1;
        while (!catalogue.ReadSome(clock, TimeSpan.FromMilliseconds(4)))
            slices++;
        catalogue.Complete();

        Assert.InRange(slices, 10, 50);
        Assert.Equal(50, source.Readers.Count);
    }

    [Fact]
    public void Prefabs_read_before_the_first_world_are_not_read_again_by_any_worlds_catalogue()
    {
        var source = new ThreadedSource(60);
        var reads = new PrefabReads();
        var clock = new TickClock(TimeSpan.FromMilliseconds(1));
        reads.ReadSome(source, clock, TimeSpan.FromMilliseconds(20));
        var early = source.Readers.Count;
        Assert.InRange(early, 1, 59);

        var first = new PrefabCatalogue(reads.Over(source));
        first.Complete();
        var second = new PrefabCatalogue(reads.Over(source));
        second.Complete();

        Assert.Equal(60, source.Readers.Count);
        Assert.Equal(60, reads.Count);
        foreach (var key in source.Structures)
            Assert.Equal(first.Piece(key), second.Piece(key));
    }
}

/// <summary>
/// The catalogue's cost at world start. The game reads every structure prefab in about 127 ms (its catalogue export on
/// the test machine), so those reads happen a slice a frame before the first world; at start the catalogue maps the
/// prefab data read already, which stays under <see cref="StartBudget"/> for 1,000 structures (the game has about 800),
/// and a slice before the world stays within its 2 ms.
/// </summary>
[Collection(nameof(Timing))]
public sealed class CatalogueStartCostTests(ITestOutputHelper output)
{
    private static readonly TimeSpan StartBudget = TimeSpan.FromMilliseconds(16);
    private static readonly TimeSpan Slice = TimeSpan.FromMilliseconds(2);

    [Fact]
    [Trait(Perf.Trait, Perf.Value)]
    public void Completing_a_catalogue_from_prefabs_read_before_the_world_costs_under_a_frame()
    {
        var best = TimeSpan.MaxValue;
        var worstSlice = TimeSpan.Zero;
        for (var run = 0; run < 5; run++)
        {
            var source = new ThreadedSource(1000);
            var reads = new PrefabReads();
            var clock = new StopwatchClock();
            var sliced = Stopwatch.StartNew();
            while (!reads.ReadSome(source, clock, Slice))
            {
                worstSlice = sliced.Elapsed > worstSlice ? sliced.Elapsed : worstSlice;
                sliced.Restart();
            }

            Perf.Collect();
            var catalogue = new PrefabCatalogue(reads.Over(source));
            var start = Stopwatch.StartNew();
            catalogue.Complete();
            start.Stop();
            best = start.Elapsed < best ? start.Elapsed : best;
        }

        output.WriteLine($"1,000 structures: completed at world start in {best.TotalMilliseconds:F2} ms; worst slice before it {worstSlice.TotalMilliseconds:F2} ms");
        Assert.True(best < StartBudget, $"completing took {best.TotalMilliseconds:F2} ms");
        Assert.True(worstSlice < Slice + TimeSpan.FromMilliseconds(1), $"a slice took {worstSlice.TotalMilliseconds:F2} ms");
    }
}
