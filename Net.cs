using System.Collections.Generic;
using Il2CppFusion;
using JoofOTHFramework;

namespace JeepGlider;

// Multiplayer: each player tells the others with the mod whether their own car has the glider
// mounted, whether it's open and how far they're pitching it, which isn't in the game's own
// network input. Each change is stamped with the network tick it took effect on, and the host
// applies it on that same tick (Timeline), so it flies the car just as the player's own game
// predicted. The game runs in host mode, so clients send to the host and the host passes it on
// for the other players to draw. Only changes are sent, the tick they happen. Players who join
// late are caught up when they say hello, and a player who leaves is announced as having none.
static class Net
{
    internal const string Channel = "JeepGlider";

    // Serialised as JSON by HillNet, hence properties
    internal sealed class CarState
    {
        public uint Car { get; set; }
        public int Tick { get; set; }
        public bool Mounted { get; set; }
        public bool Open { get; set; }
        public float Pitch { get; set; }
    }

    static readonly Dictionary<uint, CarState> remote = new();     // the latest from each car, for drawing
    static readonly Dictionary<uint, Timeline> timelines = new();  // host: each car's changes by tick, for flying
    static readonly Dictionary<uint, int> owners = new(); // host only: car id to the player who sent it
    static CarState own;     // what our own car last said
    static bool ownSent;     // whether that has gone out to the host or the other players

    internal static void Register()
    {
        HillNet.Register<CarState>(Channel, OnReceive);
        HillNet.SessionReset += () => { remote.Clear(); timelines.Clear(); owners.Clear(); ownSent = false; };
        HillNet.PlayerLeft += id =>
        {
            var gone = new List<uint>();
            foreach (var (car, owner) in owners) if (owner == id) gone.Add(car);
            foreach (var car in gone)
            {
                owners.Remove(car);
                remote.Remove(car);
                timelines.Remove(car);
                HillNet.Broadcast(Channel, new CarState { Car = car });
            }
        };
        // Host: catch a newly arrived player up on every car, ours included
        HillNet.PlayerReady += player =>
        {
            if (own != null) HillNet.SendTo(player, Channel, own);
            foreach (var s in remote.Values) HillNet.SendTo(player, Channel, s);
        };
        // Client: the host can hear us now
        HillNet.HostReady += () => { if (own != null) ownSent = Send(HillNet.Runner, own); };
    }

    // The glider flies only where this machine's physics agrees with the host's
    internal static bool Flies => HillNet.IsAuthority || HillNet.HostHas(Channel);

    // Our own car, every forward tick: only a change goes out
    internal static void SendOwn(NetworkObject car, int tick, bool mounted, bool open, float pitch)
    {
        var runner = HillNet.Runner;
        if (runner == null || car == null) return;
        uint id = car.Id.Raw;
        if (ownSent && own != null && own.Car == id && own.Mounted == mounted && own.Open == open && own.Pitch == pitch) return;
        own = new CarState { Car = id, Tick = tick, Mounted = mounted, Open = open, Pitch = pitch };
        ownSent = Send(runner, own);
    }

    static bool Send(NetworkRunner runner, CarState s)
    {
        if (runner == null) return false;
        if (runner.IsServer) { HillNet.Broadcast(Channel, s); return true; }
        if (!HillNet.HostHas(Channel)) return false; // sent once the host answers (HostReady)
        HillNet.SendToHost(Channel, s);
        return true;
    }

    // What another player's car last said, or null if we haven't heard
    internal static CarState For(NetworkObject car) =>
        car != null && remote.TryGetValue(car.Id.Raw, out var s) ? s : null;

    // Host: another player's car's changes, to apply as their ticks come round
    internal static Timeline TimelineFor(NetworkObject car)
    {
        uint id = car.Id.Raw;
        if (!timelines.TryGetValue(id, out var t)) timelines[id] = t = new Timeline();
        return t;
    }

    static void OnReceive(NetworkRunner runner, PlayerRef from, CarState s)
    {
        remote[s.Car] = s;
        if (!runner.IsServer) return;
        if (!timelines.TryGetValue(s.Car, out var t)) timelines[s.Car] = t = new Timeline();
        t.Add(s.Tick, s.Mounted && s.Open, s.Pitch);
        owners[s.Car] = from.PlayerId;
        HillNet.Broadcast(Channel, s, from.PlayerId);
    }
}
