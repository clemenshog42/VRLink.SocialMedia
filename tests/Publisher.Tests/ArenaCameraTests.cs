using Microsoft.Extensions.Options;
using VRLink.Shared.Calibration;
using VRLinkPublisher.Domain;
using VRLinkPublisher.Services;

namespace VRLinkPublisher.Tests;

public class ArenaCameraTests
{
    [Fact]
    public void Calibration_is_saved_per_venue_and_camera_and_reloaded()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vrlinkpub-tests", "arena-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CalibrationStore(Options.Create(new PublisherOptions { DataDir = dir }));
            var cal = new CameraCalibration
            {
                VenueId = "homelane_6c21", CameraId = "cam1", TimeOffsetMs = 212.5, RmsErrorPx = 0.4, SavedUtc = DateTime.UtcNow,
                Pose = new CameraPose { X = 4.2, Y = 2.6, Z = -3.9, Yaw = -42, Pitch = 24, Roll = 1.5, VFovDeg = 78, Width = 3840, Height = 2160 },
                Points = [new Correspondence(1, 1.9, 2, 1000, 500, "TU 1 right")],
            };
            var path = store.Save(cal);
            Assert.EndsWith(Path.Combine("arena-camera", "homelane_6c21", "cam1.json"), path);
            var back = store.Load("homelane_6c21", "cam1")!;
            Assert.Equal(cal.Pose, back.Pose);
            Assert.Equal(212.5, back.TimeOffsetMs);
            Assert.Single(back.Points);
            Assert.Contains("left-handed", back.CoordinateSystem);
            Assert.Null(store.Load("other", "cam1"));
            Assert.DoesNotContain("..", store.PathFor("../../evil", "cam1"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Demo_catalog_only_opens_files_below_demos_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "vrlinkpub-tests", "demos-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "calib"));
        try
        {
            File.WriteAllBytes(Path.Combine(root, "calib", "x.vrdemo"), [1]);
            File.WriteAllBytes(Path.Combine(root, "r.vrdemo"), [1]);
            var cat = new DemoCatalog(Options.Create(new PublisherOptions { DemosRoot = root }));
            var list = cat.List();
            Assert.Equal(2, list.Count);
            Assert.True(list[0].IsCalibFolder); // calibration recordings first
            Assert.Throws<UnauthorizedAccessException>(() => cat.Open(Path.Combine(root, "..", "elsewhere.vrdemo")));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
