using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Ports;

namespace Lattice.Tests.Contracts;

/// <summary>
/// What every <see cref="IWorldView"/> promises, fake or over the game: a structure reads the same by id and by each
/// cell it is in; networks and structures point at each other; a network's cells hold its pieces; rooms named by a
/// cell exist and list that cell; absent things read as none. Derive it and give the subject plus some structures it is known to hold.
/// </summary>
public abstract class WorldViewContract
{
    internal abstract IWorldView Subject { get; }

    /// <summary>Structures the subject holds (at least one).</summary>
    internal abstract IReadOnlyList<StructureInfo> Known { get; }

    /// <summary>An id the subject does not hold.</summary>
    internal virtual ReferenceId Absent => new(long.MaxValue);

    /// <summary>A cell with nothing in it.</summary>
    internal virtual Cell Empty => new(-1_000_000, -1_000_000, -1_000_000);

    [Fact]
    public void Knows_some_structures() => Assert.NotEmpty(Known);

    [Fact]
    public void A_known_structure_reads_the_same_by_id()
    {
        foreach (var info in Known)
            Assert.Equal(Option.Some(info), Subject.Structure(info.Ref.Id));
    }

    [Fact]
    public void A_known_structure_is_in_its_own_cell()
    {
        foreach (var info in Known)
            Assert.Contains(info, Subject.At(info.Ref.At));
    }

    [Fact]
    public void Everything_in_a_cell_reads_the_same_by_id()
    {
        foreach (var info in Known)
            foreach (var here in Subject.At(info.Ref.At))
                Assert.Equal(Option.Some(here), Subject.Structure(here.Ref.Id));
    }

    [Fact]
    public void A_structure_on_a_network_is_listed_by_that_network()
    {
        foreach (var info in Known)
        {
            if (!Subject.NetworkOf(info.Ref.Id).TryGet(out var network))
                continue;
            Assert.True(Subject.Network(network).TryGet(out var facts), $"Network {network} of {info.Ref.Id} is missing.");
            Assert.Equal(network, facts.Ref);
            Assert.True(Contains(facts.Pieces, info.Ref.Id) || Contains(facts.Devices, info.Ref.Id),
                $"Network {network} does not list {info.Ref.Id}.");
        }
    }

    [Fact]
    public void Every_piece_of_a_network_is_on_that_network()
    {
        foreach (var info in Known)
        {
            if (!Subject.NetworkOf(info.Ref.Id).TryGet(out var network) || !Subject.Network(network).TryGet(out var facts))
                continue;
            foreach (var piece in facts.Pieces)
                Assert.Equal(Option.Some(network), Subject.NetworkOf(piece));
        }
    }

    [Fact]
    public void A_network_fills_the_cell_of_each_of_its_pieces()
    {
        foreach (var info in Known)
        {
            if (!Subject.NetworkOf(info.Ref.Id).TryGet(out var network) || !Subject.Network(network).TryGet(out var facts))
                continue;
            foreach (var piece in facts.Pieces)
                if (Subject.Structure(piece).TryGet(out var structure))
                    Assert.True(Contains(facts.Cells, structure.Ref.At), $"Network {network} does not fill {structure.Ref.At} of {piece}.");
        }
    }

    [Fact]
    public void A_room_named_by_a_cell_exists_and_lists_that_cell()
    {
        foreach (var info in Known)
            if (Subject.RoomAt(info.Ref.At).TryGet(out var room))
            {
                Assert.True(Subject.Room(room).TryGet(out var facts), $"Room {room} at {info.Ref.At} is missing.");
                Assert.True(Contains(facts.Cells, info.Ref.At), $"Room {room} does not list {info.Ref.At}.");
            }
    }

    [Fact]
    public void Absent_things_read_as_none()
    {
        Assert.True(Subject.Structure(Absent).IsNone);
        Assert.True(Subject.NetworkOf(Absent).IsNone);
        Assert.Empty(Subject.At(Empty));
        Assert.True(Subject.RoomAt(Empty).IsNone);
    }

    private static bool Contains<T>(ValueList<T> items, T item)
    {
        foreach (var each in items)
            if (EqualityComparer<T>.Default.Equals(each, item))
                return true;
        return false;
    }
}
