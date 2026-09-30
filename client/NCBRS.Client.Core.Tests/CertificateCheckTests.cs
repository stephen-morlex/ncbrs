using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using NCBRS.Certificates;
using NCBRS.Client.Certificates;
using NCBRS.Client.Localization;
using NCBRS.Models;
using Xunit;

namespace NCBRS.Client.Tests;

/// <summary>
/// What the checker is told (B8). The verdicts themselves are the centre's
/// verifier's and are tested there; these pin how each one reads — above all
/// that "cannot be checked here" never reads as accepted, and that nothing a
/// forged payload says is shown.
/// </summary>
public class CertificateCheckTests
{
    private static OfflineVerification Answer(
        OfflineVerdict verdict, RevocationReason? reason = null, DateTime? revokedAt = null)
        => new(verdict, "100103", "Achol Deng Garang", new DateOnly(2026, 9, 1), "Female",
            new DateTime(2026, 9, 10, 8, 0, 0, DateTimeKind.Utc), reason, revokedAt, "centre's words");

    [Fact]
    public void AValidCertificateIsAcceptedAndSaysWhatItCertifies()
    {
        var reading = CertificateCheck.Read(Answer(OfflineVerdict.Valid), RevocationCoverage.Complete);

        Assert.True(reading.Accept);
        Assert.Equal(Strings.Check_ValidHeadline, reading.Headline);
        Assert.Contains(reading.Facts, fact => fact.Value == "100103");
        Assert.Contains(reading.Facts, fact => fact.Value == "Achol Deng Garang");
        // The coded sex is named in the checker's language, not printed as a code.
        Assert.Contains(reading.Facts, fact => fact.Value == Language.Name(Sex.Female));
        Assert.Equal(5, reading.Facts.Count);
    }

    [Theory]
    [InlineData(RevocationCoverage.Stale)]
    [InlineData(RevocationCoverage.Incomplete)]
    [InlineData(RevocationCoverage.Untrusted)]
    [InlineData(RevocationCoverage.Empty)]
    public void UnknownIsNeverAcceptedAndSaysWhy(RevocationCoverage coverage)
    {
        var reading = CertificateCheck.Read(Answer(OfflineVerdict.Unknown), coverage);

        Assert.False(reading.Accept);
        Assert.Equal(Strings.Check_UnknownHeadline, reading.Headline);
        Assert.NotEqual(Strings.Check_ValidHeadline, reading.Headline);
        Assert.NotEqual(Strings.Check_FakeHeadline, reading.Headline);
        Assert.Contains(Strings.ResourceManager.GetString($"Coverage_{coverage}", Language.Current)!, reading.Explanation);
    }

    [Fact]
    public void ATabletThatNeverDownloadedTheBundleSaysSo()
    {
        var reading = CertificateCheck.Check(CachedVerificationBundle.Empty, "NCBRS1.k.p.s", DateTime.UtcNow);

        Assert.False(reading.Accept);
        Assert.Equal(Strings.Check_NoBundle, reading.Explanation);
        Assert.Empty(reading.Facts);
        Assert.Null(reading.ListDownloaded);
    }

    [Fact]
    public void NothingAForgedPayloadSaysIsShown()
    {
        // Even were a verifier to return fields with NotGenuine, they are
        // whatever someone printed, and must not be read out as facts.
        var reading = CertificateCheck.Read(Answer(OfflineVerdict.NotGenuine), RevocationCoverage.Complete);

        Assert.False(reading.Accept);
        Assert.Equal(Strings.Check_FakeHeadline, reading.Headline);
        Assert.Empty(reading.Facts);
    }

    [Fact]
    public void AWithdrawnCertificateSaysWhyAndWhatNext()
    {
        var withdrawn = new DateTime(2026, 9, 20, 9, 0, 0, DateTimeKind.Utc);

        var amended = CertificateCheck.Read(Answer(OfflineVerdict.Revoked, RevocationReason.Amended, withdrawn), RevocationCoverage.Complete);
        var annulled = CertificateCheck.Read(Answer(OfflineVerdict.Revoked, RevocationReason.RegistrationAnnulled, withdrawn), RevocationCoverage.Stale);

        Assert.False(amended.Accept);
        Assert.Contains(Language.Name(RevocationReason.Amended), amended.Explanation);
        Assert.Equal(Strings.Check_RevokedDo, amended.WhatToDo);

        // An annulment has no replacement: there was no such birth.
        Assert.False(annulled.Accept);
        Assert.Equal(Strings.Check_AnnulledDo, annulled.WhatToDo);
    }

    [Fact]
    public void AGarbledCodeIsNotGenuineAndSaysToCheckTheTyping()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Test Ministry", ecdsa, HashAlgorithmName.SHA256);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        using var bundle = CachedVerificationBundle.From(
            [new VerificationKey("test-key", certificate.ExportCertificatePem(), Active: true)], [], DateTime.UtcNow);

        // One character typed wrong reads exactly as a forgery does, which is
        // why the advice says to check the typing.
        var reading = CertificateCheck.Check(bundle, "  NCBRS1.test-key.bm90.c2ln \n", DateTime.UtcNow);

        Assert.Equal(OfflineVerdict.NotGenuine, reading.Verdict);
        Assert.Equal(Strings.Check_FakeDo, reading.WhatToDo);
    }
}
