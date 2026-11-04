namespace PitchWise.Engine;

/// <summary>
/// Decides who has the ball. This is the crux of the engine: nearly every football rule is a
/// statement about possession changing hands, so a possession signal that flickers frame to
/// frame makes every downstream rule fire noise.
///
/// Possession is therefore a <b>state that persists and demands evidence to change</b>, not a
/// per-frame nearest-player lookup:
///
/// <list type="bullet">
/// <item>A challenger must be the nearest eligible player, inside <see cref="EngineConfig.ControlRadius"/>,
///       <i>continuously</i> for <see cref="EngineConfig.CaptureDwell"/> before they take over.
///       Any frame where they are not, the dwell resets — otherwise a player flickering in and
///       out would accumulate the threshold by accident.</item>
/// <item>The current owner keeps the ball for <see cref="EngineConfig.ReleaseGrace"/> after it
///       leaves their radius. Because the grace (0.60s) exceeds the capture dwell (0.30s),
///       ownership is sticky: that asymmetry is the hysteresis.</item>
/// <item>Two opponents both inside <see cref="EngineConfig.ContestedRadius"/> and within
///       <see cref="EngineConfig.ContestMargin"/> of each other own nothing — it is a duel.</item>
/// </list>
///
/// A ball the filter has lost (<c>Confidence == 0</c>) cannot be possessed by anyone.
/// </summary>
public sealed class PossessionTracker
{
    private readonly EngineConfig _cfg;

    private int? _ownerId;
    private TeamId _ownerTeam = TeamId.Unknown;

    /// <summary>Who is currently accumulating dwell toward a capture, and for how long.</summary>
    private int? _challengerId;
    private double _challengerDwell;

    /// <summary>How long the ball has been outside the owner's control radius.</summary>
    private double _outOfRangeFor;

    /// <summary>How long an opponent has been contesting the ball at the owner's feet. Like a
    /// capture, a duel must be sustained before it strips possession.</summary>
    private double _contestDwell;

    /// <summary>Continuous control by the possessing TEAM. Survives a pass between team-mates.</summary>
    private double _teamDwell;

    private double _lastTimestamp = double.NaN;

    public PossessionTracker(EngineConfig cfg) => _cfg = cfg;

    /// <summary>Folds one frame into the possession state machine. Players must already carry
    /// <see cref="PlayerState.DistanceToBall"/>.</summary>
    public FootballContext Step(BallState ball, IReadOnlyList<PlayerState> players, double timestampSeconds)
    {
        double dt = double.IsNaN(_lastTimestamp) ? 0.0 : timestampSeconds - _lastTimestamp;
        _lastTimestamp = timestampSeconds;
        if (dt < 0) dt = 0;   // out-of-order frame: advance no clocks

        // A ball nobody can see is a ball nobody owns.
        if (ball.Confidence <= 0)
            return ClearAll();

        (PlayerState? nearest, PlayerState? runnerUp) = TwoNearest(players);
        if (nearest is not PlayerState first)
            return ClearAll();

        PlayerState? ownerNow = FindOwner(players);
        // The owner keeps the ball as long as the BALL is within their reach. Whether some
        // opponent is momentarily a few centimetres closer is not the question — a defender
        // brushing past does not take possession, and measuring "am I still nearest" here would
        // let one flickering frame start the release clock on a player who never lost the ball.
        bool ownerHoldsBall = ownerNow is PlayerState o
            && o.DistanceToBall <= _cfg.ControlRadius
            && CanPossess(o);

        if (ownerHoldsBall)
        {
            _outOfRangeFor = 0;

            // An opponent has come to challenge. A duel takes the ball away from BOTH players,
            // but — like a capture — only if it lasts. A defender flashing past for one frame is
            // not a duel; sustained pressure at the same distance as the owner is.
            if (IsContested(first, runnerUp))
            {
                _contestDwell += dt;
                _challengerId = null;
                _challengerDwell = 0;
                if (_contestDwell >= _cfg.CaptureDwell)
                {
                    _ownerId = null;
                    _ownerTeam = TeamId.Unknown;
                    _teamDwell = 0;
                    return new FootballContext(PossessionState.Contested, null, TeamId.Unknown, 0.0);
                }
                _teamDwell += dt;
                return Controlled();
            }
            _contestDwell = 0;

            // Otherwise an opponent may still wrestle it away, but only by being genuinely
            // closer to the ball than the owner, continuously, for the full capture dwell.
            if (first.PlayerId != _ownerId
                && first.DistanceToBall <= _cfg.ControlRadius
                && first.DistanceToBall < ownerNow!.Value.DistanceToBall
                && CanPossess(first))
            {
                if (TryCapture(first, dt)) return Controlled();
            }
            else
            {
                _challengerId = null;
                _challengerDwell = 0;
            }

            _teamDwell += dt;
            return Controlled();
        }

        // --- duel: two opponents equally close, and the owner is not on the ball ---
        if (IsContested(first, runnerUp))
        {
            // Still honour the grace period: a contested moment during a tackle should not
            // instantly erase possession, or every challenge would read as a turnover.
            if (_ownerId is not null)
            {
                _outOfRangeFor += dt;
                if (_outOfRangeFor <= _cfg.ReleaseGrace)
                {
                    _challengerId = null;
                    _challengerDwell = 0;
                    _teamDwell += dt;
                    return Controlled();
                }
            }
            _ownerId = null;
            _ownerTeam = TeamId.Unknown;
            _challengerId = null;
            _challengerDwell = 0;
            _contestDwell = 0;
            _teamDwell = 0;
            return new FootballContext(PossessionState.Contested, null, TeamId.Unknown, 0.0);
        }
        _contestDwell = 0;

        // --- the ball is off the owner's foot: someone else may claim it ---
        if (first.DistanceToBall <= _cfg.ControlRadius && CanPossess(first) && first.PlayerId != _ownerId)
        {
            if (TryCapture(first, dt)) return Controlled();
        }
        else
        {
            _challengerId = null;
            _challengerDwell = 0;
        }

        // --- the owner has lost touch: hold them for the grace period ---
        if (_ownerId is not null)
        {
            _outOfRangeFor += dt;
            if (_outOfRangeFor <= _cfg.ReleaseGrace)
            {
                _teamDwell += dt;
                return Controlled();
            }
            _ownerId = null;
            _ownerTeam = TeamId.Unknown;
            _teamDwell = 0;
        }

        return new FootballContext(PossessionState.Loose, null, TeamId.Unknown, 0.0);
    }

    /// <summary>Accumulates <paramref name="challenger"/>'s dwell and commits the capture once it
    /// reaches <see cref="EngineConfig.CaptureDwell"/>. The dwell must be CONTINUOUS: any frame
    /// where a different player challenges resets it, so a player flickering in and out of range
    /// can never accumulate the threshold by accident.</summary>
    private bool TryCapture(PlayerState challenger, double dt)
    {
        if (_challengerId == challenger.PlayerId) _challengerDwell += dt;
        else { _challengerId = challenger.PlayerId; _challengerDwell = dt; }

        if (_challengerDwell < _cfg.CaptureDwell) return false;

        // Team control is continuous only if the ball stayed within the same team; a turnover
        // resets it, an intra-team pass does not.
        bool sameTeam = _ownerTeam != TeamId.Unknown && challenger.Team == _ownerTeam;
        _teamDwell = sameTeam ? _teamDwell + dt : 0.0;

        _ownerId = challenger.PlayerId;
        _ownerTeam = challenger.Team;
        _challengerId = null;
        _challengerDwell = 0;
        _outOfRangeFor = 0;
        _contestDwell = 0;
        return true;
    }

    private PlayerState? FindOwner(IReadOnlyList<PlayerState> players)
    {
        if (_ownerId is not int id) return null;
        foreach (PlayerState p in players)
            if (p.PlayerId == id) return p;
        return null;   // owner left the frame
    }

    /// <summary>Referees never possess the ball; nor does anyone whose team is unresolved,
    /// because a possession we cannot attribute to a side is useless to every rule. Nor does an
    /// unidentified player (<see cref="PlayerIdentity.None"/>): every anonymous detection shares
    /// that id, so treating them as possessors would let one "player" teleport across the pitch
    /// between frames.</summary>
    private static bool CanPossess(PlayerState p) =>
        p.PlayerId != PlayerIdentity.None
        && p.Role != PlayerRole.Referee && p.Team != TeamId.Unknown;

    private bool IsContested(PlayerState first, PlayerState? runnerUp)
    {
        if (runnerUp is not PlayerState second) return false;
        if (!CanPossess(first) || !CanPossess(second)) return false;
        if (first.Team == second.Team) return false;
        if (second.DistanceToBall > _cfg.ContestedRadius) return false;
        return second.DistanceToBall - first.DistanceToBall < _cfg.ContestMargin;
    }

    private FootballContext Controlled()
    {
        PossessionState state = _ownerTeam == TeamId.A
            ? PossessionState.TeamAControlled
            : PossessionState.TeamBControlled;
        return new FootballContext(state, _ownerId, _ownerTeam, _teamDwell);
    }

    private FootballContext ClearAll()
    {
        _ownerId = null;
        _ownerTeam = TeamId.Unknown;
        _challengerId = null;
        _challengerDwell = 0;
        _outOfRangeFor = 0;
        _contestDwell = 0;
        _teamDwell = 0;
        return FootballContext.Empty;
    }

    /// <summary>Nearest and second-nearest possession-eligible player to the ball, by
    /// <see cref="PlayerState.DistanceToBall"/>. Ineligible players are skipped rather than ranked:
    /// an anonymous detection standing over the ball must not shadow the identified player behind
    /// it. One pass, no allocation — this runs on every frame of every match.</summary>
    private static (PlayerState? nearest, PlayerState? runnerUp) TwoNearest(IReadOnlyList<PlayerState> players)
    {
        PlayerState? a = null, b = null;
        foreach (PlayerState p in players)
        {
            if (!CanPossess(p)) continue;
            if (a is null || p.DistanceToBall < a.Value.DistanceToBall) { b = a; a = p; }
            else if (b is null || p.DistanceToBall < b.Value.DistanceToBall) b = p;
        }
        return (a, b);
    }
}
