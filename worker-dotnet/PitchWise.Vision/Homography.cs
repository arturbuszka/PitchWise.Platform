using OpenCvSharp;

namespace PitchWise.Vision;

/// <summary>
/// Pitch homography: maps pixel positions to real-world pitch coordinates.
/// Standard football pitch 105m x 68m; origin (0,0) = left goal line, bottom touchline.
///
/// </summary>
public sealed class Homography
{
    private readonly Mat _matrix;  // 3x3

    private Homography(Mat matrix) => _matrix = matrix;

    /// <summary>Builds a homography from >= 4 pixel↔pitch point pairs (RANSAC).</summary>
    public static Homography FromPoints(
        IReadOnlyList<(double x, double y)> pixelPts,
        IReadOnlyList<(double x, double y)> pitchPts)
    {
        if (pixelPts.Count < 4 || pitchPts.Count < 4)
            throw new ArgumentException("Need at least 4 point pairs for homography");

        var src = new Point2f[pixelPts.Count];
        var dst = new Point2f[pitchPts.Count];
        for (int i = 0; i < pixelPts.Count; i++)
        {
            src[i] = new Point2f((float)pixelPts[i].x, (float)pixelPts[i].y);
            dst[i] = new Point2f((float)pitchPts[i].x, (float)pitchPts[i].y);
        }

        Mat h = Cv2.FindHomography(
            InputArray.Create(src), InputArray.Create(dst), HomographyMethods.Ransac);
        if (h.Empty())
            throw new ArgumentException("FindHomography failed — check point correspondences");
        return new Homography(h);
    }

    /// <summary>Projects the foot position (bottom-center of the box) to pitch coords.</summary>
    public (double x, double y) Project(double x1, double y1, double x2, double y2)
    {
        double footX = (x1 + x2) / 2.0;
        double footY = y2;

        // Read the 3x3 matrix and apply it to the homogeneous point.
        double[] m = new double[9];
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
                m[r * 3 + c] = _matrix.At<double>(r, c);

        double px = m[0] * footX + m[1] * footY + m[2];
        double py = m[3] * footX + m[4] * footY + m[5];
        double w = m[6] * footX + m[7] * footY + m[8];
        if (Math.Abs(w) < 1e-9) return (0.0, 0.0);
        return (px / w, py / w);
    }

    /// <summary>Fallback when no homography is set: normalise foot position to [0,1].</summary>
    public static (double x, double y) PixelToNormalized(
        double x1, double y1, double x2, double y2, int frameW, int frameH)
    {
        double footX = (x1 + x2) / 2.0;
        return (frameW > 0 ? footX / frameW : 0.0, frameH > 0 ? y2 / frameH : 0.0);
    }
}
