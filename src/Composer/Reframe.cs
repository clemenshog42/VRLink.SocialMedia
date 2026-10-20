namespace VRLink.Composer;

/// <summary>Crop window in source (camera) pixels: centre + size.</summary>
public readonly record struct CropRect(double Cx, double Cy, double W, double H)
{
    public double Left => Cx - W / 2;
    public double Top => Cy - H / 2;
}

public sealed record ReframeOptions
{
    /// <summary>Output aspect width/height: 9/16 for the full vertical clip, 9/8 for the split top half.</summary>
    public double Aspect { get; init; } = 9.0 / 16.0;
    /// <summary>Output height in pixels (1920 full, 960 split top) — used for the upscale limit.</summary>
    public int OutHeight { get; init; } = 1920;
    public double MaxZoom { get; init; } = 1.35;
    /// <summary>Never enlarge source pixels more than this (else the zoom is limited and a warning is logged).</summary>
    public double MaxUpscale { get; init; } = 1.5;
    /// <summary>Hero head at this fraction of the crop height from the top (upper-middle third).</summary>
    public double HeroY { get; init; } = 0.40;
    /// <summary>Dead zone as a fraction of the crop width/height: smaller target moves are ignored.</summary>
    public double DeadZone { get; init; } = 0.06;
    /// <summary>Critically damped spring frequency (rad/s) for the pan; zoom uses half of it.</summary>
    public double Omega { get; init; } = 4.0;
    /// <summary>Pan speed limit in crop widths per second (keeps fast hero moves watchable).</summary>
    public double MaxPanPerSec { get; init; } = 1.5;
    /// <summary>Push-in starts this long before each kill and relaxes this long after it.</summary>
    public double ZoomInSec { get; init; } = 1.5;
    public double ZoomHoldSec { get; init; } = 0.8;
}

public sealed record ReframeResult(List<CropRect> Crops, double EffectiveMaxZoom, double BaseUpscale, List<string> Warnings);

/// <summary>
/// 9:16 (or 9:8) auto-reframe that follows the hero (pure, unit-tested). Per source frame: dead-zoned target, critically damped
/// spring on centre and zoom, push-in toward kills (≤ MaxZoom, limited so the output never upscales more than MaxUpscale),
/// hero not visible → hold the last position and widen. The crop always stays inside the source image.
/// </summary>
public static class Reframer
{
    /// <param name="track">Hero head per source frame in SOURCE pixels (already scaled from overlay pixels).</param>
    /// <param name="killSrc">Kill times (source seconds).</param>
    public static ReframeResult Compute(IReadOnlyList<TrackPoint> track, int srcW, int srcH, double fps, IReadOnlyList<double> killSrc, ReframeOptions? o = null)
    {
        o ??= new ReframeOptions();
        var warnings = new List<string>();
        // largest crop of the wanted aspect that fits the source
        double baseH = Math.Min(srcH, srcW / o.Aspect);
        double baseUpscale = o.OutHeight / baseH;
        double maxZoom = Math.Min(o.MaxZoom, o.MaxUpscale / baseUpscale);
        if (maxZoom < 1)
        {
            warnings.Add($"source too small: the crop is upscaled {baseUpscale:0.00}x (> {o.MaxUpscale}x) even without zoom; push-in disabled");
            maxZoom = 1;
        }
        else if (maxZoom < o.MaxZoom)
            warnings.Add($"push-in limited to {maxZoom:0.00}x so the output never upscales more than {o.MaxUpscale}x (source {srcW}x{srcH})");

        int n = track.Count;
        var crops = new List<CropRect>(n);
        if (n == 0) return new ReframeResult(crops, maxZoom, baseUpscale, warnings);

        double dt = 1 / fps;
        // initial state: first visible point (or image centre)
        var firstVis = track.FirstOrDefault(t => t.Visible);
        double tx = firstVis?.X ?? srcW / 2.0, ty = firstVis?.Y ?? srcH / 2.0;
        double cx = tx, cy = ty + (0.5 - o.HeroY) * baseH, z = 1;
        double vx = 0, vy = 0, vz = 0;
        double lastX = tx, lastY = ty;
        int invisible = 0;

        for (int i = 0; i < n; i++)
        {
            double t = i * dt;
            var p = track[i];
            if (p.Visible) { lastX = p.X; lastY = p.Y; invisible = 0; } else invisible++;

            // zoom target: push in toward kills, widen when the hero is lost for > 0.5 s
            double zt = 1;
            foreach (var k in killSrc)
            {
                // ramp in over ZoomInSec, fully zoomed 0.3 s before the kill (the spring needs a moment), hold, relax
                double kin = k - 0.3;
                double wk = t < kin - o.ZoomInSec ? 0
                         : t < kin ? (t - (kin - o.ZoomInSec)) / o.ZoomInSec
                         : t < k + o.ZoomHoldSec ? 1
                         : Math.Max(0, 1 - (t - k - o.ZoomHoldSec) / o.ZoomInSec);
                zt = Math.Max(zt, 1 + (maxZoom - 1) * Smooth(wk));
            }
            if (invisible > fps * 0.5) zt = 1;

            double h = baseH / z, w0 = h * o.Aspect;
            double wantX = lastX, wantY = lastY + (0.5 - o.HeroY) * h;
            // dead zone: only move the target when the hero leaves the central box
            if (Math.Abs(wantX - tx) > o.DeadZone * w0) tx = wantX - Math.Sign(wantX - tx) * o.DeadZone * w0;
            if (Math.Abs(wantY - ty) > o.DeadZone * h) ty = wantY - Math.Sign(wantY - ty) * o.DeadZone * h;

            // critically damped springs (semi-implicit Euler)
            double vmax = o.MaxPanPerSec * w0;
            Spring(ref cx, ref vx, tx, o.Omega, dt, vmax);
            Spring(ref cy, ref vy, ty, o.Omega, dt, vmax);
            Spring(ref z, ref vz, zt, o.Omega, dt, double.MaxValue);
            z = Math.Clamp(z, 1, maxZoom);

            h = baseH / z; double w = h * o.Aspect;
            double ccx = Math.Clamp(cx, w / 2, srcW - w / 2), ccy = Math.Clamp(cy, h / 2, srcH - h / 2);
            crops.Add(new CropRect(ccx, ccy, w, h));
        }
        return new ReframeResult(crops, maxZoom, baseUpscale, warnings);
    }

    private static double Smooth(double u) { u = Math.Clamp(u, 0, 1); return u * u * (3 - 2 * u); }

    private static void Spring(ref double x, ref double v, double target, double omega, double dt, double vmax)
    {
        double a = omega * omega * (target - x) - 2 * omega * v;
        v = Math.Clamp(v + a * dt, -vmax, vmax);
        x += v * dt;
    }

    /// <summary>Crop at a fractional source time (linear interpolation between frames), with an extra punch zoom.</summary>
    public static CropRect At(IReadOnlyList<CropRect> crops, double srcT, double fps, double punch, int srcW, int srcH)
    {
        if (crops.Count == 0) return new CropRect(srcW / 2.0, srcH / 2.0, srcW, srcH);
        double f = Math.Clamp(srcT * fps, 0, crops.Count - 1);
        int i = (int)Math.Floor(f); int j = Math.Min(i + 1, crops.Count - 1); double u = f - i;
        var a = crops[i]; var b = crops[j];
        double w = (a.W + (b.W - a.W) * u) / (1 + punch), h = (a.H + (b.H - a.H) * u) / (1 + punch);
        double cx = Math.Clamp(a.Cx + (b.Cx - a.Cx) * u, w / 2, srcW - w / 2);
        double cy = Math.Clamp(a.Cy + (b.Cy - a.Cy) * u, h / 2, srcH - h / 2);
        return new CropRect(cx, cy, w, h);
    }
}
