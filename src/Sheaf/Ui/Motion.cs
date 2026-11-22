using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using Sheaf.Platform;

namespace Sheaf.Ui;

internal enum Easing
{
    /// <summary>Entering, leaving, a press: cubic-bezier(0.23, 1, 0.32, 1).</summary>
    Out,
    /// <summary>Something moving across the screen: cubic-bezier(0.77, 0, 0.175, 1).</summary>
    InOut,
    /// <summary>Hover and colour changes: the CSS "ease" curve.</summary>
    Standard,
    /// <summary>Sheets and toasts: cubic-bezier(0.32, 0.72, 0, 1).</summary>
    Drawer,
    /// <summary>Something let go that settles into place: a damped spring that overshoots by about 4 %.</summary>
    Spring,
    /// <summary>Plain time, for an animation that eases its parts itself.</summary>
    Linear,
}

/// <summary>
/// One clock drives every running animation, once per screen refresh (<see cref="FrameClock"/>), interpolated by
/// elapsed time so a dropped frame never slows it down. With Windows' "Show animations" off, every animation jumps
/// to its end at once. Keyboard shortcuts never animate.
/// </summary>
internal static class Motion
{
    static readonly List<IFrame> Running = [];

    public static bool Enabled => Native.AnimationsEnabled;

    public static Tween Run(int durationMs, Action<double> step, Easing easing = Easing.Out, Action? done = null)
    {
        var tween = new Tween(durationMs, step, easing, done);
        if (!Enabled || durationMs <= 0)
        {
            tween.Complete();
            return tween;
        }
        Running.Add(tween);
        FrameClock.Start(Step);
        return tween;
    }

    /// <summary>
    /// Calls <paramref name="frame"/> every frame with the seconds since the last one, until it returns false: for
    /// motion with no set length, like a <see cref="Spring"/>. Callers check <see cref="Enabled"/> themselves.
    /// </summary>
    public static FrameLoop Loop(Func<double, bool> frame)
    {
        var loop = new FrameLoop(frame);
        Running.Add(loop);
        FrameClock.Start(Step);
        return loop;
    }

    static void Step()
    {
        foreach (IFrame animation in Running.ToArray())
            if (animation.Advance()) Running.Remove(animation);
        if (Running.Count == 0) FrameClock.Stop();
    }

    internal static void Forget(IFrame animation) => Running.Remove(animation);

    public static double Ease(Easing easing, double t) => easing switch
    {
        Easing.Out => Bezier(0.23, 1, 0.32, 1, t),
        Easing.InOut => Bezier(0.77, 0, 0.175, 1, t),
        Easing.Drawer => Bezier(0.32, 0.72, 0, 1, t),
        Easing.Spring => t >= 1 ? 1 : 1 - Math.Exp(-8 * t) * Math.Cos(8 * t),
        Easing.Linear => Math.Clamp(t, 0, 1),
        _ => Bezier(0.25, 0.1, 0.25, 1, t),
    };

    /// <summary>The y of a CSS cubic-bezier at progress x, found by Newton's method on x(t).</summary>
    static double Bezier(double x1, double y1, double x2, double y2, double x)
    {
        if (x <= 0) return 0;
        if (x >= 1) return 1;
        double t = x;
        for (int i = 0; i < 8; i++)
        {
            double error = Curve(x1, x2, t) - x;
            double slope = Slope(x1, x2, t);
            if (Math.Abs(error) < 1e-5 || Math.Abs(slope) < 1e-6) break;
            t = Math.Clamp(t - error / slope, 0, 1);
        }
        return Curve(y1, y2, t);
    }

    static double Curve(double p1, double p2, double t) => 3 * (1 - t) * (1 - t) * t * p1 + 3 * (1 - t) * t * t * p2 + t * t * t;

    static double Slope(double p1, double p2, double t) => 3 * (1 - t) * (1 - t) * p1 + 6 * (1 - t) * t * (p2 - p1) + 3 * t * t * (1 - p2);
}

internal interface IFrame
{
    /// <summary>Draws the next frame. Returns true when the animation has ended.</summary>
    bool Advance();
}

internal sealed class Tween(int durationMs, Action<double> step, Easing easing, Action? done) : IFrame
{
    // Starts on the first frame, so work done right after the animation began (a page switch, say) doesn't skip it ahead.
    readonly Stopwatch _clock = new();
    bool _finished;

    public bool IsRunning => !_finished;

    public bool Advance()
    {
        if (_finished) return true;
        _clock.Start();
        double progress = Math.Min(1, _clock.Elapsed.TotalMilliseconds / durationMs);
        step(Motion.Ease(easing, progress));
        if (progress < 1) return false;
        Complete();
        return true;
    }

    internal void Complete()
    {
        if (_finished) return;
        _finished = true;
        step(1);
        done?.Invoke();
    }

    /// <summary>Stops where it is, without jumping to the end.</summary>
    public void Cancel()
    {
        _finished = true;
        Motion.Forget(this);
    }
}

/// <summary>
/// Ticks once per screen refresh while animations run. A background thread waits for each frame of the desktop
/// compositor and posts one step to the UI thread, so steps keep in time with the screen rather than with the coarse
/// 15.6 ms Windows timer; when the UI thread falls behind, a frame is skipped instead of steps piling up.
/// </summary>
internal static class FrameClock
{
    const double FallbackFrameMs = 1000 / 60.0;
    static readonly ManualResetEventSlim Wanted = new(false);
    static SynchronizationContext? _ui;
    static Thread? _thread;
    static Action? _tick;
    static int _queued;
    static bool _precise;

    public static void Start(Action tick)
    {
        _tick = tick;
        _ui ??= SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        if (!_precise) _precise = Native.TimeBeginPeriod(1) == 0;
        _thread ??= StartThread();
        Wanted.Set();
    }

    public static void Stop()
    {
        Wanted.Reset();
        if (_precise && Native.TimeEndPeriod(1) == 0) _precise = false;
    }

    static Thread StartThread()
    {
        var thread = new Thread(Run) { IsBackground = true, Name = "Frame clock", Priority = ThreadPriority.AboveNormal };
        thread.Start();
        return thread;
    }

    static void Run()
    {
        var clock = Stopwatch.StartNew();
        double last = 0;
        while (true)
        {
            Wanted.Wait();
            double before = clock.Elapsed.TotalMilliseconds;
            // With nothing to show, the compositor may not wait for a frame; then keep to 60 a second by sleeping.
            if (Native.DwmFlush() != 0 || clock.Elapsed.TotalMilliseconds - before < 1)
            {
                double wait = last + FallbackFrameMs - clock.Elapsed.TotalMilliseconds;
                if (wait > 0) Thread.Sleep((int)Math.Ceiling(wait));
            }
            last = clock.Elapsed.TotalMilliseconds;
            if (Interlocked.Exchange(ref _queued, 1) == 1) continue;
            try
            {
                _ui!.Post(_ =>
                {
                    Volatile.Write(ref _queued, 0);
                    _tick?.Invoke();
                }, null);
            }
            catch (Exception ex) when (ex is InvalidAsynchronousStateException or ObjectDisposedException)
            {
                return; // the app is closing
            }
        }
    }
}

/// <summary>A callback run every frame until it returns false or is stopped; see <see cref="Motion.Loop"/>.</summary>
internal sealed class FrameLoop(Func<double, bool> frame) : IFrame
{
    readonly Stopwatch _clock = Stopwatch.StartNew();
    double _last;
    bool _stopped;

    public bool IsRunning => !_stopped;

    public bool Advance()
    {
        if (_stopped) return true;
        double now = _clock.Elapsed.TotalSeconds;
        // A long gap (the window was dragged, the PC was busy) counts as about two frames: motion pauses rather than leaps.
        double seconds = Math.Min(now - _last, 0.034);
        _last = now;
        if (frame(seconds)) return false;
        _stopped = true;
        return true;
    }

    public void Stop()
    {
        _stopped = true;
        Motion.Forget(this);
    }
}

/// <summary>
/// A value pulled toward <see cref="Target"/> by a damped spring, stepped in real time. It keeps its speed when the
/// target moves, so motion that is thrown or interrupted carries on smoothly instead of starting again.
/// </summary>
/// <param name="response">Seconds for one swing: smaller is snappier.</param>
/// <param name="damping">1 settles without overshooting; lower bounces more.</param>
internal sealed class Spring(double response, double damping)
{
    const double MaxStep = 1 / 480.0;

    public double Value { get; set; }
    public double Velocity { get; set; }
    public double Target { get; set; }
    public double Response { get; set; } = response;
    public double Damping { get; set; } = damping;

    public void Step(double seconds)
    {
        double stiffness = Math.Pow(2 * Math.PI / Response, 2), friction = 4 * Math.PI * Damping / Response;
        // Small fixed steps keep a stiff spring stable however long the frame took.
        for (double left = seconds; left > 1e-9; left -= MaxStep)
        {
            double dt = Math.Min(MaxStep, left);
            Velocity += ((Target - Value) * stiffness - Velocity * friction) * dt;
            Value += Velocity * dt;
        }
    }

    /// <summary>Near enough to the target, and slow enough, that stopping there can't be seen.</summary>
    public bool IsSettled(double tolerance) => Math.Abs(Target - Value) < tolerance && Math.Abs(Velocity) < tolerance * 20;

    public void Snap()
    {
        Value = Target;
        Velocity = 0;
    }
}

/// <summary>A value that glides between 0 and 1, for hover and press states that blend colours.</summary>
internal sealed class Fade(Control owner, int durationMs = 120)
{
    Tween? _tween;
    double _target;

    public double Value { get; private set; }

    public void To(double target)
    {
        if (Math.Abs(target - _target) < 0.001 && (_tween?.IsRunning ?? false)) return;
        _tween?.Cancel();
        _target = target;
        double from = Value;
        _tween = Motion.Run(durationMs, progress =>
        {
            Value = from + (target - from) * progress;
            if (!owner.IsDisposed) owner.Invalidate();
        }, Easing.Standard);
    }

    public void Set(double value)
    {
        _tween?.Cancel();
        Value = _target = value;
    }
}
