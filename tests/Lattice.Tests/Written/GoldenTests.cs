using System;
using System.IO;
using System.Runtime.CompilerServices;
using Lattice.Api;
using Lattice.Core.Written;
using Xunit;
using static Lattice.Tests.Written.RoundTripTests;

namespace Lattice.Tests.Written;

/// <summary>
/// Each document written from the samples equals its golden file, and each golden file reads back and writes out
/// unchanged. Set LATTICE_UPDATE_GOLDEN=1 to rewrite the files after a deliberate change of form, then review the diff.
/// </summary>
public sealed class GoldenTests
{
    private static PlanFile File() => new(WrittenForms.Schema, ValueList.From(new FileLine[]
    {
        new FileLine.Read(Samples.EveryOpLine()),
        new FileLine.Read(Samples.TypicalLine() with { Number = new LineNumber(2), Follows = Option.Some(new LineNumber(1)) }),
        new FileLine.Read(Samples.TypicalLine() with { Number = new LineNumber(3), Follows = Option.Some(new LineNumber(1)) }),
    }));

    private static ValueList<ChangeLine> Live() => ValueList.Of(Samples.TypicalLine());

    [Fact]
    public void Plan_file() => Golden("plan-file.lplan", PlanFileForm.Write(File()), text => PlanFileForm.Write(Ok(PlanFileForm.Read(text)).File));

    [Fact]
    public void Plan_file_json() => Golden("plan-file.json", PlanFileForm.WriteJson(File()), text => PlanFileForm.WriteJson(Ok(PlanFileForm.ReadJson(text))));

    [Fact]
    public void Live_message() => Golden("live.lines", LiveMessageForm.Write(Live()), text => LiveMessageForm.Write(Lines(LiveMessageForm.Read(text).Lines)));

    [Fact]
    public void Live_message_json() => Golden("live.json", LiveMessageForm.WriteJson(Live()), text => LiveMessageForm.WriteJson(Lines(Ok(LiveMessageForm.ReadJson(text)))));

    [Fact]
    public void Join() => Golden("join.ljoin", JoinForm.Write(RoundTripTests.Join()), text => JoinForm.Write(Ok(JoinForm.Read(text)).Join));

    [Fact]
    public void Join_json() => Golden("join.json", JoinForm.WriteJson(RoundTripTests.Join()), text => JoinForm.WriteJson(Ok(JoinForm.ReadJson(text))));

    [Fact]
    public void Template() => Golden("template.ltemplate", TemplateForm.Write(RoundTripTests.Template()), text => TemplateForm.Write(Ok(TemplateForm.ReadTemplate(text)).Template));

    [Fact]
    public void Template_json() => Golden("template.json", TemplateForm.WriteJson(RoundTripTests.Template()), text => TemplateForm.WriteJson(Ok(TemplateForm.ReadTemplateJson(text))));

    [Fact]
    public void Clipboard() =>
        Golden("clipboard.lclip", TemplateForm.Write(new WrittenClipboard(RoundTripTests.Template().Ghosts)), text => TemplateForm.Write(Ok(TemplateForm.ReadClipboard(text)).Clipboard));

    [Fact]
    public void Clipboard_json() =>
        Golden("clipboard.json", TemplateForm.WriteJson(new WrittenClipboard(RoundTripTests.Template().Ghosts)), text => TemplateForm.WriteJson(Ok(TemplateForm.ReadClipboardJson(text))));

    private static ValueList<ChangeLine> Lines(ValueList<FileLine> lines)
    {
        var read = new System.Collections.Generic.List<ChangeLine>();
        foreach (var line in lines)
            read.Add(((FileLine.Read)line).Line);
        return ValueList.From(read);
    }

    private static void Golden(string name, string written, Func<string, string> rewrite, [CallerFilePath] string here = "")
    {
        var path = Path.Combine(Path.GetDirectoryName(here)!, "Golden", name);
        if (Environment.GetEnvironmentVariable("LATTICE_UPDATE_GOLDEN") == "1")
            System.IO.File.WriteAllText(path, written);
        Assert.True(System.IO.File.Exists(path), $"{path} is missing; run with LATTICE_UPDATE_GOLDEN=1 to write it");
        var golden = System.IO.File.ReadAllText(path);

        Assert.Equal(golden, written);
        Assert.Equal(golden, rewrite(golden));
    }
}
