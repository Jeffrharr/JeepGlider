using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using Il2CppVehiclePhysics;
using JoofOTHFramework;
using MelonLoader;
using UnityEngine;
using NVector3 = System.Numerics.Vector3;

namespace JeepGlider;

// A hang glider folded on the roof of any car with the JeepGlider roof cosmetic mounted (see
// RoofItem). Honk to open it: the mast rises and the leading edges swing out from the keel into a
// striped delta wing; honk again to fold it back. While it's open the car's own network tick
// (VehicleTick) flies it with GliderMath. This is the Unity and game side.
//
// Cars are picked up when the game sets up their cosmetics (RoofItem's patches), never by
// searching the scene; each frame only looks at the cars already rigged.
[RegisterTypeInIl2Cpp]
public class Glider : MonoBehaviour
{
    internal static Glider Instance { get; private set; }

    class Rig
    {
        public Transform Root;     // the car's top object, which the game sets up
        public Transform Frame;    // what the glider hangs from: the car's rigidbody, or Root on the garage preview
        public VehicleBase Car;    // null on the garage preview
        public Il2CppFusion.NetworkObject Net;
        public List<GameObject> Parts;
        public CarAudioManager Audio;
        public GameObject Holder;
        public Mesh Sail;
        public Transform Keel, LeftEdge, RightEdge, Crossbar, MastLeft, MastRight;
        public Vector3 Mount;      // roof centre, in Frame's frame
        public float RoofWidth;
        public bool Mounted, Open, HonkWas;
        public float Pitch;        // -1 nose down to 1 nose up, as asked for this frame
        public TickState Tick;     // what the car's network tick last flew with
        public readonly History History = new(128);
        public float Opened = -1f, Drawn = -1f, LastToggle = float.NegativeInfinity;
        public int TickedFrame = int.MinValue;  // the last frame the car's network tick flew it
        public bool TickLogged;
    }

    // By the car's top object, and by its VehicleBase for the network tick
    readonly Dictionary<IntPtr, Rig> rigs = new();
    readonly Dictionary<IntPtr, Rig> byCar = new();
    readonly List<IntPtr> dead = new();
    // Cars whose customization was just set up, waiting to be rigged. A real car's colliders may
    // not all be there yet, so it's retried every few frames for a while.
    static readonly Dictionary<IntPtr, (Transform Root, int Tries)> pending = new();
    const int RetryFrames = 10, MaxTries = 30;
    // Where the roof is on each car model, in its top object's frame: measured on a real car,
    // reused for the garage's preview copy of it, which has no colliders
    static readonly Dictionary<string, (Vector3 Mount, float Width)> roofs = new();
    static Material sailMaterial, tubeMaterial;
    static int steerId, inputChannel;
    static bool inputIds;

    // A honk held for a moment counts once
    const float ToggleCooldown = 0.4f;
    const float StickDeadZone = 0.15f;
    const int PitchSteps = 10;
    // A car whose network tick hasn't run for this many frames isn't simulated here: a client that
    // doesn't predict its own car, which only the host moves
    const int TickedRecently = 30;

    public Glider(IntPtr ptr) : base(ptr) { }

    void Awake()
    {
        Instance = this;
        Core.DiagnosticsRequested += Diagnostics;
    }

    void OnDestroy()
    {
        Core.DiagnosticsRequested -= Diagnostics;
        if (Instance == this) Instance = null;
    }

    internal static void CarReady(Transform root)
    {
        if (root != null) pending[root.Pointer] = (root, 0);
    }

    // Pure: "Car_DingDing" for "Car_DingDing(Clone)" and its garage preview "Car_DingDing_Dummy(Clone)"
    internal static string ModelName(string objectName)
    {
        var n = objectName.Replace("(Clone)", "").Trim();
        return n.EndsWith("_Dummy") ? n[..^"_Dummy".Length] : n;
    }

    void LateUpdate()
    {
        try { Tick(); }
        catch (Exception e) { Mod.Log.Error(e); }
    }

    void Tick()
    {
        bool enabled = Mod.Enabled.Value;
        if (pending.Count > 0 && Time.frameCount % RetryFrames == 0) RigPending();

        float dt = Time.deltaTime;
        dead.Clear();
        foreach (var (ptr, rig) in rigs)
        {
            if (rig.Root == null || rig.Holder == null || rig.Parts.Exists(p => p == null)) { Destroy(rig); dead.Add(ptr); continue; }

            bool mounted = enabled && rig.Parts.Exists(p => p.activeInHierarchy);
            var obj = rig.Net;
            bool own = obj == null || obj.HasInputAuthority;
            // Cars this machine simulates open and fold in their network tick (FixedTick)
            bool simulated = rig.Car != null && (own || obj.HasStateAuthority) && Time.frameCount - rig.TickedFrame <= TickedRecently;
            if (own && mounted && rig.Car != null)
            {
                // Our own car: the horn opens and folds it
                bool honk = rig.Audio != null && rig.Audio.wasHonkPressedLastFrame;
                if (honk && !rig.HonkWas && Time.time - rig.LastToggle > ToggleCooldown)
                {
                    rig.Open = !rig.Open;
                    rig.LastToggle = Time.time;
                    Mod.Log.Msg($"Glider {(rig.Open ? "opening" : "folding")}{(rig.Open && !Net.Flies ? " (looks only: the host doesn't have JeepGlider)" : "")}");
                }
                rig.HonkWas = honk;
                rig.Pitch = rig.Opened > 0f ? ReadPitch() : 0f;
            }
            if (!mounted) { rig.Open = false; rig.Pitch = 0f; }
            if (!own) Sync(rig, ref mounted);
            // Our own car with no network tick here: tell the host from the frame instead
            else if (!simulated && obj != null && HillNet.Runner is { } runner)
            {
                bool open = mounted && rig.Open;
                Net.SendOwn(obj, runner.Tick.Raw, mounted, open, open ? rig.Pitch : 0f);
            }

            rig.Mounted = mounted;
            if (rig.Holder.activeSelf != mounted) rig.Holder.SetActive(mounted);
            if (!mounted) { rig.Opened = 0f; continue; }
            if (!simulated) rig.Opened = GliderMath.Animate(Mathf.Max(rig.Opened, 0f), rig.Open, Mod.OpenSeconds.Value, dt);
            if (rig.Opened != rig.Drawn) Draw(rig);
        }
        foreach (var d in dead)
        {
            if (rigs.Remove(d, out var rig) && rig.Car != null) byCar.Remove(rig.Car.Pointer);
        }
    }

    // Other players' cars show what they last said, falling back to the game's own mounted parts.
    // Our own car speaks from its network tick (FixedTick).
    static void Sync(Rig rig, ref bool mounted)
    {
        if (rig.Net == null || HillNet.Runner == null) return;
        var s = Net.For(rig.Net);
        if (s == null) { rig.Open = false; rig.Pitch = 0f; return; }
        mounted = Mod.Enabled.Value && s.Mounted;
        rig.Open = mounted && s.Open;
        rig.Pitch = s.Pitch;
    }

    void RigPending()
    {
        if (!RoofItem.EnsureRegistered()) return;
        foreach (var (ptr, (root, tries)) in new List<KeyValuePair<IntPtr, (Transform, int)>>(pending))
        {
            if (root == null || TryRig(root)) pending.Remove(ptr);
            else if (tries + 1 >= MaxTries) { pending.Remove(ptr); Mod.Log.Warning($"Couldn't find the roof of {root.name}"); }
            else pending[ptr] = (root, tries + 1);
        }
    }

    // ---- Flying, inside the car's network tick ----

    internal static void FixedTick(IntPtr car)
    {
        var self = Instance;
        if (self == null || !self.byCar.TryGetValue(car, out var rig)) return;
        var v = rig.Car;
        if (v == null) return;
        var obj = rig.Net;
        bool own = obj == null || obj.HasInputAuthority;
        // The host simulates every car, a client only predicts its own
        if (!own && !obj.HasStateAuthority) return;
        rig.TickedFrame = Time.frameCount;
        if (!rig.TickLogged && obj != null && !obj.HasStateAuthority)
        {
            rig.TickLogged = true;
            Mod.Log.Msg($"Our car {rig.Root.name} gets network ticks here (client prediction)");
        }

        var runner = v.Runner;
        float dt = runner != null ? runner.DeltaTime : Time.fixedDeltaTime;
        int tick = runner != null ? runner.Tick.Raw : 0;
        // Correcting our own car: replay exactly what this tick did the first time
        if (runner != null && runner.IsResimulation && rig.History.TryGet(tick, out var past)) rig.Tick = past;
        else
        {
            bool open;
            float pitch;
            if (own)
            {
                open = rig.Mounted && rig.Open;
                pitch = open ? rig.Pitch : 0f;
                Net.SendOwn(obj, tick, rig.Mounted, open, pitch);
            }
            else
            {
                // Host: another player's car, with their changes on the ticks they made them
                var timeline = Net.TimelineFor(obj);
                timeline.Advance(tick, runner != null ? runner.TickRate * 2 : 0);
                open = rig.Mounted && timeline.Open;
                pitch = open ? timeline.Pitch : 0f;
            }
            float opened = rig.Mounted ? GliderMath.Animate(rig.Tick.Opened, open, Mod.OpenSeconds.Value, dt) : 0f;
            rig.Tick = new TickState(open, pitch, opened);
            rig.History.Record(tick, rig.Tick);
        }
        rig.Opened = rig.Tick.Opened;
        if (rig.Mounted && rig.Tick.Opened > 0f) Fly(rig, v, dt);
    }

    static void Fly(Rig rig, VehicleBase v, float dt)
    {
        if (!Mod.Enabled.Value || !Mod.Flight.Value || !Net.Flies) return;
        var rb = v.cachedRigidbody;
        if (rb == null || rb.isKinematic) return;

        var air = Mod.Airframe();
        var t = rb.transform;
        var body = new Body(N(rb.velocity), N(t.right), N(t.up), N(t.forward), rb.mass);
        float spread = GliderMath.Spread(rig.Tick.Opened);
        var aero = GliderMath.Forces(air, body, spread);
        if (aero.Airspeed <= 0f) return;
        // At the centre of mass: the pendulum and the glider's own stability are in Righting
        rb.AddForce(U(aero.Force) * dt, ForceMode.Impulse);

        float steer = ReadSteer(v);
        float authority = GliderMath.Authority(air, spread, aero.Airspeed, Grounded(v)) * Mod.Stability.Value;
        var acc = GliderMath.Righting(air, aero, body, N(t.InverseTransformDirection(rb.angularVelocity)), steer,
            GliderMath.Trim(air, rig.Tick.Pitch), authority);
        // Cars' inertia tensors line up with their own axes closely enough to skip the rotation
        var torque = Vector3.Scale(U(acc), rb.inertiaTensor);
        rb.AddTorque(t.TransformDirection(torque) * dt, ForceMode.Impulse);
    }

    // The driver's steering from the car's data bus, which the game fills from the network input
    // on the host too. Vehicle Physics Pro stores it as an integer scaled by 10000.
    static float ReadSteer(VehicleBase v)
    {
        if (!inputIds)
        {
            inputIds = true;
            inputChannel = Il2CppVehiclePhysics.Channel.Input;
            steerId = InputData.Steer;
        }
        var data = v.data;
        return data == null ? 0f : data.Get(inputChannel, steerId) / 10000f;
    }

    // The left stick's up and down, which the game doesn't use for driving and doesn't send to
    // the host, so Net does. Back (down) pulls the nose up, like pushing a hang glider's bar out.
    static float ReadPitch()
    {
        var pad = UnityEngine.InputSystem.Gamepad.current;
        if (pad == null) return 0f;
        float y = GliderMath.DeadZone(pad.leftStick.y.ReadValue(), StickDeadZone);
        return GliderMath.Quantize(Mod.InvertPitch.Value ? y : -y, PitchSteps);
    }

    static float Grounded(VehicleBase v)
    {
        var wheels = v.m_wheelState;
        if (wheels == null || wheels.Length == 0) return 0f;
        int down = 0;
        foreach (var w in wheels) if (w != null && w.grounded) down++;
        return (float)down / wheels.Length;
    }

    // ---- Building ----

    // Whether the car is rigged (or has nothing to rig)
    bool TryRig(Transform root)
    {
        var parts = RoofItem.PartObjects(root);
        if (parts.Count == 0) return true;
        if (rigs.TryGetValue(root.Pointer, out var old))
        {
            // Set up again (the garage does this): keep the rig, pick up the new part objects
            old.Parts = parts;
            return true;
        }

        var car = root.GetComponentInChildren<VehicleBase>(true);
        var rb = car == null ? null : car.cachedRigidbody ?? car.GetComponent<Rigidbody>();
        if (car != null && rb == null) return false;
        var model = ModelName(root.name);
        Transform frame;
        Vector3 mount;
        float width;
        if (rb != null)
        {
            // Its colliders may not all be there yet; try again in a few frames
            if (!TryMeasureRoof(rb, out mount, out width)) return false;
            frame = rb.transform;
            roofs[model] = (root.InverseTransformPoint(frame.TransformPoint(mount)), width);
        }
        else
        {
            // The garage preview: copy a real car of the same model, or go by how it looks
            frame = root;
            if (roofs.TryGetValue(model, out var r)) (mount, width) = r;
            else if (!TryMeasureLooks(root, out mount, out width)) return false;
        }
        EnsureMaterials();

        var holder = new GameObject("JeepGlider");
        holder.transform.SetParent(frame, false);
        var sail = new Mesh { name = "JeepGliderSail" };
        sail.MarkDynamic();
        var sailGo = new GameObject("Sail");
        sailGo.transform.SetParent(holder.transform, false);
        sailGo.AddComponent<MeshFilter>().sharedMesh = sail;
        sailGo.AddComponent<MeshRenderer>().sharedMaterial = sailMaterial;

        var rig = new Rig
        {
            Root = root, Frame = frame, Car = car, Net = root.GetComponentInChildren<Il2CppFusion.NetworkObject>(true), Parts = parts,
            Audio = root.GetComponentInChildren<CarAudioManager>(true), Holder = holder, Sail = sail, Mount = mount, RoofWidth = width,
            Keel = Tube(holder, "Keel"), LeftEdge = Tube(holder, "LeftEdge"), RightEdge = Tube(holder, "RightEdge"),
            Crossbar = Tube(holder, "Crossbar"), MastLeft = Tube(holder, "MastLeft"), MastRight = Tube(holder, "MastRight"),
        };
        rig.Opened = 0f;
        Draw(rig);
        holder.SetActive(false);
        rigs[root.Pointer] = rig;
        if (car != null) byCar[car.Pointer] = rig;
        Mod.Log.Msg($"Glider rigged on {root.name} (roof {mount.y:0.00} m up, {width:0.0} m wide)");
        return true;
    }

    // The middle of the roof, in the rigidbody's frame: straight down onto the car's own
    // colliders from above the middle of their box
    static bool TryMeasureRoof(Rigidbody rb, out Vector3 mount, out float width)
    {
        mount = default; width = 0f;
        var t = rb.transform;
        bool any = false;
        var box = new Bounds();
        foreach (var c in rb.GetComponentsInChildren<Collider>())
        {
            if (c == null || c.isTrigger || !c.enabled || c.attachedRigidbody != rb) continue;
            Encapsulate(ref box, ref any, t, c.bounds);
        }
        if (!any || box.size.y < 0.3f) return false;

        // A little back from the middle, where the roof usually is
        var top = new Vector3(box.center.x, box.max.y, box.center.z - box.size.z * 0.08f);
        float roof = box.max.y;
        var from = t.TransformPoint(top + Vector3.up);
        float best = float.MaxValue;
        foreach (var hit in Physics.RaycastAll(from, -t.up, box.size.y + 1f, ~0, QueryTriggerInteraction.Ignore))
        {
            if (hit.collider == null || hit.collider.attachedRigidbody != rb || hit.distance >= best) continue;
            best = hit.distance;
            roof = t.InverseTransformPoint(hit.point).y;
        }
        mount = new Vector3(top.x, roof, top.z);
        width = box.size.x;
        return true;
    }

    // Rougher, for a car with no colliders: the top of its drawn box
    static bool TryMeasureLooks(Transform root, out Vector3 mount, out float width)
    {
        mount = default; width = 0f;
        bool any = false;
        var box = new Bounds();
        foreach (var r in root.GetComponentsInChildren<MeshRenderer>())
            if (r != null && r.enabled) Encapsulate(ref box, ref any, root, r.bounds);
        if (!any || box.size.y < 0.3f) return false;
        mount = new Vector3(box.center.x, box.max.y - 0.05f, box.center.z - box.size.z * 0.08f);
        width = box.size.x;
        return true;
    }

    static void Encapsulate(ref Bounds box, ref bool any, Transform t, Bounds world)
    {
        for (int i = 0; i < 8; i++)
        {
            var corner = world.center + Vector3.Scale(world.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
            var lp = t.InverseTransformPoint(corner);
            if (!any) { box = new Bounds(lp, Vector3.zero); any = true; }
            else box.Encapsulate(lp);
        }
    }

    // A thin cylinder with no collider, so it never touches the car's physics
    static Transform Tube(GameObject holder, string name)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        DestroyImmediate(go.GetComponent<Collider>());
        go.name = name;
        go.transform.SetParent(holder.transform, false);
        go.GetComponent<MeshRenderer>().sharedMaterial = tubeMaterial;
        return go.transform;
    }

    // ---- Drawing ----

    // Builds the frame and sail for how far open it is, in the car's frame. Folded it's a bundle
    // along the keel lying on the roof; open, a delta wing on a mast above it.
    static void Draw(Rig rig)
    {
        rig.Drawn = rig.Opened;
        float s = Mod.Span.Value / 10f;
        float rise = GliderMath.Rise(rig.Opened), spread = GliderMath.Spread(rig.Opened);

        var hang = rig.Mount + Vector3.up * Mathf.Lerp(0.18f, 1.5f * s, rise);
        // Open, the sail sits at the angle of attack it trims to, so it looks right in flight
        var pitch = Quaternion.AngleAxis(-Mod.Airframe().TrimAngle * Mathf.Rad2Deg * spread, Vector3.right);
        Vector3 fwd = pitch * Vector3.forward, up = pitch * Vector3.up, right = Vector3.right;

        var nose = hang + fwd * (2.6f * s);
        var tail = hang - fwd * (2.0f * s);
        float half = Mathf.Lerp(3f, 62f, spread) * Mathf.Deg2Rad;
        float edge = 5.6f * s;
        Vector3 Tip(float side) => nose + (-fwd * Mathf.Cos(half) + right * (side * Mathf.Sin(half))) * edge + up * (0.12f * s * spread);
        var tipL = Tip(-1f);
        var tipR = Tip(1f);

        BuildSail(rig.Sail, nose, tail, tipL, tipR, fwd, up, right, s, spread);

        float r = 0.035f * Mathf.Sqrt(s);
        Place(rig.Keel, nose, tail + -fwd * 0.2f * s, r);
        Place(rig.LeftEdge, nose, tipL, r);
        Place(rig.RightEdge, nose, tipR, r);
        // The crossbar spreads the leading edges; folded it lies along the keel
        Place(rig.Crossbar, Vector3.Lerp(nose, tipL, 0.55f) - up * 0.04f, Vector3.Lerp(nose, tipR, 0.55f) - up * 0.04f, r * 0.9f);
        // An inverted V from the roof's edges up to the hang point
        float foot = Mathf.Min(rig.RoofWidth * 0.35f, 0.7f);
        Place(rig.MastLeft, rig.Mount - Vector3.right * foot, hang, r);
        Place(rig.MastRight, rig.Mount + Vector3.right * foot, hang, r);
    }

    // Each half is a fan around a billowed middle point: nose, tip, trailing edge, keel tail.
    // Both faces, so it lights from above and below. u runs across the span for the stripes.
    static void BuildSail(Mesh mesh, Vector3 nose, Vector3 tail, Vector3 tipL, Vector3 tipR, Vector3 fwd, Vector3 up, Vector3 right, float s, float spread)
    {
        var verts = new List<Vector3>();
        var uvs = new List<Vector2>();
        var normals = new List<Vector3>();
        var tris = new List<int>();
        float span = Mathf.Max(Vector3.Distance(tipL, tipR) * 0.5f, 0.01f);

        foreach (var (tip, side) in new[] { (tipL, -1f), (tipR, 1f) })
        {
            // Scalloped trailing edge, and the sail sagging into a billow between the tubes
            var trailing = Vector3.Lerp(tail, tip, 0.55f) + fwd * (0.6f * s * spread) - up * (0.1f * s * spread);
            var middle = (nose + tip + trailing + tail) * 0.25f - up * (0.18f * s * spread);
            var ring = new[] { nose, tip, trailing, tail };
            for (int face = 0; face < 2; face++)
            {
                int start = verts.Count;
                var n = (face == 0 ? up : -up);
                foreach (var p in ring)
                {
                    verts.Add(p);
                    uvs.Add(new Vector2(Mathf.Abs(Vector3.Dot(p - nose, right)) / span, 0.5f));
                    normals.Add(n);
                }
                verts.Add(middle);
                uvs.Add(new Vector2(Mathf.Abs(Vector3.Dot(middle - nose, right)) / span, 0.5f));
                normals.Add(n);
                for (int i = 0; i < 4; i++)
                {
                    int a = start + i, b = start + (i + 1) % 4, c = start + 4;
                    // Wound so the top face points up on both halves
                    bool flip = (face == 1) ^ (side < 0f);
                    if (flip) { tris.Add(a); tris.Add(c); tris.Add(b); }
                    else { tris.Add(a); tris.Add(b); tris.Add(c); }
                }
            }
        }
        mesh.Clear();
        mesh.vertices = verts.ToArray();
        mesh.uv = uvs.ToArray();
        mesh.normals = normals.ToArray();
        mesh.triangles = tris.ToArray();
        mesh.RecalculateBounds();
    }

    // Stretches a unit cylinder (2 m tall, 1 m across, along y) from a to b
    static void Place(Transform tube, Vector3 a, Vector3 b, float radius)
    {
        var d = b - a;
        float len = d.magnitude;
        tube.localPosition = (a + b) * 0.5f;
        tube.localRotation = len > 1e-4f ? Quaternion.FromToRotation(Vector3.up, d / len) : Quaternion.identity;
        tube.localScale = new Vector3(radius * 2f, len * 0.5f, radius * 2f);
    }

    static void EnsureMaterials()
    {
        // Unity null checks too: a scene change's asset unload can destroy the cached objects
        if (sailMaterial != null && tubeMaterial != null) return;
        var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Universal Render Pipeline/Simple Lit") ?? Shader.Find("Standard");

        sailMaterial = new Material(shader) { name = "JeepGliderSail", hideFlags = HideFlags.DontUnloadUnusedAsset };
        var tex = StripeTexture();
        sailMaterial.mainTexture = tex;
        if (sailMaterial.HasProperty("_BaseMap")) sailMaterial.SetTexture("_BaseMap", tex);
        if (sailMaterial.HasProperty("_BaseColor")) sailMaterial.SetColor("_BaseColor", Color.white);
        if (sailMaterial.HasProperty("_Smoothness")) sailMaterial.SetFloat("_Smoothness", 0.35f);
        if (sailMaterial.HasProperty("_Cull")) sailMaterial.SetFloat("_Cull", 0f);

        tubeMaterial = new Material(shader) { name = "JeepGliderTube", hideFlags = HideFlags.DontUnloadUnusedAsset };
        var grey = new Color(0.55f, 0.57f, 0.6f);
        tubeMaterial.color = grey;
        if (tubeMaterial.HasProperty("_BaseColor")) tubeMaterial.SetColor("_BaseColor", grey);
        if (tubeMaterial.HasProperty("_Metallic")) tubeMaterial.SetFloat("_Metallic", 0.8f);
        if (tubeMaterial.HasProperty("_Smoothness")) tubeMaterial.SetFloat("_Smoothness", 0.7f);
    }

    // Joof colours: cream, yellow, brown outline
    internal static readonly Color Cream = new(1f, 0.953f, 0.886f), Yellow = new(1f, 0.788f, 0.235f), Brown = new(0.29f, 0.141f, 0.086f);

    // Pure: the sail's colour at u across the half span (0 at the keel, 1 at the tip)
    internal static Color Stripe(float u) => u < 0.3f ? Yellow : u < 0.45f ? Cream : u < 0.6f ? Yellow : u < 0.8f ? Cream : Brown;

    // Bands across the span, brown at the tips
    static Texture2D StripeTexture()
    {
        const int w = 64;
        var px = new Color[w * 2];
        for (int x = 0; x < w; x++) px[x] = px[w + x] = Stripe((x + 0.5f) / w);
        var tex = new Texture2D(w, 2, TextureFormat.RGBA32, false)
        {
            name = "JeepGliderStripes", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear,
            hideFlags = HideFlags.DontUnloadUnusedAsset,
        };
        tex.SetPixels(px);
        tex.Apply();
        return tex;
    }

    static void Destroy(Rig rig)
    {
        if (rig.Holder != null) UnityEngine.Object.Destroy(rig.Holder);
        if (rig.Sail != null) UnityEngine.Object.Destroy(rig.Sail);
    }

    void Diagnostics()
    {
        var runner = HillNet.Runner;
        Mod.Log.Msg($"Diag: {rigs.Count} cars rigged, {pending.Count} waiting" +
                    (runner == null ? ", offline" : $", network tick {runner.TickRate} Hz ({runner.DeltaTime * 1000f:0.#} ms), physics step {Time.fixedDeltaTime * 1000f:0.#} ms"));
        foreach (var rig in rigs.Values)
        {
            if (rig.Car == null || rig.Root == null) continue;
            var rb = rig.Car.cachedRigidbody;
            var obj = rig.Net;
            string who = obj == null ? "offline" : $"net id {obj.Id.Raw}, input {obj.HasInputAuthority}, state {obj.HasStateAuthority}";
            Mod.Log.Msg($"Diag: {rig.Root.name} ({who}) mounted {rig.Mounted}, open {rig.Open} ({rig.Opened:0.00}), pitch {rig.Tick.Pitch:0.0}, flies {Net.Flies}, " +
                        $"ticked {Time.frameCount - rig.TickedFrame} frames ago, kinematic {rb != null && rb.isKinematic}, " +
                        $"speed {(rb == null ? 0f : rb.velocity.magnitude):0.0} m/s, grounded {Grounded(rig.Car):0.00}");
        }
        Net.Diagnostics(id => { foreach (var r in rigs.Values) if (r.Net != null && r.Net.Id.Raw == id) return true; return false; });
    }

    static NVector3 N(Vector3 v) => new(v.x, v.y, v.z);
    static Vector3 U(NVector3 v) => new(v.X, v.Y, v.Z);
}

// The car's own network tick (the host for every car, clients for prediction)
[HarmonyPatch(typeof(VehicleBase), nameof(VehicleBase.FixedUpdateNetwork))]
static class VehicleTick
{
    static void Postfix(VehicleBase __instance)
    {
        if (Core.Quitting) return;
        try { Glider.FixedTick(__instance.Pointer); }
        catch (Exception e) { Mod.Log.Error(e.ToString()); }
    }
}
