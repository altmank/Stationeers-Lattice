using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Assets.Scripts;
using Assets.Scripts.Networking;
using LaunchPadBooster.Networking;
using Lattice.Api;
using Lattice.Core.Sync;

namespace Lattice.Game.Sync;

/// <summary>
/// One sync message as LaunchPadBooster carries it: a UTF-8 text with its byte length in front. Reading takes the
/// length first and drops a text over <see cref="GameSync.MaxBytes"/> without reading it, so a client cannot make the
/// host hold more than that; a text cut short is dropped too. LaunchPadBooster calls <c>Deserialize</c>, then
/// <c>Process</c> with the sender's connection, on the main
/// thread, and hands the text to the world's <see cref="GameSync"/>.
/// </summary>
internal abstract class SyncMessage : INetworkMessage
{
    /// <summary>The text; empty after a read that dropped it.</summary>
    public string Text { get; set; } = "";

    /// <summary>Why the read dropped the text; none when it was read.</summary>
    public Option<Reason> Dropped { get; private set; }

    public abstract SyncKind Kind { get; }

    public void Serialize(RocketBinaryWriter writer)
    {
        var bytes = Encoding.UTF8.GetBytes(Text);
        writer.WriteInt32(bytes.Length);
        writer.WriteBytes(bytes, bytes.Length);
    }

    public void Deserialize(RocketBinaryReader reader)
    {
        try
        {
            var length = reader.ReadInt32();
            if (length < 0 || length > GameSync.MaxBytes)
            {
                Dropped = Option.Some(SyncRules.TooLarge(length, GameSync.MaxBytes));
                return;
            }

            var bytes = new byte[length];
            reader.ReadBytes(bytes, length);
            Text = Encoding.UTF8.GetString(bytes);
        }
        catch (EndOfStreamException)
        {
            Dropped = Option.Some(SyncRules.Problem("message cut short"));
        }
    }

    public void Process(long clientId) => GameSync.Current?.Received(new ConnectionId(clientId), this);
}

/// <summary>A client's command, client to host.</summary>
internal sealed class CommandMessage : SyncMessage
{
    public override SyncKind Kind => SyncKind.Command;
}

/// <summary>The lines of one change set, host to client.</summary>
internal sealed class LinesMessage : SyncMessage
{
    public override SyncKind Kind => SyncKind.Lines;
}

/// <summary>A part of the board for a joining client, host to client.</summary>
internal sealed class JoinMessage : SyncMessage
{
    public override SyncKind Kind => SyncKind.Join;
}

/// <summary>The outcome of a client's command, host to that client.</summary>
internal sealed class AnswerMessage : SyncMessage
{
    public override SyncKind Kind => SyncKind.Answer;
}

/// <summary>Executors' claims and status on some ghosts, host to client.</summary>
internal sealed class StatusMessage : SyncMessage
{
    public override SyncKind Kind => SyncKind.Status;
}

/// <summary>
/// <see cref="ISyncLink"/> over LaunchPadBooster: one message type per kind, to the host or to a client by its
/// connection (a connection the game no longer lists is skipped). Main thread.
/// </summary>
internal sealed class LaunchPadLink : ISyncLink
{
    public void ToHost(SyncKind kind, string text) => ModNetworkingExtensions.SendToHost(Message(kind, text));

    public void ToClient(ConnectionId to, SyncKind kind, string text)
    {
        var client = Client.Find(to.Value);
        if (client != null)
            ModNetworkingExtensions.SendToClient(Message(kind, text), client);
    }

    private static SyncMessage Message(SyncKind kind, string text) => kind switch
    {
        SyncKind.Command => new CommandMessage { Text = text },
        SyncKind.Lines => new LinesMessage { Text = text },
        SyncKind.Join => new JoinMessage { Text = text },
        SyncKind.Status => new StatusMessage { Text = text },
        _ => new AnswerMessage { Text = text },
    };
}

/// <summary>
/// The running world's sync in the game: the host's (<see cref="HostSync"/>) or a client's (<see cref="ClientSync"/>).
/// On the host a command's player is the game's <c>Client.ClientId</c> of the connection it came on
/// (<c>Client.Find</c>, decompile Assets.Scripts/Client.cs), never anything in the message; a client joins
/// when the game lists it Ready (the game drops mod messages to a client that is not) and
/// leaves when it is gone or Disconnected (<see cref="Poll"/>, once a frame). Set <see cref="Current"/> when a world
/// runs and clear it when it ends; register the message types once at plugin load.
/// </summary>
internal sealed class GameSync
{
    /// <summary>The largest text a message carries, in UTF-8 bytes.</summary>
    public static readonly int MaxBytes = SyncLimits.Default.MaxMessageBytes;

    private readonly Option<HostSync> _host;
    private readonly Option<ClientSync> _client;
    private readonly Action<Reason> _report;
    private readonly HashSet<long> _joined = new();
    private readonly List<long> _gone = new();

    private GameSync(Option<HostSync> host, Option<ClientSync> client, Action<Reason> report)
    {
        _host = host;
        _client = client;
        _report = report;
    }

    /// <summary>The running world's sync; set when a world runs, cleared when it ends.</summary>
    public static GameSync? Current { get; set; }

    /// <summary>Registers the five message types with Lattice's LaunchPadBooster mod.</summary>
    public static void Register(IModNetworking networking)
    {
        networking.RegisterMessage<CommandMessage>();
        networking.RegisterMessage<LinesMessage>();
        networking.RegisterMessage<JoinMessage>();
        networking.RegisterMessage<AnswerMessage>();
        networking.RegisterMessage<StatusMessage>();
    }

    public static GameSync ForHost(HostSync host, Action<Reason> report) => new(Option.Some(host), Option<ClientSync>.None, report);

    public static GameSync ForClient(ClientSync client, Action<Reason> report) => new(Option<HostSync>.None, Option.Some(client), report);

    /// <summary>On the host, once a frame: joins the clients the game lists Ready and drops those gone. Linear in the clients.</summary>
    public void Poll()
    {
        if (!_host.TryGet(out var host))
            return;
        foreach (var client in NetworkBase.Clients)
            if (client.state == ClientState.Ready && _joined.Add(client.connectionId))
                host.Joined(new ConnectionId(client.connectionId));

        _gone.Clear();
        foreach (var connection in _joined)
        {
            var client = Client.Find(connection);
            if (client == null || client.state == ClientState.Disconnected)
                _gone.Add(connection);
        }

        foreach (var connection in _gone)
        {
            _joined.Remove(connection);
            host.Left(new ConnectionId(connection));
        }
    }

    /// <summary>A message LaunchPadBooster read: a command on the host, anything else on a client; the rest is reported.</summary>
    public void Received(ConnectionId from, SyncMessage message)
    {
        if (message.Dropped.TryGet(out var dropped))
        {
            _report(dropped.WithConnection(from));
            return;
        }

        if (_host.TryGet(out var host) && message.Kind == SyncKind.Command)
        {
            var client = Client.Find(from.Value);
            if (client == null)
                _report(SyncRules.Problem("command from an unknown connection").WithConnection(from));
            else
                host.Receive(from, new ClientId(client.ClientId), message.Text);
            return;
        }

        if (_client.TryGet(out var sync) && message.Kind != SyncKind.Command)
        {
            sync.Receive(message.Kind, message.Text);
            return;
        }

        _report(SyncRules.Problem("a " + message.Kind + " message this side does not take").WithConnection(from));
    }
}
