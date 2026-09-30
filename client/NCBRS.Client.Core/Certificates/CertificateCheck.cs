using NCBRS.Certificates;
using NCBRS.Client.Localization;
using NCBRS.Models;

namespace NCBRS.Client.Certificates;

/// <summary>One thing the certificate says, as the checker reads it.</summary>
public sealed record CertificateFact(string Label, string Value);

/// <summary>
/// A verdict as the person holding the tablet reads it: a headline, why, what
/// to do, and what the certificate says.
/// </summary>
public sealed record CertificateReading(
    OfflineVerdict Verdict,
    string Headline,
    string Explanation,
    string WhatToDo,
    IReadOnlyList<CertificateFact> Facts,
    string? ListDownloaded)
{
    /// <summary>Only a positive answer: Unknown is never read as accepted.</summary>
    public bool Accept => Verdict is OfflineVerdict.Valid;
}

/// <summary>
/// B8's screen, minus the screen. The verdict comes from the centre's own
/// verifier, reused unchanged; this only says it in the checker's language.
///
/// The verifier's own <see cref="OfflineVerification.Detail"/> is English and
/// written for the registry, so it is never shown. What the checker needs is
/// which of four answers this is and what to do about it, and "cannot be
/// checked here" has to read as different from both yes and no — a tablet whose
/// list is out of date is in no position to approve, but it has found nothing
/// wrong either.
/// </summary>
public static class CertificateCheck
{
    public static CertificateReading Check(CachedVerificationBundle bundle, string scanned, DateTime nowUtc)
    {
        // A slip's code is not a certificate's, and calling it a forgery would
        // accuse a family of holding exactly what they were handed.
        if (Printing.SlipCode.TryRead(scanned, out var brn))
        {
            return new CertificateReading(OfflineVerdict.Unknown,
                Strings.Check_SlipHeadline, Strings.Check_Slip, Strings.Check_SlipDo,
                [new CertificateFact(Strings.Check_Brn, brn)], null);
        }

        return Read(bundle.Verify(scanned.Trim(), nowUtc), bundle.CoverageAt(nowUtc), bundle.FetchedAtUtc);
    }

    public static CertificateReading Read(
        OfflineVerification result, RevocationCoverage? coverage, DateTime? downloadedAtUtc = null)
    {
        var downloaded = downloadedAtUtc is { } at
            ? Language.Format(Strings.Check_ListDownloaded, at.ToLocalTime())
            : null;

        return result.Verdict switch
        {
            OfflineVerdict.Valid => new(result.Verdict,
                Strings.Check_ValidHeadline, Strings.Check_Valid, Strings.Check_ValidDo,
                Facts(result), downloaded),

            OfflineVerdict.Revoked => new(result.Verdict,
                Strings.Check_RevokedHeadline,
                Language.Format(Strings.Check_Revoked,
                    result.RevokedAtUtc?.ToLocalTime(),
                    result.RevocationReason is { } reason ? Language.Name(reason) : ""),
                result.RevocationReason is RevocationReason.RegistrationAnnulled
                    ? Strings.Check_AnnulledDo
                    : Strings.Check_RevokedDo,
                Facts(result), downloaded),

            // Nothing from a payload that did not verify is shown: it is
            // whatever someone chose to print.
            OfflineVerdict.NotGenuine => new(result.Verdict,
                Strings.Check_FakeHeadline, Strings.Check_Fake, Strings.Check_FakeDo,
                [], null),

            _ => new(result.Verdict,
                Strings.Check_UnknownHeadline,
                coverage is null
                    ? Strings.Check_NoBundle
                    : Language.Format(Strings.Check_UnknownGenuine, Why(coverage.Value)).TrimEnd(),
                Strings.Check_UnknownDo,
                Facts(result), downloaded),
        };
    }

    private static string Why(RevocationCoverage coverage) => coverage switch
    {
        RevocationCoverage.Stale => Strings.Coverage_Stale,
        RevocationCoverage.Incomplete => Strings.Coverage_Incomplete,
        RevocationCoverage.Untrusted => Strings.Coverage_Untrusted,
        RevocationCoverage.Empty => Strings.Coverage_Empty,
        _ => "",
    };

    private static List<CertificateFact> Facts(OfflineVerification result)
    {
        var facts = new List<CertificateFact>();

        if (result.Brn is { } brn)
        {
            facts.Add(new(Strings.Check_Brn, brn));
        }

        if (result.ChildFullName is { } name)
        {
            facts.Add(new(Strings.Register_ChildName, name));
        }

        if (result.DateOfBirth is { } born)
        {
            facts.Add(new(Strings.Register_DateOfBirth, Language.Format(Strings.Check_Date, born)));
        }

        if (result.Sex is { } sex)
        {
            facts.Add(new(Strings.Register_Sex, Enum.TryParse<Sex>(sex, out var coded) ? Language.Name(coded) : sex));
        }

        if (result.IssueDateUtc is { } issued)
        {
            facts.Add(new(Strings.Check_Issued, Language.Format(Strings.Check_Date, issued.ToLocalTime())));
        }

        return facts;
    }
}
