using Assets.Scripts.UI;
using TMPro;

namespace Lattice.Game.Looks;

/// <summary>The game's text, for Lattice's labels to match it.</summary>
internal static class GameText
{
    /// <summary>The font the game's own messages use (decompile Assets.Scripts.UI/AlertPanel.cs:22, <c>Message</c>); none before the panel exists.</summary>
    public static TMP_FontAsset? Font()
    {
        var panel = AlertPanel.Instance;
        return panel != null && panel.Message != null && panel.Message.font != null ? panel.Message.font : null;
    }
}
