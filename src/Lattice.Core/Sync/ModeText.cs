using Lattice.Api;

namespace Lattice.Core.Sync;

/// <summary>The written words of builder modes and mode access, as commands and builder lines carry them.</summary>
internal static class ModeText
{
    /// <summary><c>on</c>, <c>off</c> or <c>batch</c>; null for a mode this version does not know.</summary>
    public static string? Of(BuildMode? mode) => mode switch
    {
        BuildMode.On => "on",
        BuildMode.Off => "off",
        BuildMode.Batch => "batch",
        _ => null,
    };

    public static Result<BuildMode, string> Read(string text) => text switch
    {
        "on" => Result<BuildMode, string>.Ok(new BuildMode.On()),
        "off" => Result<BuildMode, string>.Ok(new BuildMode.Off()),
        "batch" => Result<BuildMode, string>.Ok(new BuildMode.Batch()),
        _ => Result<BuildMode, string>.Error("bad mode " + text),
    };

    /// <summary><c>none</c>, <c>owner-and-host</c> or <c>anyone</c>; null for an access this version does not know.</summary>
    public static string? Of(BuildModeAccess? access) => access switch
    {
        BuildModeAccess.None => "none",
        BuildModeAccess.OwnerAndHost => "owner-and-host",
        BuildModeAccess.Anyone => "anyone",
        _ => null,
    };

    public static Result<BuildModeAccess, string> Access(string text) => text switch
    {
        "none" => Result<BuildModeAccess, string>.Ok(new BuildModeAccess.None()),
        "owner-and-host" => Result<BuildModeAccess, string>.Ok(new BuildModeAccess.OwnerAndHost()),
        "anyone" => Result<BuildModeAccess, string>.Ok(new BuildModeAccess.Anyone()),
        _ => Result<BuildModeAccess, string>.Error("bad access " + text),
    };
}
