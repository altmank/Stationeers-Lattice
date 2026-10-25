using System.Collections.Generic;
using Lattice.Api;
using static Lattice.Core.Styles.Library.LookValues;

namespace Lattice.Core.Styles.Library;

/// <summary>
/// Lattice's starter library: the Library band's looks, the three library
/// palettes, the <c>blueprint</c> example theme and the opt-in label layers. Pure data, made once per world.
/// Fill carries the ghost's tint when it has one; state lives on edge, pattern, effect and icon, so every state differs
/// from every other on a channel besides hue. Looks match facets only (state, finding, intent, part, status, sets),
/// never a piece, so modded pieces get the same looks as the game's.
/// </summary>
internal sealed class StarterLibrary
{
    /// <summary>Where a label shows from, in metres.</summary>
    public const float LabelRange = 30f;

    /// <summary>Makes the library's looks, palettes and layers.</summary>
    public StarterLibrary()
    {
        Looks = MakeLooks();
        var rules = new List<StyleRule>(Looks.Count);
        foreach (var look in Looks)
            rules.Add(look.Rule);
        Rules = ValueList.From(rules);
        Default = LibraryColours.Default();
        ColourSafe = LibraryColours.ColourSafe();
        HighContrast = LibraryColours.HighContrast();
        Palettes = ValueList.From(new[] { Default, ColourSafe, HighContrast });
        Blueprint = LibraryLayers.Blueprint;
        LabelLayers = ValueList.From(new[] { LibraryLayers.StepNumbers, LibraryLayers.BillCounts, LibraryLayers.Reasons, LibraryLayers.PlanNames });
    }

    /// <summary>The Library band's looks, in cascade order: for each property the last look whose match holds wins.</summary>
    public ValueList<LibraryLook> Looks { get; }

    /// <summary>The looks as the Library band's rules, in the same order; a rule's position is its <see cref="StyleSource.Library"/> index.</summary>
    public ValueList<StyleRule> Rules { get; }

    /// <summary>The default palette: the game construction cursor's colours.</summary>
    public Palette Default { get; }

    /// <summary>Okabe-Ito hues.</summary>
    public Palette ColourSafe { get; }

    /// <summary>Saturated colours far apart in lightness.</summary>
    public Palette HighContrast { get; }

    /// <summary>The three library palettes; each defines every <see cref="LibraryPalette"/> token and an owner ramp.</summary>
    public ValueList<Palette> Palettes { get; }

    /// <summary>The <c>blueprint</c> example theme (<see cref="LibraryLayers.Blueprint"/>).</summary>
    public StyleLayerDescriptor Blueprint { get; }

    /// <summary>The opt-in label layers (<see cref="LibraryLayers"/>): step numbers, bill counts, reasons and plan names.</summary>
    public ValueList<StyleLayerDescriptor> LabelLayers { get; }

    /// <summary>The library look of a row.</summary>
    public LibraryLook Look(LibraryRow row)
    {
        foreach (var look in Looks)
            if (look.Row == row)
                return look;
        throw new KeyNotFoundException(row.ToString());
    }

    /// <summary>The library palette with this id; none for another id.</summary>
    public Option<Palette> PaletteOf(PaletteId id)
    {
        foreach (var palette in Palettes)
            if (palette.Id == id)
                return Option.Some(palette);
        return Option<Palette>.None;
    }

    private static ValueList<LibraryLook> MakeLooks()
    {
        var planned = Token(LibraryPalette.Planned);
        var hovered = new StyleMatch.InSet(LibrarySets.Hovered);
        var existing = new StyleMatch.Part(PartKind.Existing);
        var removeLook = new Style
        {
            Fill = Option.Some(FillMode.None),
            Edge = Option.Some(Line(LineStyle.Solid, 2f, Token(LibraryPalette.Remove))),
            Pattern = Option.Some(Pattern.CrossHatch),
        };

        var looks = new List<LibraryLook>(24)
        {
            new(LibraryRow.Planned, new StyleMatch.Part(PartKind.Placement), new Style
            {
                Fill = Option.Some(FillMode.Ghost),
                FillPaint = Option.Some<Paint>(new Paint.GhostTint(LibraryPalette.Planned)),
                // The game's construction cursor mesh alpha (InventoryManager.cs:218, CursorAlphaConstructionMesh).
                FillOpacity = Option.Some(Part(0.3f)),
            }),
            new(LibraryRow.Started, new StyleMatch.State(StateKind.Started), new Style
            {
                FillOpacity = Option.Some(Part(0.45f)),
            }),
            new(LibraryRow.Remove, All(new StyleMatch.Intent(IntentKind.Remove), existing), removeLook with
            {
                Icon = Option.Some(Icon(LibraryIcons.Remove)),
            }),
            new(LibraryRow.SwapOld, All(new StyleMatch.Intent(IntentKind.Swap), existing), removeLook),
            new(LibraryRow.Finished, All(new StyleMatch.Finishes(), new StyleMatch.Part(PartKind.Placement)), new Style
            {
                Icon = Option.Some(Icon(LibraryIcons.Finish)),
            }),
            new(LibraryRow.Swap, new StyleMatch.Intent(IntentKind.Swap), new Style
            {
                Icon = Option.Some(Icon(LibraryIcons.Swap)),
            }),
            new(LibraryRow.Unchecked, All(Finding(FindingKind.Unchecked), new StyleMatch.Part(PartKind.Placement)), new Style
            {
                Edge = Option.Some(Line(LineStyle.Dotted, 1f, planned)),
            }),
            new(LibraryRow.Draft, new StyleMatch.Draft(), new Style
            {
                FillOpacity = Option.Some(Part(0.25f)),
                Edge = Option.Some(Line(LineStyle.Dashed, 1f, Token(LibraryPalette.Draft))),
            }),
            new(LibraryRow.Next, Finding(FindingKind.Passes), new Style
            {
                Edge = Option.Some(Line(LineStyle.Solid, 2f, Token(LibraryPalette.Next))),
            }),
            // After Next: a passing finish-only ghost shows its paint, and its solid edge still tells it is ready
            // (unchecked is dotted, waits dashed); waits, fails and warns come later and win over it.
            new(LibraryRow.Finish, new StyleMatch.Intent(IntentKind.FinishOnly), new Style
            {
                Fill = Option.Some(FillMode.None),
                Edge = Option.Some(Line(LineStyle.Solid, 2f, new Paint.FinishColour(LibraryPalette.Planned))),
                Icon = Option.Some(Icon(LibraryIcons.Finish)),
            }),
            new(LibraryRow.Waits, Finding(FindingKind.WaitsFor), new Style
            {
                FillOpacity = Option.Some(Part(0.2f)),
                Edge = Option.Some(Line(LineStyle.Dashed, 2f, Token(LibraryPalette.Waits))),
            }),
            new(LibraryRow.WaitsHovered, All(Finding(FindingKind.WaitsFor), hovered), new Style
            {
                WaitsLinks = Option.Some(ShowMode.Shown),
            }),
            new(LibraryRow.Fails, Finding(FindingKind.WillFail), new Style
            {
                Edge = Option.Some(Line(LineStyle.Solid, 2f, Token(LibraryPalette.Fail))),
                Pattern = Option.Some(Pattern.Hatch),
                Effect = Option.Some<EffectLook>(new EffectLook.Pulse(Every(1.5f), Part(0.4f))),
                Enter = Option.Some<EnterLook>(new EnterLook.Flash(Times(3), Every(0.2f))),
                Icon = Option.Some(Icon(LibraryIcons.Fails)),
            }),
            new(LibraryRow.FailsHovered, All(Finding(FindingKind.WillFail), hovered), new Style
            {
                Label = Option.Some(Label(new LabelText.FindingReason(), 100)),
            }),
            new(LibraryRow.Warns, Finding(FindingKind.Warns), new Style
            {
                Edge = Option.Some(Line(LineStyle.Solid, 2f, Token(LibraryPalette.Warn))),
                Pattern = Option.Some(Pattern.Dots),
                Icon = Option.Some(Icon(LibraryIcons.Warns)),
            }),
            new(LibraryRow.Stale, new StyleMatch.State(StateKind.Stale), new Style
            {
                FillPaint = Option.Some(Token(LibraryPalette.Stale)),
                Edge = Option.Some(Line(LineStyle.Dashed, 1f, Token(LibraryPalette.Stale))),
                Icon = Option.Some(Icon(LibraryIcons.Stale)),
            }),
            new(LibraryRow.Pending, new StyleMatch.Pending(), new Style
            {
                FillOpacity = Option.Some(Part(0.12f)),
                Edge = Option.Some<EdgeLook>(new EdgeLook.None()),
            }),
            new(LibraryRow.Working, Status(StatusKind.Working), new Style
            {
                Glow = Option.Some<GlowLook>(new GlowLook.Lit(Token(LibraryPalette.Next), Part(0.6f))),
                Effect = Option.Some<EffectLook>(new EffectLook.Pulse(Every(2f), Part(0.5f))),
            }),
            new(LibraryRow.Waiting, Status(StatusKind.Waiting), new Style
            {
                Icon = Option.Some(Icon(LibraryIcons.Waiting)),
            }),
            new(LibraryRow.Paused, Status(StatusKind.Paused), new Style
            {
                Icon = Option.Some(Icon(LibraryIcons.Paused)),
            }),
            new(LibraryRow.Others, All(new StyleMatch.Not(new StyleMatch.Mine()), new StyleMatch.Not(new StyleMatch.Draft()), new StyleMatch.Not(new StyleMatch.Structure())), new Style
            {
                Dim = Option.Some(Part(0.6f)),
                Edge = Option.Some(Line(LineStyle.Solid, 1f, new Paint.OwnerColour())),
            }),
            new(LibraryRow.Selected, new StyleMatch.InSet(LibrarySets.Selected), new Style
            {
                Edge = Option.Some(Line(LineStyle.Solid, 2f, Token(LibraryPalette.Select))),
                Glow = Option.Some<GlowLook>(new GlowLook.Lit(Token(LibraryPalette.Select), Part(0.5f))),
            }),
            new(LibraryRow.Hovered, hovered, new Style
            {
                Glow = Option.Some<GlowLook>(new GlowLook.Lit(Token(LibraryPalette.Select), Part(0.8f))),
                Ease = Option.Some(Seconds(0.12f)),
            }),
            new(LibraryRow.Done, new StyleMatch.Everything(), new Style
            {
                Vanish = Option.Some(Seconds(0.4f)),
            }),
        };
        return ValueList.From(looks);
    }

    private static LabelLook Label(LabelText says, int priority) =>
        new LabelLook.Shown(says, LibraryText.Default, LabelAnchor.Above, priority, Distance(LabelRange));

    private static StyleMatch Finding(FindingKind kind) => new StyleMatch.Finding(kind, Option<ReasonCode>.None);

    private static StyleMatch Status(StatusKind kind) => new StyleMatch.Status(kind, Option<ExecutorId>.None);

    private static StyleMatch All(params StyleMatch[] matches) => new StyleMatch.All(ValueList.From(matches));
}
