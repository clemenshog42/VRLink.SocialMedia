using System.Numerics;
using System.Text.Json.Serialization;

namespace VRLink.Shared.Calibration;

/// <summary>
/// Pinhole camera with optional radial distortion k1, in Unity arena space.
///
/// Arena space (same as the game, Docs/ARCHITECTURE.md): LEFT-handed, metres, +Y up, +X right, +Z forward.
/// Camera pose = Unity transform: Position + Euler (Pitch = X, Yaw = Y, Roll = Z, degrees; Unity applies Z, then X, then Y,
/// i.e. R = Ry · Rx · Rz). The camera looks along its local +Z, local +Y is up. Positive pitch looks down, positive yaw turns right.
/// Image: pixel (0,0) top-left, u to the right, v down. Principal point = image centre. VFovDeg = vertical field of view.
/// Distortion: normalized (x/z, y/z) scaled by (1 + K1·r²) before applying the focal length.
/// </summary>
public sealed record CameraPose
{
    public double X { get; init; }
    public double Y { get; init; }
    public double Z { get; init; }
    public double Pitch { get; init; }
    public double Yaw { get; init; }
    public double Roll { get; init; }
    public double VFovDeg { get; init; } = 70;
    public double K1 { get; init; }
    public int Width { get; init; } = 3840;
    public int Height { get; init; } = 2160;

    [JsonIgnore] public Vector3 Position => new((float)X, (float)Y, (float)Z);
    [JsonIgnore] public double FocalPx => Height / 2.0 / Math.Tan(VFovDeg * Math.PI / 360.0);

    public static double[,] Rotation(double pitchDeg, double yawDeg, double rollDeg)
    {
        double p = pitchDeg * Math.PI / 180, y = yawDeg * Math.PI / 180, r = rollDeg * Math.PI / 180;
        double cp = Math.Cos(p), sp = Math.Sin(p), cy = Math.Cos(y), sy = Math.Sin(y), cr = Math.Cos(r), sr = Math.Sin(r);
        var ry = new double[,] { { cy, 0, sy }, { 0, 1, 0 }, { -sy, 0, cy } };
        var rx = new double[,] { { 1, 0, 0 }, { 0, cp, -sp }, { 0, sp, cp } };
        var rz = new double[,] { { cr, -sr, 0 }, { sr, cr, 0 }, { 0, 0, 1 } };
        return Mul(Mul(ry, rx), rz);
    }

    private static double[,] Mul(double[,] a, double[,] b)
    {
        var m = new double[3, 3];
        for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) for (int k = 0; k < 3; k++) m[i, j] += a[i, k] * b[k, j];
        return m;
    }

    /// <summary>Projects a world point; false if it is behind the camera (z ≤ 1 cm).</summary>
    public bool TryProject(double wx, double wy, double wz, out double u, out double v)
    {
        var r = Rotation(Pitch, Yaw, Roll);
        double dx = wx - X, dy = wy - Y, dz = wz - Z;
        // camera coords = Rᵀ · (P − C)
        double cx = r[0, 0] * dx + r[1, 0] * dy + r[2, 0] * dz;
        double cy = r[0, 1] * dx + r[1, 1] * dy + r[2, 1] * dz;
        double cz = r[0, 2] * dx + r[1, 2] * dy + r[2, 2] * dz;
        u = v = double.NaN;
        if (cz <= 0.01) return false;
        double xn = cx / cz, yn = cy / cz, s = 1 + K1 * (xn * xn + yn * yn), f = FocalPx;
        u = Width / 2.0 + f * xn * s;
        v = Height / 2.0 - f * yn * s;
        return true;
    }

    public bool TryProject(Vector3 p, out double u, out double v) => TryProject(p.X, p.Y, p.Z, out u, out v);

    /// <summary>Angle (deg) between the viewing directions of two poses.</summary>
    public static double ForwardAngleDeg(CameraPose a, CameraPose b)
    {
        var ra = Rotation(a.Pitch, a.Yaw, a.Roll); var rb = Rotation(b.Pitch, b.Yaw, b.Roll);
        // compare full rotations: angle of Raᵀ·Rb
        double trace = 0;
        for (int i = 0; i < 3; i++) for (int k = 0; k < 3; k++) trace += ra[k, i] * rb[k, i];
        return Math.Acos(Math.Clamp((trace - 1) / 2, -1, 1)) * 180 / Math.PI;
    }
}

/// <summary>One clicked correspondence: a known arena point (e.g. the controller at a demo time) and where it is in the image.</summary>
public sealed record Correspondence(double WorldX, double WorldY, double WorldZ, double U, double V, string? Note = null);

/// <summary>Saved per venue and camera (json in the data dir).</summary>
public sealed record CameraCalibration
{
    public string VenueId { get; init; } = "default";
    public string CameraId { get; init; } = "cam1";
    public CameraPose Pose { get; init; } = new();
    /// <summary>Footage time − demo time (clap test). Footage for demo time t is at camera time t + offset.</summary>
    public double TimeOffsetMs { get; init; }
    public List<Correspondence> Points { get; init; } = new();
    public double RmsErrorPx { get; init; }
    public DateTime SavedUtc { get; init; }
    public string CoordinateSystem { get; init; } = "Unity arena space: left-handed, metres, +Y up, +X right, +Z forward; Euler Z→X→Y";
}
