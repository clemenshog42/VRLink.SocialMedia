using System.IO.Compression;
using System.Numerics;
using System.Text;
using VRLink.Shared.Demo;

namespace VRLink.Shared.Tests;

/// <summary>Minimal writer mirroring the Unity DemoWriter layout (frames + a few events) for synthetic demos in tests.</summary>
public sealed class DemoTestWriter
{
    public ushort Version = 2;
    public string MatchId = "test", MapScene = "Arena";
    public int Round = 1;
    public double StartTime, EndTime;
    public float SampleRate = 30;
    public DateTime CreatedUtc = new(2026, 10, 20, 10, 0, 0, DateTimeKind.Utc);
    public DemoV3Header? V3;
    public List<DemoPlayerInfo> Players = new();
    public List<(double t, (Vector3 head, Vector3 left, Vector3 right)[] p)> Frames = new();
    public List<double> PhaseEvents = new();

    public byte[] Build()
    {
        var body = new MemoryStream(); var w = new BinaryWriter(body, Encoding.UTF8);
        foreach (var (t, ps) in Frames)
        {
            w.Write((byte)DemoRecord.Frame); w.Write(t); w.Write((byte)ps.Length);
            for (int i = 0; i < ps.Length; i++)
            {
                w.Write((byte)i); w.Write(DemoFormat.FlagAlive); w.Write((byte)Team.TacticalUnit);
                Vec(w, ps[i].head); Quat(w); Vec(w, ps[i].left); Quat(w); Vec(w, ps[i].right); Quat(w); Vec(w, ps[i].right); Quat(w);
                w.Write(DemoFormat.NoString);
            }
        }
        foreach (var t in PhaseEvents) { w.Write((byte)DemoRecord.Phase); w.Write(t); w.Write((byte)RoundPhase.Action); }
        w.Flush();

        var file = new MemoryStream(); var f = new BinaryWriter(file, Encoding.UTF8);
        f.Write("VRDM"u8.ToArray()); f.Write(Version);
        f.Write(MatchId); f.Write(Round);
        if (Version >= 2) f.Write(MapScene);
        f.Write(StartTime); f.Write(EndTime); f.Write(SampleRate); f.Write(CreatedUtc.Ticks);
        if (Version >= 3)
        {
            var h = V3 ?? new DemoV3Header();
            f.Write(h.UtcAnchorTicks); f.Write(h.UtcAnchorServerTime); f.Write(h.VenueId);
            f.Write((ushort)h.Boundary.Length);
            foreach (var b in h.Boundary) { f.Write(b.X); f.Write(b.Y); }
        }
        f.Write((ushort)Players.Count);
        foreach (var p in Players) { f.Write(p.ClientId); f.Write((byte)p.Team); f.Write(p.IsBot); f.Write(p.Female); f.Write(p.EyeHeight); f.Write(p.Label); }
        f.Write((ushort)0);
        f.Write(Frames.Count); f.Write(PhaseEvents.Count); f.Write((int)body.Length);
        f.Write(body.ToArray());
        f.Write((byte)DemoRecord.End);
        f.Flush();
        return file.ToArray();
    }

    public string WriteGz(string path)
    {
        var bytes = Build();
        using var fs = File.Create(path);
        using var gz = new GZipStream(fs, CompressionLevel.Fastest);
        gz.Write(bytes);
        return path;
    }

    private static void Vec(BinaryWriter w, Vector3 v) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
    private static void Quat(BinaryWriter w) { w.Write((short)0); w.Write((short)0); w.Write((short)0); w.Write((short)32767); }
}
