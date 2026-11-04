namespace PitchWise.Engine;

/// <summary>
/// The pitch template: the fixed real-world positions of the lines and intersections a keypoint
/// model detects, in metres. This is the "known" side of every pixel↔pitch correspondence that
/// feeds <c>Homography.FromPoints</c>.
///
/// <b>Coordinate convention</b> — pinned here once, because three places disagreed and a
/// homography fitted in one convention and rendered in another silently mirrors the pitch:
/// <list type="bullet">
/// <item>Pitch is <b>105 m × 68 m</b> (the real FIFA dimensions the frontend and
///       <c>Homography.cs</c> already use — NOT Roboflow's default 120×70).</item>
/// <item>Origin (0,0) is the <b>top-left</b> corner. x runs 0→105 along the length (left goal
///       line to right goal line); y runs 0→68 down the width (top touchline to bottom
///       touchline). This matches <c>web/components/PitchMinimap.tsx</c>, which renders the dots,
///       so it is the source of truth. (The prose comment in <c>Homography.cs</c> says "bottom
///       touchline"; that comment is wrong relative to what actually renders — the fit and the
///       render must share this convention, and the frontend wins because the user sees it.)</item>
/// </list>
///
/// The 32 keypoints and their ordering are the Roboflow <c>SoccerPitchConfiguration</c> set (so a
/// model trained against it maps index-for-index onto this array), rescaled from Roboflow's
/// 120×70 m to 105×68 m. A model's output channel <c>i</c> corresponds to <see cref="Keypoints"/>[i].
/// </summary>
public static class PitchModel
{
    public const double Length = 105.0;   // m, along x
    public const double Width = 68.0;      // m, along y

    // Roboflow's template is defined on a 120×70 m pitch in centimetres. Scale each axis onto the
    // real 105×68 m pitch. This is an approximation — the two pitches are not similar rectangles,
    // so penalty-box depths etc. shift by a few percent — but it keeps the template internally
    // consistent with the 105×68 world the rest of the system uses. If accuracy matters, replace
    // these with directly-measured 105×68 coordinates rather than rescaling.
    private const double Sx = Length / 120.0;
    private const double Sy = Width / 70.0;

    private static (double X, double Y) P(double xCm, double yCm) =>
        (xCm / 100.0 * Sx, yCm / 100.0 * Sy);

    /// <summary>The 32 pitch keypoints in metres, indexed to match the Roboflow model's output
    /// channel order. Origin top-left, x→right (length), y→down (width).</summary>
    public static readonly IReadOnlyList<(double X, double Y)> Keypoints = new[]
    {
        P(0, 0),        //  0  left goal line × top touchline (top-left corner)
        P(0, 1450),     //  1  left goal line × penalty-box top edge extended
        P(0, 2584),     //  2  left goal line × 6-yd box top
        P(0, 4416),     //  3  left goal line × 6-yd box bottom
        P(0, 5550),     //  4  left goal line × penalty-box bottom edge extended
        P(0, 7000),     //  5  left goal line × bottom touchline (bottom-left corner)
        P(550, 2584),   //  6  left 6-yd box top-inner
        P(550, 4416),   //  7  left 6-yd box bottom-inner
        P(1100, 3500),  //  8  left goal-area centre depth (goal centre line)
        P(2015, 1450),  //  9  left penalty box top-outer
        P(2015, 2584),  // 10  left penalty box top-inner
        P(2015, 4416),  // 11  left penalty box bottom-inner
        P(2015, 5550),  // 12  left penalty box bottom-outer
        P(6000, 0),     // 13  halfway line × top touchline
        P(6000, 2585),  // 14  centre circle top
        P(6000, 4415),  // 15  centre circle bottom
        P(6000, 7000),  // 16  halfway line × bottom touchline
        P(9985, 1450),  // 17  right penalty box top-outer
        P(9985, 2584),  // 18  right penalty box top-inner
        P(9985, 4416),  // 19  right penalty box bottom-inner
        P(9985, 5550),  // 20  right penalty box bottom-outer
        P(11100, 3500), // 21  right goal-area centre depth
        P(11450, 2584), // 22  right 6-yd box top-inner
        P(11450, 4416), // 23  right 6-yd box bottom-inner
        P(12000, 0),    // 24  right goal line × top touchline (top-right corner)
        P(12000, 1450), // 25  right goal line × penalty-box top edge extended
        P(12000, 2584), // 26  right goal line × 6-yd box top
        P(12000, 4416), // 27  right goal line × 6-yd box bottom
        P(12000, 5550), // 28  right goal line × penalty-box bottom edge extended
        P(12000, 7000), // 29  right goal line × bottom touchline (bottom-right corner)
        P(4085, 3500),  // 30  centre circle left (penalty-arc-adjacent) point
        P(7915, 3500),  // 31  centre circle right point
    };

    public static int Count => Keypoints.Count;
}
