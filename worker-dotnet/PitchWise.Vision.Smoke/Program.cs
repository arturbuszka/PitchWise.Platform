using System.Text.Json;
using PitchWise.Vision;

// End-to-end smoke: run the full .NET vision pipeline (detect → track → events) on a
// video and print duration/fps/frame count + detected events. Exercises Detector
// (YOLO11 ONNX + ByteTrack.NET), Events and Pipeline together.
//
// Usage:
//   dotnet run -- --onnx <model.onnx> --golden <golden.json> --video <in.mp4>
//                 [--imgsz 640] [--stride 3] [--max-frames N]

string onnx = "", golden = "", video = "";
int imgsz = 640, stride = 3;
for (int i = 0; i < args.Length; i++)
{
    string Next() => args[++i];
    switch (args[i])
    {
        case "--onnx": onnx = Next(); break;
        case "--golden": golden = Next(); break;
        case "--video": video = Next(); break;
        case "--imgsz": imgsz = int.Parse(Next()); break;
        case "--stride": stride = int.Parse(Next()); break;
    }
}
if (onnx == "" || golden == "" || video == "")
{
    Console.Error.WriteLine("Required: --onnx <m.onnx> --golden <g.json> --video <v.mp4>");
    return 2;
}

// class-id → name from the golden JSON.
using JsonDocument gdoc = JsonDocument.Parse(File.ReadAllText(golden));
var names = new Dictionary<int, string>();
foreach (JsonProperty p in gdoc.RootElement.GetProperty("names").EnumerateObject())
    names[int.Parse(p.Name)] = p.Value.GetString()!;

Console.WriteLine($"Running pipeline on {video} (imgsz={imgsz}, stride={stride})...");

// First, a direct Detector pass to summarise detections/classes/track ids — proves
// the detect→track path produces data (independent of whether events fire).
int frameRate = 50;
using (var det = new Detector(onnx, names, frameRate: frameRate, frameStride: stride, imgsz: imgsz))
{
    var classCounts = new Dictionary<string, int>();
    var trackIds = new HashSet<int>();
    int frames = 0, dets = 0;
    foreach (FrameResult fr in det.Run(video))
    {
        frames++;
        foreach (Detection d in fr.Detections)
        {
            dets++;
            classCounts[d.Cls] = classCounts.GetValueOrDefault(d.Cls) + 1;
            if (d.TrackId is int id) trackIds.Add(id);
        }
    }
    Console.WriteLine($"Detector: {frames} frames, {dets} detections, {trackIds.Count} unique track ids");
    Console.WriteLine("  classes: " + string.Join(", ", classCounts.Select(kv => $"{kv.Key}={kv.Value}")));
}

double lastPct = -1;
PipelineResult result = Pipeline.AnalyzeVideo(
    video, onnx, names, frameStride: stride, imgsz: imgsz,
    onProgress: p =>
    {
        int pct = (int)(p * 100);
        if (pct >= lastPct + 25) { Console.WriteLine($"  progress {pct}%"); lastPct = pct; }
    });

Console.WriteLine();
Console.WriteLine($"duration={result.DurationSeconds:F1}s fps={result.Fps:F2} framesProcessed={result.FramesProcessed}");
Console.WriteLine($"events: {result.Events.Count}");
foreach (DetectedEvent e in result.Events)
    Console.WriteLine($"  [{e.Type}] t={e.TimestampSeconds:F2}s conf={e.Confidence:F3} — {e.Label}");

return 0;
