using Lattice.Api;

namespace Lattice.Tests.Render;

internal static class RenderResults
{
    public static T Ok<T>(this Result<T, Reason> result) where T : notnull =>
        result.TryGet(out var value) ? value : throw new Xunit.Sdk.XunitException("refused: " + result.Match(static _ => "", static why => why.ToString()));

    public static Reason Refusal<T>(this Result<T, Reason> result) where T : notnull =>
        result.TryGetError(out var why) ? why : throw new Xunit.Sdk.XunitException("not refused");
}
