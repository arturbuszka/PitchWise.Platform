namespace PitchWise.Data.Models;

// Stored in the database as snake_case strings; see EnumMap in AppDbContext.

public enum SessionStatus
{
    Draft,
    Processing,
    Done,
}

public enum EventType
{
    Goal,
    Shot,
    WaywardPass,    // wayward_pass
    Foul,
    FreeKick,       // free_kick
    Offside,
    Substitution,
    SetPiece,       // set_piece
    Manual,
}

public enum EventSource
{
    Auto,
    Manual,
}

public enum HighlightStatus
{
    Pending,
    Running,
    Done,
    Failed,
}

// Waiting: the program input has no signal, either not yet or for now, as encoders drop and
// reconnect. Live: viewers can watch. Ended: closed for good by the customer.
public enum MatchStatus
{
    Waiting,
    Live,
    Ended,
}

// What is arriving on an input right now. Not stored: it comes from polling MediaMTX.
public enum SignalState
{
    NoSignal,   // no_signal: no encoder is publishing
    Receiving,
    Stalled,    // the encoder is connected, but no data has arrived for a few seconds
}
