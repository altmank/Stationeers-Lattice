using System;
using System.IO;
using Lattice.TestKit.Fixtures;

namespace Lattice.Tests.Saving;

/// <summary>
/// What the saving module's live runs recorded on the test instance (Lattice.Game.Live; logs copied by
/// make-fixtures.ps1): a Monday/Tuesday example over the game's own saves. A new world places A, makes its
/// first save (the head save, Monday), places B and autosaves (Tuesday); loading the head save shows A and places C
/// without saving; loading the autosave shows A and B; loading the head save again shows A and offers C.
/// </summary>
public sealed class LiveSavingTests
{
    [SaveFact("saving-new.log")]
    public void A_new_worlds_plans_move_to_its_folder_and_each_save_gets_a_record()
    {
        var log = Lines("saving-new.log");
        Assert.Contains(log, l => l.StartsWith("LIVE saving: opened NewWorld from", StringComparison.Ordinal) && l.Contains("_new-"));
        Assert.Contains(log, l => l.Contains(@"saving: record ") && l.Contains(@"\saves\lattice-saving\lattice-saving.save") && l.EndsWith("heads 1@1", StringComparison.Ordinal));
        Assert.Contains(log, l => l.Contains(@"saving: record ") && l.Contains(@"\autosave\") && l.EndsWith("heads 1@2", StringComparison.Ordinal));
    }

    [SaveFact("saving-head-place.log")]
    public void Monday_shows_its_plan_and_a_ghost_after_it_takes_a_new_id()
    {
        var log = Lines("saving-head-place.log");
        Assert.Contains(log, l => l.StartsWith("LIVE saving: opened Record from", StringComparison.Ordinal) && l.Contains(@"\lattice\lattice-saving"));
        Assert.Contains("LIVE saving: ghosts at x 1010", log);
        Assert.Contains("LIVE saving: placed ghost 3 at x 1050", log);
    }

    [SaveFact("saving-auto.log")]
    public void Tuesday_shows_its_plan()
    {
        var log = Lines("saving-auto.log");
        Assert.Contains("LIVE saving: ghosts at x 1010,1030", log);
        Assert.DoesNotContain(log, l => l.Contains("saving: offer"));
    }

    [SaveFact("saving-head.log")]
    public void Monday_again_shows_its_plan_and_offers_the_unsaved_change()
    {
        var log = Lines("saving-head.log");
        Assert.Contains("LIVE saving: ghosts at x 1010", log);
        Assert.Contains("LIVE saving: offer plan 1 changes 1 from line 1 to 3", log);
    }

    private static string[] Lines(string file) => File.ReadAllLines(Path.Combine(Fixtures.Generated, file));
}
