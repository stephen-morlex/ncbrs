using NCBRS.Client.Certificates;
using NCBRS.Client.Localization;
using NCBRS.Client.Network;
using NCBRS.Client.Sync;
using NCBRS.Models;

namespace NCBRS.Client.Printing;

/// <summary>A certificate ready to print, or why there is none.</summary>
public sealed record CertificateToPrint(PrintedDocument? Document, bool Reprint, string? Problem)
{
    public bool Ready => Document is not null;
}

/// <summary>
/// Getting a birth's certificate onto paper from the tablet: online only,
/// because only the registry holds the key that signs one.
///
/// Issue first; if the registry already issued one, reprint it. A reprint is
/// counted at the registry, and printing again is exactly what a reprint is.
/// When the registry will not issue one at all — a late registration not yet
/// verified, a provisional number not yet reconciled, an annulled record —
/// the reason given is the issue's, because that is the one that explains it.
/// </summary>
public static class CertificateForPrint
{
    public static async Task<CertificateToPrint> FetchAsync(
        CentralClient centre,
        string brn,
        string deviceId,
        DeviceSigner signer,
        CachedVerificationBundle bundle,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        var issued = await centre.IssueCertificateAsync(brn, deviceId, signer, cancellationToken);
        var reprint = false;
        var answer = issued;

        if (!issued.Succeeded && issued.StatusCode == 409)
        {
            var again = await centre.ReprintCertificateAsync(brn, deviceId, signer, cancellationToken);
            if (again.Succeeded)
            {
                answer = again;
                reprint = true;
            }
        }

        if (answer.Value is not { } certificate)
        {
            return new CertificateToPrint(null, false, Language.Format(Strings.Print_NotIssued, Describe(issued)));
        }

        var (document, problem) = PrintedDocuments.Certificate(certificate, bundle.Verify(certificate.QrPayload, nowUtc));
        return new CertificateToPrint(document, reprint, problem);
    }

    private static string Describe(CentralResult<CertificateResponse> result)
        => result.Errors is { Count: > 0 } errors
            ? string.Join("; ", errors.Select(error => error.Message))
            : result.Detail ?? Language.Name(result.Outcome);
}
