using System;
using System.IO;
using System.Linq;
using Lattice.TestKit.Fixtures;

namespace Lattice.Tests.RealSave;

/// <summary>
/// What the two-player live run recorded on the test instance (Lattice.Game.Live <c>two-player-host</c> on the test
/// dedicated server, <c>two-player-client</c> on a copy of the game client in batch mode with Steam off; logs copied from
/// <c>LatticeFixtures\bridge</c> by make-fixtures.ps1).
/// </summary>
public sealed class LiveTwoPlayerTests
{
    private const string Host = "live-bridge-two-player-host.log";
    private const string Client = "live-bridge-two-player-client.log";

    [SaveFact(Host)]
    public void The_host_run_passes_every_check() => Passes(Host, "LIVE PASS two-player host run");

    [SaveFact(Client)]
    public void The_client_run_passes_every_check() => Passes(Client, "LIVE PASS two-player client run");

    [SaveFact(Host)]
    public void The_client_has_a_generated_id_that_the_host_stamps_on_its_ghosts()
    {
        var log = Lines(Host);
        Assert.Contains(log, static l => l.StartsWith("LIVE PASS the client's id is the generated one, not a Steam id", StringComparison.Ordinal));
        Assert.Contains(log, static l => l.StartsWith("LIVE PASS the host stamps the client's player on its ghosts: 20 of 20", StringComparison.Ordinal));
    }

    [SaveFact(Client)]
    public void Boards_stay_equal_through_quit_rejoin_and_the_hosts_reload()
    {
        var equal = Lines(Client).Where(static l => l.StartsWith("LIVE PASS client: boards equal after ", StringComparison.Ordinal)).ToList();
        foreach (var step in new[] { "joined", "client-place", "client-remove", "host-act", "refused", "big-set", "rejoined", "reloaded" })
            Assert.Contains(equal, l => l.Contains($"'{step}'", StringComparison.Ordinal));
    }

    [SaveFact(Client)]
    public void A_set_over_the_message_limit_arrives_in_frames_in_order_on_the_main_thread()
    {
        var log = Lines(Client);
        Assert.Contains(log, static l => l.StartsWith("LIVE PASS a set over 1 MB arrives in frames", StringComparison.Ordinal));
        Assert.Contains(log, static l => l.StartsWith("LIVE PASS client: frames arrive in order on channel 169", StringComparison.Ordinal));
        Assert.Contains(log, static l => l.StartsWith("LIVE PASS client: sync messages processed on the main thread", StringComparison.Ordinal));
    }

    [SaveFact(Client)]
    public void A_client_that_leaves_ends_Lattice_and_rejoins_with_a_fresh_join()
    {
        var log = Lines(Client);
        Assert.Contains(log, static l => l.StartsWith("LIVE client: left: state None, network Offline, Lattice ended", StringComparison.Ordinal));
        Assert.Contains(log, static l => l.StartsWith("LIVE PASS client: rejoin after leaving: joined in ", StringComparison.Ordinal));
        Assert.Contains(log, static l => l.StartsWith("LIVE PASS the client that left and came back gets a fresh join", StringComparison.Ordinal));
        Assert.Contains(log, static l => l.StartsWith("LIVE PASS client: rejoin after the host's reload: joined in ", StringComparison.Ordinal));
    }

    [CatalogueViewFact(Host, Client)]
    public void The_catalogue_view_without_names_is_the_same_on_the_server_and_the_client()
    {
        static string View(string file) => Lines(file).Single(static l => l.StartsWith(CatalogueViewFactAttribute.Line, StringComparison.Ordinal));

        Assert.Equal(View(Host), View(Client));
    }

    private static void Passes(string file, string verdict)
    {
        var log = Lines(file);
        Assert.DoesNotContain(log, static l => l.StartsWith("LIVE FAIL", StringComparison.Ordinal));
        Assert.Contains(verdict, log);
    }

    private static string[] Lines(string file) => File.ReadAllLines(Path.Combine(Fixtures.Generated, file));
}

/// <summary>A fact over both two-player logs, skipped until both carry the catalogue view line.</summary>
public sealed class CatalogueViewFactAttribute : FactAttribute
{
    public const string Line = "LIVE catalogue view pieces=";

    public CatalogueViewFactAttribute(params string[] logs)
    {
        foreach (var log in logs)
            if (!Fixtures.Save(log).TryGet(out var path) || !File.ReadAllText(path).Contains(Line, StringComparison.Ordinal))
                Skip = $"{log} in tests/fixtures/generated has no catalogue view line: run the two-player live run, then make-fixtures.ps1.";
    }
}
