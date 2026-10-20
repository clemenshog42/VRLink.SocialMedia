using VRLink.Shared.Calibration;
using Xunit.Abstractions;

namespace VRLink.Shared.Tests;

public class CameraSolverTests(ITestOutputHelper output)
{
    /// <summary>Corner camera at ~2.6 m, looking down into a 8 x 8 m arena, 4K, 78° vertical FOV.</summary>
    private static readonly CameraPose Truth = new()
    {
        X = 4.2, Y = 2.6, Z = -3.9, Yaw = -42, Pitch = 24, Roll = 1.5, VFovDeg = 78, Width = 3840, Height = 2160,
    };

    /// <summary>Controller held up at 6 places (what the operator clicks in 6 frames).</summary>
    private static readonly (double x, double y, double z)[] Points6 =
    [
        (-3.0, 1.9, 3.2), (2.5, 1.2, 2.8), (-2.2, 0.9, -1.0), (0.4, 2.1, 0.6), (3.0, 1.6, -0.5), (-0.8, 1.4, 3.6),
    ];

    private static List<Correspondence> Observe(CameraPose truth, IEnumerable<(double x, double y, double z)> pts, double noisePx, int seed)
    {
        var rnd = new Random(seed);
        double Gauss() => Math.Sqrt(-2 * Math.Log(1 - rnd.NextDouble())) * Math.Cos(2 * Math.PI * rnd.NextDouble());
        return pts.Select(p =>
        {
            Assert.True(truth.TryProject(p.x, p.y, p.z, out var u, out var v));
            Assert.InRange(u, 0, truth.Width); Assert.InRange(v, 0, truth.Height); // all visible
            return new Correspondence(p.x, p.y, p.z, u + Gauss() * noisePx, v + Gauss() * noisePx);
        }).ToList();
    }

    private void Report(string name, SolveResult r, CameraPose truth)
    {
        double posErr = Math.Sqrt(Math.Pow(r.Pose.X - truth.X, 2) + Math.Pow(r.Pose.Y - truth.Y, 2) + Math.Pow(r.Pose.Z - truth.Z, 2));
        double angErr = CameraPose.ForwardAngleDeg(r.Pose, truth);
        output.WriteLine($"{name}: pos err {posErr * 1000:0.00} mm, rot err {angErr:0.0000} deg, fov err {Math.Abs(r.Pose.VFovDeg - truth.VFovDeg):0.0000} deg, rms {r.RmsErrorPx:0.000} px");
    }

    [Fact]
    public void Unity_conventions_yaw_right_pitch_down()
    {
        var cam = new CameraPose { Yaw = 90, VFovDeg = 90, Width = 1000, Height = 1000 };
        Assert.True(cam.TryProject(5, 0, 0, out var u, out var v)); // +X is straight ahead after yaw 90
        Assert.Equal(500, u, 6); Assert.Equal(500, v, 6);
        var down = new CameraPose { Pitch = 30, VFovDeg = 90, Width = 1000, Height = 1000 };
        Assert.True(down.TryProject(0, 0, 5, out _, out var v2));
        Assert.True(v2 < 500); // looking down: a point at eye height appears above the centre
        Assert.False(cam.TryProject(-5, 0, 0, out _, out _)); // behind
    }

    [Fact]
    public void Six_clicks_noise_free_recover_exact_pose()
    {
        var r = CameraSolver.Solve(Observe(Truth, Points6, 0, 1), 3840, 2160);
        Report("6 pts, 0 px", r, Truth);
        Assert.True(r.RmsErrorPx < 1e-3);
        Assert.InRange(Math.Abs(r.Pose.X - Truth.X) + Math.Abs(r.Pose.Y - Truth.Y) + Math.Abs(r.Pose.Z - Truth.Z), 0, 1e-4);
    }

    private static (double pos, double ang, double fov) Errors(SolveResult r, CameraPose t) =>
        (Math.Sqrt(Math.Pow(r.Pose.X - t.X, 2) + Math.Pow(r.Pose.Y - t.Y, 2) + Math.Pow(r.Pose.Z - t.Z, 2)),
         CameraPose.ForwardAngleDeg(r.Pose, t), Math.Abs(r.Pose.VFovDeg - t.VFovDeg));

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void Six_clicks_with_known_fov_and_noise_within_1cm_and_0_2deg(int seed)
    {
        // FOV from the camera spec (or a previous calibration) locked: 6 controller clicks are enough.
        var r = CameraSolver.Solve(Observe(Truth, Points6, 0.3, seed), 3840, 2160, lockedVFovDeg: Truth.VFovDeg);
        Report($"6 pts, fov locked, 0.3 px, seed {seed}", r, Truth);
        var (pos, ang, _) = Errors(r, Truth);
        Assert.True(pos < 0.01, $"position error {pos * 100:0.00} cm");
        Assert.True(ang < 0.2, $"rotation error {ang:0.000} deg");
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void Six_clicks_free_fov_documented_limit(int seed)
    {
        // Only 6 points and FOV free: depth and zoom trade off; ~1-2 cm is the geometric limit at 0.3 px (documented in README).
        var r = CameraSolver.Solve(Observe(Truth, Points6, 0.3, seed), 3840, 2160);
        Report($"6 pts, fov free, 0.3 px, seed {seed}", r, Truth);
        var (pos, ang, _) = Errors(r, Truth);
        Assert.True(pos < 0.03, $"position error {pos * 100:0.00} cm");
        Assert.True(ang < 0.2, $"rotation error {ang:0.000} deg");
    }

    [Fact]
    public void Four_clicks_noise_free_solve()
    {
        var r = CameraSolver.Solve(Observe(Truth, Points6.Take(4), 0, 1), 3840, 2160);
        Report("4 pts, 0 px", r, Truth);
        Assert.True(r.RmsErrorPx < 0.01);
    }

    [Fact]
    public void Radial_distortion_k1_is_recovered()
    {
        var truth = Truth with { K1 = -0.08 };
        var pts = Points6.Concat([(1.5, 1.0, 3.9), (-3.5, 1.7, 0.2)]).ToList();
        var r = CameraSolver.Solve(Observe(truth, pts, 0.2, 5), 3840, 2160, solveK1: true);
        Report("8 pts, k1", r, truth);
        Assert.InRange(r.Pose.K1, -0.09, -0.07);
        Assert.True(Math.Sqrt(Math.Pow(r.Pose.X - truth.X, 2) + Math.Pow(r.Pose.Y - truth.Y, 2) + Math.Pow(r.Pose.Z - truth.Z, 2)) < 0.02);
    }

    [Fact]
    public void Refine_from_slider_start_converges()
    {
        var start = Truth with { X = Truth.X + 0.4, Yaw = Truth.Yaw + 5, VFovDeg = 70 };
        var r = CameraSolver.Solve(Observe(Truth, Points6, 0, 1), 3840, 2160, initial: start);
        Assert.True(r.RmsErrorPx < 1e-3);
    }
}
