using System;
using System.Collections.Generic;
using System.IO;
using Lattice.Api;
using Lattice.Core.Ports;
using Lattice.Core.Written;

namespace Lattice.Core.Saving;

/// <summary>
/// The inspector's read of the world's plan files (<see cref="IPlanFiles"/>) over Saving: every plan with a file,
/// active or archived; each file as the plan file form reads it; the line each open plan is at; the saves whose record
/// remembers a line of it. A save is named by its path in the world's save folder without the extension
/// (<c>Moon</c>, <c>autosave/241026_073220_auto</c>). Main thread; <see cref="Read"/> is linear in the file's lines.
/// </summary>
internal sealed class SavedPlanFiles(PlanSaving saving) : IPlanFiles
{
    public ValueList<PlanId> Plans
    {
        get
        {
            var plans = new SortedSet<long>();
            foreach (var (plan, _, _) in saving.Folder.PlanFiles())
                plans.Add(plan.Value);
            foreach (var plan in saving.Heads.Keys)
                plans.Add(plan.Value);
            var ids = new List<PlanId>(plans.Count);
            foreach (var id in plans)
                ids.Add(new PlanId(id));
            return ValueList.From(ids);
        }
    }

    public Result<PlanFileRead, Reason> Read(PlanId plan)
    {
        saving.Flush();
        var path = File.Exists(saving.Folder.PlanPath(plan)) ? saving.Folder.PlanPath(plan)
            : File.Exists(saving.Folder.ArchivedPath(plan)) ? saving.Folder.ArchivedPath(plan)
            : null;
        if (path != null)
            return PlanFileForm.Read(saving.Folder.ReadText(path));
        if (saving.Heads.ContainsKey(plan))
            return Result<PlanFileRead, Reason>.Ok(new PlanFileRead(new PlanFile(WrittenForms.Schema, ValueList<FileLine>.Empty), ValueList<ReadProblem>.Empty));
        return Result<PlanFileRead, Reason>.Error(Reason.Of(ReasonCodes.NotFound, SavingReasons.Saving, ValueMap.Of("plan", plan.Value.ToString())));
    }

    public Option<LineNumber> Head(PlanId plan) =>
        saving.Heads.TryGetValue(plan, out var head) && head > 0 && !saving.IsFinished(plan)
            ? Option.Some(new LineNumber(head))
            : Option<LineNumber>.None;

    public ValueList<SaveAt> Saves(PlanId plan)
    {
        var saves = new List<SaveAt>();
        foreach (var record in saving.Folder.Records())
            if (record.Heads.TryGetValue(plan, out var line) && line > 0 && SaveName.Create(Name(record.Save.Path)).TryGet(out var name))
                saves.Add(new SaveAt(name, new LineNumber(line)));
        saves.Sort(static (a, b) => string.CompareOrdinal(a.Save.Value, b.Save.Value));
        return ValueList.From(saves);
    }

    // The save's path in its world's save folder, without the extension, with forward slashes.
    private static string Name(string savePath)
    {
        var world = SaveFiles.WorldFolderOf(savePath);
        var full = Path.GetFullPath(savePath);
        var relative = full.StartsWith(world, StringComparison.OrdinalIgnoreCase) ? full.Substring(world.Length).TrimStart('\\', '/') : Path.GetFileName(full);
        var name = relative.EndsWith(".save", StringComparison.OrdinalIgnoreCase) ? relative.Substring(0, relative.Length - 5) : relative;
        return name.Replace('\\', '/');
    }
}
