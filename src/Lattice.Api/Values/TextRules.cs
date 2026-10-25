namespace Lattice.Api;

/// <summary>
/// Machine names (codes, keys, ids): 1 to 128 characters of lowercase letters and digits, in words joined by one of
/// '-', '.', '_'. A qualified name is two such names joined by '/': the owning mod, then its own name.
/// </summary>
internal static class Token
{
    public const int MaxLength = 128;

    public static Result<string, Reason> Check(string? value, string type) => Check(value, type, allowQualified: false);

    public static Result<string, Reason> CheckQualified(string? value, string type) => Check(value, type, allowQualified: true);

    public static Result<string, Reason> Qualify(ModId owner, string? name, string type) =>
        Check(name, type).Map(n => owner.Value + "/" + n);

    private static Result<string, Reason> Check(string? value, string type, bool allowQualified)
    {
        if (value is null || value.Length == 0)
            return Fail(type, value, "empty");
        if (value.Length > MaxLength)
            return Fail(type, value, "too-long");

        var previousWasSeparator = true;
        var slashes = 0;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            var isWordChar = c is >= 'a' and <= 'z' or >= '0' and <= '9';
            var isSlash = c == '/';
            var isSeparator = isSlash || c is '-' or '.' or '_';
            if (!isWordChar && !isSeparator)
                return Fail(type, value, "lowercase-words");
            if (isSeparator && previousWasSeparator)
                return Fail(type, value, "lowercase-words");
            if (isSlash && (!allowQualified || ++slashes > 1))
                return Fail(type, value, allowQualified ? "one-owner" : "no-owner");
            previousWasSeparator = isSeparator;
        }

        return previousWasSeparator
            ? Fail(type, value, "lowercase-words")
            : Result<string, Reason>.Ok(value);
    }

    private static Result<string, Reason> Fail(string type, string? value, string rule) =>
        Result<string, Reason>.Error(Reason.InvalidValue(type, value ?? "", rule));
}

/// <summary>
/// Names people read and type (plan names, labels, groups): 1 to 256 characters, not only spaces, no control
/// characters (written forms are line based). Kept as typed; never trimmed.
/// </summary>
internal static class Text
{
    public const int MaxLength = 256;

    public static Result<string, Reason> Check(string? value, string type)
    {
        if (value is null || value.Length == 0)
            return Fail(type, value, "empty");
        if (value.Length > MaxLength)
            return Fail(type, value, "too-long");

        var allSpace = true;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsControl(c))
                return Fail(type, value, "control-character");
            if (!char.IsWhiteSpace(c))
                allSpace = false;
        }

        return allSpace ? Fail(type, value, "blank") : Result<string, Reason>.Ok(value);
    }

    private static Result<string, Reason> Fail(string type, string? value, string rule) =>
        Result<string, Reason>.Error(Reason.InvalidValue(type, value ?? "", rule));
}
