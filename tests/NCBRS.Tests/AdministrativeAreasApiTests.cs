using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NCBRS.Controllers;
using NCBRS.Data;
using NCBRS.Models;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// The read endpoint that feeds a dependent State → County → … picker: one
/// level at a time, by parent or by level.
/// </summary>
public class AdministrativeAreasApiTests : IDisposable
{
    private readonly TestDatabase _database = TestDatabase.Create();

    public void Dispose()
    {
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    private NcbrsDbContext NewDb() => new(_database.Options);

    private static AdministrativeAreasController Controller(NcbrsDbContext db) => new(db)
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
    };

    private async Task<NcbrsDbContext> SeededAsync()
    {
        var db = NewDb();
        db.Database.EnsureCreated();
        await AdministrativeAreaSeeder.SeedAsync(db);
        return db;
    }

    [Fact]
    public async Task WithNoParameters_ReturnsTheCountryRoot()
    {
        await using var db = await SeededAsync();

        var areas = (await Controller(db).List()).Value!;

        Assert.Single(areas);
        Assert.Equal(AdministrativeLevel.Country, areas[0].Level);
        Assert.Equal("SS", areas[0].Code);
    }

    [Fact]
    public async Task ByLevel_ReturnsEveryAreaAtThatLevel()
    {
        await using var db = await SeededAsync();

        var states = (await Controller(db).List(parentId: null, level: AdministrativeLevel.State)).Value!;

        // 10 states + Abyei, Greater Pibor, Ruweng.
        Assert.Equal(13, states.Count);
        Assert.All(states, state => Assert.Equal(AdministrativeLevel.State, state.Level));
    }

    [Fact]
    public async Task ByParent_ReturnsThatAreasChildren()
    {
        await using var db = await SeededAsync();
        var centralEquatoria = await db.AdministrativeAreas.FirstAsync(a => a.Code == "SS-CE");

        var counties = (await Controller(db).List(parentId: centralEquatoria.AdministrativeAreaId)).Value!;

        Assert.NotEmpty(counties);
        Assert.All(counties, county =>
        {
            Assert.Equal(AdministrativeLevel.County, county.Level);
            Assert.Equal(centralEquatoria.AdministrativeAreaId, county.ParentId);
        });
        Assert.Contains(counties, county => county.Code == "SS-CE-JUB");
    }
}
