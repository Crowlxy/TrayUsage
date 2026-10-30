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

/// <summary>
/// How a <see cref="Spring"/> travels to a new target: the spring itself, and whether a shrinking value is stopped at its target
/// (<paramref name="Floor"/>: a shape that gets smaller never squashes below its final size, an alpha never goes negative) or may
/// swing past it and come back.
/// </summary>
internal readonly record struct Move(Tuning Spring, bool Floor);

/// <summary>
/// Every spring and every duration of the island's motion, in one place. Code never writes a spring constant, a bounce or a delay
/// of its own: it picks a role below, so a feel is changed here once and cannot drift between call sites.
/// <list type="table">
/// <item><term>Open</term><description>the island appears (bouncy)</description></item>
/// <item><term>Close</term><description>the island leaves (smooth, no bounce on the way in)</description></item>
/// <item><term>Morph</term><description>one surface becomes another (snappy)</description></item>
/// <item><term>Page</term><description>another page of the same surface; swings past on the way in as well</description></item>
/// <item><term>Rise</term><description>vertical position and hover lift</description></item>
/// <item><term>Fade</term><description>every opacity: content, outgoing content, window</description></item>
/// <item><term>Slide</term><description>tab highlight, gear, switch knobs</description></item>
/// </list>
/// </summary>
internal static class Motion
{
    /// <summary>SwiftUI's <c>.spring(duration:bounce:)</c> as spring constants: stiffness (2π/duration)², damping 2(1 − bounce)·2π/duration.</summary>
    public static Tuning FromSwiftUI(double duration, double bounce)
    {
        var omega = 2 * Math.PI / duration;
        return new(omega * omega, 2 * (1 - bounce) * omega);
    }

    // SwiftUI's presets with duration 0.4 s: .bouncy (bounce 0.3), .smooth (0), .snappy (0.15).
    public static readonly Move Open = new(FromSwiftUI(0.4, 0.3), Floor: true);
    public static readonly Move Close = new(FromSwiftUI(0.4, 0), Floor: true);
    public static readonly Move Morph = new(FromSwiftUI(0.4, 0.15), Floor: true);
    // Not a SwiftUI preset: Morph overshoots only 0.6 %, too little to see on a tab change.
    public static readonly Move Page = new(FromSwiftUI(0.45, 0.35), Floor: false);
    public static readonly Move Rise = new(new(300, 34.6), Floor: true);
    public static readonly Move Fade = new(new(700, 52.9), Floor: true);
    // Not from Oyenza: switch knobs (zeta 0.75, a hair of overshoot).
    public static readonly Move Slide = new(new(520, 34.2), Floor: false);

    /// <summary>Seconds the content waits for the shape before it fades in: a growing shape is read sooner than a shrinking one.</summary>
    public const double GrowDelay = 0.03, ShrinkDelay = 0.05;

    /// <summary>Notice glow: one pulse lasts this long (twice over for warnings).</summary>
    public const double GlowSeconds = 1.8;

    /// <summary>A working session's ring makes one turn in this many seconds.</summary>
    public const double SweepSeconds = 1.1;

    /// <summary>DIPs the entering content drops through, how much of its height it is squeezed to at the start, and how small outgoing content gets.</summary>
    public const float ContentDrop = 2, EnterSqueeze = 0.6f, GhostMinScale = 0.6f;

    /// <summary>The resting capsule under the pointer: grows by <c>LiftSize</c> DIPs, floats up <c>LiftTop</c> and rounds <c>LiftRadius</c> more.</summary>
    public const float LiftSize = 6, LiftTop = 2, LiftRadius = 3;

    /// <summary>Integration: 2 ms substeps keep the stiffest spring (radius, 1200) stable; a stalled frame advances at most 50 ms.</summary>
    public const double Substep = 0.002, MaxFrame = 0.05;
}

/// <summary>
/// One animated value. Create it with <see cref="Pixels"/> or <see cref="Unit"/> so it knows when it has come to rest; the island
/// advances, snaps and checks every spring it owns the same way, so a new spring only has to be added to its list.
/// </summary>
internal sealed class Spring
{
    private Tuning _tuning = Motion.Fade.Spring;
    private bool _floored;
    private readonly double _restPosition, _restVelocity;
    private double _velocity;

    private Spring(double initial, double restPosition, double restVelocity)
    {
        Value = Target = initial;
        (_restPosition, _restVelocity) = (restPosition, restVelocity);
    }

    /// <summary>A value in DIPs or degrees: rests within 0.02 of its target.</summary>
    public static Spring Pixels(double initial) => new(initial, 0.02, 0.2);

    /// <summary>A value between 0 and 1 (opacity, progress): rests within 0.002 of its target.</summary>
    public static Spring Unit(double initial) => new(initial, 0.002, 0.02);

    public double Value { get; private set; }
    public double Target { get; private set; }

    public bool IsSettled => Math.Abs(Value - Target) < _restPosition && Math.Abs(_velocity) < _restVelocity;

    /// <summary>Aims at <paramref name="target"/> the way <paramref name="move"/> says.</summary>
    public void To(double target, Move move)
    {
        _floored = move.Floor && target < Value;
        _tuning = move.Spring;
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
        if (_floored && Value < Target)
        {
            Value = Target;
            if (_velocity < 0) _velocity = 0;
        }
    }
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
