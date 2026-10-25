using System.Collections.Generic;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Written;
using Lattice.TestKit;
using Xunit;
using Xunit.Abstractions;

namespace Lattice.Tests.Written;

/// <summary>Every form reads back to what was written, in text and in JSON.</summary>
public sealed class RoundTripTests(ITestOutputHelper output)
{
    public static TheoryData<int> GhostIndexes => new() { 0, 1, 2, 3, 4 };

    [Theory]
    [MemberData(nameof(GhostIndexes))]
    public void A_ghost_reads_back_from_text_and_json(int index)
    {
        var ghost = Samples.EveryGhost()[index];
        var entry = Entry.Of(GhostForm.Write(GhostRecord.Of(ghost), LineContext.None));

        var text = Ok(TextForm.Read(TextForm.Write(entry, 0), []));
        var json = Ok(JsonForm.ReadEntry(JsonForm.Write(entry)));

        Assert.Equal(GhostRecord.Of(ghost), GhostForm.Read(text.Fields, LineContext.None));
        Assert.Equal(GhostRecord.Of(ghost), GhostForm.Read(json.Fields, LineContext.None));
    }

    [Fact]
    public void A_ghost_in_a_line_leaves_out_what_the_line_says_and_reads_it_back()
    {
        var ghost = Samples.EveryGhost()[0];
        var context = new LineContext(Option.Some(ghost.PlacedBy), Option.Some(ghost.PlacedAt), Option.Some(ghost.Revision));

        var fields = GhostForm.Write(GhostRecord.Of(ghost), context);

        Assert.DoesNotContain(fields, f => f.Key is "by" or "at" or "rev");
        Assert.Equal(GhostRecord.Of(ghost), GhostForm.Read(fields, context));
    }

    [Fact]
    public void Plans_and_drafts_read_back()
    {
        foreach (var plan in Samples.EveryPlan())
            Assert.Equal(PlanRecord.Of(plan), PlanForm.Read(Reread(PlanForm.Write(PlanRecord.Of(plan), LineContext.None)), LineContext.None));
        foreach (var draft in Samples.EveryDraft())
            Assert.Equal(DraftRecord.Of(draft), DraftForm.Read(Reread(DraftForm.Write(DraftRecord.Of(draft)))));
    }

    [Fact]
    public void A_change_line_with_every_operation_reads_back_from_text_and_json()
    {
        var line = Samples.EveryOpLine();

        var text = ChangeLineForm.Write(line);
        Assert.Equal(line, Ok(ChangeLineForm.Read(text)));
        Assert.Equal(line, Ok(ChangeLineForm.FromEntry(Ok(JsonForm.ReadEntry(JsonForm.Write(ChangeLineForm.ToEntry(line)))))));
        Assert.DoesNotContain('\n', text);
    }

    [Fact]
    public void A_plan_file_reads_back_from_text_and_json()
    {
        var file = new PlanFile(WrittenForms.Schema, ValueList.From(new FileLine[]
        {
            new FileLine.Read(Samples.EveryOpLine()),
            new FileLine.Read(Samples.TypicalLine() with { Number = new LineNumber(2), Follows = Option.Some(new LineNumber(1)) }),
        }));

        var read = Ok(PlanFileForm.Read(PlanFileForm.Write(file)));

        Assert.Empty(read.Problems);
        Assert.Equal(file, read.File);
        Assert.Equal(file, Ok(PlanFileForm.ReadJson(PlanFileForm.WriteJson(file))));
    }

    [Fact]
    public void Appending_lines_one_by_one_gives_the_file_the_whole_writer_gives()
    {
        var lines = new[] { Samples.EveryOpLine(), Samples.TypicalLine() with { Number = new LineNumber(2), Follows = Option.Some(new LineNumber(1)) } };
        var file = new PlanFile(WrittenForms.Schema, ValueList.From(lines.Select(l => (FileLine)new FileLine.Read(l))));

        var appended = PlanFileForm.Header(WrittenForms.Schema, ValueList<Field>.Empty) + string.Concat(lines.Select(PlanFileForm.Append));

        Assert.Equal(PlanFileForm.Write(file), appended);
    }

    [Fact]
    public void A_live_message_is_the_lines_the_plan_file_appends()
    {
        var lines = ValueList.From(new[] { Samples.TypicalLine(), Samples.EveryOpLine() with { Number = new LineNumber(42), Follows = Option.Some(new LineNumber(41)) } });

        var message = LiveMessageForm.Write(lines);
        var read = LiveMessageForm.Read(message);

        Assert.Equal(string.Concat(lines.Select(PlanFileForm.Append)), message);
        Assert.Empty(read.Problems);
        Assert.Equal(lines, read.Lines.Select(l => ((FileLine.Read)l).Line));
        Assert.Equal(lines, Ok(LiveMessageForm.ReadJson(LiveMessageForm.WriteJson(lines))).Select(l => ((FileLine.Read)l).Line));
    }

    [Fact]
    public void A_join_snapshot_reads_back_from_text_and_json()
    {
        var join = Join();

        var (read, problems) = Ok(JoinForm.Read(JoinForm.Write(join)));

        Assert.Empty(problems);
        Assert.Equal(join, read);
        Assert.Equal(join, Ok(JoinForm.ReadJson(JoinForm.WriteJson(join))));
    }

    [Fact]
    public void A_template_and_the_clipboard_read_back_from_text_and_json()
    {
        var template = Template();
        var clipboard = new WrittenClipboard(template.Ghosts);

        Assert.Equal(template, Ok(TemplateForm.ReadTemplate(TemplateForm.Write(template))).Template);
        Assert.Equal(template, Ok(TemplateForm.ReadTemplateJson(TemplateForm.WriteJson(template))));
        Assert.Equal(clipboard, Ok(TemplateForm.ReadClipboard(TemplateForm.Write(clipboard))).Clipboard);
        Assert.Equal(clipboard, Ok(TemplateForm.ReadClipboardJson(TemplateForm.WriteJson(clipboard))));
        Assert.Equal(Samples.EveryDraft(), Ok(TemplateForm.ToTemplate(template)).Ghosts);
    }

    [Fact]
    public void Map_keys_and_groups_are_written_sorted_whatever_order_they_were_made_in()
    {
        var ghost = Samples.EveryGhost()[0];
        var one = ghost with { Attached = Samples.Data(("b", "2"), ("a", "1"), ("c", "3")), Groups = Samples.Groups("z", "a") };
        var other = ghost with { Attached = Samples.Data(("c", "3"), ("a", "1"), ("b", "2")), Groups = Samples.Groups("a", "z") };

        var written = TextForm.Write(Entry.Of(GhostForm.Write(GhostRecord.Of(one), LineContext.None)), 0);

        Assert.Equal(written, TextForm.Write(Entry.Of(GhostForm.Write(GhostRecord.Of(other), LineContext.None)), 0));
        Assert.Contains("group=a group=z data.builder/a=1 data.builder/b=2 data.builder/c=3", written);
    }

    [Fact]
    public void Text_that_would_break_a_line_is_escaped_and_the_rest_kept_as_written()
    {
        Assert.Equal("North%20store%20#2%20(100%25)", TextForm.Escape("North store #2 (100%)", key: false));
        Assert.Equal("a=b%0Ac", TextForm.Escape("a=b\nc", key: false));
        Assert.Equal("k%3Dv%23", TextForm.Escape("k=v#", key: true));
        Assert.Equal("Störung", TextForm.Escape("Störung", key: false));
        Assert.Equal("North store #2 (100%)", Ok(TextForm.Unescape("North%20store%20#2%20(100%25)")));
    }

    [Fact]
    public void A_change_set_splits_into_one_part_per_plan_and_a_moved_ghost_is_in_both()
    {
        var ghosts = Samples.EveryGhost();
        var set = new ChangeSet(
            new ChangeSetId(5), Samples.Command, Samples.Player, new Revision(9), Samples.Monday,
            ValueList.Of(ghosts[0] with { Plan = new PlanId(7) }),
            ValueList.Of(new GhostChange(ghosts[1], ghosts[1] with { Plan = new PlanId(4) })),
            ValueList.Of(new GhostRemoval(ghosts[2], new RemovalCause.Removed())),
            ValueList.Of(Samples.Plan(7)),
            ValueList<PlanChange>.Empty,
            ValueList.Of(new PlanId(4)));

        var parts = ChangeLines.Of(set);

        Assert.Equal(new[] { 3L, 4L, 7L }, parts.Select(p => p.Plan.Value));
        Assert.Equal(2, parts[0].Ops.Count);
        Assert.IsType<ChangeOp.ChangeGhost>(parts[0].Ops[0]);
        Assert.IsType<ChangeOp.RemoveGhost>(parts[0].Ops[1]);
        Assert.IsType<ChangeOp.ChangeGhost>(parts[1].Ops[0]);
        Assert.IsType<ChangeOp.FinishPlan>(parts[1].Ops[1]);
        Assert.IsType<ChangeOp.OpenPlan>(parts[2].Ops[0]);
        Assert.IsType<ChangeOp.AddGhost>(parts[2].Ops[1]);
    }

    [Fact]
    public void A_board_change_set_written_as_a_line_reads_back_to_the_same_operations()
    {
        var board = new Board.TestBoard();
        var set = board.Placed(board.Draft("StructureFrameIron", new Cell(0, 0, 0)), board.Draft("StructureFrameIron", new Cell(20, 0, 0)));

        foreach (var part in ChangeLines.Of(set))
        {
            var line = ChangeLine.For(new LineNumber(1), Option<LineNumber>.None, set, part.Ops);
            var read = Ok(ChangeLineForm.Read(ChangeLineForm.Write(line)));

            Assert.Equal(line, read);
            Assert.Equal(set.Added.Count, read.Ops.Count);
        }
    }

    [Fact]
    public void A_typical_change_line_is_small()
    {
        var text = ChangeLineForm.Write(Samples.TypicalLine());
        var bytes = System.Text.Encoding.UTF8.GetByteCount(text) + 1;
        output.WriteLine($"typical change line, {bytes} bytes: {text}");

        Assert.InRange(bytes, 50, 180);
    }

    internal static JoinSnapshot Join() =>
        new(new Revision(120), ValueList.From(Samples.EveryPlan().Select(PlanRecord.Of)), ValueList.From(Samples.EveryGhost().Select(GhostRecord.Of)));

    internal static WrittenTemplate Template() =>
        new(TestKitIds.Must(TemplateName.Create("Airlock, small")), ValueList.From(Samples.EveryDraft().Select(DraftRecord.Of)));

    internal static T Ok<T, TError>(Result<T, TError> result)
        where T : notnull
        where TError : notnull =>
        result.Match(static v => v, static e => throw new Xunit.Sdk.XunitException($"expected a value, got {e}"));

    private static ValueList<Field> Reread(ValueList<Field> fields) =>
        Ok(TextForm.Read(TextForm.Write(Entry.Of(fields), 0), [])).Fields;
}
