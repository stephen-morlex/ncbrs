using System.Net;
using System.Text.Json;
using NCBRS.Client.Network;
using NCBRS.Client.Storage;
using NCBRS.Client.Time;
using NCBRS.Devices;
using NCBRS.Models;
using Xunit;
using static NCBRS.Client.Tests.CentralClientTests;

namespace NCBRS.Client.Tests;

/// <summary>
/// "Report a problem" on the tablet (plan §17 item 37). What these pin: the
/// report is counts and versions, never a name or a number; it waits on the
/// tablet until there is signal and goes last in a window; a dropped link
/// keeps it, and a registry refusal drops it rather than resending it forever.
/// </summary>
public class ProblemReportTests
{
    private static readonly DateTime Now = new(2026, 11, 25, 9, 0, 0, DateTimeKind.Utc);
    private static readonly AppInfo App = new("1.0.4", "Android 14", "ar");

    private static DeviceState State()
    {
        var state = new DeviceState
        {
            Identity = new DeviceIdentity("TAB-0A1B2C3D4E5F", Guid.Parse("0199c000-0000-7000-8000-0000000f0003"), new Uri("https://registry.ncbrs.ss/")),
            Brn = new BrnState(400_000, 400_199, 400_150, 0, 400_200, 400_399),
            OfflineToken = "token",
            LastSyncedAtUtc = Now.AddDays(-4),
            Attempts = new PinAttempts(2, null, Lockouts: 1),
        };
        state.Outbox.Add(new SyncBirthRecord { Birth = Birth("Ayen Deng") with { Brn = "400100" } });
        state.Drafts.Add(new FormDraft(Guid.NewGuid(), 2, Now, Birth("Deng Majok"), true, false));
        return state;
    }

    private static readonly ClockReading Behind = new(Now, ClockVerdict.Behind, Now.AddDays(-1));

    [Fact]
    public void AReportCarriesCountsAndVersions()
    {
        var report = ProblemReports.Build(State(), App, Behind, "  Sync keeps failing.  ", ["The centre did not answer."]);

        Assert.Equal("Sync keeps failing.", report.Description);
        Assert.Equal(Now, report.WrittenAtUtc);
        var diagnostics = report.Diagnostics;
        Assert.Equal(("1.0.4", "Android 14", "ar"), (diagnostics.AppVersion, diagnostics.Platform, diagnostics.Language));
        Assert.Equal((1, 1, true), (diagnostics.WaitingToSync, diagnostics.Drafts, diagnostics.SignedIn));
        Assert.Equal(50 + 200, diagnostics.NumbersLeft);  // the rest of this block, and the one staged behind it
        Assert.Equal(("Behind", -86_400d), (diagnostics.ClockVerdict, diagnostics.ClockOffsetSeconds));
        Assert.Equal((2, 1), (diagnostics.FailedPinAttempts, diagnostics.PinLockouts));
        Assert.Equal(["The centre did not answer."], diagnostics.LastSyncProblems);
    }

    /// <summary>
    /// The report travels to a queue several people read. Whatever the tablet
    /// holds about births stays on the tablet.
    /// </summary>
    [Fact]
    public void NoNameOrNumberLeavesTheTablet()
    {
        var json = JsonSerializer.Serialize(ProblemReports.Build(State(), App, Behind, null, []));

        Assert.DoesNotContain("Ayen", json);
        Assert.DoesNotContain("Majok", json);
        Assert.DoesNotContain("400100", json);
    }

    [Fact]
    public void WhatIsWrittenIsCappedToWhatTheRegistryAccepts()
    {
        var report = ProblemReports.Build(
            State(), App, Behind, new string('x', 2000), [.. Enumerable.Repeat(new string('y', 500), 9)]);

        Assert.Equal(ProblemReports.MaxDescription, report.Description!.Length);
        Assert.Equal(ProblemReports.MaxProblems, report.Diagnostics.LastSyncProblems.Count);
        Assert.All(report.Diagnostics.LastSyncProblems, problem => Assert.Equal(ProblemReports.MaxProblemLength, problem.Length));
    }

    [Fact]
    public void ReportsSurviveTheStore()
    {
        var state = State();
        state.ProblemReports.Add(ProblemReports.Build(state, App, Behind, "Printer will not pair.", []));

        var restored = JsonSerializer.Deserialize<DeviceState>(JsonSerializer.Serialize(state))!;

        Assert.Equal("Printer will not pair.", Assert.Single(restored.ProblemReports).Description);
    }

    // --- in a connectivity window -----------------------------------------------------------

    private static async Task<(WindowReport Report, ClientSyncState State, FakeNetwork Network)> WindowAsync(
        Func<HttpRequestMessage, HttpResponseMessage?> reports, int pending = 2)
    {
        var (facility, signer) = Facility_();
        var state = new ClientSyncState();
        for (var i = 0; i < pending; i++)
        {
            state.ProblemReports.Add(ProblemReports.Build(State(), App, Behind, $"Report {i}", []));
        }

        var network = new FakeNetwork
        {
            Respond = request => request.RequestUri!.AbsolutePath.EndsWith("/api/problem-reports", StringComparison.Ordinal)
                ? reports(request) ?? throw new HttpRequestException("No route to host")
                : FakeNetwork.Envelope("{}"),
        };

        var report = await new ConnectivityWindow(facility, CentreClient(network), signer, (_, _) => Task.CompletedTask)
            .RunAsync(state, Now);
        return (report, state, network);
    }

    [Fact]
    public async Task PendingReportsAreSentSignedAndLeaveTheTablet()
    {
        var (report, state, network) = await WindowAsync(_ => FakeNetwork.Envelope("{}", HttpStatusCode.Created));

        Assert.Equal(2, report.ProblemReportsSent);
        Assert.Empty(state.ProblemReports);
        var sent = network.Requests.Where(entry => entry.Request.RequestUri!.AbsolutePath.EndsWith("/api/problem-reports")).ToList();
        Assert.Equal(2, sent.Count);
        Assert.All(sent, entry => Assert.True(entry.Request.Headers.Contains(DeviceSignature.HeaderName)));
    }

    /// <summary>No signal: the reports wait for the next window, in order.</summary>
    [Fact]
    public async Task WithoutAnAnswerTheyWait()
    {
        var (report, state, _) = await WindowAsync(_ => null);

        Assert.Equal(0, report.ProblemReportsSent);
        Assert.Equal(["Report 0", "Report 1"], state.ProblemReports.Select(entry => entry.Description));
    }

    /// <summary>Refused outright, a report would be refused every window, forever: it is dropped, and said.</summary>
    [Fact]
    public async Task ARefusedReportIsDroppedNotResentForever()
    {
        var (report, state, _) = await WindowAsync(_ => FakeNetwork.Envelope("""{"status":400,"title":"Invalid","errors":[]}""", HttpStatusCode.BadRequest));

        Assert.Empty(state.ProblemReports);
        Assert.Equal(0, report.ProblemReportsSent);
        Assert.NotEmpty(report.Problems);
    }
}
