using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Saving;
using Lattice.Core.Slices;
using Lattice.Core.Written;
using Xunit.Abstractions;

namespace Lattice.Tests.Saving;

/// <summary>
/// A big plan's change line is one very long line; saving appends it a part at a time, each part at most
/// <see cref="PlanSaving.AppendChars"/> characters, the line break the last character of the last part. The file is the
/// one the line written at once gives, and a stop between parts leaves a torn tail the load already recovers from.
/// </summary>
public sealed class LongLineTests(ITestOutputHelper output)
{
    private const int Ghosts = 5000;

    private static readonly long HeaderBytes = PlanFileForm.Header(WrittenForms.Schema, ValueList<Field>.Empty).Length;

    [Fact]
    public void Each_step_appends_at_most_one_part_and_the_file_equals_the_line_flushed_at_once()
    {
        using var world = new SavedWorld();
        var set = PlaceMany(world);
        var plan = set.Added[0].Plan;
        var path = world.Folder.PlanPath(plan);

        var parts = 0;
        var before = 0L;
        while (world.Saving.Step() == StepOutcome.More)
        {
            var length = File.Exists(path) ? new FileInfo(path).Length : 0;
            var grew = length - Math.Max(before, Math.Min(length, HeaderBytes));
            Assert.True(grew <= PlanSaving.AppendChars, $"a step appended {grew} bytes");
            if (grew > 0)
                parts++;
            before = length;
        }

        var line = ChangeLine.For(new LineNumber(1), Option<LineNumber>.None, set, ChangeLines.Of(set).Single().Ops);
        var whole = PlanFileForm.Append(line);
        Assert.True(whole.Length > 4 * PlanSaving.AppendChars, $"{whole.Length} chars is not a long line");
        Assert.True(parts >= 5, $"{parts} parts");

        using var other = new SavedWorld();
        other.Saving.Record(set);
        other.Saving.Flush();
        Assert.Equal(File.ReadAllBytes(other.Folder.PlanPath(plan)), File.ReadAllBytes(path));
        Assert.EndsWith(whole, File.ReadAllText(path));
    }

    [Fact]
    public void Flush_after_two_parts_writes_the_rest_and_the_file_is_the_same()
    {
        using var world = new SavedWorld();
        var set = PlaceMany(world);
        var plan = set.Added[0].Plan;
        var path = world.Folder.PlanPath(plan);
        StepPastParts(world, path, 2);

        world.Saving.Flush();

        using var other = new SavedWorld();
        other.Saving.Record(set);
        other.Saving.Flush();
        Assert.Equal(File.ReadAllBytes(other.Folder.PlanPath(plan)), File.ReadAllBytes(path));
        Assert.Equal(0, world.Saving.Pending);
    }

    [Fact]
    public void A_stop_between_parts_loads_the_plan_at_its_last_whole_line()
    {
        using var world = new SavedWorld();
        world.Place(10);
        world.WriteQueued();
        var first = world.Ghosts.ToArray();
        var set = PlaceMany(world);
        var path = world.Folder.PlanPath(set.Added[0].Plan);
        StepPastParts(world, path, 2);

        var report = world.Load(Option<SaveKey>.None);

        Assert.Contains(report.Problems, p => p is FileProblem.TornLine);
        Assert.Equal(first.OrderBy(g => g.Value), world.Ghosts.OrderBy(g => g.Value));
        Assert.Equal(1, world.Saving.Heads[set.Added[0].Plan]);
    }

    [Fact]
    public void An_empty_plan_is_one_append_of_the_line_written_at_once()
    {
        using var world = new SavedWorld();
        var set = world.Apply(new Command.CreatePlan(new PlanDraft(
            PlanName.Create("Empty").TryGet(out var name) ? name : throw new InvalidOperationException(),
            Lattice.Tests.Board.TestBoard.TestMaker,
            new Visibility.Everyone(),
            ValueMap<AttachmentKey, Attachment>.Empty)));
        var plan = set.PlansOpened.Single().Id;
        var path = world.Folder.PlanPath(plan);

        Assert.Equal(StepOutcome.Done, world.Saving.Step());

        var line = ChangeLine.For(new LineNumber(1), Option<LineNumber>.None, set, ChangeLines.Of(set).Single(p => p.Plan == plan).Ops);
        Assert.Equal(PlanFileForm.Header(WrittenForms.Schema, ValueList<Field>.Empty) + PlanFileForm.Append(line), File.ReadAllText(path));
    }

    [Fact]
    public void A_second_change_set_recorded_while_the_first_is_in_parts_follows_it_and_the_load_equals_the_board()
    {
        using var world = new SavedWorld();
        var set = PlaceMany(world);
        var plan = set.Added[0].Plan;
        var path = world.Folder.PlanPath(plan);
        StepPastParts(world, path, 2);

        world.Place(7);
        world.WriteQueued();

        var expected = world.Ghosts.OrderBy(g => g.Value).ToArray();
        var report = world.Load(Option<SaveKey>.None);
        Assert.Empty(report.Problems);
        Assert.Equal(Ghosts + 1, expected.Length);
        Assert.Equal(expected, world.Ghosts.OrderBy(g => g.Value));
        Assert.Equal(2, world.Saving.Heads[plan]);
    }

    // Worst of five standalone runs under the perf lock: 1.079, 0.930, 0.903, 0.826, 0.688 ms; plus 30%, rounded up: 1.5 ms.
    [Fact]
    [Trait("Category", "Perf")]
    public void The_longest_step_over_a_big_plan_is_at_most_one_and_a_half_milliseconds()
    {
        var worst = 0.0;
        for (var run = 0; run < 2; run++)
        {
            using var world = new SavedWorld();
            PlaceMany(world);
            worst = 0;
            var n = 0;
            StepOutcome outcome;
            do
            {
                var clock = Stopwatch.StartNew();
                outcome = world.Saving.Step();
                var took = clock.Elapsed.TotalMilliseconds;
                if (took > 0.5) output.WriteLine($"run {run} step {n} {took:F3} ms");
                n++;
                worst = Math.Max(worst, took);
            }
            while (outcome == StepOutcome.More);
        }

        output.WriteLine($"{Ghosts} ghosts: longest saving step {worst:F3} ms");
        Assert.True(worst <= 1.5, $"{worst:F3} ms");
    }

    // Steps until the file holds the given number of parts past its header.
    private static void StepPastParts(SavedWorld world, string path, int parts)
    {
        var seen = 0;
        var before = File.Exists(path) ? new FileInfo(path).Length : 0;
        while (seen < parts)
        {
            Assert.Equal(StepOutcome.More, world.Saving.Step());
            var length = File.Exists(path) ? new FileInfo(path).Length : 0;
            if (length > before && before >= HeaderBytes)
                seen++;
            before = length;
        }
    }

    private static ChangeSet PlaceMany(SavedWorld world)
    {
        var drafts = Enumerable.Range(0, Ghosts)
            .Select(i => world.Board.Draft("StructureFrame", new Cell(10 + (20 * i), 10, 10)))
            .ToList();
        return world.Apply(new Command.Place(ValueList.From(drafts)));
    }
}
