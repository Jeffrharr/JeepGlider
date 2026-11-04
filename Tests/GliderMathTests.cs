using System;
using System.Numerics;
using JeepGlider;
using Xunit;

namespace JeepGlider.Tests;

public class GliderMathTests
{
    static readonly Airframe A = Airframe.Default;
    const float Deg = MathF.PI / 180f;

    // A car pitched so its nose sits `alpha` above its flight path in the x = 0 plane
    static Body Flying(Vector3 velocity, float alpha, float mass = 1500f)
    {
        var dir = Vector3.Normalize(velocity);
        float pitch = MathF.Atan2(dir.Y, dir.Z) + alpha;
        var fwd = new Vector3(0f, MathF.Sin(pitch), MathF.Cos(pitch));
        var up = new Vector3(0f, MathF.Cos(pitch), -MathF.Sin(pitch));
        return new Body(velocity, Vector3.UnitX, up, fwd, mass);
    }

    [Fact]
    public void Folded_or_still_makes_no_force()
    {
        Assert.Equal(Vector3.Zero, GliderMath.Forces(A, Flying(new Vector3(0, 0, 20), 0.1f), 0f).Force);
        Assert.Equal(Vector3.Zero, GliderMath.Forces(A, Flying(new Vector3(0, 0, 0.1f), 0.1f), 1f).Force);
    }

    [Fact]
    public void Stalls_at_hang_glider_speed()
    {
        float vs = GliderMath.StallSpeed(A);
        Assert.InRange(vs, 8f, 12f);
    }

    [Fact]
    public void Lifts_up_and_drags_back_in_forward_flight()
    {
        var aero = GliderMath.Forces(A, Flying(new Vector3(0, 0, 15), 6f * Deg), 1f);
        Assert.True(aero.Force.Y > 0f);
        Assert.True(aero.Force.Z < 0f);
        Assert.Equal(6f * Deg, aero.Alpha, 3);
    }

    [Fact]
    public void Lift_falls_away_past_the_stall()
    {
        float atStall = GliderMath.LiftCoefficient(A, A.StallAngle);
        float deep = GliderMath.LiftCoefficient(A, A.StallAngle + 15f * Deg);
        Assert.True(deep < atStall);
        Assert.True(GliderMath.DragCoefficient(A, 40f * Deg) > 3f * GliderMath.DragCoefficient(A, A.TrimAngle));
    }

    [Fact]
    public void Sideslip_pushes_back_against_it()
    {
        var b = new Body(new Vector3(3, 0, 15), Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, 1500f);
        var aero = GliderMath.Forces(A, b, 1f);
        Assert.True(aero.Sideslip > 0f);
        Assert.True(aero.Force.X < 0f);
    }

    // Held at the trim angle of attack, it settles into a hang glider's glide: about 8:1, at a
    // bit above stall speed, whatever the car weighs
    [Theory]
    [InlineData(900f)]
    [InlineData(2500f)]
    public void Glides_like_a_hang_glider(float mass)
    {
        var v = new Vector3(0, -2, 20);
        const float dt = 1f / 60f;
        for (int i = 0; i < 60 * 60; i++)
        {
            var aero = GliderMath.Forces(A, Flying(v, A.TrimAngle, mass), 1f);
            v += (aero.Force / mass - new Vector3(0, GliderMath.Gravity, 0)) * dt;
        }
        float glide = v.Z / -v.Y;
        Assert.InRange(glide, 6.5f, 10f);
        Assert.InRange(v.Length(), GliderMath.StallSpeed(A), 16f);
    }

    [Fact]
    public void Pitch_moves_the_trim_but_not_past_the_stall()
    {
        Assert.Equal(A.TrimAngle, GliderMath.Trim(A, 0f));
        Assert.True(GliderMath.Trim(A, -1f) < A.TrimAngle);
        Assert.True(GliderMath.Trim(A, 1f) > A.TrimAngle);
        Assert.True(GliderMath.Trim(A, 5f) < A.StallAngle);
    }

    [Fact]
    public void Stick_dead_zone_and_steps()
    {
        Assert.Equal(0f, GliderMath.DeadZone(0.1f, 0.15f));
        Assert.Equal(-1f, GliderMath.DeadZone(-1f, 0.15f), 4);
        Assert.InRange(GliderMath.DeadZone(0.5f, 0.15f), 0.4f, 0.42f);
        Assert.Equal(0.3f, GliderMath.Quantize(0.33f, 10), 4);
        Assert.Equal(-1f, GliderMath.Quantize(-3f, 10));
    }

    [Fact]
    public void Righting_pitches_banks_and_yaws_the_right_way()
    {
        var level = new Body(new Vector3(0, 0, 15), Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, 1500f);
        Aero At(float alpha, float beta) => new(Vector3.Zero, alpha, beta, 15f, 0f, 0f);

        // Nose too high: pitch nose down (+x)
        Assert.True(GliderMath.Righting(A, At(A.TrimAngle + 0.1f, 0f), level, Vector3.Zero, 0f, A.TrimAngle, 1f).X > 0f);
        // Air from the left of the nose: yaw right (+y), into it
        Assert.True(GliderMath.Righting(A, At(A.TrimAngle, 0.1f), level, Vector3.Zero, 0f, A.TrimAngle, 1f).Y > 0f);
        // Steering right: roll right, right wing down (-z)
        Assert.True(GliderMath.Righting(A, At(A.TrimAngle, 0f), level, Vector3.Zero, 1f, A.TrimAngle, 1f).Z < 0f);
        // Right wing up: back down (-z)
        var tilted = new Body(level.Velocity, Vector3.Normalize(new Vector3(1, 0.3f, 0)), Vector3.Normalize(new Vector3(-0.3f, 1, 0)), Vector3.UnitZ, 1500f);
        Assert.True(GliderMath.Righting(A, At(A.TrimAngle, 0f), tilted, Vector3.Zero, 0f, A.TrimAngle, 1f).Z < 0f);
        // Spinning: damped
        Assert.True(GliderMath.Righting(A, At(A.TrimAngle, 0f), level, new Vector3(0, 0, 2f), 0f, A.TrimAngle, 1f).Z < 0f);
        // Banked to match the steering: nothing to do
        var banked = new Body(level.Velocity, new Vector3(MathF.Cos(A.MaxBank), -MathF.Sin(A.MaxBank), 0), new Vector3(MathF.Sin(A.MaxBank), MathF.Cos(A.MaxBank), 0), Vector3.UnitZ, 1500f);
        Assert.Equal(0f, GliderMath.Righting(A, At(A.TrimAngle, 0f), banked, Vector3.Zero, 1f, A.TrimAngle, 1f).Z, 3);
    }

    [Fact]
    public void No_righting_on_the_ground_folded_or_stopped()
    {
        Assert.Equal(0f, GliderMath.Authority(A, 1f, 20f, 1f));
        Assert.Equal(0f, GliderMath.Authority(A, 0f, 20f, 0f));
        Assert.Equal(0f, GliderMath.Authority(A, 1f, 0f, 0f));
        Assert.Equal(1f, GliderMath.Authority(A, 1f, 20f, 0f));
    }

    [Fact]
    public void Opens_and_folds_over_the_set_time()
    {
        float open = 0f;
        for (int i = 0; i < 10; i++) open = GliderMath.Animate(open, true, 1f, 0.1f);
        Assert.Equal(1f, open, 4);
        open = GliderMath.Animate(open, false, 1f, 0.5f);
        Assert.Equal(0.5f, open, 4);
        Assert.Equal(1f, GliderMath.Animate(0f, true, 0f, 0.01f));
    }

    [Fact]
    public void Mast_rises_before_the_wings_spread()
    {
        Assert.Equal(1f, GliderMath.Rise(0.4f));
        Assert.Equal(0f, GliderMath.Spread(0.2f));
        Assert.True(GliderMath.Spread(0.6f) > 0f && GliderMath.Spread(0.6f) < 1f);
    }
}
