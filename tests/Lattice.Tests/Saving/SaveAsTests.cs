using System;
using System.IO;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Saving;
using Lattice.Tests.Board;

namespace Lattice.Tests.Saving;

/// <summary>
/// A world saved under another name takes its plans with it: a loaded world's plan folder is copied to the new name's
/// folder, a new world's is moved, a folder already there is renamed aside, and a failure leaves the plans where they were.
/// </summary>
public sealed class SaveAsTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "lattice-saveas-" + Guid.NewGuid().ToString("N"));
    private int _saves;

    /// <summary>One game session over one plan folder: a board started from what the folder opened.</summary>
    private sealed class Session
    {
        public required PlanSaving Saving { get; init; }

        public required TestBoard Board { get; set; }

        public int Ghosts { get; init; }

        public void Place(int x) => Saving.Record(Board.Placed(Board.Draft("StructureFrame", new Cell(x, 10, 10))));
    }

    private string Lattice(string world) => SaveFiles.LatticeFolderFor(Path.Combine(_data, "saves", world));

    private string SavePath(string world, string file = null!) => Path.Combine(_data, "saves", world, (file ?? world) + ".save");

    private Session Start(string world, Option<SaveKey> save = default)
    {
        var saving = new PlanSaving(new WorldFolder(Lattice(world)));
        var opened = saving.Open(save);
        return new Session { Saving = saving, Board = new TestBoard(Option.Some(opened.Seed)), Ghosts = opened.Seed.Ghosts.Count };
    }

    private SaveKey Save(Session session, string path)
    {
        session.Saving.Flush();
        var key = new SaveKey(path, "fp-" + ++_saves);
        session.Saving.OnGameSave(key, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(_saves));
        return key;
    }

    private static Result<Unit, Reason> SaveStarting(Session session, string savePath, bool newWorld) =>
        session.Saving.FollowSave(savePath, newWorld);

    private Session AWithThreeGhosts(out SaveKey saveA)
    {
        var a = Start("A");
        for (var i = 0; i < 3; i++)
            a.Place(i * 2);
        saveA = Save(a, SavePath("A"));
        return a;
    }

    private int GhostsOnLoad(string world, SaveKey save) => Start(world, Option.Some(save)).Ghosts;

    [Fact]
    public void A_loaded_world_saved_under_a_new_name_loads_with_its_plans_and_the_old_world_keeps_its_own()
    {
        AWithThreeGhosts(out var saveA);
        var loaded = Start("A", Option.Some(saveA));

        Assert.True(SaveStarting(loaded, SavePath("B"), newWorld: false).IsOk);
        var saveB = Save(loaded, SavePath("B"));

        Assert.Equal(3, GhostsOnLoad("B", saveB));
        Assert.Equal(3, GhostsOnLoad("A", saveA));
    }

    [Fact]
    public void A_ghost_added_after_the_copy_is_in_the_new_world_only()
    {
        AWithThreeGhosts(out var saveA);
        var loaded = Start("A", Option.Some(saveA));
        SaveStarting(loaded, SavePath("B"), newWorld: false);
        Save(loaded, SavePath("B", "first"));
        loaded.Place(20);
        var saveB = Save(loaded, SavePath("B"));

        Assert.Equal(4, GhostsOnLoad("B", saveB));
        Assert.Equal(3, GhostsOnLoad("A", saveA));
    }

    [Fact]
    public void Saving_a_world_over_itself_copies_nothing()
    {
        AWithThreeGhosts(out var saveA);
        var loaded = Start("A", Option.Some(saveA));
        var before = Directory.GetDirectories(Path.Combine(_data, "lattice")).Length;

        Assert.True(SaveStarting(loaded, SavePath("A"), newWorld: false).IsOk);

        Assert.Equal(Lattice("A"), loaded.Saving.Folder.Root);
        Assert.Equal(before, Directory.GetDirectories(Path.Combine(_data, "lattice")).Length);
    }

    [Theory]
    [InlineData("autosave")]
    [InlineData("quicksave")]
    [InlineData("manualsave")]
    public void A_rolling_save_of_the_loaded_world_copies_nothing(string rolling)
    {
        AWithThreeGhosts(out var saveA);
        var loaded = Start("A", Option.Some(saveA));

        Assert.True(SaveStarting(loaded, Path.Combine(_data, "saves", "A", rolling, "x.save"), newWorld: false).IsOk);

        Assert.Equal(Lattice("A"), loaded.Saving.Folder.Root);
        Assert.Single(Directory.GetDirectories(Path.Combine(_data, "lattice")));
    }

    [Fact]
    public void A_stale_folder_at_the_new_name_is_renamed_aside_and_not_merged_when_a_loaded_world_is_saved_as_it()
    {
        AWithThreeGhosts(out var saveA);
        Directory.CreateDirectory(Lattice("B"));
        File.WriteAllText(Path.Combine(Lattice("B"), "stale.txt"), "old");
        var loaded = Start("A", Option.Some(saveA));

        Assert.True(SaveStarting(loaded, SavePath("B"), newWorld: false).IsOk);
        var saveB = Save(loaded, SavePath("B"));

        Assert.False(File.Exists(Path.Combine(Lattice("B"), "stale.txt")));
        var aside = Assert.Single(Directory.GetDirectories(Path.Combine(_data, "lattice"), "_replaced-B-*"));
        Assert.True(File.Exists(Path.Combine(aside, "stale.txt")));
        Assert.Equal(3, GhostsOnLoad("B", saveB));
    }

    [Fact]
    public void A_stale_folder_at_the_new_name_is_renamed_aside_when_a_new_world_is_first_saved()
    {
        var fresh = new PlanSaving(new WorldFolder(SaveFiles.NewWorldFolder(Path.Combine(_data, "saves"), Guid.NewGuid())));
        var opened = fresh.Open(Option<SaveKey>.None);
        var session = new Session { Saving = fresh, Board = new TestBoard(Option.Some(opened.Seed)) };
        session.Place(0);
        session.Place(2);
        Directory.CreateDirectory(Lattice("B"));
        File.WriteAllText(Path.Combine(Lattice("B"), "stale.txt"), "old");

        Assert.True(SaveStarting(session, SavePath("B"), newWorld: true).IsOk);
        var saveB = Save(session, SavePath("B"));

        Assert.Equal(Lattice("B"), session.Saving.Folder.Root);
        Assert.Single(Directory.GetDirectories(Path.Combine(_data, "lattice"), "_replaced-B-*"));
        Assert.Empty(Directory.GetDirectories(Path.Combine(_data, "lattice"), "_new-*"));
        Assert.Equal(2, GhostsOnLoad("B", saveB));
    }

    [Fact]
    public void A_copy_that_fails_leaves_the_plans_in_the_old_folder_and_the_target_as_it_was()
    {
        AWithThreeGhosts(out var saveA);
        var loaded = Start("A", Option.Some(saveA));
        Directory.CreateDirectory(Lattice("B"));
        File.WriteAllText(Path.Combine(Lattice("B"), "stale.txt"), "old");
        var locked = Path.Combine(Lattice("A"), "locked.txt");
        File.WriteAllText(locked, "x");

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = SaveStarting(loaded, SavePath("B"), newWorld: false);

            Assert.True(result.IsError);
        }

        Assert.Equal(Lattice("A"), loaded.Saving.Folder.Root);
        Assert.True(File.Exists(Path.Combine(Lattice("B"), "stale.txt")));
        Assert.Empty(Directory.GetDirectories(Path.Combine(_data, "lattice"), "*copying*"));
        loaded.Place(30);
        var next = Save(loaded, SavePath("A", "later"));
        Assert.Equal(4, GhostsOnLoad("A", next));
        Assert.Equal(3, GhostsOnLoad("A", saveA));
        Assert.False(Directory.Exists(Lattice("B")) && Directory.GetFiles(Lattice("B"), "*.lplan", SearchOption.AllDirectories).Length > 0);
    }

    [Fact]
    public void A_move_that_fails_after_the_stale_folder_was_renamed_aside_puts_it_back()
    {
        var fresh = new PlanSaving(new WorldFolder(SaveFiles.NewWorldFolder(Path.Combine(_data, "saves"), Guid.NewGuid())));
        var opened = fresh.Open(Option<SaveKey>.None);
        var session = new Session { Saving = fresh, Board = new TestBoard(Option.Some(opened.Seed)) };
        session.Place(0);
        Directory.CreateDirectory(Lattice("B"));
        File.WriteAllText(Path.Combine(Lattice("B"), "stale.txt"), "old");
        var newFolder = fresh.Folder.Root;
        Directory.CreateDirectory(newFolder);
        var locked = Path.Combine(newFolder, "locked.txt");
        File.WriteAllText(locked, "x");

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.True(SaveStarting(session, SavePath("B"), newWorld: true).IsError);

        Assert.Equal(newFolder, session.Saving.Folder.Root);
        Assert.True(File.Exists(Path.Combine(Lattice("B"), "stale.txt")));
        Assert.Empty(Directory.GetDirectories(Path.Combine(_data, "lattice"), "_replaced-*"));
    }

    [Fact]
    public void A_new_world_whose_first_move_failed_moves_at_the_next_save_and_leaves_no_new_world_folder()
    {
        var fresh = new PlanSaving(new WorldFolder(SaveFiles.NewWorldFolder(Path.Combine(_data, "saves"), Guid.NewGuid())));
        var opened = fresh.Open(Option<SaveKey>.None);
        var session = new Session { Saving = fresh, Board = new TestBoard(Option.Some(opened.Seed)) };
        session.Place(0);
        var newFolder = fresh.Folder.Root;
        Directory.CreateDirectory(newFolder);
        var locked = Path.Combine(newFolder, "locked.txt");
        File.WriteAllText(locked, "x");

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.True(SaveStarting(session, SavePath("B"), newWorld: true).IsError);

        Assert.True(SaveStarting(session, SavePath("B"), newWorld: true).IsOk);
        Assert.Equal(Lattice("B"), session.Saving.Folder.Root);
        Assert.False(Directory.Exists(newFolder));
        Assert.Empty(Directory.GetDirectories(Path.Combine(_data, "lattice"), "_new-*"));
    }

    [Fact]
    public void A_staging_folder_left_by_a_cut_short_copy_is_removed_when_plans_open()
    {
        var half = Lattice("B") + ".copying-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.Combine(half, "plans"));
        File.WriteAllText(Path.Combine(half, "plans", "1.lplan"), "x");

        Start("A");

        Assert.False(Directory.Exists(half));
    }

    [Fact]
    public void Saved_as_B_then_as_C_holds_everything_in_C_and_leaves_B_as_it_was_and_A_untouched()
    {
        AWithThreeGhosts(out var saveA);
        var session = Start("A", Option.Some(saveA));
        SaveStarting(session, SavePath("B"), newWorld: false);
        var saveB = Save(session, SavePath("B"));
        session.Place(20);
        SaveStarting(session, SavePath("C"), newWorld: false);
        var saveC = Save(session, SavePath("C"));

        Assert.Equal(4, GhostsOnLoad("C", saveC));
        Assert.Equal(3, GhostsOnLoad("B", saveB));
        Assert.Equal(3, GhostsOnLoad("A", saveA));
    }

    [Fact]
    public void A_save_path_that_differs_only_by_letter_case_copies_nothing()
    {
        AWithThreeGhosts(out var saveA);
        var loaded = Start("A", Option.Some(saveA));

        Assert.True(SaveStarting(loaded, SavePath("a"), newWorld: false).IsOk);

        Assert.Equal(Lattice("A"), loaded.Saving.Folder.Root);
        Assert.Single(Directory.GetDirectories(Path.Combine(_data, "lattice")));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_data, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
