using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Lattice.Api;

namespace Lattice.Core.Saving;

/// <summary>
/// How Lattice finds a world and tells saves apart, from how the game writes them (decompile
/// <c>Assets.Scripts.Serialization/SaveHelper.cs</c>, <c>SaveLoadConstants.cs</c>, <c>LoadHelper.cs</c>):
/// <list type="bullet">
/// <item>A world's saves sit in <c>&lt;SavePath&gt;/saves/&lt;world&gt;/</c>: the head save <c>&lt;world&gt;.save</c>, and
/// rolling saves in <c>autosave/</c>, <c>quicksave/</c> and <c>manualsave/</c> under it. A load names the save file
/// (<c>LoadHelper.LoadGame(path, stationName)</c>; the station name can be a raw path), so the world folder comes from
/// the file: its folder, or the one above for a rolling save.</item>
/// <item>Lattice's folder for that world is <c>&lt;SavePath&gt;/lattice/&lt;world&gt;/</c>: beside the saves, never in them.</item>
/// <item>A save's fingerprint is the SHA-1 of its <c>world_meta.xml</c> (the world id, the save time as a file time, the
/// days past): every save writes a new one, and a copy (the game's own SaveAs copies the manual save over the head
/// save) keeps it.</item>
/// </list>
/// </summary>
internal static class SaveFiles
{
    public const string MetaFileName = "world_meta.xml";
    public const string LatticeFolderName = "lattice";
    private const string AutoSaves = "autosave";
    private const string QuickSaves = "quicksave";
    private const string ManualSaves = "manualsave";

    /// <summary>The world's save folder for a save file.</summary>
    public static string WorldFolderOf(string saveFile)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(saveFile)) ?? "";
        var name = Path.GetFileName(folder);
        return Rolling(name, AutoSaves) || Rolling(name, QuickSaves) || Rolling(name, ManualSaves)
            ? Path.GetDirectoryName(folder) ?? folder
            : folder;
    }

    /// <summary>Lattice's folder for a world whose saves are in <paramref name="worldSaves"/> (under the game's saves folder).</summary>
    public static string LatticeFolderFor(string worldSaves)
    {
        var full = Path.GetFullPath(worldSaves);
        var savesRoot = Path.GetDirectoryName(full) ?? full;
        var dataRoot = Path.GetDirectoryName(savesRoot) ?? savesRoot;
        return Path.Combine(dataRoot, LatticeFolderName, Path.GetFileName(full));
    }

    /// <summary>A folder for a new world's plans until its first save names the world.</summary>
    public static string NewWorldFolder(string savesRoot, Guid id) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(savesRoot)) ?? savesRoot, LatticeFolderName, "_new-" + id.ToString("N"));

    /// <summary>The save's fingerprint, or why it has none (no such file, not a zip, no meta entry).</summary>
    public static Result<string, Reason> Fingerprint(string saveFile)
    {
        try
        {
            using var zip = ZipFile.OpenRead(saveFile);
            var meta = zip.GetEntry(MetaFileName);
            if (meta == null)
                return Result<string, Reason>.Error(Problem(saveFile, "no " + MetaFileName));
            using var stream = meta.Open();
            using var sha = SHA1.Create();
            var hash = sha.ComputeHash(stream);
            var text = new StringBuilder(40);
            foreach (var b in hash)
                text.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return Result<string, Reason>.Ok(text.ToString());
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return Result<string, Reason>.Error(Problem(saveFile, e.Message));
        }
    }

    private static bool Rolling(string folder, string rolling) => string.Equals(folder, rolling, StringComparison.OrdinalIgnoreCase);

    private static Reason Problem(string file, string problem) =>
        Reason.Of(ReasonCodes.InvalidValue, SavingReasons.Saving, ValueMap<string, string>.Empty.With("file", file).With("problem", problem));
}
