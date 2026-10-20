using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace VRLinkPublisher.Data;

/// <summary>
/// Used only by the local dotnet-ef tool (migrations add/script) so it does not start the web host or workers.
/// The connection string is a throw-away design-time file; runtime uses ServiceSetup.
/// </summary>
public class DesignTimeDbFactory : IDesignTimeDbContextFactory<PublisherDb>
{
    public PublisherDb CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<PublisherDb>()
            .UseSqlite("Data Source=" + Path.Combine(Path.GetTempPath(), "vrlinkpublisher-design.db"))
            .Options);
}
