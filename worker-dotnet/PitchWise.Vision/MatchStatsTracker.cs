using PitchWise.Engine;

namespace PitchWise.Vision;

/// <summary>
/// Accumulates whole-match statistics — possession share and pass/turnover counts per team — from
/// the per-frame <see cref="WorldState"/> stream and the engine's pass events.
///
/// Same shape as <see cref="TimeOnPitchTracker"/>: a stateful <see cref="Add"/> per frame plus
/// <see cref="AddEvent"/> per emitted <see cref="GameEvent"/>, and a <see cref="Report"/> at the
/// end of the run. Fed in <c>StreamingAnnotator.ProcessBatch</c> alongside the time-on-pitch
/// tracker.
///
/// Possession is measured in TIME, not frames: each frame contributes the interval since the
/// previous frame to whichever team controlled the ball. Loose and contested time is tracked but
/// excluded from the A/B split, so the two possession percentages sum to 100 (of controlled time).
///
/// Passes come from the engine's events BEFORE they are filtered for persistence — <c>pass</c> and
/// <c>wayward_pass</c> both feed the counts here, even though only <c>wayward_pass</c> survives as
/// a database event.
/// </summary>
public sealed class MatchStatsTracker
{
    private double _controlledA;
    private double _controlledB;
    private double _loose;
    private double _lastTimestamp = double.NaN;

    private int _passesA, _passesB;
    private int _turnoversA, _turnoversB;

    /// <summary>Folds one processed frame's possession state into the time accumulators.</summary>
    public void Add(WorldState ws)
    {
        double dt = double.IsNaN(_lastTimestamp) ? 0.0 : ws.TimestampSeconds - _lastTimestamp;
        _lastTimestamp = ws.TimestampSeconds;
        if (dt <= 0) return;   // first frame, or out-of-order: no interval to attribute

        switch (ws.Context.Possession)
        {
            case PossessionState.TeamAControlled: _controlledA += dt; break;
            case PossessionState.TeamBControlled: _controlledB += dt; break;
            default: _loose += dt; break;   // Loose or Contested
        }
    }

    /// <summary>Counts a pass or turnover against the team that MADE it. The event carries the
    /// previous owner's team (see <see cref="Rules.PassRule"/>), which is the team being credited
    /// with the pass or debited with the turnover.</summary>
    public void AddEvent(GameEvent e)
    {
        switch (e.Type)
        {
            case GameEventType.Pass:
                if (e.Team == TeamId.A) _passesA++;
                else if (e.Team == TeamId.B) _passesB++;
                break;
            case GameEventType.WaywardPass:
                if (e.Team == TeamId.A) _turnoversA++;
                else if (e.Team == TeamId.B) _turnoversB++;
                break;
        }
    }

    public MatchStatsReport Report()
    {
        double controlled = _controlledA + _controlledB;
        double pctA = controlled > 0 ? _controlledA / controlled * 100.0 : 0.0;
        double pctB = controlled > 0 ? _controlledB / controlled * 100.0 : 0.0;

        return new MatchStatsReport(
            ControlledSeconds: controlled,
            LooseSeconds: _loose,
            TeamA: TeamOf(pctA, _passesA, _turnoversA),
            TeamB: TeamOf(pctB, _passesB, _turnoversB));
    }

    private static TeamStats TeamOf(double possessionPct, int passes, int turnovers)
    {
        int attempts = passes + turnovers;
        double accuracy = attempts > 0 ? passes / (double)attempts * 100.0 : 0.0;
        return new TeamStats(possessionPct, passes, turnovers, accuracy);
    }
}
