using System;
using System.Numerics;

namespace JeepGlider;

// The glider's flight model, kept free of Unity and the game so it can be unit tested. Vectors
// are in the game's axes: x right, y up, z forward.
//
// The sail is a real hang glider's wing (aspect ratio, camber, stall, best glide around 8:1),
// sized so the car has a hang glider's wing loading: a 1500 kg car gets a couple of hundred
// square metres of "effective" sail, and flies, stalls and glides at the speeds a pilot would.
// Pitch, roll and yaw stability, which a real glider gets from sweep, twist and the pilot
// hanging under it, are modelled as springs on the car's attitude, so they behave the same
// whatever the car's shape and inertia. Steering banks it like shifting weight; the pitch input
// pulls the bar in (faster, nose down) or pushes it out (slower, nose up).
public readonly struct Airframe
{
    public readonly float WingLoading;   // kg of car per m² of effective sail
    public readonly float AspectRatio;
    public readonly float Cl0;           // lift coefficient at zero angle of attack (camber)
    public readonly float StallAngle;    // rad
    public readonly float Cd0;           // drag with no lift
    public readonly float Oswald;        // span efficiency
    public readonly float SideForce;     // side force coefficient per rad of sideslip
    public readonly float TrimAngle;     // rad, angle of attack it settles at hands-off
    public readonly float TrimDown;      // rad, how far full nose-down pitch lowers the trim
    public readonly float TrimUp;        // rad, how far full nose-up pitch raises it (keep short of the stall)
    public readonly float MaxBank;       // rad, bank at full steering
    public readonly float StabilityHz;   // how briskly it rights itself at flying speed
    public readonly float DampingRatio;

    public Airframe(float wingLoading, float aspectRatio, float cl0, float stallAngle, float cd0, float oswald, float sideForce,
        float trimAngle, float trimDown, float trimUp, float maxBank, float stabilityHz, float dampingRatio)
    {
        WingLoading = wingLoading; AspectRatio = aspectRatio; Cl0 = cl0; StallAngle = stallAngle; Cd0 = cd0; Oswald = oswald;
        SideForce = sideForce; TrimAngle = trimAngle; TrimDown = trimDown; TrimUp = trimUp; MaxBank = maxBank; StabilityHz = stabilityHz;
        DampingRatio = dampingRatio;
    }

    const float Deg = MathF.PI / 180f;

    // A typical intermediate hang glider: 7 kg/m² loaded, aspect ratio 6, stalls at 15°
    public static readonly Airframe Default = new(7f, 6f, 0.2f, 15f * Deg, 0.05f, 0.8f, 0.6f,
        8f * Deg, 10f * Deg, 6f * Deg, 35f * Deg, 1.2f, 0.8f);

    // Lift curve slope per rad, for a finite wing
    public float LiftSlope => 2f * MathF.PI * AspectRatio / (AspectRatio + 2f);

    public float InducedDrag => 1f / (MathF.PI * Oswald * AspectRatio);
}

// The car as the tick sees it, in world space
public readonly struct Body
{
    public readonly Vector3 Velocity, Right, Up, Forward;
    public readonly float Mass;
    public Body(Vector3 velocity, Vector3 right, Vector3 up, Vector3 forward, float mass)
    {
        Velocity = velocity; Right = right; Up = up; Forward = forward; Mass = mass;
    }
}

public readonly struct Aero
{
    public readonly Vector3 Force;       // world, newtons
    public readonly float Alpha;         // rad, angle of attack of the sail
    public readonly float Sideslip;      // rad, positive when the air comes from the left (moving right of the nose)
    public readonly float Airspeed;
    public readonly float Lift, Drag;    // coefficients
    public Aero(Vector3 force, float alpha, float sideslip, float airspeed, float lift, float drag)
    {
        Force = force; Alpha = alpha; Sideslip = sideslip; Airspeed = airspeed; Lift = lift; Drag = drag;
    }
    public static readonly Aero None = default;
}

public static class GliderMath
{
    public const float Gravity = 9.81f, AirDensity = 1.225f;

    // Effective sail area for a car of this mass, opened this far (0 folded, 1 open)
    public static float Area(Airframe a, float mass, float open) => mass / a.WingLoading * Math.Clamp(open, 0f, 1f);

    // Lift coefficient: the wing's straight line up to the stall, blending over a few degrees
    // into a flat plate beyond it (which is also right flying backwards or upside down)
    public static float LiftCoefficient(Airframe a, float alpha)
    {
        float linear = Math.Clamp(a.Cl0 + a.LiftSlope * alpha, -1.4f, 1.4f);
        float plate = 2f * MathF.Sin(alpha) * MathF.Cos(alpha);
        return Lerp(linear, plate, PastStall(a, alpha));
    }

    public static float DragCoefficient(Airframe a, float alpha)
    {
        float cl = Math.Clamp(a.Cl0 + a.LiftSlope * alpha, -1.4f, 1.4f);
        float attached = a.Cd0 + a.InducedDrag * cl * cl;
        float plate = a.Cd0 + 2f * MathF.Sin(alpha) * MathF.Sin(alpha);
        return Lerp(attached, plate, PastStall(a, alpha));
    }

    // 0 while the flow is attached, 1 once fully stalled
    static float PastStall(Airframe a, float alpha) => SmoothStep(a.StallAngle, a.StallAngle + 0.15f, MathF.Abs(alpha));

    // Lift, drag and side force on the sail in still air
    public static Aero Forces(Airframe a, Body b, float open)
    {
        float area = Area(a, b.Mass, open);
        float speed = b.Velocity.Length();
        if (area <= 0f || speed < 0.5f) return Aero.None;

        // Lift acts in the plane across the span; sideslip is the rest
        float side = Vector3.Dot(b.Velocity, b.Right);
        var inPlane = b.Velocity - b.Right * side;
        float planeSpeed = inPlane.Length();
        float alpha = MathF.Atan2(-Vector3.Dot(b.Velocity, b.Up), Vector3.Dot(b.Velocity, b.Forward));
        float beta = MathF.Asin(Math.Clamp(side / speed, -1f, 1f));

        float cl = LiftCoefficient(a, alpha), cd = DragCoefficient(a, alpha);
        float q = 0.5f * AirDensity * speed * speed;
        var force = -b.Velocity / speed * (q * area * cd) - b.Right * (q * area * a.SideForce * beta);
        if (planeSpeed > 0.1f)
        {
            // Square to the airflow, toward the car's top when cl is positive
            var liftDir = Vector3.Normalize(Vector3.Cross(inPlane / planeSpeed, b.Right));
            float qPlane = 0.5f * AirDensity * planeSpeed * planeSpeed;
            force += liftDir * (qPlane * area * cl);
        }
        return new Aero(force, alpha, beta, speed, cl, cd);
    }

    // Speed it stalls at, for this airframe at full trim lift
    public static float StallSpeed(Airframe a) =>
        MathF.Sqrt(2f * Gravity * a.WingLoading / (AirDensity * LiftCoefficient(a, a.StallAngle)));

    // How much the sail's stability can act: none folded, at a standstill, or with the wheels
    // down (it shouldn't fight the car on a slope); full in flight from about stall speed
    public static float Authority(Airframe a, float open, float airspeed, float groundedFraction)
    {
        float v = airspeed / StallSpeed(a);
        return Math.Clamp(open, 0f, 1f) * Math.Clamp(v * v, 0f, 1f) * (1f - Math.Clamp(groundedFraction, 0f, 1f));
    }

    // Angle of attack the pilot is asking for, from pitch -1 (bar pulled in, nose down) to 1
    // (bar pushed out, nose up)
    public static float Trim(Airframe a, float pitch)
    {
        pitch = Math.Clamp(pitch, -1f, 1f);
        return a.TrimAngle + pitch * (pitch < 0f ? a.TrimDown : a.TrimUp);
    }

    // Pure: a stick axis with a dead zone, rescaled so it still reaches ±1
    public static float DeadZone(float axis, float zone)
    {
        float m = MathF.Abs(axis);
        if (m <= zone || zone >= 1f) return 0f;
        return MathF.Sign(axis) * MathF.Min((m - zone) / (1f - zone), 1f);
    }

    // Pure: the pitch rounded to steps, so small stick wobbles don't each need sending
    public static float Quantize(float pitch, int steps) => MathF.Round(Math.Clamp(pitch, -1f, 1f) * steps) / steps;

    // Angular acceleration (rad/s², in the car's own axes: x pitch nose-down, y yaw right, z
    // roll left) that pitches the car to the trim angle of attack, banks it to the steering
    // and points the nose into the airflow, damped. Multiply by the car's inertia.
    public static Vector3 Righting(Airframe a, Aero aero, Body b, Vector3 localAngularVelocity, float steer, float trim, float authority)
    {
        if (authority <= 0f) return Vector3.Zero;
        float w = 2f * MathF.PI * a.StabilityHz, k = w * w, c = 2f * a.DampingRatio * w;

        float pitchErr = aero.Alpha - trim;
        // How far the right wing sits above level, against how far the steering wants it down
        float bank = MathF.Asin(Math.Clamp(b.Right.Y, -1f, 1f));
        float bankErr = bank + Math.Clamp(steer, -1f, 1f) * a.MaxBank;
        float yawErr = aero.Sideslip;

        var acc = new Vector3(k * pitchErr, k * yawErr, -k * bankErr) - c * localAngularVelocity;
        return acc * authority;
    }

    // ---- Opening and folding ----

    // Pure: how far open after dt, opening or closing over the given time
    public static float Animate(float open, bool deployed, float seconds, float dt) =>
        seconds <= 0f ? (deployed ? 1f : 0f) : Math.Clamp(open + (deployed ? dt : -dt) / seconds, 0f, 1f);

    // The mast rises first, then the leading edges swing out; folding runs it backwards
    public static float Rise(float open) => SmoothStep(0f, 0.35f, open);
    public static float Spread(float open) => SmoothStep(0.25f, 1f, open);

    public static float Lerp(float a, float b, float t) => a + (b - a) * t;

    public static float SmoothStep(float from, float to, float x)
    {
        float t = Math.Clamp((x - from) / (to - from), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
