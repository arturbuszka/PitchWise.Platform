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

// Idle: waiting for the encoder, which may reconnect. Ended: closed for good.
public enum LiveStreamStatus
{
    Idle,
    Live,
    Ended,
}
