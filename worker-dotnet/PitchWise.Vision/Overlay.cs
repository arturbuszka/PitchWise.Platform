using OpenCvSharp;

namespace PitchWise.Vision;

/// <summary>Which overlay layers to draw on a live frame.</summary>
public sealed class OverlayFlags
{
    public bool Boxes { get; set; } = true;
    public bool Labels { get; set; } = true;
    public bool Traces { get; set; }

    public bool AnyOverlay() => Boxes || Labels || Traces;
}

/// <summary>Bounding-box + label overlay rendering.</summary>
public static class Overlay
{
    // BGR colors per class.
    private static readonly Dictionary<string, Scalar> ColorMap = new()
    {
        [ObjectClass.Player] = new Scalar(255, 100, 30),   // orange-blue
        [ObjectClass.Ball] = new Scalar(0, 220, 255),      // yellow
        [ObjectClass.Referee] = new Scalar(0, 0, 220),     // red
        [ObjectClass.Goalkeeper] = new Scalar(0, 200, 0),  // green
    };
    private static readonly Scalar DefaultColor = new(200, 200, 200);

    /// <summary>Draws boxes and labels onto <paramref name="frame"/> in place (BGR Mat).</summary>
    public static void Draw(Mat frame, FrameResult fr, OverlayFlags flags)
    {
        foreach (Detection det in fr.Detections)
        {
            int x1 = (int)det.X1, y1 = (int)det.Y1, x2 = (int)det.X2, y2 = (int)det.Y2;
            Scalar color = ColorMap.TryGetValue(det.Cls, out Scalar c) ? c : DefaultColor;

            if (flags.Boxes)
                Cv2.Rectangle(frame, new Point(x1, y1), new Point(x2, y2), color, thickness: 2);

            if (flags.Labels)
            {
                string label = det.TrackId is int id ? $"{det.Cls} #{id}" : det.Cls;
                Cv2.PutText(
                    frame, label, new Point(x1, Math.Max(y1 - 6, 10)),
                    HersheyFonts.HersheySimplex, 0.45, color, thickness: 1,
                    lineType: LineTypes.AntiAlias);
            }
        }
    }
}
