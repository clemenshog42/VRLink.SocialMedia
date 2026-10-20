namespace VRLinkPublisher.Domain;

/// <summary>Static host options from appsettings.json section "Publisher" (restart required).</summary>
public class PublisherOptions
{
    public string Urls { get; set; } = "http://localhost:5080";
    public string ClipsRoot { get; set; } = @"C:\vr\VRCsGo\Builds\Highlights\clips";
    /// <summary>Folder for the SQLite DB. Empty = %ProgramData%\VRLinkPublisher.</summary>
    public string DataDir { get; set; } = "";
    /// <summary>Initial value of the runtime DryRun setting when the DB is created.</summary>
    public bool DryRunOnFirstStart { get; set; } = true;
    /// <summary>Path to ffprobe.exe. Empty = look on PATH; not found = trust clip.json.</summary>
    public string FfprobePath { get; set; } = "";
    public int RescanSeconds { get; set; } = 60;
    /// <summary>"LocalMachine" (console + service can both read tokens) or "CurrentUser".</summary>
    public string DpapiScope { get; set; } = "LocalMachine";
    /// <summary>Optional public https base URL that serves the clips folder (Instagram video_url fallback).</summary>
    public string PublicBaseUrl { get; set; } = "";
    public string GraphApiVersion { get; set; } = "v26.0";
    /// <summary>Local Capture service (arena camera footage, src/Capture).</summary>
    public string CaptureUrl { get; set; } = "http://localhost:5090";
    /// <summary>Folder with .vrdemo files (round demos + calib/ recordings) for the Arena camera page.</summary>
    public string DemosRoot { get; set; } = @"C:\vr\VRCsGo\Builds\Highlights\demos";
    /// <summary>Worker loop intervals (seconds). Tests set them small.</summary>
    public int WorkerTickSeconds { get; set; } = 15;

    public string ResolveDataDir() =>
        string.IsNullOrWhiteSpace(DataDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VRLinkPublisher")
            : Environment.ExpandEnvironmentVariables(DataDir);
}

public enum TikTokPostMode { Inbox, Direct }

/// <summary>Runtime settings edited in the web UI; stored as JSON in the DB.</summary>
public class RuntimeSettings
{
    /// <summary>Settings schema/defaults version (see SettingsUpgrade). New installs start at the current version.</summary>
    public int SettingsVersion { get; set; } = 2;
    public bool DryRun { get; set; } = true;
    public bool PauseAll { get; set; }

    // Approver
    public double ScoreThreshold { get; set; } = 60;
    public double MinDurationSec { get; set; } = 5;
    public double MaxDurationSec { get; set; } = 60;
    public int RequiredWidth { get; set; } = 1080;
    public int RequiredHeight { get; set; } = 1920;
    public double MaxFileSizeMb { get; set; } = 250;
    public double MinFileSizeKb { get; set; } = 0;
    /// <summary>Clips whose json says "heroIsBot": true are rejected unless this is on.</summary>
    public bool AllowBotHighlights { get; set; }

    // Scheduler
    public bool TikTokEnabled { get; set; } = true;
    public bool InstagramEnabled { get; set; } = true;
    /// <summary>Local times "HH:mm".</summary>
    public List<string> TikTokSlots { get; set; } = new() { "12:00", "19:00" };
    public List<string> InstagramSlots { get; set; } = new() { "11:30", "20:00" };
    /// <summary>V5: use the hours learned from own data. null = automatic (on once ≥ 10 measured posts per platform), false = never.</summary>
    public bool? UseLearnedSlots { get; set; }
    /// <summary>V5: share of caption-variant picks that explore uniformly (rest = Thompson sampling).</summary>
    public double ExplorationShare { get; set; } = 0.2;
    /// <summary>Empty = Windows local time zone.</summary>
    public string TimeZoneId { get; set; } = "";
    /// <summary>A slot is still filled if the worker sees it at most this many minutes late.</summary>
    public int SlotGraceMinutes { get; set; } = 60;
    /// <summary>True: never post the same highlight type twice in a row (slot is skipped if only that type is waiting).</summary>
    public bool StrictTypeAlternation { get; set; } = true;

    // Captions (A/B variants). Key lookup: "format.type", "format.*", "type", "*" (lower case; format irl|split|reaction|game).
    // Each list entry is one variant (id = "key#index", stored per post and learned in the V5 model). Hook first.
    // Placeholders: {caption} {type} {venue} {round} {score}
    public Dictionary<string, List<string>> CaptionTemplates { get; set; } = new()
    {
        ["irl.ace"] = new() { "5 kills. Real legs. {caption}", "This ace happened in a real room. {caption}", "No controller on a couch. Real ace. {caption}" },
        ["irl.clutch"] = new() { "1 vs all. In real life. {caption}", "Heart rate: yes. Real clutch. {caption}" },
        ["irl.*"] = new() { "Real room. Real legs. Real game. {caption}", "This is what VR looks like from outside. {caption}" },
        ["split.*"] = new() { "Top: real. Bottom: in game. {caption}", "Same moment, two worlds. {caption}" },
        ["reaction.*"] = new() { "When it gets real. {caption}", "Nobody was ready for this. {caption}", "Real reactions only. {caption}" },
        ["game.*"] = new() { "{caption}", "Highlight from VRLink {venue}: {caption}" },
        ["*"] = new() { "{caption}", "{caption} | VRLink {venue}", "Highlight from VRLink {venue}: {caption}" },
        ["ace"] = new() { "ACE! {caption}", "One player, five eliminations. {caption}", "Ace in round {round}. {caption}" },
        ["clutch"] = new() { "Clutch! {caption}", "1vX in round {round}. {caption}", "Nerves of steel. {caption}" },
        ["multikill"] = new() { "{caption}", "Multi kill at VRLink {venue}. {caption}" },
        ["defuse"] = new() { "Last-second defuse. {caption}", "Defused! {caption}" },
    };
    public string DefaultHashtags { get; set; } = "#VRLink #VR #freeroamvr #vrgaming";

    // V4 customer share pack (VRLinkWeb). Empty base URL or no API key → local share folder/page (testing).
    public string ShareWebBaseUrl { get; set; } = "";
    public string ShareExpiryNote { get; set; } = "Your clips are online for 30 days.";

    // TikTok
    /// <summary>Privacy chosen manually by the owner (TikTok UX: no default). Until the app is audited the publisher always forces SELF_ONLY.</summary>
    public string TikTokPrivacyLevel { get; set; } = "";
    public bool TikTokAppAudited { get; set; }
    public bool TikTokConsentGiven { get; set; }
    /// <summary>Inbox (default until the audit decision) = upload to the creator's TikTok inbox, finished in the app. Direct = Direct Post API.</summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
    public TikTokPostMode TikTokMode { get; set; } = TikTokPostMode.Inbox;
    public bool TikTokAllowComments { get; set; } // TikTok UX: interactions off by default
    public bool TikTokAllowDuet { get; set; }
    public bool TikTokAllowStitch { get; set; }

    // Instagram
    public bool InstagramShareToFeed { get; set; } = true;
    public bool InstagramUseVideoUrl { get; set; }

    // Publish / stats
    public int MaxAttempts { get; set; } = 5;
    public int StatsIntensiveDays { get; set; } = 14;
    public int StatsIntensiveHours { get; set; } = 3;
    public int StatsStopAfterDays { get; set; } = 90;

    public RuntimeSettings Clone() =>
        System.Text.Json.JsonSerializer.Deserialize<RuntimeSettings>(System.Text.Json.JsonSerializer.Serialize(this))!;
}
