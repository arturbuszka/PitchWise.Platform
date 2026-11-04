using System.Text.Json;
using OpenCvSharp;
using PitchWise.Vision;

// Detection parity test: run Yolo11OnnxDetector on the same frames the Python
// golden reference was generated from, and confirm the boxes/classes/scores match
// ultralytics within tolerance. Proves the C# pre/post-processing is correct.
//
// Usage:
//   dotnet run -- --onnx <model.onnx> --golden <golden.json> --video <in.mp4>
//                 [--imgsz 640] [--iou-match 0.9] [--score-tol 0.05]

var opts = ParseArgs(args);
if (opts is null) return 2;

using JsonDocument golden = JsonDocument.Parse(File.ReadAllText(opts.GoldenPath));
JsonElement root = golden.RootElement;

// Build class-id → name from golden "names" and an identity class map (keep ALL raw
// COCO names) so the parity check compares decoder output, not the football filter.
var names = new Dictionary<int, string>();
foreach (JsonProperty p in root.GetProperty("names").EnumerateObject())
    names[int.Parse(p.Name)] = p.Value.GetString()!;

int imgsz = opts.Imgsz ?? root.GetProperty("imgsz").GetInt32();
float conf = (float)root.GetProperty("conf").GetDouble();
float iou = (float)root.GetProperty("iou").GetDouble();

using var detector = new Yolo11OnnxDetector(
    opts.OnnxPath, names, mapClass: raw => raw, imgsz: imgsz,
    scoreThresh: conf, nmsThresh: iou);

// Which frame indices does the golden cover?
var wantFrames = root.GetProperty("frames").EnumerateObject()
    .Select(f => int.Parse(f.Name)).OrderBy(x => x).ToList();
int maxFrame = wantFrames.Max();

using var cap = new VideoCapture(opts.VideoPath);
if (!cap.IsOpened())
{
    Console.Error.WriteLine($"Cannot open video: {opts.VideoPath}");
    return 2;
}

// Batch-vs-single check: grab the first few frames, run DetectBatch on all at once and
// compare against Detect() one-by-one. Confirms the dynamic-batch model + batched pre/post
// processing produce the SAME detections as single-frame (the core batching invariant).
if (args.Contains("--batch-check"))
{
    var grabbed = new List<Mat>();
    using (var bc = new VideoCapture(opts.VideoPath))
        for (int i = 0; i < 6; i++) { var m = new Mat(); if (!bc.Read(m) || m.Empty()) break; grabbed.Add(m); }

    var single = grabbed.Select(m => detector.Detect(m)).ToList();
    var batched = detector.DetectBatch(grabbed);

    bool ok = true;
    for (int i = 0; i < grabbed.Count; i++)
    {
        if (single[i].Count != batched[i].Count) { ok = false; Console.WriteLine($"  frame {i}: count {single[i].Count} vs {batched[i].Count}"); continue; }
        for (int k = 0; k < single[i].Count; k++)
        {
            var a = single[i][k]; var b = batched[i][k];
            double d = Math.Abs(a.X1 - b.X1) + Math.Abs(a.Y1 - b.Y1) + Math.Abs(a.X2 - b.X2) + Math.Abs(a.Y2 - b.Y2) + Math.Abs(a.Confidence - b.Confidence);
            if (a.Cls != b.Cls || d > 1e-2) { ok = false; Console.WriteLine($"  frame {i} det {k}: diff cls={a.Cls}/{b.Cls} d={d:F4}"); }
        }
    }
    foreach (var m in grabbed) m.Dispose();
    Console.WriteLine($"BATCH-CHECK ({grabbed.Count} frames, batch=1 vs batch=N): {(ok ? "IDENTICAL ✓" : "MISMATCH ✗")}");
    return ok ? 0 : 1;
}

int totalMatched = 0, totalGolden = 0, totalExtra = 0;
bool allPass = true;

using var frame = new Mat();
int idx = 0;
while (idx <= maxFrame && cap.Read(frame) && !frame.Empty())
{
    if (wantFrames.Contains(idx))
    {
        JsonElement gframe = root.GetProperty("frames").GetProperty(idx.ToString());
        var goldDets = gframe.GetProperty("detections").EnumerateArray()
            .Select(d => (
                name: d.GetProperty("name").GetString()!,
                conf: d.GetProperty("conf").GetDouble(),
                box: d.GetProperty("xyxy").EnumerateArray().Select(v => v.GetDouble()).ToArray()))
            .ToList();

        IReadOnlyList<Detection> got = detector.Detect(frame);

        // Greedy match: for each golden box, find an unused .NET box of the same class
        // with IoU >= iouMatch and |conf| within scoreTol.
        var used = new bool[got.Count];
        int matched = 0;
        foreach (var g in goldDets)
        {
            int bestJ = -1; double bestIou = opts.IouMatch;
            for (int j = 0; j < got.Count; j++)
            {
                if (used[j] || got[j].Cls != g.name) continue;
                double iouV = BoxIou(g.box, got[j]);
                if (iouV >= bestIou) { bestIou = iouV; bestJ = j; }
            }
            if (bestJ >= 0)
            {
                double dConf = Math.Abs(got[bestJ].Confidence - g.conf);
                if (dConf <= opts.ScoreTol) { used[bestJ] = true; matched++; }
                else Console.WriteLine($"  frame {idx}: {g.name} matched by IoU but conf off by {dConf:F3} (gold {g.conf:F3} vs got {got[bestJ].Confidence:F3})");
            }
            else
            {
                Console.WriteLine($"  frame {idx}: MISSING {g.name} conf={g.conf:F3} box=[{string.Join(",", g.box.Select(v => v.ToString("F0")))}]");
            }
        }
        for (int j = 0; j < got.Count; j++)
        {
            if (used[j]) continue;
            Detection d = got[j];
            Console.WriteLine($"  frame {idx}: EXTRA {d.Cls} conf={d.Confidence:F3} box=[{d.X1:F0},{d.Y1:F0},{d.X2:F0},{d.Y2:F0}]");
        }
        int extra = used.Count(u => !u);
        totalMatched += matched; totalGolden += goldDets.Count; totalExtra += extra;
        bool pass = matched == goldDets.Count && extra == 0;
        allPass &= pass;
        Console.WriteLine($"frame {idx}: {(pass ? "PASS" : "FAIL")} matched {matched}/{goldDets.Count}, extra .NET dets={extra}");
    }
    idx++;
}

Console.WriteLine();
Console.WriteLine($"TOTAL: matched {totalMatched}/{totalGolden}, extra .NET dets={totalExtra}");
Console.WriteLine(allPass ? "PARITY OK" : "PARITY FAILED");
return allPass ? 0 : 1;

static double BoxIou(double[] a, Detection b)
{
    double ix1 = Math.Max(a[0], b.X1), iy1 = Math.Max(a[1], b.Y1);
    double ix2 = Math.Min(a[2], b.X2), iy2 = Math.Min(a[3], b.Y2);
    double iw = Math.Max(0.0, ix2 - ix1), ih = Math.Max(0.0, iy2 - iy1);
    double inter = iw * ih;
    double areaA = Math.Max(0.0, a[2] - a[0]) * Math.Max(0.0, a[3] - a[1]);
    double areaB = Math.Max(0.0, b.X2 - b.X1) * Math.Max(0.0, b.Y2 - b.Y1);
    double union = areaA + areaB - inter;
    return union <= 0 ? 0 : inter / union;
}

static Opts? ParseArgs(string[] args)
{
    var o = new Opts();
    for (int i = 0; i < args.Length; i++)
    {
        string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Missing value for {args[i]}");
        switch (args[i])
        {
            case "--onnx": o.OnnxPath = Next(); break;
            case "--golden": o.GoldenPath = Next(); break;
            case "--video": o.VideoPath = Next(); break;
            case "--imgsz": o.Imgsz = int.Parse(Next()); break;
            case "--iou-match": o.IouMatch = double.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
            case "--score-tol": o.ScoreTol = double.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
            case "--batch-check": break;   // handled in main, before the golden loop
            default: Console.Error.WriteLine($"Unknown arg {args[i]}"); return null;
        }
    }
    if (o.OnnxPath == "" || o.GoldenPath == "" || o.VideoPath == "")
    {
        Console.Error.WriteLine("Required: --onnx <m.onnx> --golden <g.json> --video <v.mp4>");
        return null;
    }
    return o;
}

sealed class Opts
{
    public string OnnxPath = "";
    public string GoldenPath = "";
    public string VideoPath = "";
    public int? Imgsz;
    public double IouMatch = 0.9;
    public double ScoreTol = 0.05;
}
