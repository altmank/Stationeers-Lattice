using System.Collections.Generic;
using Lattice.Api;
using Lattice.Render.Labels;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Lattice.Render.Unity;

/// <summary>
/// The label pool's text objects: world-space TextMeshPro under one root, each font drawn from Lattice's copy of its
/// material at the labels' queue (the consumer's font material is never written). Main thread.
/// </summary>
internal sealed class TmpLabelSurface(Transform root, MainCamera camera, int queue) : ILabelSurface
{
    // A world-space TextMeshPro point at scale 1 is taken as 0.1 m, so this scale gives LabelDraw's metres per point;
    // the live check judges the size on screen.
    private const float PointAtScaleOne = 0.1f;

    private readonly List<TmpLabelSlot> _slots = new(LabelDraw.Pool);
    private readonly Dictionary<TMP_FontAsset, Material> _materials = new(4);

    public MainCamera Camera { get; } = camera;

    public ILabelSlot NewSlot()
    {
        var holder = new GameObject("Lattice label");
        holder.transform.SetParent(root, false);
        holder.transform.localScale = Vector3.one * (LabelDraw.MetresPerPoint / PointAtScaleOne);
        var text = holder.AddComponent<TextMeshPro>();
        text.alignment = TextAlignmentOptions.Center;
        text.enableWordWrapping = false;
        text.raycastTarget = false;
        holder.SetActive(false);
        var slot = new TmpLabelSlot(this, holder, text);
        _slots.Add(slot);
        return slot;
    }

    public bool Has(TextStyle style, char glyph) => style.Font != null && style.Font.HasCharacter(glyph, true, true);

    /// <summary>Lattice's copy of a font's material at the labels' queue, made once per font.</summary>
    public Material MaterialFor(TMP_FontAsset font)
    {
        if (!_materials.TryGetValue(font, out var material))
        {
            material = new Material(font.material) { renderQueue = queue, name = font.name + " (Lattice labels)" };
            _materials.Add(font, material);
        }

        return material;
    }

    /// <summary>Destroys the text objects and the material copies (world end).</summary>
    public void End()
    {
        foreach (var slot in _slots)
            slot.Destroy();
        _slots.Clear();
        foreach (var material in _materials.Values)
            Object.Destroy(material);
        _materials.Clear();
    }
}

/// <summary>One pooled TextMeshPro text, shown facing the camera.</summary>
internal sealed class TmpLabelSlot(TmpLabelSurface surface, GameObject holder, TextMeshPro text) : ILabelSlot
{
    private bool _shown;

    public void Text(string words) => text.text = words;

    public void Style(TextStyle style)
    {
        if (style.Font != null)
        {
            text.font = style.Font;
            text.fontSharedMaterial = surface.MaterialFor(style.Font);
        }

        text.fontSize = style.Size;
        text.color = new Color32(style.Colour.R, style.Colour.G, style.Colour.B, style.Colour.A);
    }

    public void Place(Vector3 world, float alpha)
    {
        if (!_shown)
        {
            holder.SetActive(true);
            _shown = true;
        }

        holder.transform.SetPositionAndRotation(world, surface.Camera.Rotation);
        text.alpha = alpha;
    }

    public void Hide()
    {
        if (!_shown)
            return;
        holder.SetActive(false);
        _shown = false;
    }

    public void Destroy() => Object.Destroy(holder);
}
