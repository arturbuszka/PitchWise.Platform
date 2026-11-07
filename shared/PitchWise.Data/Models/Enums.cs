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

// What became of a match's recordings, which are put together once the match has ended.
public enum RecordingStatus
{
    None,       // not ended yet, or nothing was received
    Processing,
    Ready,
    Failed,     // at least one input could not be put together; its raw segments are kept
}

// What is arriving on an input right now. Not stored: it comes from polling MediaMTX.
public enum SignalState
{
    NoSignal,   // no_signal: no encoder is publishing
    Receiving,
    Stalled,    // the encoder is connected, but no data has arrived for a few seconds
}

// What an output is doing. Offline is never stored: the API reports it when the service has gone quiet.
public enum OutputStatus
{
    Stopped,
    WaitingForSignal,   // waiting_for_signal: wanted, but nothing is arriving on the input on air
    Connecting,
    Live,
    Retrying,           // failed, and will try again at NextRetryAt
    Incompatible,       // the signal can't be sent as it is; it starts by itself when the signal changes
    Failed,             // gave up, or can't start without a change; needs someone to act
    Offline,
}

// What a match's switcher is doing. Offline is never stored: the API reports it when the service has gone quiet.
public enum SwitcherStatus
{
    Stopped,
    WaitingForCameras,  // waiting_for_cameras: no program until the first camera arrives, so time zero isn't a black screen
    Starting,
    Running,
    Restarting,         // it stopped, and starts again at NextRetryAt
    Offline,
}

public enum SwitcherCutKind
{
    Video,
    Audio,
}

public enum SwitcherCutOrigin
{
    Operator,
    Automatic,  // the slate took over from a lost camera, or the camera came back
}
