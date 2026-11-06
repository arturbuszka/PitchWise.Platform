using System.Text.Json;
using System.Text.Json.Serialization;
using PitchWise.Data;
using PitchWise.Data.Models;

namespace PitchWise.Api.Dtos;

// JSON converters so enums in responses/requests carry exactly the same string
// values the frontend expects (e.g. "wayward_pass", "free_kick").

public class EventTypeJsonConverter : JsonConverter<EventType>
{
    public override EventType Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o)
        => EventTypeMap.FromDb(reader.GetString() ?? "manual");

    public override void Write(Utf8JsonWriter writer, EventType value, JsonSerializerOptions o)
        => writer.WriteStringValue(EventTypeMap.ToDb(value));
}

public class EventSourceJsonConverter : JsonConverter<EventSource>
{
    public override EventSource Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o)
        => (reader.GetString() == "auto") ? EventSource.Auto : EventSource.Manual;

    public override void Write(Utf8JsonWriter writer, EventSource value, JsonSerializerOptions o)
        => writer.WriteStringValue(value == EventSource.Auto ? "auto" : "manual");
}

public class SessionStatusJsonConverter : JsonConverter<SessionStatus>
{
    public override SessionStatus Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o)
        => reader.GetString() switch
        {
            "processing" => SessionStatus.Processing,
            "done" => SessionStatus.Done,
            _ => SessionStatus.Draft,
        };

    public override void Write(Utf8JsonWriter writer, SessionStatus value, JsonSerializerOptions o)
        => writer.WriteStringValue(value switch
        {
            SessionStatus.Processing => "processing",
            SessionStatus.Done => "done",
            _ => "draft",
        });
}

public class MatchStatusJsonConverter : JsonConverter<MatchStatus>
{
    public override MatchStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        EnumMap.MatchFromDb(reader.GetString() ?? "");

    public override void Write(Utf8JsonWriter writer, MatchStatus value, JsonSerializerOptions options) =>
        writer.WriteStringValue(EnumMap.MatchToDb(value));
}

public class SignalStateJsonConverter : JsonConverter<SignalState>
{
    public override SignalState Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetString() switch
        {
            "receiving" => SignalState.Receiving,
            "stalled" => SignalState.Stalled,
            _ => SignalState.NoSignal,
        };

    public override void Write(Utf8JsonWriter writer, SignalState value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch
        {
            SignalState.Receiving => "receiving",
            SignalState.Stalled => "stalled",
            _ => "no_signal",
        });
}

public class HighlightStatusJsonConverter : JsonConverter<HighlightStatus>
{
    public override HighlightStatus Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o)
        => EnumMap.HighlightFromDb(reader.GetString() ?? "pending");

    public override void Write(Utf8JsonWriter writer, HighlightStatus value, JsonSerializerOptions o)
        => writer.WriteStringValue(EnumMap.HighlightToDb(value));
}

public class RecordingStatusJsonConverter : JsonConverter<RecordingStatus>
{
    public override RecordingStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        EnumMap.RecordingFromDb(reader.GetString() ?? "");

    public override void Write(Utf8JsonWriter writer, RecordingStatus value, JsonSerializerOptions options) =>
        writer.WriteStringValue(EnumMap.RecordingToDb(value));
}

public class OutputStatusJsonConverter : JsonConverter<OutputStatus>
{
    public override OutputStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        EnumMap.OutputFromDb(reader.GetString() ?? "");

    public override void Write(Utf8JsonWriter writer, OutputStatus value, JsonSerializerOptions options) =>
        writer.WriteStringValue(EnumMap.OutputToDb(value));
}
