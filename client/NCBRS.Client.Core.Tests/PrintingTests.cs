using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using NCBRS.Certificates;
using NCBRS.Client.Certificates;
using NCBRS.Client.Localization;
using NCBRS.Client.Network;
using NCBRS.Client.Printing;
using NCBRS.Client.Sync;
using NCBRS.Models;
using Xunit;
using static NCBRS.Client.Tests.CentralClientTests;

namespace NCBRS.Client.Tests;

/// <summary>
/// B7: what goes on paper, and the bytes a thermal printer takes. The
/// printers themselves are the shell's; this pins that a certificate prints
/// only what its own code proves, that a slip is never mistaken for a
/// certificate, and that the raster bytes are what ESC/POS expects.
/// </summary>
public class PrintingTests
{
    private static readonly Uri Centre = new("https://registry.ncbrs.ss/");

    // --- ESC/POS -----------------------------------------------------------

    [Fact]
    public void ARasterRowIsPackedLeftmostDotFirst_AndPaddedToAByte()
    {
        var black = new bool[10 * 2];
        black[0] = true;          // row 0, x 0 -> byte 0, top bit
        black[9] = true;          // row 0, x 9 -> byte 1, second bit
        black[10 + 8] = true;     // row 1, x 8 -> byte 1 of row 1, top bit

        var bytes = EscPos.Raster(new MonochromeImage(10, 2, black));

        Assert.Equal(new byte[] { 0x1D, 0x76, 0x30, 0x00, 2, 0, 2, 0, 0x80, 0x40, 0x00, 0x80 }, bytes);
    }

    [Fact]
    public void ATallImageIsSentInBands_SoSmallPrintersDoNotDropLines()
    {
        var bytes = EscPos.Raster(new MonochromeImage(8, 300, new bool[8 * 300]));

        // Two commands: 255 rows, then 45, each a header plus one byte a row.
        Assert.Equal(8 + 255 + 8 + 45, bytes.Length);
        Assert.Equal(new byte[] { 0x1D, 0x76, 0x30, 0x00, 1, 0, 255, 0 }, bytes[..8]);
        Assert.Equal(new byte[] { 0x1D, 0x76, 0x30, 0x00, 1, 0, 45, 0 }, bytes[263..271]);
    }

    [Fact]
    public void AJobResetsPrintsFeedsAndCuts()
    {
        var job = EscPos.Job(new MonochromeImage(8, 1, [true, false, false, false, false, false, false, false]));

        Assert.Equal(EscPos.Initialise, job[..2]);
        Assert.Equal(new byte[] { 0x1B, 0x64, 4, 0x1D, 0x56, 0x42, 0x00 }, job[^7..]);
    }

    [Fact]
    public void AnImageWhosePixelsDoNotMatchItsSizeIsRefused()
        => Assert.Throws<ArgumentException>(() => new MonochromeImage(8, 2, new bool[8]));

    // --- the slip --------------------------------------------------------------

    private static RegisterBirthRequest Birth(string? mother = "Achol Garang") => new()
    {
        ChildFullName = "Garang Deng",
        // Midnight UTC, as the tablet sends a date of birth: the printed date
        // must be that date wherever the tablet's clock is set.
        DateOfBirth = new DateTime(2026, 9, 29, 0, 0, 0, DateTimeKind.Utc),
        Sex = Sex.Male,
        MotherFullName = mother,
        RegisteredAtUtc = new DateTime(2026, 9, 29, 10, 0, 0, DateTimeKind.Utc),
    };

    [Fact]
    public void ASlipCarriesTheNumberAsASlipCode_AndSaysItIsNotACertificate()
    {
        var slip = PrintedDocuments.Slip(new RegistrationDraft("100104", false, false), Birth(), "Juba Teaching Hospital", "Alice Lado");

        Assert.Equal(DocumentKind.Slip, slip.Kind);
        Assert.False(slip.Provisional);
        Assert.Equal("NCBRS-SLIP1.100104", slip.QrText);
        Assert.Equal(Strings.Print_SlipNotice, slip.Notice);
        Assert.Contains(slip.Lines, line => line.Value == "100104");
        Assert.Contains(slip.Lines, line => line.Value == Language.Format(Strings.Check_Date, new DateOnly(2026, 9, 29)));
        Assert.Contains(slip.Lines, line => line.Value == "Alice Lado");
    }

    [Fact]
    public void AProvisionalSlipSaysSoLoudly()
    {
        var slip = PrintedDocuments.Slip(new RegistrationDraft("PROV-TAB-1-0001", true, true), Birth(mother: null), "Terekeka", null);

        Assert.True(slip.Provisional);
        Assert.Equal(Strings.Print_SlipProvisionalTitle, slip.Title);
        Assert.Equal(Strings.Print_SlipProvisionalNotice, slip.Notice);
        Assert.Contains(slip.Lines, line => line.Label == Strings.Print_ProvisionalNumber && line.Value == "PROV-TAB-1-0001");
        Assert.DoesNotContain(slip.Lines, line => line.Label == Strings.Register_Mother);
    }

    [Theory]
    [InlineData("NCBRS-SLIP1.100104", true, "100104")]
    [InlineData("  NCBRS-SLIP1.PROV-TAB-1-0001 \n", true, "PROV-TAB-1-0001")]
    [InlineData("NCBRS-SLIP1.", false, "")]
    [InlineData("NCBRS1.key.payload.signature", false, "")]
    public void ASlipCodeIsReadBack(string text, bool isSlip, string brn)
    {
        Assert.Equal(isSlip, SlipCode.TryRead(text, out var read));
        Assert.Equal(brn, read);
    }

    /// <summary>
    /// A slip is not a certificate, but it is not a forgery either: the family
    /// is holding exactly what they were handed.
    /// </summary>
    [Fact]
    public void TheCheckScreenReadsASlipAsASlip_NeverAsAForgery()
    {
        var reading = CertificateCheck.Check(CachedVerificationBundle.Empty, "NCBRS-SLIP1.100104", DateTime.UtcNow);

        Assert.False(reading.Accept);
        Assert.Equal(Strings.Check_SlipHeadline, reading.Headline);
        Assert.NotEqual(Strings.Check_FakeHeadline, reading.Headline);
        Assert.Contains(reading.Facts, fact => fact.Value == "100104");
    }

    // --- the certificate --------------------------------------------------------

    private sealed class Ministry : IDisposable
    {
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        public CachedVerificationBundle Bundle { get; }

        public Ministry()
        {
            var request = new CertificateRequest("CN=Test Ministry", _key, HashAlgorithmName.SHA256);
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
            Bundle = CachedVerificationBundle.From(
                [new VerificationKey("test-key", certificate.ExportCertificatePem(), Active: true)], [], DateTime.UtcNow);
        }

        public string Sign(string brn, string name)
        {
            var payload = Encoding.UTF8.GetBytes($"NCBRS|v1|{brn}|{name}|2026-09-29|Male|0199c000-0000-7000-8000-0000000f0003|2026-09-30T08:00:00Z");
            var signature = _key.SignData(payload, HashAlgorithmName.SHA256);
            return $"NCBRS1.test-key.{CertificateSignatureForms.Encode(payload)}.{CertificateSignatureForms.Encode(signature)}";
        }

        public void Dispose()
        {
            Bundle.Dispose();
            _key.Dispose();
        }
    }

    private static CertificateResponse Issued(string qr, string brn = "100102", string name = "Garang Deng")
        => new(Guid.NewGuid(), brn, name, new DateTime(2026, 9, 29), Sex.Male, "Juba Teaching Hospital",
            new DateTime(2026, 9, 30, 8, 0, 0, DateTimeKind.Utc), qr, qr.Split('.')[^1], 0);

    /// <summary>
    /// What the paper says is what its code proves: the facts come from the
    /// signed payload, not from the response's other fields.
    /// </summary>
    [Fact]
    public void ACertificatePrintsTheFactsItsCodeProves()
    {
        using var ministry = new Ministry();
        var qr = ministry.Sign("100102", "Garang Deng");

        // A response whose loose fields disagree with what was signed.
        var (document, problem) = PrintedDocuments.Certificate(
            Issued(qr, name: "Someone Else"), ministry.Bundle.Verify(qr, DateTime.UtcNow));

        Assert.Null(problem);
        Assert.Equal(DocumentKind.Certificate, document!.Kind);
        Assert.Equal(qr, document.QrText);
        Assert.Contains(document.Lines, line => line.Value == "Garang Deng");
        Assert.DoesNotContain(document.Lines, line => line.Value == "Someone Else");
        Assert.Contains(document.Lines, line => line.Value == "Juba Teaching Hospital");
    }

    [Fact]
    public void ACertificateThisTabletCannotVerifyIsNotPrinted()
    {
        using var ministry = new Ministry();
        using var elsewhere = new Ministry();
        var qr = elsewhere.Sign("100102", "Garang Deng");

        var (document, problem) = PrintedDocuments.Certificate(Issued(qr), ministry.Bundle.Verify(qr, DateTime.UtcNow));

        Assert.Null(document);
        Assert.Equal(Strings.Print_NotGenuine, problem);
    }

    [Fact]
    public void ACertificateForAnotherBirthIsNotPrinted()
    {
        using var ministry = new Ministry();
        var qr = ministry.Sign("100999", "Garang Deng");

        var (document, problem) = PrintedDocuments.Certificate(Issued(qr, brn: "100102"), ministry.Bundle.Verify(qr, DateTime.UtcNow));

        Assert.Null(document);
        Assert.Equal(Strings.Print_NotGenuine, problem);
    }

    [Fact]
    public void AWithdrawnCertificateIsNotPrinted()
    {
        var withdrawn = new OfflineVerification(OfflineVerdict.Revoked, "100102", "Garang Deng", new DateOnly(2026, 9, 29),
            "Male", DateTime.UtcNow, RevocationReason.Amended, DateTime.UtcNow, "");

        var (document, problem) = PrintedDocuments.Certificate(Issued("NCBRS1.k.p.s"), withdrawn);

        Assert.Null(document);
        Assert.Equal(Strings.Print_Withdrawn, problem);
    }

    // --- fetching one from the registry ------------------------------------------

    private static async Task<(CertificateToPrint Result, FakeNetwork Network)> FetchAsync(
        Func<HttpRequestMessage, HttpResponseMessage> respond, CachedVerificationBundle bundle)
    {
        var network = new FakeNetwork { Respond = respond };
        var centre = new CentralClient(new HttpClient(network), new CentralEndpoints(Centre), _ => Task.FromResult<string?>("token"));
        var result = await CertificateForPrint.FetchAsync(centre, "100102", "TAB-1", DeviceSigner.Generate(), bundle, DateTime.UtcNow);
        return (result, network);
    }

    private static string Json(CertificateResponse response) => JsonSerializer.Serialize(response, ClientJson.Options);

    private static HttpResponseMessage Refusal(string title, string message, HttpStatusCode status)
        => FakeNetwork.Envelope(
            $$"""{"status":{{(int)status}},"title":"{{title}}","errors":[{"field":"brn","message":"{{message}}"}]}""", status);

    [Fact]
    public async Task AFirstCertificateIsIssued_SignedByTheDevice()
    {
        using var ministry = new Ministry();
        var qr = ministry.Sign("100102", "Garang Deng");

        var (result, network) = await FetchAsync(_ => FakeNetwork.Envelope(Json(Issued(qr)), HttpStatusCode.Created), ministry.Bundle);

        Assert.True(result.Ready);
        Assert.False(result.Reprint);
        var (request, _) = Assert.Single(network.Requests);
        Assert.EndsWith("api/BirthRecords/100102/certificate", request.RequestUri!.AbsolutePath);
        Assert.True(request.Headers.Contains(DeviceSigner.HeaderName));
    }

    [Fact]
    public async Task OneAlreadyIssuedIsReprinted_SoTheRegistryCountsIt()
    {
        using var ministry = new Ministry();
        var qr = ministry.Sign("100102", "Garang Deng");

        var (result, network) = await FetchAsync(request => request.RequestUri!.AbsolutePath.EndsWith("/reprint")
            ? FakeNetwork.Envelope(Json(Issued(qr) with { ReprintCount = 1 }))
            : Refusal("Certificate already issued.", "Use the reprint endpoint.", HttpStatusCode.Conflict), ministry.Bundle);

        Assert.True(result.Ready);
        Assert.True(result.Reprint);
        Assert.Equal(2, network.Requests.Count);
    }

    /// <summary>
    /// A late registration not yet verified has no certificate to reprint
    /// either; the reason the family is given is the issue's, which explains it.
    /// </summary>
    [Fact]
    public async Task WhenTheRegistryWillNotIssueOne_ItsReasonIsGiven()
    {
        using var ministry = new Ministry();

        var (result, _) = await FetchAsync(request => request.RequestUri!.AbsolutePath.EndsWith("/reprint")
            ? Refusal("Not found.", "No certificate has been issued.", HttpStatusCode.NotFound)
            : Refusal("Late registration not verified.", "Awaiting verification by a district registrar.", HttpStatusCode.Conflict),
            ministry.Bundle);

        Assert.False(result.Ready);
        Assert.Contains("Awaiting verification by a district registrar.", result.Problem);
    }

    // --- the page for Android's print system ----------------------------------

    private static PrintedDocument Page(string name = "Garang Deng", bool provisional = false, bool rightToLeft = false)
        => new(DocumentKind.Slip, "Heading", "Title", [new PrintedLine("Child", name)], "Notice", "NCBRS-SLIP1.100104", provisional, rightToLeft);

    /// <summary>Every value on the page was typed by someone: a name is text, never markup.</summary>
    [Fact]
    public void ANameIsPrintedAsText_NeverAsMarkup()
    {
        var html = PrintedDocumentHtml.Render(Page("<script>alert(1)</script> & Deng"), new bool[1, 1]);

        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt; &amp; Deng", html);
    }

    [Fact]
    public void AnArabicPageRunsRightToLeft()
    {
        Assert.Contains("dir=\"rtl\"", PrintedDocumentHtml.Render(Page(rightToLeft: true), new bool[1, 1]));
        Assert.Contains("dir=\"ltr\"", PrintedDocumentHtml.Render(Page(), new bool[1, 1]));
    }

    [Fact]
    public void OnlyAProvisionalSlipCarriesTheWarningBox()
    {
        Assert.Contains("class=\"provisional\"", PrintedDocumentHtml.Render(Page(provisional: true), new bool[1, 1]));
        Assert.DoesNotContain("class=\"provisional\"", PrintedDocumentHtml.Render(Page(), new bool[1, 1]));
    }

    [Fact]
    public void TheQrCodeIsDrawnModuleForModule_WithAQuietZone()
    {
        var modules = new bool[3, 2];
        modules[0, 0] = true;
        modules[2, 1] = true;

        var svg = PrintedDocumentHtml.Svg(modules);

        Assert.Contains("viewBox=\"0 0 11 10\"", svg);
        Assert.Contains("M4 4h1v1h-1z", svg);
        Assert.Contains("M6 5h1v1h-1z", svg);
        Assert.Equal(2, svg.Split("h1v1h-1z").Length - 1);
    }

    /// <summary>A printer test page names the tablet and no birth, and its code is not a slip's.</summary>
    [Fact]
    public void APrinterTestPageNamesNoBirth()
    {
        var page = PrintedDocuments.Test("Juba Teaching Hospital", "TAB-8C4EC4EB5E47");

        Assert.Equal(Strings.Printer_TestTitle, page.Title);
        Assert.DoesNotContain(page.Lines, line => line.Label == Strings.Check_Brn || line.Label == Strings.Register_ChildName);
        Assert.False(SlipCode.TryRead(page.QrText, out _));
    }
}
