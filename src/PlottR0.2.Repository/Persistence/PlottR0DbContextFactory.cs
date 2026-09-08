using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace PlottR0._2.Repository.Persistence;

/// <summary>
/// Design-time factory so <c>dotnet ef migrations add</c> / <c>dotnet ef database update</c>
/// can construct the context without running the web application.
/// </summary>
public class PlottR0DbContextFactory : IDesignTimeDbContextFactory<PlottR0DbContext>
{
    public PlottR0DbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<PlottR0DbContext>();
        const string connectionString =
            "Host=localhost;Port=5432;Database=plottr02;Username=plottr02;Password=plottr02";
        optionsBuilder.UseNpgsql(connectionString);
        return new PlottR0DbContext(optionsBuilder.Options);
    }
}
