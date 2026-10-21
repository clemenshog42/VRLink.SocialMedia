using System.IO.Compression;
using System.Numerics;

namespace VRLink.Shared.Demo;

/// <summary>Reads .vrdemo files (port of Tactical.Highlights.DemoReader; v1, v2 and the provisional v3 header).</summary>
public static class DemoReader
{
    public static DemoData ReadFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        ms.Position = 0;
        return Read(ms);
    }

    /// <summary>Reads uncompressed demo bytes.</summary>
    public static DemoData Read(Stream s)
    {
        var r = new BinaryReader(s);
        var magic = r.ReadBytes(4);
        if (magic.Length < 4 || !magic.AsSpan().SequenceEqual(DemoFormat.Magic)) throw new InvalidDataException("not a .vrdemo file");
        var d = new DemoData { Version = r.ReadUInt16() };
        if (d.Version < DemoFormat.MinReadVersion || d.Version > DemoFormat.MaxReadVersion)
            throw new InvalidDataException("unsupported .vrdemo version " + d.Version);
        d.MatchId = r.ReadString();
        d.Round = r.ReadInt32();
        if (d.Version >= 2) d.MapScene = r.ReadString();
        if (d.Version >= 3) d.V3 = ReadV3Header(r);
        d.StartTime = r.ReadDouble();
        d.EndTime = r.ReadDouble();
        d.SampleRate = r.ReadSingle();
        d.CreatedUtc = new DateTime(r.ReadInt64(), DateTimeKind.Utc);

        int np = r.ReadUInt16();
        for (int i = 0; i < np; i++)
            d.Players.Add(new DemoPlayerInfo(r.ReadUInt64(), (Team)r.ReadByte(), r.ReadBoolean(), r.ReadBoolean(), r.ReadSingle(), r.ReadString()));
        int ns = r.ReadUInt16();
        for (int i = 0; i < ns; i++) d.Strings.Add(r.ReadString());
        d.HeaderFrames = r.ReadInt32();
        d.HeaderEvents = r.ReadInt32();
        d.HeaderBodyBytes = r.ReadInt32();
        d.Frames.Capacity = Math.Max(0, d.HeaderFrames);

        while (true)
        {
            var tag = (DemoRecord)r.ReadByte();
            if (tag == DemoRecord.End) break;
            double t = r.ReadDouble();
            switch (tag)
            {
                case DemoRecord.Frame:
                {
                    int n = r.ReadByte();
                    var f = new DemoFrame { Time = t, Players = new DemoPlayerSample[n] };
                    for (int i = 0; i < n; i++)
                    {
                        var p = new DemoPlayerSample { Slot = r.ReadByte() };
                        byte flags = r.ReadByte();
                        p.Team = (Team)r.ReadByte();
                        p.Alive = (flags & DemoFormat.FlagAlive) != 0;
                        p.HasWeapon = (flags & DemoFormat.FlagWeapon) != 0;
                        p.WeaponInLeftHand = (flags & DemoFormat.FlagWeaponLeft) != 0;
                        p.HeadPos = DemoFormat.ReadVec(r); p.HeadRot = DemoFormat.ReadQuat(r);
                        p.LeftPos = DemoFormat.ReadVec(r); p.LeftRot = DemoFormat.ReadQuat(r);
                        p.RightPos = DemoFormat.ReadVec(r); p.RightRot = DemoFormat.ReadQuat(r);
                        p.WeaponPos = DemoFormat.ReadVec(r); p.WeaponRot = DemoFormat.ReadQuat(r);
                        p.WeaponId = Str(d, r.ReadUInt16());
                        f.Players[i] = p;
                    }
                    d.Frames.Add(f);
                    break;
                }
                case DemoRecord.Shot:
                    d.Events.Add(new DemoEvent { Kind = tag, Time = t, A = r.ReadUInt64(), P1 = DemoFormat.ReadVec(r), P2 = DemoFormat.ReadVec(r) });
                    break;
                case DemoRecord.Impact:
                    d.Events.Add(new DemoEvent { Kind = tag, Time = t, P1 = DemoFormat.ReadVec(r), P2 = DemoFormat.ReadVec(r), Code = r.ReadByte() });
                    break;
                case DemoRecord.Kill:
                    d.Events.Add(new DemoEvent
                    {
                        Kind = tag, Time = t, A = r.ReadUInt64(), B = r.ReadUInt64(), Text = Str(d, r.ReadUInt16()), Flag = r.ReadBoolean(),
                        IntA = r.ReadInt16(), IntB = r.ReadInt16(), P1 = DemoFormat.ReadVec(r), P2 = DemoFormat.ReadVec(r),
                    });
                    break;
                case DemoRecord.Grenade:
                    d.Events.Add(new DemoEvent { Kind = tag, Time = t, P1 = DemoFormat.ReadVec(r), Value = r.ReadSingle(), Text = Str(d, r.ReadUInt16()), A = r.ReadUInt64() });
                    break;
                case DemoRecord.BombPlanted:
                {
                    var e = new DemoEvent { Kind = tag, Time = t, A = r.ReadUInt64() };
                    if (d.Version >= 2) { e.P1 = DemoFormat.ReadVec(r); e.Flag = true; }
                    d.Events.Add(e);
                    break;
                }
                case DemoRecord.FreeObjects:
                {
                    int n = r.ReadByte();
                    var ff = new DemoFreeFrame { Time = t, Objects = new DemoFreeObject[n] };
                    for (int i = 0; i < n; i++)
                        ff.Objects[i] = new DemoFreeObject
                        {
                            ObjectId = r.ReadUInt32(), Kind = r.ReadByte(), WeaponId = Str(d, r.ReadUInt16()),
                            Pos = DemoFormat.ReadVec(r), Rot = DemoFormat.ReadQuat(r),
                        };
                    d.FreeFrames.Add(ff);
                    break;
                }
                case DemoRecord.Doors:
                {
                    int n = r.ReadByte();
                    var df = new DemoDoorFrame { Time = t, Doors = new DemoDoorState[n] };
                    for (int i = 0; i < n; i++)
                    {
                        var ds = new DemoDoorState { Pos = DemoFormat.ReadVec(r) };
                        int leaves = r.ReadByte();
                        ds.Angles = new float[leaves];
                        for (int l = 0; l < leaves; l++) ds.Angles[l] = r.ReadSingle();
                        df.Doors[i] = ds;
                    }
                    d.DoorFrames.Add(df);
                    break;
                }
                case DemoRecord.BombDefused:
                    d.Events.Add(new DemoEvent { Kind = tag, Time = t, A = r.ReadUInt64(), Value = r.ReadSingle() });
                    break;
                case DemoRecord.BombExploded:
                    d.Events.Add(new DemoEvent { Kind = tag, Time = t });
                    break;
                case DemoRecord.Phase:
                    d.Events.Add(new DemoEvent { Kind = tag, Time = t, Code = r.ReadByte() });
                    break;
                case DemoRecord.UtcSync:
                    d.Events.Add(new DemoEvent { Kind = tag, Time = t, A = (ulong)r.ReadInt64() });
                    break;
                case DemoRecord.RoundEnd:
                    d.Events.Add(new DemoEvent { Kind = tag, Time = t, Code = r.ReadByte(), Code2 = r.ReadByte() });
                    break;
                default:
                    throw new InvalidDataException("unknown record " + (byte)tag);
            }
        }
        return d;
    }

    /// <summary>v3 header block (final layout, Highlights/README.md ".vrdemo byte layout").</summary>
    public static DemoV3Header ReadV3Header(BinaryReader r)
    {
        var h = new DemoV3Header
        {
            Kind = r.ReadByte(),
            VenueId = r.ReadString(),
            UtcAnchorTicks = r.ReadInt64(),
            UtcAnchorServerTime = r.ReadDouble(),
            BoundaryBuffer = r.ReadSingle(),
        };
        int n = r.ReadUInt16();
        h.Outline = new Vector2[n];
        for (int i = 0; i < n; i++) h.Outline[i] = new Vector2(r.ReadSingle(), r.ReadSingle());
        return h;
    }

    private static string? Str(DemoData d, ushort i) => i == DemoFormat.NoString || i >= d.Strings.Count ? null : d.Strings[i];
}
