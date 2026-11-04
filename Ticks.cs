using System.Collections.Generic;

namespace JeepGlider;

// The glider's state on one network tick: what the pilot asked for and how far open it was
public readonly struct TickState
{
    public readonly bool Open;
    public readonly float Pitch, Opened;
    public TickState(bool open, float pitch, float opened) { Open = open; Pitch = pitch; Opened = opened; }
}

// What a remote player asked for, stamped with the tick it took effect on in their game. The host
// applies each change on that same tick, so its simulation of their car matches their prediction.
// Their game runs ahead of the host's, so a change normally arrives before its tick; a late one
// applies at once.
public sealed class Timeline
{
    readonly Queue<(int Tick, bool Open, float Pitch)> pending = new();

    public bool Open { get; private set; }
    public float Pitch { get; private set; }

    public void Add(int tick, bool open, float pitch) => pending.Enqueue((tick, open, pitch));

    // Applies every change due by this tick. One stamped more than maxLead ticks ahead can't be
    // right (a different clock, a new session), so it applies now rather than waiting.
    public void Advance(int tick, int maxLead)
    {
        while (pending.Count > 0)
        {
            var next = pending.Peek();
            if (next.Tick > tick && next.Tick - tick <= maxLead) return;
            pending.Dequeue();
            Open = next.Open;
            Pitch = next.Pitch;
        }
    }

    public void Clear()
    {
        pending.Clear();
        Open = false;
        Pitch = 0f;
    }
}

// The last few ticks' states, so when Fusion resimulates ticks to correct our own car, each one
// replays what it did the first time instead of stepping the opening again with today's inputs
public sealed class History
{
    readonly int[] ticks;
    readonly TickState[] states;

    public History(int size)
    {
        ticks = new int[size];
        states = new TickState[size];
        for (int i = 0; i < size; i++) ticks[i] = int.MinValue;
    }

    public void Record(int tick, TickState state)
    {
        int i = Slot(tick);
        ticks[i] = tick;
        states[i] = state;
    }

    public bool TryGet(int tick, out TickState state)
    {
        int i = Slot(tick);
        state = states[i];
        return ticks[i] == tick;
    }

    int Slot(int tick) => (int)((uint)tick % (uint)ticks.Length);
}
