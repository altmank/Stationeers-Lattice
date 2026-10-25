using System;
using System.IO;
using System.IO.Compression;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Saving;

namespace Lattice.Tests.Saving;

public sealed class SaveFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lattice-savefiles-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData("Moon.save")]
    [InlineData("autosave/2026-10-24_auto.save")]
    [InlineData("quicksave/2026-10-24_quick.save")]
    [InlineData("manualsave/before-launch.save")]
    public void Every_save_of_a_world_names_the_same_world_folder(string file)
    {
        var world = Path.Combine(_root, "data", "saves", "Moon");
        Assert.Equal(Path.GetFullPath(world), SaveFiles.WorldFolderOf(Path.Combine(world, file)));
    }

    [Fact]
    public void Lattices_folder_sits_beside_the_saves_never_in_them()
    {
        var world = Path.Combine(_root, "data", "saves", "Moon");
        Assert.Equal(Path.GetFullPath(Path.Combine(_root, "data", "lattice", "Moon")), SaveFiles.LatticeFolderFor(world));
    }

    [Fact]
    public void A_copy_has_the_fingerprint_of_its_save_and_a_new_save_another()
    {
        var first = Save("first.save", "<WorldMetaData><Id>w1</Id><DateTime>1</DateTime></WorldMetaData>");
        var copy = Path.Combine(_root, "copy.save");
        File.Copy(first, copy);
        var second = Save("second.save", "<WorldMetaData><Id>w1</Id><DateTime>2</DateTime></WorldMetaData>");

        Assert.True(SaveFiles.Fingerprint(first).TryGet(out var a));
        Assert.Equal(Result<string, Reason>.Ok(a), SaveFiles.Fingerprint(copy));
        Assert.NotEqual(Result<string, Reason>.Ok(a), SaveFiles.Fingerprint(second));
    }

    [Fact]
    public void A_file_that_is_no_save_has_no_fingerprint()
    {
        Directory.CreateDirectory(_root);
        var text = Path.Combine(_root, "notes.save");
        File.WriteAllText(text, "not a zip");

        Assert.True(SaveFiles.Fingerprint(text).IsError);
        Assert.True(SaveFiles.Fingerprint(Path.Combine(_root, "missing.save")).IsError);
    }

    [Fact]
    public void A_new_worlds_files_move_to_its_folder_at_the_first_save_and_lines_continue_there()
    {
        using var world = new SavedWorld();
        var a = world.Place(10);
        var named = new WorldFolder(Path.Combine(_root, "lattice", "Moon"));

        Assert.True(world.Saving.MoveTo(named).IsOk);
        var b = world.Place(30);
        var save = world.Save("first");

        Assert.False(Directory.Exists(world.Root));
        var reopened = new PlanSaving(named).Open(Option.Some(save));
        Assert.Equal(new[] { a, b }, Ids(reopened.Seed));
    }

    [Fact]
    public void A_move_onto_another_worlds_folder_is_refused()
    {
        using var world = new SavedWorld();
        world.Place(10);
        var taken = Path.Combine(world.Root, "taken");
        Directory.CreateDirectory(taken);

        Assert.True(world.Saving.MoveTo(new WorldFolder(taken)).IsError);
    }

    private static GhostId[] Ids(BoardSeed seed)
    {
        var ids = new GhostId[seed.Ghosts.Count];
        for (var i = 0; i < ids.Length; i++)
            ids[i] = seed.Ghosts[i].Id;
        return ids;
    }

    private string Save(string name, string meta)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(zip.CreateEntry(SaveFiles.MetaFileName).Open()))
            writer.Write(meta);
        using (var writer = new StreamWriter(zip.CreateEntry("world.xml").Open()))
            writer.Write("<WorldData />");
        return path;
    }
}
