using Microsoft.EntityFrameworkCore;
using VRLinkPublisher.Domain;
using VRLinkPublisher.Services;

namespace VRLinkPublisher.Tests;

public class V2ContractTests
{
    [Fact]
    public async Task Composer_fields_are_imported_and_approval_rule_is_unchanged()
    {
        await using var h = new TestHost(new DateTime(2026, 10, 20, 10, 0, 0, DateTimeKind.Utc));
        var d = Path.Combine(h.ClipsRoot, "2026-10-20");
        Directory.CreateDirectory(d);
        await File.WriteAllBytesAsync(Path.Combine(d, "m1-r02-3-split.mp4"), [1, 2, 3]);
        await File.WriteAllTextAsync(Path.Combine(d, "m1-r02-3-split.json"), """
            {"highlightId":"m1-r02-3-split","type":"Kills3","score":80,"durationSec":14.2,"width":1080,"height":1920,
             "format":"split","sources":["arenaCam:cam1","overlay","pov","gameAudio","roomAudio"],"musicFree":true,
             "players":[{"label":"SYN 1","team":"SYN"},{"label":"TU 2","team":"TU","hero":true}]}
            """);
        await h.Get<ClipImporter>().ScanAsync(h.ClipsRoot);
        await h.Get<Pipeline>().CheckNewAsync();
        await using var db = h.Db();
        var c = await db.Clips.SingleAsync();
        Assert.Equal("split", c.Format);
        Assert.Equal("arenaCam:cam1,overlay,pov,gameAudio,roomAudio", c.Sources);
        Assert.True(c.MusicFree);
        Assert.Equal("TU 2,SYN 1", c.Players); // hero first
        Assert.Equal(ClipStatus.AwaitingApproval, c.Status); // still needs the owner's click
        Assert.Null(c.ApprovedAtUtc);
        Assert.Empty(await db.Posts.ToListAsync());
    }
}
