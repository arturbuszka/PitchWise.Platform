namespace PitchWise.Data.Models;

// Stored in the database as snake_case strings; see EnumMap in AppDbContext.

public enum SessionStatus
{
    Draft,
    Processing,
    Done,
}

public enum VisionJobStatus
{
    Pending,
    Running,
    Done,
    Failed,
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
