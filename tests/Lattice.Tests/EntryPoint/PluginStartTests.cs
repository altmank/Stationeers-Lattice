using System;
using Lattice.Api;
using Lattice.Core.EntryPoint;

namespace Lattice.Tests.EntryPoint;

public sealed class PluginStartTests
{
    private static readonly LatticeCopy Workshop = new("0.13.1", @"C:\Steam\workshop\content\544550\1\Lattice.Game.dll");
    private static readonly LatticeCopy Local = new("0.13.0", @"D:\Games\Stationeers\mods\Lattice\Lattice.Game.dll");

    [Fact]
    public void The_first_copy_to_start_owns_lattice()
    {
        var slot = new HeldSlot();

        Assert.IsType<CopyStart.Owns>(PluginStart.Claim(slot, Workshop));
        Assert.Equal(Option.Some(Workshop), slot.Read());
    }

    [Fact]
    public void A_later_copy_stands_down_and_names_the_copy_that_runs()
    {
        var slot = new HeldSlot();
        PluginStart.Claim(slot, Workshop);

        var start = PluginStart.Claim(slot, Local);

        Assert.Equal(new CopyStart.StandsDown(Workshop), start);
        Assert.Equal(Option.Some(Workshop), slot.Read());
    }

    [Fact]
    public void A_second_start_from_the_same_file_stands_down()
    {
        var slot = new HeldSlot();
        PluginStart.Claim(slot, Local);

        Assert.Equal(new CopyStart.StandsDown(Local), PluginStart.Claim(slot, Local));
    }

    [Fact]
    public void The_same_file_stand_down_line_names_the_copy_that_runs_once()
    {
        var line = PluginStart.StandDownLine(Local, Local with { Location = Local.Location.ToUpperInvariant() });

        Assert.Contains("Lattice is enabled twice", line);
        Assert.Contains("started again", line);
        Assert.Contains("Keep one copy of Lattice enabled", line);
    }

    [Fact]
    public void The_stand_down_line_names_both_copies_and_which_runs()
    {
        var line = PluginStart.StandDownLine(Local, Workshop);

        Assert.Contains($"Lattice {Workshop.Version} from {Workshop.Location} runs", line);
        Assert.Contains($"Lattice {Local.Version} from {Local.Location}, does nothing", line);
        Assert.Contains("Keep one copy of Lattice enabled", line);
    }

    [Fact]
    public void The_appdomain_slot_is_shared_by_separate_readers()
    {
        var key = "lattice.tests.running." + Guid.NewGuid().ToString("N");
        try
        {
            Assert.IsType<CopyStart.Owns>(PluginStart.Claim(new AppDomainCopySlot(key), Workshop));

            var later = new AppDomainCopySlot(key);
            Assert.Equal(Option.Some(Workshop), later.Read());
            Assert.Equal(new CopyStart.StandsDown(Workshop), PluginStart.Claim(later, Local));
        }
        finally
        {
            AppDomain.CurrentDomain.SetData(key, null);
        }
    }

    [Fact]
    public void The_appdomain_slot_holds_only_runtime_types()
    {
        var key = "lattice.tests.running." + Guid.NewGuid().ToString("N");
        try
        {
            new AppDomainCopySlot(key).Write(Workshop);

            Assert.IsType<string[]>(AppDomain.CurrentDomain.GetData(key));
        }
        finally
        {
            AppDomain.CurrentDomain.SetData(key, null);
        }
    }

    private sealed class HeldSlot : IRunningCopySlot
    {
        private Option<LatticeCopy> _held = Option<LatticeCopy>.None;

        public Option<LatticeCopy> Read() => _held;

        public void Write(LatticeCopy copy) => _held = Option.Some(copy);
    }
}
