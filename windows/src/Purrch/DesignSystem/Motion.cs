using System.Windows;
using System.Windows.Media.Animation;

namespace Purrch.DesignSystem;

/// Motion curves shared by the whole panel, tuned to feel like the Mac app's springs.
public static class Motion
{
    /// Opening: quick start with a small overshoot, like the panel is stretching out.
    public static readonly Curve Open = new(0.46, Frozen(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.22 }));
    /// Closing: fast and settled, no bounce.
    public static readonly Curve Close = new(0.3, Frozen(new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 5 }));
    /// Small springy pop for selections.
    public static readonly Curve Pop = new(0.32, Frozen(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 }));
    /// Hover anticipation on the closed pill.
    public static readonly Curve Peek = new(0.3, Frozen(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 }));

    /// Expo-out for fades and slides, close to cubic-bezier(.16, 1, .3, 1).
    public static Curve ExpoOut(double seconds = 0.5) =>
        new(seconds, Frozen(new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 7 }));

    /// Plain ease-out for small state changes such as ticks and fades, like SwiftUI's .easeOut(duration:).
    public static Curve EaseOut(double seconds) =>
        new(seconds, Frozen(new QuadraticEase { EasingMode = EasingMode.EaseOut }));

    public static Curve Linear(double seconds) => new(seconds, null);

    private static EasingFunctionBase Frozen(EasingFunctionBase easing)
    {
        easing.Freeze();
        return easing;
    }

    /// Animates a double property from its current (possibly mid-animation) value.
    public static AnimationTimeline Animate(this IAnimatable target, DependencyProperty property, double to,
                                            Curve curve, double delay = 0, Action? completed = null)
    {
        var animation = new DoubleAnimation(to, TimeSpan.FromSeconds(curve.Seconds))
        {
            EasingFunction = curve.Easing,
            BeginTime = TimeSpan.FromSeconds(delay),
        };
        if (completed != null) animation.Completed += (_, _) => completed();
        target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
        return animation;
    }

    /// Jumps straight to a value, cancelling any running animation.
    public static void Set(this IAnimatable target, DependencyProperty property, double value)
    {
        target.BeginAnimation(property, null);
        if (target is DependencyObject d) d.SetValue(property, value);
    }
}

public sealed record Curve(double Seconds, IEasingFunction? Easing);
