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

public class VisionJobStatusJsonConverter : JsonConverter<VisionJobStatus>
{
    public override VisionJobStatus Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o)
        => reader.GetString() switch
        {
            "running" => VisionJobStatus.Running,
            "done" => VisionJobStatus.Done,
            "failed" => VisionJobStatus.Failed,
            _ => VisionJobStatus.Pending,
        };

    public override void Write(Utf8JsonWriter writer, VisionJobStatus value, JsonSerializerOptions o)
        => writer.WriteStringValue(value switch
        {
            VisionJobStatus.Running => "running",
            VisionJobStatus.Done => "done",
            VisionJobStatus.Failed => "failed",
            _ => "pending",
        });
}

public class HighlightStatusJsonConverter : JsonConverter<HighlightStatus>
{
    public override HighlightStatus Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o)
        => EnumMap.HighlightFromDb(reader.GetString() ?? "pending");

    public override void Write(Utf8JsonWriter writer, HighlightStatus value, JsonSerializerOptions o)
        => writer.WriteStringValue(EnumMap.HighlightToDb(value));
}
