namespace PitchWise.Engine;

/// <summary>
/// Turns a stuttering sequence of ball detections into a continuous trajectory.
///
/// Constant-velocity Kalman filter over (position, velocity). Because the process and
/// measurement models are axis-independent, the 4-state filter factorises exactly into two
/// 2-state filters — one per axis (see <see cref="Axis"/>). Same maths, half the code, no
/// hand-rolled 4x4 matrix multiply to get wrong.
///
/// Two things happen here that the rules downstream depend on:
///
/// 1. <b>Gating.</b> A measurement further from the prediction than the ball could physically
///    have travelled is rejected before it corrupts the state. Ball detectors routinely fire on
///    a head, a boot, or a line marking; without this every such blip becomes a 60 m/s "pass".
///
/// 2. <b>Coasting.</b> With no measurement the filter predicts forward and flags the state
///    <c>Interpolated</c>. After <see cref="EngineConfig.MaxCoastSeconds"/> it gives up and
///    reports zero confidence, so rules stop inventing possession for a ball nobody can see.
///
/// This generalises the gap interpolation in <c>Events.BuildTrack</c>, which only bridged short
/// gaps and had no notion of an impossible jump.
/// </summary>
public sealed class BallFilter
{
    /// <summary>One axis of a constant-velocity filter: state (p, v), covariance P (2x2,
    /// symmetric so only three entries are stored).</summary>
    private struct Axis
    {
        public double P;      // position
        public double V;      // velocity
        public double Cpp;    // cov(p,p)
        public double Cpv;    // cov(p,v) == cov(v,p)
        public double Cvv;    // cov(v,v)

        /// <summary>x' = F x, P' = F P F^T + Q, with F = [[1, dt], [0, 1]].</summary>
        public void Predict(double dt, double processNoise)
        {
            P += V * dt;

            // F P F^T, expanded.
            double cpp = Cpp + dt * (2 * Cpv + dt * Cvv);
            double cpv = Cpv + dt * Cvv;
            double cvv = Cvv;

            // Q for a constant-velocity model driven by white acceleration noise of
            // variance a^2: the standard [[dt^4/4, dt^3/2], [dt^3/2, dt^2]] * a^2.
            double a2 = processNoise * processNoise;
            double dt2 = dt * dt, dt3 = dt2 * dt, dt4 = dt2 * dt2;
            Cpp = cpp + 0.25 * dt4 * a2;
            Cpv = cpv + 0.5 * dt3 * a2;
            Cvv = cvv + dt2 * a2;
        }

        /// <summary>Scalar position measurement. Returns the innovation covariance so the
        /// caller can gate on it if it wants a Mahalanobis test.</summary>
        public double Update(double measurement, double measurementNoise)
        {
            double s = Cpp + measurementNoise * measurementNoise;   // innovation covariance
            double kp = Cpp / s;                                    // Kalman gain, position
            double kv = Cpv / s;                                    // Kalman gain, velocity
            double innovation = measurement - P;

            P += kp * innovation;
            V += kv * innovation;

            // (I - K H) P, with H = [1, 0].
            double cpp = Cpp, cpv = Cpv;
            Cpp = (1 - kp) * cpp;
            Cpv = (1 - kp) * cpv;
            Cvv -= kv * cpv;
            return s;
        }

        public void Reset(double position, double measurementNoise)
        {
            P = position;
            V = 0;
            Cpp = measurementNoise * measurementNoise;
            Cpv = 0;
            // No velocity information yet: start wide so the second measurement dominates.
            Cvv = 100.0;
        }
    }

    private readonly EngineConfig _cfg;

    private Axis _x;
    private Axis _y;
    private bool _initialized;
    private double _lastTimestamp;
    /// <summary>Timestamp of the last ACCEPTED measurement — drives the coast timeout.</summary>
    private double _lastMeasuredAt;
    private double _lastConfidence;

    public BallFilter(EngineConfig cfg) => _cfg = cfg;

    /// <summary>Advances the filter to <paramref name="timestampSeconds"/> and folds in the
    /// observation if it survives the confidence and physics gates.</summary>
    public BallState Step(BallObservation obs, double timestampSeconds)
    {
        bool usable = obs.Detected && obs.Confidence >= _cfg.MinBallConfidence;

        if (!_initialized)
        {
            // Nothing to predict from. Wait for the first usable measurement.
            if (!usable) return new BallState(0, 0, 0, 0, 0, true, 0);

            _x.Reset(obs.X, _cfg.BallMeasurementNoise);
            _y.Reset(obs.Y, _cfg.BallMeasurementNoise);
            _initialized = true;
            _lastTimestamp = timestampSeconds;
            _lastMeasuredAt = timestampSeconds;
            _lastConfidence = obs.Confidence;
            return new BallState(obs.X, obs.Y, 0, 0, 0, false, obs.Confidence);
        }

        double dt = timestampSeconds - _lastTimestamp;
        _lastTimestamp = timestampSeconds;
        // Out-of-order or duplicate frame: predicting backwards would blow up the covariance.
        if (dt <= 0) dt = 1e-3;

        _x.Predict(dt, _cfg.BallProcessNoise);
        _y.Predict(dt, _cfg.BallProcessNoise);

        // Physics gate: reject a measurement the ball could not have reached in dt. The bound
        // grows with dt, so a long occlusion correctly tolerates a distant reappearance.
        bool accepted = false;
        if (usable)
        {
            double dx = obs.X - _x.P;
            double dy = obs.Y - _y.P;
            double jump = Math.Sqrt(dx * dx + dy * dy);
            double maxJump = _cfg.MaxBallSpeed * dt;
            if (jump <= maxJump)
            {
                _x.Update(obs.X, _cfg.BallMeasurementNoise);
                _y.Update(obs.Y, _cfg.BallMeasurementNoise);
                accepted = true;
                _lastMeasuredAt = timestampSeconds;
                _lastConfidence = obs.Confidence;
            }
            else if (timestampSeconds - _lastMeasuredAt > _cfg.MaxCoastSeconds)
            {
                // The ball has been lost long enough that the prediction is worthless; a distant
                // detection is more likely the real ball than our drifting estimate. Re-acquire.
                _x.Reset(obs.X, _cfg.BallMeasurementNoise);
                _y.Reset(obs.Y, _cfg.BallMeasurementNoise);
                accepted = true;
                _lastMeasuredAt = timestampSeconds;
                _lastConfidence = obs.Confidence;
            }
        }

        double coasted = timestampSeconds - _lastMeasuredAt;
        if (!accepted && coasted > 0)
        {
            // Coasting on a constant-velocity model extrapolates a straight line at the last
            // measured speed. A real ball is kicked, deflected, trapped — after a fraction of a
            // second that line is fiction, and the further it runs the further the "ball" flies
            // from every player. Bleed the velocity off over the coast window so a lost ball
            // settles where it was last seen rather than sailing out of the stadium.
            double decay = Math.Max(0.0, 1.0 - coasted / _cfg.MaxCoastSeconds);
            _x.V *= decay;
            _y.V *= decay;
        }

        double confidence = coasted <= 0
            ? _lastConfidence
            : coasted >= _cfg.MaxCoastSeconds
                ? 0.0
                : _lastConfidence * (1.0 - coasted / _cfg.MaxCoastSeconds);

        double speed = Math.Sqrt(_x.V * _x.V + _y.V * _y.V);
        return new BallState(_x.P, _y.P, _x.V, _y.V, speed, !accepted, confidence);
    }
}
