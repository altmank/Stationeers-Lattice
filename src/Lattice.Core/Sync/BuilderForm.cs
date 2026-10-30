using System.Collections.Generic;
using System.Globalization;
using Lattice.Api;
using Lattice.Core.Executors;
using Lattice.Core.Written;

namespace Lattice.Core.Sync;

/// <summary>One builder line of a status message: what a builder says, or that it went.</summary>
internal abstract record BuilderLine
{
    private BuilderLine() { }

    /// <summary>The builder's whole word, its supply stamped on the reader's clock.</summary>
    public sealed record Says(BuilderSays Word) : BuilderLine;

    /// <summary>It went: its handle was disposed.</summary>
    public sealed record Gone(ExecutorId Id) : BuilderLine;
}

/// <summary>
/// The fields of a builder line (<see cref="StatusForm"/>): <c>id name</c>, <c>owner</c> when it has one,
/// <c>builds=any</c> or <c>builds=kits</c> with one <c>kit</c> per kit, or <c>builds=families</c> with one
/// <c>family</c> each; <c>area=sphere at=x,y,z reach= range=</c> (range left out for none); <c>word=ready</c>, or
/// <c>word=stuck</c> with its reason under <c>stuck</c>; with a supply, <c>supply-age</c> (game seconds since the host
/// took it, so the reader stamps it on its own clock), <c>reaches=item:count;...</c> and one <c>toward=plan|item:count;...</c>
/// per plan named; one <c>accepted=plan</c> per plan it took on and one <c>refused=plan</c> per plan it refused, with
/// the reason under <c>refused-&lt;plan&gt;</c>. Each line is the builder's whole word, so a later line wins.
/// </summary>
internal static class BuilderForm
{
    public static void Write(FieldList fields, BuilderSays says, GameTime now)
    {
        var info = says.Info;
        fields.Add("id", info.Id.Value).Add("name", info.Name.Value);
        if (info.Owner.TryGet(out var owner))
            fields.Add("owner", WrittenValues.Actor(owner));
        switch (info.Builds)
        {
            case BuildScope.Kits kits:
                fields.Add("builds", "kits");
                foreach (var kit in Sorted(kits.Of, static k => k.Hash))
                    fields.Add("kit", WrittenValues.Number(kit.Hash));
                break;
            case BuildScope.Families families:
                fields.Add("builds", "families");
                var names = new List<string>(families.Of.Count);
                foreach (var family in families.Of)
                    names.Add(family.Value);
                names.Sort(string.CompareOrdinal);
                foreach (var name in names)
                    fields.Add("family", name);
                break;
            default:
                fields.Add("builds", "any");
                break;
        }

        if (says.Area.TryGet(out var area) && area is BuilderArea.Sphere sphere)
        {
            fields.Add("area", "sphere")
                .Add("at", Float(sphere.At.X) + "," + Float(sphere.At.Y) + "," + Float(sphere.At.Z))
                .Add("reach", Float(sphere.BuildReach.Value));
            if (sphere.SourceRange.TryGet(out var range))
                fields.Add("range", Float(range.Value));
        }

        if (says.Word is BuilderWord.Stuck stuck)
        {
            fields.Add("word", "stuck");
            ReasonForm.Write(fields, "stuck", stuck.Why);
        }
        else
            fields.Add("word", "ready");

        if (says.Supply.TryGet(out var supply))
        {
            fields.Add("supply-age", WrittenValues.Double(System.Math.Max(0, System.Math.Round(now.Seconds - supply.At.Seconds, 2))))
                .Add("reaches", Items(supply.Report.Reaches));
            foreach (var plan in Sorted(supply.Report.Toward.Keys, static p => p.Value))
                fields.Add("toward", WrittenValues.Number(plan.Value) + "|" + Items(supply.Report.Toward[plan]));
        }

        foreach (var plan in Sorted(says.Answers.Keys, static p => p.Value))
            if (says.Answers[plan] is AssignmentAnswer.Refused refused)
            {
                fields.Add("refused", WrittenValues.Number(plan.Value));
                ReasonForm.Write(fields, "refused-" + WrittenValues.Number(plan.Value), refused.Why);
            }
            else
                fields.Add("accepted", WrittenValues.Number(plan.Value));
    }

    public static Result<BuilderSays, string> Read(FieldReader read, GameTime now)
    {
        var problem = "";
        if (!Common.Get(read.Need("id").Bind(static t => WrittenValues.Checked(ExecutorId.Parse(t), "executor", t)), out var id, ref problem) ||
            !Common.Get(read.Need("name").Bind(static t => WrittenValues.Checked(DisplayName.Create(t), "name", t)), out var name, ref problem) ||
            !Common.Get(Common.Optional(read.Take("owner"), WrittenValues.Actor), out var owner, ref problem) ||
            !Common.Get(Scope(read), out var builds, ref problem) ||
            !Common.Get(Area(read), out var area, ref problem) ||
            !Common.Get(Word(read), out var word, ref problem) ||
            !Common.Get(Supply(read, now), out var supply, ref problem) ||
            !Common.Get(Answers(read), out var answers, ref problem))
            return Result<BuilderSays, string>.Error(problem);
        var info = new ExecutorInfo(id, name) { Owner = owner, Builds = builds };
        return Result<BuilderSays, string>.Ok(new BuilderSays(info, area, word, supply, answers));
    }

    private static Result<BuildScope, string> Scope(FieldReader read)
    {
        switch (read.Take("builds").Reduce(""))
        {
            case "any":
                return Result<BuildScope, string>.Ok(new BuildScope.Any());
            case "kits":
                var kits = new List<KitKey>();
                foreach (var text in read.TakeAll("kit"))
                {
                    if (!WrittenValues.Int(text).TryGet(out var hash))
                        return Result<BuildScope, string>.Error("bad kit");
                    kits.Add(new KitKey(hash));
                }

                return Result<BuildScope, string>.Ok(new BuildScope.Kits(ValueSet.From(kits)));
            case "families":
                var families = new List<PieceFamily>();
                foreach (var text in read.TakeAll("family"))
                {
                    if (!PieceFamily.Parse(text).TryGet(out var family))
                        return Result<BuildScope, string>.Error("bad family");
                    families.Add(family);
                }

                return Result<BuildScope, string>.Ok(new BuildScope.Families(ValueSet.From(families)));
            default:
                return Result<BuildScope, string>.Error("bad builds");
        }
    }

    private static Result<Option<BuilderArea>, string> Area(FieldReader read)
    {
        if (!read.Take("area").TryGet(out var kind))
            return Result<Option<BuilderArea>, string>.Ok(Option<BuilderArea>.None);
        if (kind != "sphere" || !read.Take("at").TryGet(out var at) || !read.Take("reach").TryGet(out var reachText))
            return Result<Option<BuilderArea>, string>.Error("bad area");
        var parts = at.Split(',');
        if (parts.Length != 3 || !Float(parts[0], out var x) || !Float(parts[1], out var y) || !Float(parts[2], out var z) ||
            !Float(reachText, out var reachValue) || !Metres.Create(reachValue).TryGet(out var reach))
            return Result<Option<BuilderArea>, string>.Error("bad area");
        var range = Option<Metres>.None;
        if (read.Take("range").TryGet(out var rangeText))
        {
            if (!Float(rangeText, out var rangeValue) || !Metres.Create(rangeValue).TryGet(out var metres))
                return Result<Option<BuilderArea>, string>.Error("bad range");
            range = Option.Some(metres);
        }

        return Result<Option<BuilderArea>, string>.Ok(Option.Some<BuilderArea>(new BuilderArea.Sphere(new WorldPoint(x, y, z), reach, range)));
    }

    private static Result<BuilderWord, string> Word(FieldReader read) => read.Take("word").Reduce("") switch
    {
        "ready" => Result<BuilderWord, string>.Ok(new BuilderWord.Ready()),
        "stuck" => ReasonForm.Read(read, "stuck").Map(static why => (BuilderWord)new BuilderWord.Stuck(why)),
        _ => Result<BuilderWord, string>.Error("bad word"),
    };

    private static Result<Option<Supply>, string> Supply(FieldReader read, GameTime now)
    {
        if (!read.Take("supply-age").TryGet(out var ageText))
            return Result<Option<Supply>, string>.Ok(Option<Supply>.None);
        if (!WrittenValues.Double(ageText).TryGet(out var age) || age < 0 || double.IsNaN(age) || double.IsInfinity(age) ||
            !read.Take("reaches").TryGet(out var reachesText) || !Items(reachesText).TryGet(out var reaches))
            return Result<Option<Supply>, string>.Error("bad supply");
        var toward = new Dictionary<PlanId, ValueList<ItemQuantity>>();
        foreach (var text in read.TakeAll("toward"))
        {
            var bar = text.IndexOf('|');
            if (bar < 1 || !WrittenValues.Long(text.Substring(0, bar)).TryGet(out var plan) || !Items(text.Substring(bar + 1)).TryGet(out var items))
                return Result<Option<Supply>, string>.Error("bad toward");
            toward[new PlanId(plan)] = items;
        }

        var report = new SupplyReport(reaches, ValueMap<PlanId, ValueList<ItemQuantity>>.From(toward));
        return Result<Option<Supply>, string>.Ok(Option.Some(new Supply(report, new GameTime(now.Seconds - age))));
    }

    private static Result<ValueMap<PlanId, AssignmentAnswer>, string> Answers(FieldReader read)
    {
        var answers = new Dictionary<PlanId, AssignmentAnswer>();
        foreach (var text in read.TakeAll("accepted"))
        {
            if (!WrittenValues.Long(text).TryGet(out var plan))
                return Result<ValueMap<PlanId, AssignmentAnswer>, string>.Error("bad accepted");
            answers[new PlanId(plan)] = new AssignmentAnswer.Accepted();
        }

        foreach (var text in read.TakeAll("refused"))
        {
            if (!WrittenValues.Long(text).TryGet(out var plan) || !ReasonForm.Read(read, "refused-" + text).TryGet(out var why))
                return Result<ValueMap<PlanId, AssignmentAnswer>, string>.Error("bad refused");
            answers[new PlanId(plan)] = new AssignmentAnswer.Refused(why);
        }

        return Result<ValueMap<PlanId, AssignmentAnswer>, string>.Ok(ValueMap<PlanId, AssignmentAnswer>.From(answers));
    }

    private static string Items(ValueList<ItemQuantity> items)
    {
        var text = new System.Text.StringBuilder(items.Count * 12);
        foreach (var item in items)
        {
            if (text.Length > 0)
                text.Append(';');
            text.Append(WrittenValues.Number(item.Item.Hash)).Append(':').Append(WrittenValues.Number(item.Count));
        }

        return text.ToString();
    }

    private static Result<ValueList<ItemQuantity>, string> Items(string text)
    {
        if (text.Length == 0)
            return Result<ValueList<ItemQuantity>, string>.Ok(ValueList<ItemQuantity>.Empty);
        var parts = text.Split(';');
        var items = new ItemQuantity[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            var colon = parts[i].IndexOf(':');
            if (colon < 1 || !WrittenValues.Int(parts[i].Substring(0, colon)).TryGet(out var hash) || !WrittenValues.Int(parts[i].Substring(colon + 1)).TryGet(out var count) ||
                !ItemQuantity.Create(new ItemKey(hash), count).TryGet(out var item))
                return Result<ValueList<ItemQuantity>, string>.Error("bad item");
            items[i] = item;
        }

        return Result<ValueList<ItemQuantity>, string>.Ok(ValueList.From(items));
    }

    private static string Float(float value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static bool Float(string text, out float value) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && !float.IsNaN(value) && !float.IsInfinity(value);

    private static List<T> Sorted<T, TKey>(IEnumerable<T> items, System.Func<T, TKey> key)
    {
        var sorted = new List<T>(items);
        var comparer = Comparer<TKey>.Default;
        sorted.Sort((a, b) => comparer.Compare(key(a), key(b)));
        return sorted;
    }
}
