using OpenCvSharp;
using PitchWise.Engine;
using PitchWise.Engine.Rules;

namespace PitchWise.Vision;

/// <summary>
/// Single-pass streaming analysis: one walk over the video that BOTH (a) burns detection
/// boxes onto every frame and pipes them into a growing HLS VOD playlist (watchable while
/// still encoding, fully seekable, nothing deleted) AND (b) detects goal/shot events in
/// rolling time windows and hands them back as they are found.
///
/// Replaces the old two-pass approach (separate analysis + annotated render) — one YOLO
/// pass instead of two. Reuses Detector.DetectFrame, Overlay.Draw, Events.Detect and the
/// HLS encoder from FfmpegTools.
/// </summary>
public static class StreamingAnnotator
{
    /// <summary>Result of a streaming run (probe metadata; events are delivered via callback).</summary>
    public sealed class Result
    {
        public double? DurationSeconds { get; init; }
        public double? Fps { get; init; }
        public int FramesProcessed { get; init; }
        public bool EncodeOk { get; init; }
        public bool Cancelled { get; init; }

        /// <summary>Per-player on-pitch time, ordered by descending time. Empty when Re-ID
        /// is disabled (no reidModelPath).</summary>
        public IReadOnlyList<PlayerTimeOnPitch> TimeOnPitch { get; init; } = Array.Empty<PlayerTimeOnPitch>();

        /// <summary>Whole-match possession and pass aggregates. Zeroed when the engine is disabled
        /// or ran without pitch coordinates; still reported so the UI has an honest empty state.</summary>
        public MatchStatsReport? MatchStats { get; init; }
    }

    /// <param name="videoPath">Source recording.</param>
    /// <param name="hlsDir">Output dir for index.m3u8 + seg_*.ts.</param>
    /// <param name="onFirstSegment">Fired once, as soon as the first HLS segment lands, so the
    /// caller can flag playback-ready while analysis continues.</param>
    /// <param name="onEvents">Fired per window with newly detected events (absolute timestamps).</param>
    /// <param name="windowSeconds">Rolling event-detection window length (default 30s).</param>
    /// <param name="homography">A FIXED pitch calibration, used only when no
    /// <paramref name="pitchModelPath"/> is given — i.e. a static camera whose homography was set
    /// once (manual calibration). With a pitch model, homography is fitted PER FRAME and this is
    /// ignored. When both are null the engine runs in normalized coordinates and, by design,
    /// emits no distance-derived events — metre thresholds on pixel fractions are confident
    /// nonsense.</param>
    /// <param name="engineDumpPath">Write one observation per processed frame here (JSONL) for
    /// offline rule tuning via <see cref="WorldStateReplay"/>. Null disables the dump.</param>
    /// <param name="engineConfig">Football thresholds; null uses the defaults.</param>
    /// <param name="pitchModelPath">Exported pitch-keypoint YOLO-pose .onnx. When set, each frame
    /// is registered to pitch metres (per-frame homography via <see cref="PitchRegistrar"/>),
    /// which is what a panning/zooming broadcast camera needs. Null falls back to
    /// <paramref name="homography"/>.</param>
    public static Result Run(
        string videoPath,
        string hlsDir,
        string modelPath,
        IReadOnlyDictionary<int, string> classNames,
        int frameStride = 5,
        int imgsz = 640,
        EventConfig? eventConfig = null,
        Action<double>? onProgress = null,
        Action? onFirstSegment = null,
        Action<IReadOnlyList<DetectedEvent>>? onEvents = null,
        double windowSeconds = 30.0,
        string executionProvider = "dml",
        int deviceId = 0,
        Func<bool>? isCancelled = null,
        string? reidModelPath = null,
        Homography? homography = null,
        string? engineDumpPath = null,
        EngineConfig? engineConfig = null,
        string? pitchModelPath = null)
    {
        (double? probeDuration, double? probeFps) = FfmpegTools.ProbeVideo(videoPath);

        using var cap = new VideoCapture(videoPath);
        if (!cap.IsOpened())
            throw new InvalidOperationException($"Cannot open video: {videoPath}");

        double fps = cap.Get(VideoCaptureProperties.Fps);
        if (fps <= 0 || double.IsNaN(fps)) fps = probeFps is > 0 ? probeFps!.Value : 25.0;
        int width = (int)cap.Get(VideoCaptureProperties.FrameWidth);
        int height = (int)cap.Get(VideoCaptureProperties.FrameHeight);
        if (width <= 0 || height <= 0)
            return new Result { DurationSeconds = probeDuration, Fps = fps, EncodeOk = false };

        int stride = Math.Max(1, frameStride);
        double duration = probeDuration ?? (cap.Get(VideoCaptureProperties.FrameCount) / fps);

        // The football engine needs a stable PlayerId to vote a player's team and to track who
        // holds the ball, so it rides on Re-ID: no Re-ID model, no engine.
        bool engineEnabled = !string.IsNullOrWhiteSpace(reidModelPath);

        using var detector = new Detector(
            modelPath, classNames, frameRate: (int)Math.Round(fps),
            frameStride: stride, imgsz: imgsz,
            executionProvider: executionProvider, deviceId: deviceId,
            reidModelPath: reidModelPath, classifyTeams: engineEnabled,
            pitchModelPath: pitchModelPath);

        // Per-frame pitch registration when a pitch model is loaded; otherwise the fixed
        // homography (if any) is used for every frame. The registrar is stateful (it smooths and
        // rejects jumpy fits across frames), so it lives here, not in the per-frame Detector.
        bool pitchModelActive = !string.IsNullOrWhiteSpace(pitchModelPath);
        var registrar = pitchModelActive ? new PitchRegistrar(width, height) : null;

        // Per-player on-pitch time, fed from the (PlayerId-carrying) FrameResults. Only
        // meaningful when Re-ID is enabled; harmless no-op otherwise (no PlayerIds present).
        var timeOnPitch = new TimeOnPitchTracker(maxGapSeconds: Math.Max(2.0, 3.0 * stride / fps));

        // Vision -> WorldState -> football rules. Everything below this line reasons about the
        // match, not about pixels; swapping the detector leaves it untouched.
        EngineConfig engCfg = engineConfig ?? new EngineConfig();
        WorldStateBuilder? world = engineEnabled ? new WorldStateBuilder(engCfg) : null;
        RuleEngine? ruleEngine = engineEnabled
            ? new RuleEngine(new List<IGameRule> { new PossessionRule(), new PassRule(engCfg) }, engCfg)
            : null;
        // Whole-match aggregates, mirrored on timeOnPitch. Always constructed so the run reports
        // an (empty) MatchStats even when the engine never attributes possession.
        var matchStats = new MatchStatsTracker();

        // The dump header carries the two team colours, which the classifier only knows after it
        // has seeded. Opening it lazily keeps that diagnostic in the file; a header written on
        // frame 0 could only ever say "unknown".
        WorldStateJsonl? dump = null;
        bool dumpAttempted = false;
        var pendingObservations = new List<FrameObservation>();

        var ffmpeg = FfmpegTools.StartEncodeHls(hlsDir, width, height, fps);
        if (ffmpeg is null)
            return new Result { DurationSeconds = duration, Fps = fps, EncodeOk = false };

        var flags = new OverlayFlags { Boxes = true, Labels = true, Traces = false };
        FrameResult? lastResult = null;
        bool firstSegmentSeen = false;
        int framesProcessed = 0;

        // Rolling window of detected frames; on each window boundary we run Events.Detect on
        // it and emit. A small overlap (kept tail) avoids losing an event straddling the edge.
        var window = new List<FrameResult>();
        double windowStart = 0.0;
        var emittedKeys = new HashSet<string>();   // dedup across overlap (type|rounded-ts)

        // Both event sources — the legacy ball-trajectory heuristic and the football engine —
        // funnel through here, so the same event can never be reported twice.
        void EmitFresh(IReadOnlyList<DetectedEvent> found)
        {
            var fresh = new List<DetectedEvent>();
            foreach (DetectedEvent e in found)
            {
                string key = $"{e.Type}|{Math.Round(e.TimestampSeconds, 1)}";
                if (emittedKeys.Add(key)) fresh.Add(e);
            }
            if (fresh.Count > 0) onEvents?.Invoke(fresh);
        }

        void FlushWindow()
        {
            if (window.Count == 0) return;
            EmitFresh(Events.Detect(window, eventConfig));
        }

        // Buffer observations until the team classifier has settled, then open the dump with the
        // real team colours in its header and replay the buffer into it. If the classifier never
        // settles, flush anyway once the buffer is large enough: a dump with null colours is
        // itself the diagnosis.
        void RecordObservation(FrameObservation obs)
        {
            if (engineDumpPath is null || dumpAttempted && dump is null) return;

            if (dump is not null) { dump.Append(obs); return; }

            pendingObservations.Add(obs);
            (string? colorA, string? colorB) = detector.TeamColors;
            bool settled = colorA is not null;
            bool givenUp = pendingObservations.Count >= 200;
            if (!settled && !givenUp) return;

            dumpAttempted = true;
            dump = WorldStateJsonl.TryCreate(engineDumpPath, new WorldStateJsonl.Header(
                Fps: fps, FrameStride: stride,
                PitchLength: engCfg.PitchLength, PitchWidth: engCfg.PitchWidth,
                // Header describes the RUN, not one frame: coordinates are in metres whenever a
                // pitch model or a fixed homography is available. (Per-frame, an individual
                // observation may still be normalized before the registrar catches the lines —
                // that lives in each frame's own "n" field, not here.)
                NormalizedCoords: !pitchModelActive && homography is null,
                TeamColorA: colorA, TeamColorB: colorB));
            if (dump is not null)
                foreach (FrameObservation buffered in pendingObservations) dump.Append(buffered);
            pendingObservations.Clear();
            pendingObservations.TrimExcess();
        }

        bool cancelled = false;
        bool writeFailed = false;

        // Buffer of decoded frames awaiting a batch flush. Each Mat is a CLONE (cap.Read
        // reuses one Mat), so every entry MUST be disposed after it's written.
        int batchN = ReadBatchEnv();
        var pending = new List<(Mat frame, int srcIndex, double ts, bool isStride)>();
        int pendingStride = 0;

        // Draws boxes on every buffered frame and writes them to ffmpeg IN ORDER; the
        // stride frames drive detection (batched) + the event window. lastResult carries
        // across batches so in-between frames keep the previous boxes.
        void ProcessBatch()
        {
            if (pending.Count == 0) return;

            // 1. Batched YOLO + sequential tracking on this batch's stride frames.
            var strideItems = new List<(Mat, int, double)>(pendingStride);
            foreach (var p in pending)
                if (p.isStride) strideItems.Add((p.frame, p.srcIndex, p.ts));

            IReadOnlyList<TrackedFrame> tracked = strideItems.Count > 0
                ? detector.TrackFramesBatched(strideItems)
                : Array.Empty<TrackedFrame>();

            // 2. Feed results into the rolling event window in order (same logic as before),
            //    and — additively — through the football engine.
            foreach (TrackedFrame tf in tracked)
            {
                FrameResult fr = tf.Frame;
                window.Add(fr);
                timeOnPitch.Add(fr);
                framesProcessed++;

                if (world is not null && ruleEngine is not null)
                {
                    // Per-frame homography from the registrar when a pitch model is active
                    // (broadcast camera); otherwise the fixed one passed in (static camera).
                    Homography? frameHomography = registrar is not null
                        ? registrar.Update(tf.PitchKeypoints)
                        : homography;

                    FrameObservation obs = ObservationMapper.ToObservation(
                        fr, tf.Teams, frameHomography, width, height);
                    RecordObservation(obs);
                    WorldState ws = world.Add(obs);
                    matchStats.Add(ws);

                    // Capture raw engine events BEFORE the persistence shim: the shim drops
                    // `pass` and `possession_change`, but the stats tracker needs `pass` to count
                    // completed passes, not just turnovers.
                    IReadOnlyList<GameEvent> raw = ruleEngine.OnFrame(ws);
                    foreach (GameEvent ge in raw) matchStats.AddEvent(ge);
                    IReadOnlyList<DetectedEvent> engineEvents = EngineEventShim.ToDetectedEvents(raw);
                    if (engineEvents.Count > 0) EmitFresh(engineEvents);
                }

                if (fr.TimestampSeconds - windowStart >= windowSeconds)
                {
                    FlushWindow();
                    int keepFrom = window.FindIndex(f => f.TimestampSeconds >= fr.TimestampSeconds - 2.0);
                    if (keepFrom > 0) window.RemoveRange(0, keepFrom);
                    windowStart = window.Count > 0 ? window[0].TimestampSeconds : fr.TimestampSeconds;
                }
            }

            // 3. Draw + write EVERY buffered frame in srcIndex order; dispose the clones.
            int resultCursor = 0;
            foreach (var p in pending)
            {
                if (p.isStride) lastResult = tracked[resultCursor++].Frame;

                if (lastResult is not null)
                    Overlay.Draw(p.frame, lastResult, flags);

                if (!WriteFrame(ffmpeg, p.frame)) { writeFailed = true; }

                if (!firstSegmentSeen && File.Exists(Path.Combine(hlsDir, "index.m3u8")))
                {
                    firstSegmentSeen = true;
                    onFirstSegment?.Invoke();
                }
                if (onProgress is not null && duration > 0)
                    onProgress(Math.Min(0.99, p.ts / duration));

                p.frame.Dispose();
            }
            pending.Clear();
            pendingStride = 0;
        }

        try
        {
            using var frame = new Mat();
            int srcIndex = 0;
            double lastCancelCheck = -100.0;

            while (cap.Read(frame) && !frame.Empty())
            {
                double ts = srcIndex / fps;

                // Cooperative cancellation: poll at most every ~2s (isCancelled hits the DB).
                if (isCancelled is not null && ts - lastCancelCheck >= 2.0)
                {
                    lastCancelCheck = ts;
                    if (isCancelled()) { cancelled = true; break; }
                }

                bool isStride = srcIndex % stride == 0;
                pending.Add((frame.Clone(), srcIndex, ts, isStride));   // clone: cap.Read reuses `frame`
                if (isStride) pendingStride++;

                if (pendingStride >= batchN) ProcessBatch();
                if (writeFailed) break;

                srcIndex++;
            }

            // Flush the tail unless we're bailing out.
            if (!cancelled && !writeFailed) ProcessBatch();

            // Final (partial) window — skip if cancelled (no point emitting stale events).
            if (!cancelled && !writeFailed)
            {
                FlushWindow();
                if (ruleEngine is not null)
                {
                    IReadOnlyList<GameEvent> flushed = ruleEngine.Flush();
                    foreach (GameEvent ge in flushed) matchStats.AddEvent(ge);
                    EmitFresh(EngineEventShim.ToDetectedEvents(flushed));
                }
            }

            ffmpeg.StandardInput.Close();
            ffmpeg.WaitForExit();
            if (!cancelled && !writeFailed) onProgress?.Invoke(1.0);

            return new Result
            {
                Cancelled = cancelled,
                DurationSeconds = duration > 0 ? duration : probeDuration,
                Fps = fps,
                FramesProcessed = framesProcessed,
                EncodeOk = ffmpeg.ExitCode == 0 && File.Exists(Path.Combine(hlsDir, "index.m3u8")),
                TimeOnPitch = cancelled ? Array.Empty<PlayerTimeOnPitch>() : timeOnPitch.Report(),
                MatchStats = cancelled ? null : matchStats.Report(),
            };
        }
        finally
        {
            // A short video may end before the team classifier seeds. Write what we have —
            // an observation dump is diagnostics, and one with null team colours still says
            // exactly why possession was never attributed.
            if (engineDumpPath is not null && dump is null && pendingObservations.Count > 0)
            {
                (string? colorA, string? colorB) = detector.TeamColors;
                dump = WorldStateJsonl.TryCreate(engineDumpPath, new WorldStateJsonl.Header(
                    Fps: fps, FrameStride: stride,
                    PitchLength: engCfg.PitchLength, PitchWidth: engCfg.PitchWidth,
                    NormalizedCoords: !pitchModelActive && homography is null,
                    TeamColorA: colorA, TeamColorB: colorB));
                if (dump is not null)
                    foreach (FrameObservation buffered in pendingObservations) dump.Append(buffered);
            }
            dump?.Dispose();

            // Dispose any buffered clones not yet written (cancel / write error / exception).
            foreach (var p in pending) { try { p.frame.Dispose(); } catch { } }
            pending.Clear();
            try { if (!ffmpeg.HasExited) ffmpeg.Kill(); } catch { /* best effort */ }
            ffmpeg.Dispose();
        }
    }

    // Batch size for GPU inference, from ONNX_BATCH (default 8, clamped [1..32]).
    // ONNX_BATCH=1 restores the original one-frame-at-a-time behaviour (kill-switch).
    private static int ReadBatchEnv()
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("ONNX_BATCH"), out int n))
            return Math.Clamp(n, 1, 32);
        return 8;
    }

    private static bool WriteFrame(System.Diagnostics.Process ffmpeg, Mat frame)
    {
        // IMPORTANT: only dispose a clone we own. When the frame is already continuous we
        // write it directly — wrapping it in `using` would dispose the caller's reused Mat
        // and the next cap.Read() throws ObjectDisposedException.
        Mat? clone = null;
        try
        {
            Mat cont = frame.IsContinuous() ? frame : (clone = frame.Clone());
            ffmpeg.StandardInput.BaseStream.Write(cont.AsSpan<byte>());
            return true;
        }
        catch (IOException) { return false; }
        catch (ObjectDisposedException) { return false; }
        finally { clone?.Dispose(); }
    }
}
