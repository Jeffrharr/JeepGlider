using JeepGlider;
using Xunit;

namespace JeepGlider.Tests;

public class TicksTests
{
    [Fact]
    public void Changes_wait_for_their_tick()
    {
        var t = new Timeline();
        t.Add(105, true, 0.5f);
        t.Advance(104, 100);
        Assert.False(t.Open);
        t.Advance(105, 100);
        Assert.True(t.Open);
        Assert.Equal(0.5f, t.Pitch);
    }

    [Fact]
    public void Late_changes_apply_at_once_and_in_order()
    {
        var t = new Timeline();
        t.Add(10, true, 0.2f);
        t.Add(11, true, -0.4f);
        t.Add(30, false, 0f);
        t.Advance(20, 100);
        Assert.True(t.Open);
        Assert.Equal(-0.4f, t.Pitch);
        t.Advance(30, 100);
        Assert.False(t.Open);
    }

    [Fact]
    public void Implausibly_far_ahead_applies_now()
    {
        var t = new Timeline();
        t.Add(10_000, true, 1f);
        t.Advance(5, 100);
        Assert.True(t.Open);
    }

    [Fact]
    public void History_replays_recorded_ticks_only()
    {
        var h = new History(4);
        h.Record(7, new TickState(true, 0.3f, 0.5f));
        Assert.True(h.TryGet(7, out var s));
        Assert.Equal(0.5f, s.Opened);
        // Overwritten by a tick sharing its slot, and never recorded ticks miss
        h.Record(11, new TickState(false, 0f, 0f));
        Assert.False(h.TryGet(7, out _));
        Assert.False(h.TryGet(8, out _));
        Assert.True(h.TryGet(11, out _));
    }
}
