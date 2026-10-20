namespace VRLink.Shared.Calibration;

public sealed record SolveResult(CameraPose Pose, double RmsErrorPx, double MaxErrorPx, int Iterations, bool Converged);

/// <summary>
/// Solves the camera pose + vertical FOV (+ optional k1) from ≥4 point correspondences (pure C#, no OpenCV).
/// Levenberg–Marquardt on the reprojection error with a numeric Jacobian. Without an initial guess it runs a
/// multi-start (camera positions on rings around the clicked points, looking at their centroid) and keeps the best.
/// Unknowns: X, Y, Z, Pitch, Yaw, Roll, VFov (7) [+ K1]. 4 points = 8 equations; 5–6 points are recommended.
/// </summary>
public static class CameraSolver
{
    public static double Rms(CameraPose pose, IReadOnlyList<Correspondence> pts, out double max)
    {
        double sum = 0; max = 0;
        foreach (var c in pts)
        {
            double e = pose.TryProject(c.WorldX, c.WorldY, c.WorldZ, out var u, out var v) ? Math.Sqrt((u - c.U) * (u - c.U) + (v - c.V) * (v - c.V)) : 1e4;
            sum += e * e; max = Math.Max(max, e);
        }
        return pts.Count == 0 ? 0 : Math.Sqrt(sum / pts.Count);
    }

    /// <param name="lockedVFovDeg">Known vertical FOV (camera spec sheet / previous calibration): removes the depth–zoom ambiguity, most accurate with few clicks.</param>
    public static SolveResult Solve(IReadOnlyList<Correspondence> pts, int width, int height, CameraPose? initial = null, bool solveK1 = false, double? lockedVFovDeg = null)
    {
        if (pts.Count < 4) throw new ArgumentException("need at least 4 correspondences");
        if (solveK1 && pts.Count < 5) solveK1 = false; // 8 unknowns need ≥ 5 points (10 equations)
        bool lockFov = lockedVFovDeg != null;
        if (initial != null)
        {
            var init = initial with { Width = width, Height = height, VFovDeg = lockedVFovDeg ?? initial.VFovDeg };
            return Refine(init, pts, solveK1, 500, lockFov);
        }

        double mx = pts.Average(p => p.WorldX), my = pts.Average(p => p.WorldY), mz = pts.Average(p => p.WorldZ);
        SolveResult? best = null;
        foreach (var radius in new[] { 3.0, 6.0, 10.0 })
            foreach (var h in new[] { 1.0, 2.5, 4.0 })
                for (int a = 0; a < 12; a++)
                {
                    double ang = a * Math.PI / 6;
                    double cx = mx + radius * Math.Sin(ang), cz = mz + radius * Math.Cos(ang), cy = h;
                    var start = LookAt(cx, cy, cz, mx, my, mz, width, height) with { VFovDeg = lockedVFovDeg ?? 70 };
                    var r = Refine(start, pts, false, 60, lockFov);
                    if (best == null || r.RmsErrorPx < best.RmsErrorPx) best = r;
                }
        var refined = Refine(best!.Pose, pts, false, 500, lockFov);
        if (solveK1) refined = Refine(refined.Pose, pts, true, 500, lockFov);
        return refined;
    }

    /// <summary>Pose at (cx,cy,cz) looking at (tx,ty,tz), roll 0, 70° vertical FOV.</summary>
    public static CameraPose LookAt(double cx, double cy, double cz, double tx, double ty, double tz, int width, int height)
    {
        double dx = tx - cx, dy = ty - cy, dz = tz - cz;
        double yaw = Math.Atan2(dx, dz) * 180 / Math.PI;
        double pitch = -Math.Atan2(dy, Math.Sqrt(dx * dx + dz * dz)) * 180 / Math.PI;
        return new CameraPose { X = cx, Y = cy, Z = cz, Yaw = yaw, Pitch = pitch, Roll = 0, VFovDeg = 70, Width = width, Height = height };
    }

    // parameter vector: X Y Z Pitch Yaw Roll [VFov] [K1]
    private static double[] ToVec(CameraPose p, bool k1, bool lockFov)
    {
        var v = new List<double> { p.X, p.Y, p.Z, p.Pitch, p.Yaw, p.Roll };
        if (!lockFov) v.Add(p.VFovDeg);
        if (k1) v.Add(p.K1);
        return v.ToArray();
    }

    private static CameraPose FromVec(CameraPose t, double[] v, bool k1, bool lockFov)
    {
        int i = 6;
        var fov = lockFov ? t.VFovDeg : Math.Clamp(v[i++], 5, 170);
        var k = k1 ? v[i] : t.K1;
        return t with { X = v[0], Y = v[1], Z = v[2], Pitch = v[3], Yaw = v[4], Roll = v[5], VFovDeg = fov, K1 = k };
    }

    private static double[] Residuals(CameraPose pose, IReadOnlyList<Correspondence> pts)
    {
        var r = new double[pts.Count * 2];
        for (int i = 0; i < pts.Count; i++)
        {
            var c = pts[i];
            if (pose.TryProject(c.WorldX, c.WorldY, c.WorldZ, out var u, out var v)) { r[2 * i] = u - c.U; r[2 * i + 1] = v - c.V; }
            else { r[2 * i] = 1e4; r[2 * i + 1] = 1e4; } // behind the camera: strongly penalised
        }
        return r;
    }

    private static double Sq(double[] r) => r.Sum(x => x * x);

    public static SolveResult Refine(CameraPose start, IReadOnlyList<Correspondence> pts, bool solveK1, int maxIter, bool lockFov = false)
    {
        var x = ToVec(start, solveK1, lockFov);
        int n = x.Length;
        CameraPose P(double[] v) => FromVec(start, v, solveK1, lockFov);
        var r = Residuals(P(x), pts);
        double cost = Sq(r), lambda = 1e-3;
        int it = 0; bool converged = false;
        var steps = Enumerable.Repeat(1e-4, n).ToArray();
        if (solveK1) steps[n - 1] = 1e-6;
        for (; it < maxIter; it++)
        {
            // numeric Jacobian (central differences)
            var J = new double[r.Length, n];
            for (int j = 0; j < n; j++)
            {
                var xp = (double[])x.Clone(); xp[j] += steps[j];
                var xm = (double[])x.Clone(); xm[j] -= steps[j];
                var rp = Residuals(P(xp), pts); var rm = Residuals(P(xm), pts);
                for (int i = 0; i < r.Length; i++) J[i, j] = (rp[i] - rm[i]) / (2 * steps[j]);
            }
            var jtj = new double[n, n]; var jtr = new double[n];
            for (int a = 0; a < n; a++)
            {
                for (int i = 0; i < r.Length; i++) jtr[a] += J[i, a] * r[i];
                for (int b = 0; b < n; b++) for (int i = 0; i < r.Length; i++) jtj[a, b] += J[i, a] * J[i, b];
            }
            bool improved = false;
            for (int tries = 0; tries < 12; tries++)
            {
                var A = new double[n, n];
                for (int a = 0; a < n; a++) for (int b = 0; b < n; b++) A[a, b] = jtj[a, b] + (a == b ? lambda * (jtj[a, a] + 1e-9) : 0);
                var delta = SolveLinear(A, jtr.Select(v => -v).ToArray());
                if (delta == null) { lambda *= 10; continue; }
                var xn = x.Zip(delta, (p, d) => p + d).ToArray();
                var rn = Residuals(P(xn), pts);
                double cn = Sq(rn);
                if (cn < cost)
                {
                    double rel = (cost - cn) / Math.Max(cost, 1e-12);
                    x = xn; r = rn; cost = cn; lambda = Math.Max(lambda / 10, 1e-12); improved = true;
                    if (rel < 1e-12 || delta.Max(Math.Abs) < 1e-10) converged = true;
                    break;
                }
                lambda *= 10;
            }
            if (!improved || converged) { converged = true; break; }
        }
        var pose = P(x);
        double rms = Rms(pose, pts, out var max);
        return new SolveResult(pose, rms, max, it, converged);
    }

    /// <summary>Gaussian elimination with partial pivoting; null if singular.</summary>
    private static double[]? SolveLinear(double[,] a, double[] b)
    {
        int n = b.Length;
        var m = (double[,])a.Clone(); var y = (double[])b.Clone();
        for (int c = 0; c < n; c++)
        {
            int piv = c;
            for (int i = c + 1; i < n; i++) if (Math.Abs(m[i, c]) > Math.Abs(m[piv, c])) piv = i;
            if (Math.Abs(m[piv, c]) < 1e-14) return null;
            if (piv != c)
            {
                for (int k = 0; k < n; k++) (m[c, k], m[piv, k]) = (m[piv, k], m[c, k]);
                (y[c], y[piv]) = (y[piv], y[c]);
            }
            for (int i = c + 1; i < n; i++)
            {
                double f = m[i, c] / m[c, c];
                for (int k = c; k < n; k++) m[i, k] -= f * m[c, k];
                y[i] -= f * y[c];
            }
        }
        var xr = new double[n];
        for (int i = n - 1; i >= 0; i--)
        {
            double s = y[i];
            for (int k = i + 1; k < n; k++) s -= m[i, k] * xr[k];
            xr[i] = s / m[i, i];
        }
        return xr;
    }
}
