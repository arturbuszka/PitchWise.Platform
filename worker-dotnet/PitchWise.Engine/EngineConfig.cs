namespace PitchWise.Engine;

/// <summary>
/// Every number the engine believes about football. All of them are starting guesses meant to
/// be tuned offline against a recorded <c>world_state.jsonl</c> via <see cref="WorldStateReplay"/>,
/// not by re-running video.
/// </summary>
public sealed record EngineConfig
{
    // --- pitch ---
    public double PitchLength { get; init; } = 105.0;   // metres, goal line to goal line
    public double PitchWidth { get; init; } = 68.0;

    // --- ball filter (BallFilter) ---

    /// <summary>Detections below this are ignored outright.</summary>
    public double MinBallConfidence { get; init; } = 0.35;

    /// <summary>Gate: a measurement further than this from the prediction is rejected as
    /// spurious (another round object, a mis-detection) and the filter coasts. Scaled by dt,
    /// so this is really a speed cap: 40 m/s is far above any struck ball's ground speed.</summary>
    public double MaxBallSpeed { get; init; } = 40.0;   // m/s

    /// <summary>After this long with no accepted measurement, the ball is declared lost:
    /// confidence 0, and possession/pass rules go quiet. Measured on broadcast footage at
    /// stride 5, the gap between ball detections has a median of ~0.7 s, so anything under a
    /// second declares the ball lost half the time. The long tail (6 s) is genuinely lost.</summary>
    public double MaxCoastSeconds { get; init; } = 1.5;

    /// <summary>Process noise: how much the constant-velocity assumption is allowed to be wrong
    /// (a ball gets kicked — acceleration is not zero). m/s^2.</summary>
    public double BallProcessNoise { get; init; } = 12.0;

    /// <summary>Measurement noise: expected error of a projected ball position, in metres.
    /// Dominated by homography error and the Z=0 assumption, not by YOLO's box jitter.</summary>
    public double BallMeasurementNoise { get; init; } = 0.8;

    // --- possession (PossessionTracker) ---

    /// <summary>A player is close enough to control the ball.</summary>
    public double ControlRadius { get; init; } = 1.5;   // m

    /// <summary>How long a challenger must stay the nearest eligible player before possession
    /// transfers. Shorter than <see cref="ReleaseGrace"/> — that asymmetry IS the hysteresis.</summary>
    public double CaptureDwell { get; init; } = 0.30;   // s

    /// <summary>How long the current owner keeps possession after the ball leaves their radius.
    /// Longer than <see cref="CaptureDwell"/>, so ownership is sticky and does not flicker.</summary>
    public double ReleaseGrace { get; init; } = 0.60;   // s

    /// <summary>Two opponents inside this radius may be duelling.</summary>
    public double ContestedRadius { get; init; } = 2.0;  // m

    /// <summary>...and if their distances to the ball differ by less than this, neither owns it.</summary>
    public double ContestMargin { get; init; } = 0.5;    // m

    // --- rules ---

    /// <summary>Max time between one player releasing the ball and another receiving it for the
    /// two touches to count as one pass. Longer than this and the ball was loose, not passed.</summary>
    public double PassMaxFlightSeconds { get; init; } = 4.0;

    /// <summary>The ball must actually travel this far for a release+receive to be a pass rather
    /// than a scramble or a failed touch.</summary>
    public double PassMinDistance { get; init; } = 3.0;   // m

    /// <summary>Global per-type event cooldown, mirroring EventConfig.CooldownSeconds.</summary>
    public double EventCooldownSeconds { get; init; } = 1.0;
}
