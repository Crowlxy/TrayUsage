namespace TrayUsage;

// The opening, closing and morphing springs follow the macOS Dynamic Island: DynamicNotchKit (MIT License,
// Copyright (c) 2025 Kai Azim, https://github.com/MrKai77/DynamicNotchKit) opens with a bouncy spring of 0.4 s, closes with a
// smooth one of 0.4 s, converts with a snappy one of 0.4 s, and fades the window 0.25 s into the close over 0.15 s. Only those
// numbers and the order of events are reused. The rest of the motion model (separate top spring, shrink floor,
// content drop and delayed content fade) and the shadow curve follow the design of Oyenza Studio,
// MIT License, Copyright (c) 2026 Jatevo Labs:
//   https://github.com/lucacadalora/oyenza-studio  @ 6382a85be2fd149374558a5bcecd1426ede6c53b
//   crates/poni/src/pill/motion.rs, crates/poni/src/pill/shape.rs
// Only the numbers and the idea are reused; the code here is a fresh C# implementation.
// See THIRD_PARTY_NOTICES.md.

/// <summary>Spring constants for a unit mass: acceleration = -Stiffness * offset - Damping * velocity.</summary>
internal readonly record struct Tuning(double Stiffness, double Damping);

internal static class Motion
{
    /// <summary>SwiftUI's <c>.spring(duration:bounce:)</c> as spring constants: stiffness (2π/duration)², damping 2(1 − bounce)·2π/duration.</summary>
    public static Tuning FromSwiftUI(double duration, double bounce)
    {
        var omega = 2 * Math.PI / duration;
        return new(omega * omega, 2 * (1 - bounce) * omega);
    }

    // SwiftUI's presets with duration 0.4 s: .bouncy (bounce 0.3), .smooth (0), .snappy (0.15).
    public static readonly Tuning Bouncy = FromSwiftUI(0.4, 0.3);
    public static readonly Tuning Smooth = FromSwiftUI(0.4, 0);
    public static readonly Tuning Snappy = FromSwiftUI(0.4, 0.15);
    /// <summary>The closed pill sliding up into the screen edge (critical, about 0.1 s to clear the edge).</summary>
    public static readonly Tuning Retract = new(900, 60);
    public static readonly Tuning Top = new(300, 34.6);
    public static readonly Tuning Fade = new(700, 52.9);
    // Not from Oyenza: tab indicator (zeta 0.75, a hair of overshoot) and meter fill (critical, never overshoots 100 %).
    public static readonly Tuning Tab = new(520, 34.2);
    public static readonly Tuning Meter = new(90, 19.0);
}

/// <summary>One animated value. A shrinking spring never drops below its target, so shapes do not squash.</summary>
internal sealed class Spring
{
    private Tuning _tuning = Motion.Fade;
    private bool _shrinking;

    public Spring(double initial) => Value = Target = initial;

    public double Value { get; private set; }
    public double Target { get; private set; }
    private double _velocity;

    public void Retarget(double target, Tuning grow, Tuning shrink)
    {
        _shrinking = target < Value;
        _tuning = _shrinking ? shrink : grow;
        Target = target;
    }

    /// <summary>Retarget without the shrink floor: the value may overshoot on either side.</summary>
    public void Aim(double target, Tuning tuning)
    {
        _shrinking = false;
        _tuning = tuning;
        Target = target;
    }

    public void Jump(double value)
    {
        Value = Target = value;
        _velocity = 0;
    }

    public void Snap() => Jump(Target);

    public void Advance(double dt)
    {
        _velocity += (-_tuning.Stiffness * (Value - Target) - _tuning.Damping * _velocity) * dt;
        Value += _velocity * dt;
        if (_shrinking && Value < Target)
        {
            Value = Target;
            if (_velocity < 0) _velocity = 0;
        }
    }

    public bool Settled(double position, double velocity) =>
        Math.Abs(Value - Target) < position && Math.Abs(_velocity) < velocity;
}

/// <summary>
/// Key shadow (black 0.32, sigma 6) plus contact shadow (0.20, sigma 1.5), sampled from Oyenza's ramp
/// and rebuilt as stacked rounded rectangles, since GDI+ has no blur.
/// </summary>
internal static class Shadow
{
    public const float Margin = 20, Drop = 4, Reach = 15;
    private const float Step = 1.5f;

    // Distance outside the body edge (DIP) and the opacity there.
    private static readonly (double Distance, double Opacity)[] Ramp =
    [
        (-6, 0.4121), (-5, 0.3906), (-4.25, 0.3641), (-3.25, 0.3136), (-1.75, 0.2292), (-0.75, 0.1869),
        (0.5, 0.1510), (3.25, 0.0941), (6, 0.0508), (8.75, 0.0232), (12, 0.0073), (15, 0)
    ];

    /// <summary>Outermost first. Each layer's alpha is chosen so the stack composites to the ramp.</summary>
    public static readonly (float Inflate, float Alpha)[] Layers = Build();

    private static (float, float)[] Build()
    {
        var layers = new (float, float)[(int)(Reach / Step) + 1];
        var covered = 0.0;
        for (var i = 0; i < layers.Length; i++)
        {
            var distance = Reach - i * Step;
            var wanted = Math.Max(Sample(distance - Step / 2), covered);
            layers[i] = ((float)distance, (float)(1 - (1 - wanted) / (1 - covered)));
            covered = wanted;
        }
        return layers;
    }

    private static double Sample(double distance)
    {
        if (distance >= Ramp[^1].Distance) return Ramp[^1].Opacity;
        for (var i = 1; i < Ramp.Length; i++)
        {
            if (distance > Ramp[i].Distance) continue;
            var (d0, o0) = Ramp[i - 1];
            var (d1, o1) = Ramp[i];
            return o0 + (o1 - o0) * (distance - d0) / (d1 - d0);
        }
        return Ramp[0].Opacity;
    }
}
