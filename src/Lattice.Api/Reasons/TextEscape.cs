using System.Text;

namespace Lattice.Api;

/// <summary>
/// The written forms' escaping, shared with <see cref="Reason.ToString"/>: only what would break a line of
/// space-separated <c>key=value</c> tokens is written as <c>%XX</c> (space, '%' and control characters; in keys also
/// '=' and '#'), so text stays readable.
/// </summary>
internal static class TextEscape
{
    public static string Escape(string text, bool key)
    {
        var plain = true;
        foreach (var c in text)
            if (MustEscape(c, key))
            {
                plain = false;
                break;
            }

        if (plain)
            return text;
        var escaped = new StringBuilder(text.Length + 8);
        foreach (var c in text)
            if (MustEscape(c, key))
                escaped.Append('%').Append(((int)c).ToString("X2"));
            else
                escaped.Append(c);
        return escaped.ToString();
    }

    private static bool MustEscape(char c, bool key) =>
        c < ' ' || c == '\u007f' || c == ' ' || c == '%' || (key && (c == '=' || c == '#'));
}
