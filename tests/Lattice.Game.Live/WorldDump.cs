using System.Collections.Generic;
using System.IO;
using System.Text;
using Lattice.Api;
using Lattice.Game.World;

namespace Lattice.Game.Live;

/// <summary>
/// The bridge's reads of the whole world as text, for the save comparison on .NET 8 (tests/Lattice.Tests/World):
/// <c>structure id piece x y z turn state colour name setting</c> (state <c>built</c>, <c>building-N</c> or <c>broken</c>;
/// <c>-</c> for no colour or name; a name's spaces as <c>_</c>), <c>network kinds pieces</c> (piece ids sorted, one
/// line per network a structure is on) and <c>room cells</c>.
/// </summary>
internal static class WorldDump
{
    public static int Write(string path)
    {
        var view = new GameWorldView();
        var lines = new List<string>();
        var networks = new HashSet<long>();
        var rooms = new HashSet<long>();
        foreach (var info in LiveContext.Known(view))
        {
            var at = info.Ref.At;
            lines.Add($"structure {info.Ref.Id.Value} {info.Ref.Piece.Hash} {at.X} {at.Y} {at.Z} {info.Turn.Index} {State(info.State)} " +
                      $"{info.Colour.Match(c => c.Index.ToString(), () => "-")} {info.Name.Match(n => n.Value.Replace(' ', '_'), () => "-")} " +
                      $"{info.Setting.Match(s => s.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture), () => "-")}");
            if (view.NetworkOf(info.Ref.Id).TryGet(out var network) && networks.Add(network.Value) && view.Network(network).TryGet(out var facts))
            {
                var ids = new List<long>();
                foreach (var piece in facts.Pieces)
                    ids.Add(piece.Value);
                ids.Sort();
                var kinds = new List<string>();
                foreach (var kind in facts.Kinds)
                    kinds.Add(kind.GetType().Name);
                kinds.Sort();
                lines.Add($"network {string.Join(",", kinds)} {string.Join(",", ids)}");
            }

            if (view.RoomAt(at).TryGet(out var room) && rooms.Add(room.Value) && view.Room(room).TryGet(out var roomFacts))
            {
                var cells = new List<string>();
                foreach (var cell in roomFacts.Cells)
                    cells.Add($"{cell.X}:{cell.Y}:{cell.Z}");
                cells.Sort();
                lines.Add($"room {string.Join(",", cells)}");
            }
        }

        lines.Sort();
        File.WriteAllLines(path, lines, new UTF8Encoding(false));
        return lines.Count;
    }

    private static string State(BuildState state) => state switch
    {
        BuildState.Building building => $"building-{building.At.Value}",
        BuildState.Built => "built",
        BuildState.Broken => "broken",
        _ => "unknown",
    };
}
