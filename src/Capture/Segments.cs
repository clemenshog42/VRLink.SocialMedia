using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VRLink.Capture;

/// <summary>One finished segment (line of &lt;cam&gt;/index.jsonl).</summary>
public sealed record SegmentRecord
{
    [JsonPropertyName("cam")] public string Camera { get; init; } = "";
    /// <summary>File name inside &lt;cam&gt;/segments.</summary>
    [JsonPropertyName("file")] public string File { get; init; } = "";
    /// <summary>UTC of the first frame, measured: the wall-clock timestamp ffmpeg stamped on it (segment list), or end − duration for the first segment of a run.</summary>
    [JsonPropertyName("startUtc")] public DateTime StartUtc { get; init; }
    [JsonPropertyName("endUtc")] public DateTime EndUtc { get; init; }
    [JsonPropertyName("bytes")] public long Bytes { get; init; }
    /// <summary>"list" (segment list wall-clock pts) or "probe" (end − probed duration).</summary>
    [JsonPropertyName("startSource")] public string StartSource { get; init; } = "list";
    /// <summary>ffmpeg run counter: segments of different runs are not contiguous.</summary>
    [JsonPropertyName("run")] public int Run { get; init; }

    [JsonIgnore] public double DurationSec => (EndUtc - StartUtc).TotalSeconds;

    /// <summary>cam1_20261020T070202.433Z.mp4 (UTC start, millisecond precision).</summary>
    public static string FileNameFor(string cam, DateTime startUtc) =>
        $"{cam}_{startUtc.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss.fff'Z'", CultureInfo.InvariantCulture)}.mp4";

    public static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
}

/// <summary>A line of the ffmpeg segment list (csv: file,start,end in stream seconds = Unix seconds with -copyts + wall-clock timestamps).</summary>
public readonly record struct SegmentListEntry(string File, double Start, double End)
{
    public static bool TryParse(string line, out SegmentListEntry e)
    {
        e = default;
        var parts = line.Trim().Split(',');
        if (parts.Length < 3) return false;
        // file names may contain commas in theory; start/end are the last two fields
        if (!double.TryParse(parts[^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ||
            !double.TryParse(parts[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var en)) return false;
        e = new SegmentListEntry(string.Join(',', parts[..^2]).Trim('"'), s, en);
        return true;
    }

    public static DateTime UnixToUtc(double seconds) =>
        DateTime.UnixEpoch.AddTicks((long)Math.Round(seconds * TimeSpan.TicksPerSecond));

    /// <summary>The first segment of a run has start 0 in the list (the muxer does not know the first pts).</summary>
    public bool HasStart => Start > 1e8;
}

/// <summary>Segment index of one camera: in memory + append-only jsonl (rewritten on prune).</summary>
public sealed class SegmentIndex
{
    private readonly object _lock = new();
    private readonly List<SegmentRecord> _items = new();
    public string Camera { get; }
    public string Dir { get; }
    public string SegmentsDir => Path.Combine(Dir, "segments");
    public string IndexFile => Path.Combine(Dir, "index.jsonl");

    public SegmentIndex(string camera, string dir)
    {
        Camera = camera; Dir = dir;
        Directory.CreateDirectory(SegmentsDir);
        if (File.Exists(IndexFile))
            foreach (var line in File.ReadAllLines(IndexFile))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var r = JsonSerializer.Deserialize<SegmentRecord>(line, SegmentRecord.Json);
                    if (r != null && File.Exists(PathOf(r))) _items.Add(r);
                }
                catch (JsonException) { }
            }
        _items.Sort((a, b) => a.StartUtc.CompareTo(b.StartUtc));
    }

    public string PathOf(SegmentRecord r) => Path.Combine(SegmentsDir, r.File);

    public int LastRun { get { lock (_lock) return _items.Count == 0 ? 0 : _items.Max(i => i.Run); } }

    public void Add(SegmentRecord r)
    {
        lock (_lock)
        {
            _items.Add(r);
            _items.Sort((a, b) => a.StartUtc.CompareTo(b.StartUtc));
            File.AppendAllText(IndexFile, JsonSerializer.Serialize(r, SegmentRecord.Json) + "\n");
        }
    }

    public List<SegmentRecord> All() { lock (_lock) return _items.ToList(); }

    /// <summary>Segments overlapping [from, to).</summary>
    public List<SegmentRecord> Overlapping(DateTime fromUtc, DateTime toUtc)
    {
        lock (_lock) return _items.Where(s => s.EndUtc > fromUtc && s.StartUtc < toUtc).ToList();
    }

    /// <summary>Deletes the files and rewrites the index without them.</summary>
    public int Remove(IEnumerable<SegmentRecord> doomed)
    {
        int n = 0;
        lock (_lock)
        {
            foreach (var r in doomed.ToList())
            {
                try { File.Delete(PathOf(r)); } catch (IOException) { continue; } catch (UnauthorizedAccessException) { continue; }
                _items.Remove(r);
                n++;
            }
            if (n > 0)
            {
                var tmp = IndexFile + ".tmp";
                File.WriteAllLines(tmp, _items.Select(i => JsonSerializer.Serialize(i, SegmentRecord.Json)));
                File.Move(tmp, IndexFile, overwrite: true);
            }
        }
        return n;
    }
}

public sealed record Pin
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..12];
    /// <summary>null = all cameras.</summary>
    public string? Camera { get; init; }
    public DateTime FromUtc { get; init; }
    public DateTime ToUtc { get; init; }
    public string Reason { get; init; } = "";
    public DateTime CreatedUtc { get; init; }

    public bool Covers(SegmentRecord s) =>
        (Camera == null || string.Equals(Camera, s.Camera, StringComparison.OrdinalIgnoreCase)) && s.EndUtc > FromUtc && s.StartUtc < ToUtc;
}

/// <summary>Pinned windows are kept until released (pins.json in the data dir).</summary>
public sealed class PinStore
{
    private readonly object _lock = new();
    private readonly string _file;
    private List<Pin> _pins;

    public PinStore(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        _file = Path.Combine(dataDir, "pins.json");
        _pins = File.Exists(_file) ? JsonSerializer.Deserialize<List<Pin>>(File.ReadAllText(_file)) ?? new() : new();
    }

    public List<Pin> All() { lock (_lock) return _pins.ToList(); }

    public Pin Add(string? camera, DateTime fromUtc, DateTime toUtc, string reason, DateTime nowUtc)
    {
        if (toUtc <= fromUtc) throw new ArgumentException("toUtc must be after fromUtc");
        var p = new Pin { Camera = string.IsNullOrWhiteSpace(camera) ? null : camera, FromUtc = fromUtc, ToUtc = toUtc, Reason = reason, CreatedUtc = nowUtc };
        lock (_lock) { _pins.Add(p); Save(); }
        return p;
    }

    public bool Release(string id)
    {
        lock (_lock)
        {
            int n = _pins.RemoveAll(p => p.Id == id);
            if (n > 0) Save();
            return n > 0;
        }
    }

    private void Save()
    {
        var tmp = _file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_pins, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, _file, overwrite: true);
    }
}

public sealed record RetentionPlan(List<SegmentRecord> Delete, long BytesAfter, bool PinnedDeletedForCap);

/// <summary>Which segments to delete (pure, tested).</summary>
public static class RetentionPlanner
{
    /// <param name="keepNewest">Never delete the newest segment of a camera (it may still be referenced by a running cut).</param>
    public static RetentionPlan Plan(IReadOnlyList<SegmentRecord> segments, IReadOnlyList<Pin> pins, DateTime nowUtc,
                                     TimeSpan retention, long capBytes, bool keepNewest = true)
    {
        var newest = keepNewest
            ? segments.GroupBy(s => s.Camera).Select(g => g.MaxBy(s => s.EndUtc)!).ToHashSet()
            : new HashSet<SegmentRecord>();
        bool Pinned(SegmentRecord s) => pins.Any(p => p.Covers(s));

        var delete = segments.Where(s => s.EndUtc < nowUtc - retention && !Pinned(s) && !newest.Contains(s)).ToList();
        var remaining = segments.Except(delete).OrderBy(s => s.StartUtc).ToList();
        long bytes = remaining.Sum(s => s.Bytes);
        bool pinnedHit = false;

        // disk cap: oldest unpinned first, then (last resort) oldest pinned
        foreach (var pass in new[] { false, true })
        {
            foreach (var s in remaining.ToList())
            {
                if (bytes <= capBytes) break;
                if (newest.Contains(s) || Pinned(s) != pass) continue;
                delete.Add(s); remaining.Remove(s); bytes -= s.Bytes;
                if (pass) pinnedHit = true;
            }
        }
        return new RetentionPlan(delete, bytes, pinnedHit);
    }
}

/// <summary>Per-camera clock offsets from the clap test (offsets.json): footage time = demo (true) time + offset.</summary>
public sealed class OffsetStore
{
    private readonly object _lock = new();
    private readonly string _file;
    private Dictionary<string, double> _ms;

    public OffsetStore(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        _file = Path.Combine(dataDir, "offsets.json");
        _ms = File.Exists(_file) ? JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(_file)) ?? new() : new();
    }

    public double OffsetMs(string cam) { lock (_lock) return _ms.TryGetValue(cam, out var v) ? v : 0; }
    public Dictionary<string, double> All() { lock (_lock) return new(_ms); }

    public void Set(string cam, double ms)
    {
        lock (_lock)
        {
            _ms[cam] = ms;
            File.WriteAllText(_file, JsonSerializer.Serialize(_ms, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
