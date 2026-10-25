using System;
using Lattice.Api;
using Lattice.Core.Modules;

namespace Lattice.Core.Sync;

/// <summary>
/// Every game member the sync module uses, checked at load: the module is off, with the missing members named, when
/// the game lacks any. A test reads the module's compiled references and fails when one is not listed here.
/// </summary>
internal static class SyncManifest
{
    private const string Client = "Assets.Scripts.Client";
    private const string Reader = "Assets.Scripts.Networking.RocketBinaryReader";
    private const string Writer = "Assets.Scripts.Networking.RocketBinaryWriter";

    /// <summary>The sync module: its name, no other module, and every game member it uses.</summary>
    public static readonly ModuleManifest Module = new(
        ModuleName.Create("sync").TryGet(out var name) ? name : throw new InvalidOperationException("module name"),
        ValueList<ModuleName>.Empty,
        ValueList.From(new GameMember[]
        {
            new GameMember.Type(Client),
            new GameMember.Field(Client, "ClientId"),
            new GameMember.Field(Client, "connectionId"),
            new GameMember.Field(Client, "state"),
            new GameMember.Field("NetworkBase", "Clients"),
            new GameMember.Method(Client, "Find", Params("System.Int64")),
            new GameMember.Method(Reader, "ReadBytes", Params("System.Byte[]", "System.Int32")),
            new GameMember.Method(Reader, "ReadInt32", Params()),
            new GameMember.Method(Writer, "WriteBytes", Params("System.Byte[]", "System.Int32")),
            new GameMember.Method(Writer, "WriteInt32", Params("System.Int32")),
        }));

    private static ValueList<string> Params(params string[] types) => ValueList.From(types);
}
