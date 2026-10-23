namespace HDMICaptain.Core.Audio;

/// <summary>
/// Keeps the ring fill near a target by nudging the resample ratio. The capture and render clocks differ by tens of ppm;
/// a PI loop on the smoothed fill absorbs that without dropping or repeating samples audibly.
/// </summary>
public sealed class DriftController
{
    public const double MaxPpm = 2000;
    private const double SmoothingSeconds = 0.5;
    private const double ProportionalPpmPerMs = 40;
    private const double IntegralPpmPerMsSecond = 1;
    private const double RetargetMsPerSecond = 200;
    private const double HeadroomMs = 3; // packets land ~1.3 ms after the ideal phase and jitter ~0.8 ms (task 102 log)

    private readonly double _sampleRate;
    private readonly double _kp, _ki;
    private double _targetMs;
    private double _currentTargetMs;
    private double _smoothedFrames;
    private double _integralPpm;
    private bool _primed;

    /// <param name="gainScale">
    /// Multiplies both loop gains (the damping stays the same, settling is that many times faster). 1 suits real cards
    /// (tens of ppm); WSLg's RDP sink runs ~600 ppm fast, which the default loop takes ~2 min to absorb (task 111).
    /// </param>
    public DriftController(double sampleRate, double targetMs, double gainScale = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(gainScale, 0);
        _sampleRate = sampleRate;
        // ζ ∝ Kp / √Ki: scaling Kp by s and Ki by s² keeps it.
        _kp = ProportionalPpmPerMs * gainScale;
        _ki = IntegralPpmPerMsSecond * gainScale * gainScale;
        _targetMs = _currentTargetMs = targetMs;
    }

    /// <summary>Desired fill in milliseconds (delay + base buffer). The loop slews toward a new value at 200 ms/s.</summary>
    public double TargetMs
    {
        get => Volatile.Read(ref _targetMs);
        set => Volatile.Write(ref _targetMs, value);
    }

    public double SmoothedFillMs => _smoothedFrames * 1000 / _sampleRate;

    /// <summary>The target the loop is currently steering toward (follows <see cref="TargetMs"/> smoothly).</summary>
    public double CurrentTargetMs => _currentTargetMs;

    /// <summary>
    /// The lowest target (for <see cref="PhaseFreeFillFrames"/>) that never runs dry: just before a packet lands the ring
    /// holds a whole packet less, and a render read then still needs a full period plus the resampler's few frames of
    /// look-ahead. With 10 ms packets and periods a 20 ms target left no margin and underflowed every ~9 min (task 102).
    /// </summary>
    public static double MinimumTargetMs(double capturePacketMs, double renderPeriodMs) =>
        Math.Max(0, capturePacketMs) + Math.Max(0, renderPeriodMs) + HeadroomMs;

    /// <summary>
    /// The fill the loop should see: the ring's fill plus the audio that has arrived since the last capture packet
    /// (up to one packet). The raw fill is a sawtooth whose value at a render read depends on whether the latest packet
    /// landed just before or just after it; when the two clocks drift across that point the raw reading jumps by a whole
    /// packet and the loop chases a change in phase, not in latency (task 102). This reading stays flat through it.
    /// </summary>
    public static double PhaseFreeFillFrames(int fillFrames, double msSinceCapturePacket, double capturePacketMs, double sampleRate) =>
        fillFrames + Math.Clamp(msSinceCapturePacket, 0, Math.Max(0, capturePacketMs)) * sampleRate / 1000;

    /// <summary>Clears loop state, e.g. after the ring was cleared or the stream restarted.</summary>
    public void Reset()
    {
        _primed = false;
        _integralPpm = 0;
        _currentTargetMs = TargetMs;
    }

    /// <summary>
    /// Render thread, once per period. Returns the correction (about 1.0) to multiply the nominal input/output ratio by:
    /// above 1 consumes input faster (fill too high). No allocations.
    /// </summary>
    public double Update(double fillFrames, double elapsedSeconds)
    {
        if (!_primed)
        {
            _smoothedFrames = fillFrames;
            _currentTargetMs = TargetMs;
            _primed = true;
        }
        double dt = Math.Clamp(elapsedSeconds, 0, 1);
        if (dt > 0)
        {
            _smoothedFrames += (fillFrames - _smoothedFrames) * (1 - Math.Exp(-dt / SmoothingSeconds));
            double step = RetargetMsPerSecond * dt;
            _currentTargetMs += Math.Clamp(TargetMs - _currentTargetMs, -step, step);
        }

        double errorMs = SmoothedFillMs - _currentTargetMs;
        _integralPpm = Math.Clamp(_integralPpm + _ki * errorMs * dt, -MaxPpm, MaxPpm);
        double ppm = Math.Clamp(_kp * errorMs + _integralPpm, -MaxPpm, MaxPpm);
        return 1 + ppm * 1e-6;
    }
}
