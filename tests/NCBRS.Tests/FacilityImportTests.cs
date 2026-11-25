using Microsoft.EntityFrameworkCore;
using NCBRS.Data;
using NCBRS.Models;
using NCBRS.Services;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// Adding facilities from a CSV file (plan §17 item 38). What these pin: a
/// check adds nothing; a file is applied whole or not at all; every row is held
/// to the same checks as "Add a facility", plus the mistakes only a file can
/// make; and each facility added is exactly what adding it by hand would give,
/// its own range of numbers and its own audit row included.
/// </summary>
public class FacilityImportTests : IDisposable
{
    private readonly TestDatabase _database = TestDatabase.Create();

    private static readonly Registrar Ministry = new()
    {
        RegistrarId = Guid.CreateVersion7(),
        DisplayName = "Ministry Admin",
        Role = RegistrarRole.MinistryAdmin,
    };

    public void Dispose()
    {
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<NcbrsDbContext> SeededAsync()
    {
        var db = new NcbrsDbContext(_database.Options);
        await AdministrativeAreaSeeder.SeedAsync(db);
        return db;
    }

    private static Task<ImportFacilitiesResponse> ImportAsync(NcbrsDbContext db, string csv, bool apply)
    {
        var onboarding = new FacilityOnboardingService(db, new CountyLookup(db), new FacilityOnboardingOptions { BrnRangeSize = 100_000 });
        return new FacilityImportService(db, onboarding)
            .ImportAsync(new ImportFacilitiesRequest { Csv = csv, Apply = apply }, Ministry, null);
    }

    private static async Task<string> PayamCodeAsync(NcbrsDbContext db, string county, int skip = 0)
        => await db.AdministrativeAreas
            .Where(area => area.Level == AdministrativeLevel.Payam && area.Parent!.Code == county)
            .OrderBy(area => area.Code).Skip(skip).Select(area => area.Code).FirstAsync();

    private static async Task<string> ValidFileAsync(NcbrsDbContext db)
        => $"""
            name,tier,connectivity,area,office_code
            Rejaf Village Health Post,Village health post,OfflineFirst,{await PayamCodeAsync(db, "SS0101")},RVH
            Kator PHCC,Clinic,intermittent,{await PayamCodeAsync(db, "SS0101", 1)},
            Terekeka Hospital,Hospital,AlwaysOn,SS0104,TKH
            """;

    [Fact]
    public async Task ACheckAddsNothing()
    {
        await using var db = await SeededAsync();

        var result = await ImportAsync(db, await ValidFileAsync(db), apply: false);

        Assert.False(result.Applied);
        Assert.Equal((3, 0), (result.Valid, result.Invalid));
        Assert.Equal(["SS0101", "SS0101", "SS0104"], result.Rows.Select(row => row.CountyCode));
        Assert.Equal([2, 3, 4], result.Rows.Select(row => row.Line));
        Assert.Empty(await db.Facilities.ToListAsync());
    }

    [Fact]
    public async Task ApplyingAddsEachFacilityAsIfAddedByHand()
    {
        await using var db = await SeededAsync();

        var result = await ImportAsync(db, await ValidFileAsync(db), apply: true);

        Assert.True(result.Applied);
        var facilities = await db.Facilities.OrderBy(facility => facility.BrnBlockStart).ToListAsync();
        Assert.Equal(["Rejaf Village Health Post", "Kator PHCC", "Terekeka Hospital"], facilities.Select(facility => facility.Name));
        Assert.Equal(
            [(100_000L, 199_999L), (200_000L, 299_999L), (300_000L, 399_999L)],
            facilities.Select(facility => (facility.BrnBlockStart, facility.BrnBlockEnd)));
        Assert.Equal(
            [FacilityTier.VillageHealthPost, FacilityTier.Clinic, FacilityTier.Hospital],
            facilities.Select(facility => facility.Tier));
        Assert.Equal(["RVH", null, "TKH"], facilities.Select(facility => facility.OfficeCode));

        var audit = await db.AuditLogs.Select(row => row.Action).ToListAsync();
        Assert.Equal(3, audit.Count(action => action.StartsWith("FacilityCreated:", StringComparison.Ordinal)));
        Assert.Contains("FacilitiesImported:3", audit);
    }

    /// <summary>
    /// A half-applied file leaves the Ministry working out which rows exist, and
    /// re-running it would refuse those as duplicates. One bad row stops them all.
    /// </summary>
    [Fact]
    public async Task OneBadRowAndNothingIsAdded()
    {
        await using var db = await SeededAsync();
        var file = await ValidFileAsync(db) + "\nNowhere Post,Clinic,AlwaysOn,SS9999,";

        var result = await ImportAsync(db, file, apply: true);

        Assert.False(result.Applied);
        Assert.Equal((3, 1), (result.Valid, result.Invalid));
        Assert.Contains("No administrative area has the code 'SS9999'.", result.Rows.Single(row => row.Line == 5).Problems);
        Assert.Empty(await db.Facilities.ToListAsync());
    }

    [Fact]
    public async Task EveryRowIsHeldToTheChecksForAddingOne()
    {
        await using var db = await SeededAsync();
        db.Facilities.Add(new Facility
        {
            Name = "Juba Teaching Hospital", CountyCode = "SS0101", BrnBlockStart = 900_000, BrnBlockEnd = 999_999,
            BrnBlockNextAvailable = 900_000, OfficeCode = "JTH",
        });
        await db.SaveChangesAsync();
        var payam = await PayamCodeAsync(db, "SS0101");

        var result = await ImportAsync(db, $"""
            name,tier,connectivity,area,office_code
            juba teaching hospital,Hospital,AlwaysOn,SS0101,
            Central Store,Clinic,AlwaysOn,SS01,
            Bad Tier,Dispensary,AlwaysOn,{payam},
            Bad Code,Clinic,AlwaysOn,{payam},J
            Taken Code,Clinic,AlwaysOn,{payam},JTH
            ,Clinic,AlwaysOn,{payam},
            """, apply: false);

        Assert.Equal(0, result.Valid);
        var problems = result.Rows.ToDictionary(row => row.Line, row => string.Join(" ", row.Problems));
        Assert.Contains("already has a facility called", problems[2]);
        Assert.Contains("is a State", problems[3]);
        Assert.Contains("The tier must be one of", problems[4]);
        Assert.Contains("two to six letters or digits", problems[5]);
        Assert.Contains("JTH", problems[6]);
        Assert.Contains("needs a name", problems[7]);
    }

    [Fact]
    public async Task AFileCannotAddOneFacilityOrOneCodeTwice()
    {
        await using var db = await SeededAsync();
        var payam = await PayamCodeAsync(db, "SS0101");
        var other = await PayamCodeAsync(db, "SS0101", 1);

        var result = await ImportAsync(db, $"""
            name,tier,connectivity,area,office_code
            Rejaf PHCU,Clinic,AlwaysOn,{payam},RPH
            REJAF PHCU,Clinic,AlwaysOn,{other},
            Kator PHCU,Clinic,AlwaysOn,{other},rph
            """, apply: false);

        Assert.Empty(result.Rows[0].Problems);
        Assert.Contains("Line 2 already adds a facility called", result.Rows[1].Problems.Single());
        Assert.Contains("Line 2 already uses office code RPH", result.Rows[2].Problems.Single());
    }

    /// <summary>Files come from spreadsheets: quotes, a byte-order mark, semicolons, columns in any order.</summary>
    [Fact]
    public async Task FilesAreReadAsSpreadsheetsWriteThem()
    {
        await using var db = await SeededAsync();
        var payam = await PayamCodeAsync(db, "SS0101");

        var result = await ImportAsync(db,
            "﻿Area;Name;Connectivity;Tier\r\n" +
            $"{payam};\"Rejaf; St. Mary's \"\"Mission\"\" Clinic\";offline_first;Clinic\r\n",
            apply: false);

        var row = Assert.Single(result.Rows);
        Assert.Empty(row.Problems);
        Assert.Equal("Rejaf; St. Mary's \"Mission\" Clinic", row.Name);
    }

    [Theory]
    [InlineData("", "The file is empty.")]
    [InlineData("name,tier,area\nX,Clinic,SS0101", "Missing: connectivity")]
    [InlineData("name,tier,connectivity,area\n", "no facilities")]
    public async Task AFileThatIsNotAListOfFacilitiesIsSaidToBe(string csv, string problem)
    {
        await using var db = await SeededAsync();

        var result = await ImportAsync(db, csv, apply: true);

        Assert.False(result.Applied);
        Assert.Contains(problem, Assert.Single(result.Rows).Problems.Single());
    }

    [Fact]
    public async Task AFileIsAtMostFiveHundredFacilities()
    {
        await using var db = await SeededAsync();
        var rows = string.Join("\n", Enumerable.Range(1, 501).Select(i => $"Post {i},Clinic,AlwaysOn,SS0101"));

        var result = await ImportAsync(db, "name,tier,connectivity,area\n" + rows, apply: false);

        Assert.Contains("at most 500", Assert.Single(result.Rows).Problems.Single());
    }
}
