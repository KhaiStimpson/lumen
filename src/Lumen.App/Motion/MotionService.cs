using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Styling;

namespace Lumen.App.Motion;

/// <summary>The Gauge motion roles (gauge-design-kit COMPONENTS.md, Motion), with the kit's timings.</summary>
public enum MotionPreset
{
    /// <summary>Hover, pressed, selected, toggled: colour only, motion-fast.</summary>
    State,

    /// <summary>Menus, popovers, toasts: motion-fast, ease-enter.</summary>
    Reveal,

    /// <summary>Sheets and the docked inspector: motion-base in.</summary>
    Panel,

    /// <summary>Moving between peers or levels: motion-base, a motion-shift slide plus a fade.</summary>
    Navigate,

    /// <summary>Rows added, removed or reordered: motion-base.</summary>
    Layout,
}

/// <summary>
/// Motion explains a change (Gauge DESIGN.md, Motion): where something came from or went. Every role has a
/// reduced-motion equivalent: a fast crossfade or an instant change, and instant scrolls.
/// </summary>
public interface IMotionService
{
    bool ReducedMotion { get; }

    /// <summary>Morphs <paramref name="source"/> into <paramref name="destination"/> (a shared-element style transition).</summary>
    Task TransitionAsync(Visual source, Visual destination, MotionPreset preset, CancellationToken cancellationToken);

    /// <summary>Fades and lifts a control into view.</summary>
    Task RevealAsync(Visual target, MotionPreset preset, CancellationToken cancellationToken);

    /// <summary>Briefly contracts a control, e.g. the review card being left.</summary>
    Task ContractAsync(Visual target, CancellationToken cancellationToken);

    /// <summary>Drives a scroll offset to <paramref name="target"/> with a critically damped spring.</summary>
    Task SpringAsync(double from, double target, Action<double> apply, CancellationToken cancellationToken);
}

public sealed class MotionService(Func<bool> reducedMotion) : IMotionService
{
    private static readonly Easing Standard = new SplineEasing(0.2, 0, 0, 1);
    private static readonly Easing Decelerate = new SplineEasing(0, 0, 0, 1);

    public bool ReducedMotion => reducedMotion();

    /// <summary>tokens.json: motion-fast 120ms, motion-base 180ms, motion-slow 240ms.</summary>
    public static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(120);

    public static readonly TimeSpan Base = TimeSpan.FromMilliseconds(180);

    public static readonly TimeSpan Slow = TimeSpan.FromMilliseconds(240);

    public static TimeSpan DurationFor(MotionPreset preset) => preset switch
    {
        MotionPreset.State or MotionPreset.Reveal => Fast,
        _ => Base,
    };

    public async Task TransitionAsync(Visual source, Visual destination, MotionPreset preset, CancellationToken cancellationToken)
    {
        if (ReducedMotion || TopLevel.GetTopLevel(destination) is not { } top ||
            source.TransformToVisual(top) is not { } fromTransform || destination.TransformToVisual(top) is not { } toTransform)
        {
            await SafeAsync(destination, () => FadeAsync(destination, 0, 1, TimeSpan.FromMilliseconds(120), cancellationToken)).ConfigureAwait(true);
            return;
        }

        var from = new Rect(source.Bounds.Size).TransformToAABB(fromTransform);
        var to = new Rect(destination.Bounds.Size).TransformToAABB(toTransform);
        if (to.Width < 1 || to.Height < 1)
        {
            return;
        }

        // Start the destination where the source is, scaled to the source's footprint, then let it settle.
        var scaleX = Math.Clamp(from.Width / to.Width, 0.2, 1.5);
        var scaleY = Math.Clamp(from.Height / to.Height, 0.05, 1.5);
        var start = Invariant($"translate({from.X - to.X:0.##}px, {from.Y - to.Y:0.##}px) scale({scaleX:0.###}, {scaleY:0.###})");
        destination.RenderTransformOrigin = RelativePoint.TopLeft;

        var duration = DurationFor(preset);
        await SafeAsync(destination, () => Task.WhenAll(
            TransformAsync(destination, start, "translate(0px, 0px) scale(1, 1)", duration, Standard, cancellationToken),
            FadeAsync(destination, 0.4, 1, duration, cancellationToken))).ConfigureAwait(true);
    }

    public async Task RevealAsync(Visual target, MotionPreset preset, CancellationToken cancellationToken)
    {
        if (ReducedMotion)
        {
            await SafeAsync(target, () => FadeAsync(target, 0, 1, Fast, cancellationToken)).ConfigureAwait(true);
            return;
        }

        var duration = DurationFor(preset);
        // motion-shift is 24px for panels and navigation; a reveal grows in place, so it only lifts a little.
        var lift = preset is MotionPreset.Panel or MotionPreset.Navigate ? 24 : 6;
        await SafeAsync(target, () => Task.WhenAll(
            TransformAsync(target, Invariant($"translate(0px, {lift}px)"), "translate(0px, 0px)", duration, Decelerate, cancellationToken),
            FadeAsync(target, 0, 1, duration, cancellationToken))).ConfigureAwait(true);
    }

    public async Task ContractAsync(Visual target, CancellationToken cancellationToken)
    {
        if (ReducedMotion)
        {
            return;
        }

        target.RenderTransformOrigin = RelativePoint.Center;
        var duration = TimeSpan.FromMilliseconds(120);
        await SafeAsync(target, () => Task.WhenAll(
            TransformAsync(target, "scale(1, 1)", "scale(0.985, 0.97)", duration, Standard, cancellationToken),
            FadeAsync(target, 1, 0.55, duration, cancellationToken))).ConfigureAwait(true);
    }

    public async Task SpringAsync(double from, double target, Action<double> apply, CancellationToken cancellationToken)
    {
        if (ReducedMotion || Math.Abs(target - from) < 1)
        {
            apply(target);
            return;
        }

        // Critically damped spring: fast, no overshoot, settles in roughly 250–320 ms.
        const double stiffness = 260;
        var damping = 2 * Math.Sqrt(stiffness);
        var position = from;
        var velocity = 0.0;
        var last = DateTime.UtcNow;
        var started = last;

        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(8, CancellationToken.None).ConfigureAwait(true);
            var now = DateTime.UtcNow;
            var dt = Math.Min((now - last).TotalSeconds, 1 / 30.0);
            last = now;

            // Semi-implicit Euler in a few sub-steps for stability.
            for (var i = 0; i < 4; i++)
            {
                var h = dt / 4;
                var acceleration = (-stiffness * (position - target)) - (damping * velocity);
                velocity += acceleration * h;
                position += velocity * h;
            }

            apply(position);
            if ((Math.Abs(position - target) < 0.5 && Math.Abs(velocity) < 5) || (now - started).TotalMilliseconds > 900)
            {
                break;
            }
        }

        apply(target);
    }

    /// <summary>Motion must never break the product: whatever happens, leave the visual at rest.</summary>
    private static async Task SafeAsync(Visual target, Func<Task> animate)
    {
        try
        {
            await animate().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            System.Diagnostics.Trace.TraceWarning($"Animation failed: {ex.Message}");
        }
        finally
        {
            target.RenderTransform = null;
            target.Opacity = 1;
        }
    }

    private static Task FadeAsync(Visual target, double from, double to, TimeSpan duration, CancellationToken cancellationToken) =>
        Animate(target, Visual.OpacityProperty, from, to, duration, Decelerate, cancellationToken);

    private static Task TransformAsync(Visual target, string from, string to, TimeSpan duration, Easing easing, CancellationToken cancellationToken) =>
        Animate(target, Visual.RenderTransformProperty, TransformOperations.Parse(from), TransformOperations.Parse(to), duration, easing, cancellationToken);

    private static string Invariant(FormattableString value) => FormattableString.Invariant(value);

    private static Task Animate(
        Animatable target,
        AvaloniaProperty property,
        object from,
        object to,
        TimeSpan duration,
        Easing easing,
        CancellationToken cancellationToken)
    {
        var animation = new Animation
        {
            Duration = duration,
            Easing = easing,
            FillMode = FillMode.None,
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(property, from) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(property, to) } },
            },
        };
        return animation.RunAsync(target, cancellationToken);
    }
}

/// <summary>Reads the OS "show animations" preference (TDD §25.4).</summary>
public static partial class SystemMotionPreference
{
    private const uint SpiGetClientAreaAnimation = 0x1042;

    public static bool PrefersReducedMotion()
    {
        if (Environment.GetEnvironmentVariable("LUMEN_REDUCED_MOTION") is { Length: > 0 } flag)
        {
            return flag is "1" or "true";
        }

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            return SystemParametersInfo(SpiGetClientAreaAnimation, 0, out var enabled, 0) && !enabled;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(uint action, uint param, [MarshalAs(UnmanagedType.Bool)] out bool value, uint winIni);
}
