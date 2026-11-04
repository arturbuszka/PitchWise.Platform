using OpenCvSharp;
using PitchWise.Engine;
using BT = ByteTrack;

namespace PitchWise.Vision;

/// <summary>A processed frame plus the team assignment for each of its detections and, when a
/// pitch model is loaded, the frame's pitch keypoints.</summary>
/// <param name="Teams">Index-aligned with <c>Frame.Detections</c>. Empty when team
/// classification is disabled or has not yet seeded.</param>
/// <param name="PitchKeypoints">One per <see cref="PitchModel"/> keypoint (length
/// <see cref="PitchModel.Count"/>), in original-image pixels; empty when no pitch model is
/// loaded. Consumed by <see cref="PitchRegistrar"/> to fit the frame's homography.</param>
/// <remarks>Teams and keypoints travel alongside <see cref="FrameResult"/> rather than inside
/// <see cref="Detection"/> because that record struct is the contract the parity/golden tests
/// compare against ultralytics.</remarks>
public readonly record struct TrackedFrame(
    FrameResult Frame,
    IReadOnlyList<TeamId> Teams,
    IReadOnlyList<PitchKeypointDetector.Keypoint> PitchKeypoints);

/// <summary>
/// Object detection + tracking on video frames.:
/// YOLO11 (ONNX) for detection + ByteTrack.NET for tracking. Emits
/// <see cref="FrameResult"/> with per-detection track ids and our class names.
///
/// ByteTrack.NET's tracker takes only box+score and returns box+id (it drops the
/// class), so after <c>Update</c> we re-attach each track id to the originating
/// classed detection by best-IoU match.
/// </summary>
public sealed class Detector : IDisposable
{
    /// <summary>Maps a model class name onto our categories (port of _CLASS_ALIASES).
    /// Covers football-specific models and the default COCO (person/sports ball).</summary>
    public static readonly IReadOnlyDictionary<string, string> ClassAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ball"] = ObjectClass.Ball,
            ["sports ball"] = ObjectClass.Ball,
            ["football"] = ObjectClass.Ball,
            ["soccer ball"] = ObjectClass.Ball,
            ["player"] = ObjectClass.Player,
            ["person"] = ObjectClass.Player,
            ["referee"] = ObjectClass.Referee,
            ["main referee"] = ObjectClass.Referee,
            ["side referee"] = ObjectClass.Referee,
            ["goalkeeper"] = ObjectClass.Goalkeeper,
            ["goal keeper"] = ObjectClass.Goalkeeper,
            // Stage 2 hook: active only when a football model returns a goal class.
            ["goal"] = ObjectClass.Goal,
            ["goalpost"] = ObjectClass.Goal,
        };

    /// <summary>Default class map: alias lookup, drops unknown classes (returns null).</summary>
    public static string? MapClass(string raw) =>
        ClassAliases.TryGetValue(raw, out string? mapped) ? mapped : null;

    private readonly Yolo11OnnxDetector _detector;
    private readonly BT.ByteTracker _tracker;
    private readonly int _frameStride;
    // Re-ID layer on top of ByteTrack's volatile ids. Both null when no Re-ID model is
    // configured (e.g. parity/smoke tests) — the detector then behaves exactly as before.
    private readonly OsNetOnnxEmbedder? _embedder;
    private readonly PlayerReId? _reId;
    // Jersey-colour team assignment. Needs a stable PlayerId to vote over time, so it is only
    // useful alongside Re-ID; null when either is disabled.
    private readonly TeamColorClassifier? _teams;
    // Pitch registration (keypoint detection). Null when no pitch model is configured; the
    // engine then stays in normalized coordinates.
    private readonly PitchKeypointDetector? _pitch;

    /// <param name="modelPath">Exported YOLO11 .onnx.</param>
    /// <param name="classNames">model class-id → raw name (from the exported model / golden).</param>
    /// <param name="frameRate">Source fps; scales the tracker's lost-track buffer.</param>
    /// <param name="frameStride">Process every Nth frame.</param>
    /// <param name="imgsz">Model input size (must match the export).</param>
    /// <param name="mapClass">Class-name mapper; defaults to <see cref="MapClass"/>.</param>
    /// <param name="reidModelPath">Exported OSNet .onnx for player Re-ID; null disables Re-ID
    /// (detections then carry only volatile TrackId, no stable PlayerId).</param>
    /// <param name="classifyTeams">Assign each player a team from jersey colour. Requires Re-ID:
    /// the team vote is taken over a player's recent frames, which needs a stable identity.</param>
    public Detector(
        string modelPath,
        IReadOnlyDictionary<int, string> classNames,
        int frameRate = 25,
        int frameStride = 5,
        int imgsz = 640,
        Func<string, string?>? mapClass = null,
        string executionProvider = "dml",
        int deviceId = 0,
        string? reidModelPath = null,
        bool classifyTeams = false,
        string? pitchModelPath = null)
    {
        _detector = new Yolo11OnnxDetector(
            modelPath, classNames, mapClass ?? MapClass, imgsz,
            executionProvider: executionProvider, deviceId: deviceId);
        var config = new BT.ByteTrackerConfig
        {
            TrackThresh = 0.5f,
            TrackBuffer = 30,
            MatchThresh = 0.8f,
            Mot20 = false,
        };
        // Deterministic track ids across runs (see ByteTrack README).
        BT.BaseTrack.ResetCount();
        _tracker = new BT.ByteTracker(config, frameRate);
        _frameStride = Math.Max(1, frameStride);

        if (!string.IsNullOrWhiteSpace(reidModelPath))
        {
            _embedder = new OsNetOnnxEmbedder(reidModelPath!, executionProvider, deviceId);
            _reId = new PlayerReId();
            if (classifyTeams) _teams = new TeamColorClassifier();
        }

        if (!string.IsNullOrWhiteSpace(pitchModelPath))
            _pitch = new PitchKeypointDetector(pitchModelPath!, imgsz, executionProvider, deviceId);
    }

    /// <summary>The two frozen jersey colours as "#rrggbb", once team classification has seeded.
    /// Null before that, or when teams are disabled. Recorded in the world-state dump header so a
    /// bad k=2 split can be diagnosed from the file.</summary>
    public (string? A, string? B) TeamColors => (_teams?.ColorA, _teams?.ColorB);

    /// <summary>Detect + track on a single BGR frame.</summary>
    public FrameResult DetectFrame(Mat frame, int frameIndex, double timestampSeconds) =>
        TrackAndAttach(_detector.Detect(frame), frame, frameIndex, timestampSeconds).Frame;

    /// <summary>
    /// Detect on N frames in ONE batched YOLO inference, then run the (stateful, sequential)
    /// tracker frame-by-frame IN ORDER. The batch only groups the expensive YOLO calls; the
    /// tracker sees exactly the same per-frame sequence as N single DetectFrame calls, so
    /// track ids are identical. Frames MUST be passed in ascending frameIndex order.
    /// </summary>
    public IReadOnlyList<FrameResult> DetectFramesBatched(
        IReadOnlyList<(Mat frame, int frameIndex, double timestampSeconds)> batch)
    {
        IReadOnlyList<TrackedFrame> tracked = TrackFramesBatched(batch);
        var results = new List<FrameResult>(tracked.Count);
        foreach (TrackedFrame t in tracked) results.Add(t.Frame);
        return results;
    }

    /// <summary>As <see cref="DetectFramesBatched"/>, but also returns the per-detection team
    /// assignment needed to build an engine observation.</summary>
    public IReadOnlyList<TrackedFrame> TrackFramesBatched(
        IReadOnlyList<(Mat frame, int frameIndex, double timestampSeconds)> batch)
    {
        if (batch.Count == 0) return Array.Empty<TrackedFrame>();

        var mats = new List<Mat>(batch.Count);
        foreach (var b in batch) mats.Add(b.frame);
        IReadOnlyList<IReadOnlyList<Detection>> perFrame = _detector.DetectBatch(mats);

        var results = new List<TrackedFrame>(batch.Count);
        for (int k = 0; k < batch.Count; k++)
            results.Add(TrackAndAttach(perFrame[k], batch[k].frame, batch[k].frameIndex, batch[k].timestampSeconds));
        return results;
    }

    /// <summary>Runs the tracker on one frame's detections and re-attaches track ids by IoU,
    /// then (if Re-ID is enabled) assigns a stable PlayerId from the player crops in
    /// <paramref name="frame"/>. Stateful — must be called once per processed frame in
    /// ascending order.</summary>
    private TrackedFrame TrackAndAttach(IReadOnlyList<Detection> classed, Mat frame, int frameIndex, double timestampSeconds)
    {
        // Feed boxes+scores to the tracker; it returns boxes+ids (no class).
        var btDets = new List<BT.Detection>(classed.Count);
        foreach (Detection d in classed)
            btDets.Add(BT.Detection.FromTlbr(new[] { d.X1, d.Y1, d.X2, d.Y2 }, (float)d.Confidence));

        IReadOnlyList<BT.STrack> tracks = _tracker.Update(btDets);

        // Re-attach track ids to classed detections by best IoU (tracker dropped class).
        var withIds = new List<Detection>(classed.Count);
        var used = new bool[tracks.Count];
        foreach (Detection d in classed)
        {
            double[] box = { d.X1, d.Y1, d.X2, d.Y2 };
            int bestJ = -1;
            double bestIou = 0.5;
            for (int j = 0; j < tracks.Count; j++)
            {
                if (used[j]) continue;
                double iou = Iou(box, tracks[j].Tlbr);
                if (iou > bestIou) { bestIou = iou; bestJ = j; }
            }
            int? id = null;
            if (bestJ >= 0) { used[bestJ] = true; id = tracks[bestJ].TrackId; }
            withIds.Add(d with { TrackId = id });
        }

        IReadOnlyList<Detection> final = AssignPlayerIds(withIds, frame, frameIndex);

        // Teams are voted per stable PlayerId over recent frames, so this must run after Re-ID.
        IReadOnlyList<TeamId> teams = _teams is not null
            ? _teams.Classify(final, frame)
            : Array.Empty<TeamId>();

        // Pitch keypoints are per-frame and independent of the detections; the stateful
        // registration (smoothing, homography) lives in the caller, not here.
        IReadOnlyList<PitchKeypointDetector.Keypoint> pitchKps = _pitch is not null
            ? _pitch.Detect(frame)
            : Array.Empty<PitchKeypointDetector.Keypoint>();

        var result = new FrameResult
        {
            FrameIndex = frameIndex,
            TimestampSeconds = timestampSeconds,
            Detections = final,
        };
        return new TrackedFrame(result, teams, pitchKps);
    }

    /// <summary>Computes OSNet embeddings for the player crops and maps volatile track ids to
    /// stable PlayerIds via <see cref="PlayerReId"/>. No-op passthrough when Re-ID is disabled.
    /// Embeddings run as ONE batched inference over all player/goalkeeper boxes in the frame.</summary>
    private IReadOnlyList<Detection> AssignPlayerIds(IReadOnlyList<Detection> withIds, Mat frame, int frameIndex)
    {
        if (_embedder is null || _reId is null) return withIds;

        // Embed all detections (index-aligned) so PlayerReId can consume by position; only
        // player-like boxes actually get a crop, others get an empty (no-embedding) vector.
        var toEmbed = new List<Detection>(withIds.Count);
        var embedIndex = new int[withIds.Count];   // maps detection -> slot in toEmbed, or -1
        for (int i = 0; i < withIds.Count; i++)
        {
            Detection d = withIds[i];
            if ((d.Cls == ObjectClass.Player || d.Cls == ObjectClass.Goalkeeper) && d.TrackId is not null)
            {
                embedIndex[i] = toEmbed.Count;
                toEmbed.Add(d);
            }
            else embedIndex[i] = -1;
        }

        IReadOnlyList<float[]> embedded = toEmbed.Count > 0
            ? _embedder.EmbedBatch(frame, toEmbed)
            : Array.Empty<float[]>();

        // Re-expand to a per-detection embedding list (empty vector where no crop was taken).
        var embeddings = new List<float[]>(withIds.Count);
        for (int i = 0; i < withIds.Count; i++)
            embeddings.Add(embedIndex[i] >= 0 ? embedded[embedIndex[i]] : Array.Empty<float>());

        return _reId.Assign(withIds, embeddings, frameIndex);
    }

    /// <summary>Iterates frames (every frame_stride) yielding detections with track ids.
    /// Port of Detector.run(). fps is read from the video for timestamps.</summary>
    /// <param name="startSeconds">Skip decoding up to this timestamp (segment start). 0 = beginning.</param>
    /// <param name="endSeconds">Stop after this timestamp (segment end). null = end of file.</param>
    /// <remarks>Timestamps stay ABSOLUTE relative to the whole video (frameIndex/fps),
    /// so events from a mid-video segment carry their true match time.</remarks>
    public IEnumerable<FrameResult> Run(string videoPath, double startSeconds = 0.0, double? endSeconds = null)
    {
        using var cap = new VideoCapture(videoPath);
        if (!cap.IsOpened())
            throw new InvalidOperationException($"Cannot open video: {videoPath}");

        double fps = cap.Get(VideoCaptureProperties.Fps);
        if (fps <= 0 || double.IsNaN(fps)) fps = 25.0;

        // Seek to the segment start. Snap to a stride boundary so frameIndex%stride==0
        // stays true and track ids/timestamps line up with a from-zero run.
        int startFrame = 0;
        if (startSeconds > 0)
        {
            startFrame = (int)Math.Floor(startSeconds * fps);
            startFrame -= startFrame % _frameStride;
            cap.Set(VideoCaptureProperties.PosFrames, startFrame);
        }
        int? endFrame = endSeconds is double e ? (int)Math.Ceiling(e * fps) : null;

        using var frame = new Mat();
        int srcIndex = startFrame;   // absolute source frame index
        while (cap.Read(frame) && !frame.Empty())
        {
            if (endFrame is int ef && srcIndex >= ef) break;
            if (srcIndex % _frameStride == 0)
            {
                double ts = srcIndex / fps;
                yield return DetectFrame(frame, srcIndex, ts);
            }
            srcIndex++;
        }
    }

    private static double Iou(double[] a, double[] b)
    {
        double ix1 = Math.Max(a[0], b[0]), iy1 = Math.Max(a[1], b[1]);
        double ix2 = Math.Min(a[2], b[2]), iy2 = Math.Min(a[3], b[3]);
        double iw = Math.Max(0.0, ix2 - ix1), ih = Math.Max(0.0, iy2 - iy1);
        double inter = iw * ih;
        double areaA = Math.Max(0.0, a[2] - a[0]) * Math.Max(0.0, a[3] - a[1]);
        double areaB = Math.Max(0.0, b[2] - b[0]) * Math.Max(0.0, b[3] - b[1]);
        double union = areaA + areaB - inter;
        return union <= 0.0 ? 0.0 : inter / union;
    }

    public void Dispose()
    {
        _detector.Dispose();
        _embedder?.Dispose();
        _pitch?.Dispose();
    }
}
