using System.Linq;
using Lattice.Api;
using Lattice.Core.Written;
using Xunit;
using static Lattice.Tests.Written.RoundTripTests;

namespace Lattice.Tests.Written;

/// <summary>Torn and damaged lines, and lines and records from a newer schema.</summary>
public sealed class DamageAndSchemaTests
{
    private static string File(params string[] lines) =>
        PlanFileForm.Header(WrittenForms.Schema, ValueList<Field>.Empty) + string.Concat(lines.Select(l => l + "\n"));

    private static string Line(long number) =>
        ChangeLineForm.Write(Samples.TypicalLine() with
        {
            Number = new LineNumber(number),
            Follows = number == 1 ? Option<LineNumber>.None : Option.Some(new LineNumber(number - 1)),
        });

    [Fact]
    public void A_torn_last_line_is_dropped_and_reported()
    {
        var whole = File(Line(1), Line(2));
        var torn = whole + Line(3).Substring(0, 40);

        var read = Ok(PlanFileForm.Read(torn));

        Assert.Equal(2, read.File.Lines.Count);
        var problem = Assert.IsType<ReadProblem.TornLastLine>(Assert.Single(read.Problems));
        Assert.Equal(4, problem.At);
    }

    [Fact]
    public void A_complete_last_line_without_its_line_break_is_still_torn()
    {
        var read = Ok(PlanFileForm.Read(File(Line(1)) + Line(2)));

        Assert.Single(read.File.Lines);
        Assert.IsType<ReadProblem.TornLastLine>(Assert.Single(read.Problems));
    }

    [Fact]
    public void A_bad_checksum_is_reported_and_the_line_not_trusted()
    {
        var damaged = Line(2).Replace("@10,20,30", "@10,20,31");

        var read = Ok(PlanFileForm.Read(File(Line(1), damaged, Line(3))));

        Assert.Equal(new[] { 1L, 3L }, read.File.Lines.Select(l => ((FileLine.Read)l).Line.Number.Value));
        var problem = Assert.IsType<ReadProblem.BadChecksum>(Assert.Single(read.Problems));
        Assert.Equal(3, problem.At);
        Assert.Equal(damaged, problem.Text);
    }

    [Fact]
    public void A_line_without_a_checksum_is_not_trusted()
    {
        var line = Line(1);
        var read = Ok(PlanFileForm.Read(File(line.Substring(0, line.LastIndexOf(" #", System.StringComparison.Ordinal)))));

        Assert.Empty(read.File.Lines);
        Assert.IsType<ReadProblem.BadChecksum>(Assert.Single(read.Problems));
    }

    [Fact]
    public void A_line_from_a_newer_schema_survives_read_and_write_unchanged()
    {
        var typical = ChangeLineForm.Write(Samples.TypicalLine());
        var content = typical.Substring(0, typical.LastIndexOf(" #", System.StringComparison.Ordinal));
        content = content.Replace(" rev=120 ", " rev=120 shard=north ")
                  + " data.builder/x=1 stop-at=2 priority=high"
                  + " pin ghost=12 until=2027-01-01T00:00:00Z"
                  + " add ghost=30 plan=3 grow=-1234567@1,2,3 made=planner/run-tool";
        var newer = content + " #" + LineChecksum.Of(content);

        var read = Ok(PlanFileForm.Read(File(newer)));

        Assert.Empty(read.Problems);
        var line = ((FileLine.Read)Assert.Single(read.File.Lines)).Line;
        Assert.Equal("shard", Assert.Single(line.Extra).Key);
        var known = Assert.IsType<GhostRecord.Known>(((ChangeOp.AddGhost)line.Ops[0]).Ghost);
        Assert.Equal(new[] { "stop-at", "priority" }, known.Extra.Select(f => f.Key));
        Assert.Equal("pin", Assert.IsType<ChangeOp.Opaque>(line.Ops[1]).Word);
        Assert.IsType<GhostRecord.Opaque>(((ChangeOp.AddGhost)line.Ops[2]).Ghost);
        Assert.Equal(newer, ChangeLineForm.Write(line));
        Assert.Equal(File(newer), PlanFileForm.Write(read.File));
    }

    [Fact]
    public void A_newer_line_whose_header_cannot_be_read_is_kept_byte_for_byte()
    {
        var content = "5 4 b_8ZaIaLEdG0LQDPT8lk_w robot:7 2026-10-19T08:30:00Z set=88 rev=120 add ghost=12";
        var newer = content + " #" + LineChecksum.Of(content);

        var read = Ok(PlanFileForm.Read(File(Line(1), newer)));

        Assert.IsType<FileLine.Kept>(read.File.Lines[1]);
        Assert.IsType<ReadProblem.Unreadable>(Assert.Single(read.Problems));
        Assert.Equal(File(Line(1), newer), PlanFileForm.Write(read.File));
    }

    [Fact]
    public void A_newer_header_and_newer_records_in_a_join_survive_read_and_write()
    {
        var text = JoinForm.Write(Join());
        var lines = text.Split('\n').ToList();
        lines[0] += " world=mars";
        lines.Insert(lines.Count - 1, "rocket=4 name=Lander");
        var newer = string.Join("\n", lines);

        var (join, problems) = Ok(JoinForm.Read(newer));

        Assert.Empty(problems);
        Assert.Equal("world", Assert.Single(join.Extra).Key);
        Assert.Single(join.Others);
        Assert.Equal(newer, JoinForm.Write(join));
    }

    [Fact]
    public void A_ghost_of_a_newer_intent_kind_is_kept_whole_and_written_back()
    {
        var fields = Ok(TextForm.Read("ghost=40 plan=3 grow=7@1,2,3 made=planner/run-tool by=player:1 at=2026-10-19T08:30:00Z rev=3 shape=dome", [])).Fields;

        var record = GhostForm.Read(fields, LineContext.None);

        Assert.Equal("unknown intent", Assert.IsType<GhostRecord.Opaque>(record).Problem);
        Assert.Equal(fields, GhostForm.Write(record, LineContext.None));
    }

    [Fact]
    public void A_document_of_another_form_is_refused_with_a_reason()
    {
        var refused = PlanFileForm.Read(JoinForm.Write(Join()));

        Assert.True(refused.TryGetError(out var reason));
        Assert.Equal(ReasonCodes.InvalidValue, reason.Code);
    }
}
