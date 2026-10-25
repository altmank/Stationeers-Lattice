using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Ports;

namespace Lattice.Tests.Contracts;

/// <summary>
/// The extension-point suite for <see cref="IWorldChanges"/>, run on every one of its events:
/// a subscriber hears the event; subscribers hear it in subscription order; unsubscribing during a raise is safe (the
/// raise in progress reaches the subscribers it started with, the next one does not reach the removed one); a change
/// caused from inside a handler is raised after the current raise to everyone, never nested; when the world ends every
/// subscription ends. Derive it and give the subject plus a way to cause one event of each kind.
/// </summary>
public abstract class WorldChangesContract
{
    public static TheoryData<string> Events => new() { "Registered", "StateChanged", "Removed", "Painted", "Renamed", "Moved", "SettingChanged" };

    internal abstract IWorldChanges Subject { get; }

    /// <summary>Makes the world raise exactly one event of the named kind.</summary>
    internal abstract void Cause(string eventName);

    /// <summary>Ends the world the subject belongs to.</summary>
    internal abstract void EndWorld();

    [Theory]
    [MemberData(nameof(Events))]
    public void A_subscriber_hears_the_event(string eventName)
    {
        var heard = 0;
        Subscribe(eventName, () => heard++);
        Cause(eventName);
        Assert.Equal(1, heard);
    }

    [Theory]
    [MemberData(nameof(Events))]
    public void Subscribers_hear_it_in_subscription_order(string eventName)
    {
        var log = new List<string>();
        Subscribe(eventName, () => log.Add("a"));
        Subscribe(eventName, () => log.Add("b"));
        Subscribe(eventName, () => log.Add("c"));
        Cause(eventName);
        Assert.Equal(new[] { "a", "b", "c" }, log);
    }

    [Theory]
    [MemberData(nameof(Events))]
    public void Unsubscribing_during_a_raise_takes_effect_from_the_next_raise(string eventName)
    {
        var log = new List<string>();
        Action? unsubscribeC = null;
        Subscribe(eventName, () => log.Add("a"));
        Subscribe(eventName, () =>
        {
            log.Add("b");
            unsubscribeC?.Invoke();
        });
        unsubscribeC = Subscribe(eventName, () => log.Add("c"));

        Cause(eventName);
        Cause(eventName);

        Assert.Equal(new[] { "a", "b", "c", "a", "b" }, log);
    }

    [Theory]
    [MemberData(nameof(Events))]
    public void A_change_caused_in_a_handler_is_raised_after_the_current_raise(string eventName)
    {
        var log = new List<string>();
        var round = 0;
        Subscribe(eventName, () =>
        {
            log.Add($"a{++round}");
            if (round == 1)
                Cause(eventName);
        });
        Subscribe(eventName, () => log.Add($"b{round}"));

        Cause(eventName);

        Assert.Equal(new[] { "a1", "b1", "a2", "b2" }, log);
    }

    [Theory]
    [MemberData(nameof(Events))]
    public void Subscriptions_end_with_the_world(string eventName)
    {
        var heard = 0;
        Subscribe(eventName, () => heard++);
        EndWorld();
        Cause(eventName);
        Assert.Equal(0, heard);
    }

    /// <summary>Subscribes a handler to the named event; returns the unsubscribe.</summary>
    private Action Subscribe(string eventName, Action handler)
    {
        var subject = Subject;
        switch (eventName)
        {
            case "Registered":
                Action<StructureInfo> registered = _ => handler();
                subject.Registered += registered;
                return () => subject.Registered -= registered;
            case "StateChanged":
                Action<StructureRef, BuildState> stateChanged = (_, _) => handler();
                subject.StateChanged += stateChanged;
                return () => subject.StateChanged -= stateChanged;
            case "Removed":
                Action<StructureRef> removed = _ => handler();
                subject.Removed += removed;
                return () => subject.Removed -= removed;
            case "Painted":
                Action<StructureRef, PaintColour> painted = (_, _) => handler();
                subject.Painted += painted;
                return () => subject.Painted -= painted;
            case "Renamed":
                Action<StructureRef, ThingName> renamed = (_, _) => handler();
                subject.Renamed += renamed;
                return () => subject.Renamed -= renamed;
            case "SettingChanged":
                Action<StructureRef, Setting> settingChanged = (_, _) => handler();
                subject.SettingChanged += settingChanged;
                return () => subject.SettingChanged -= settingChanged;
            case "Moved":
                Action<ValueList<StructureMove>> moved = _ => handler();
                subject.Moved += moved;
                return () => subject.Moved -= moved;
            default:
                throw new ArgumentOutOfRangeException(nameof(eventName), eventName, null);
        }
    }
}
