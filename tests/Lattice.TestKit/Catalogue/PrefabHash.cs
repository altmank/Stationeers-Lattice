using System.Text;
using Lattice.Api;

namespace Lattice.TestKit.Catalogue;

/// <summary>
/// The game's prefab hash of a prefab name: Animator.StringToHash, a CRC-32 of the name's bytes read as a signed int
/// (decompile Assets.Scripts.GridSystem/ContainsCondition.cs:13; StructureFrameIron = -1240951678, as the
/// game hashes it).
/// </summary>
internal static class PrefabHash
{
    private static readonly uint[] Table = BuildTable();

    public static int Of(string name)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in Encoding.UTF8.GetBytes(name))
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return unchecked((int)~crc);
    }

    public static PrefabKey Piece(string name) => new(Of(name));

    public static KitKey Kit(string name) => new(Of(name));

    public static ItemKey Item(string name) => new(Of(name));

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (var n = 0u; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }

        return table;
    }
}
