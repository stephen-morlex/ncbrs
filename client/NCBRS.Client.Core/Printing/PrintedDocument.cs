using NCBRS.Certificates;
using NCBRS.Client.Localization;
using NCBRS.Models;

namespace NCBRS.Client.Printing;

public enum DocumentKind
{
    /// <summary>Printed by the tablet at registration. Proves nothing by itself.</summary>
    Slip,

    /// <summary>Signed by the registry. Valid only if genuine and not withdrawn.</summary>
    Certificate,
}

public sealed record PrintedLine(string Label, string Value);

/// <summary>
/// What goes on a piece of paper, in the tablet's language, independent of the
/// printer: an A4/A5 page through Android's print system and a thermal slip
/// both lay out this, so the two can never say different things.
/// </summary>
public sealed record PrintedDocument(
    DocumentKind Kind,
    string Heading,
    string Title,
    IReadOnlyList<PrintedLine> Lines,
    string Notice,
    string QrText,
    bool Provisional,
    bool RightToLeft);

/// <summary>
/// The code on a registration slip: the BRN, marked as a slip. Scanning it on a
/// tablet finds the number to fetch the certificate by; the check screen reads
/// it as a slip rather than calling it a forged certificate, which it is not.
/// It is not signed and says so: anyone could print one.
/// </summary>
public static class SlipCode
{
    public const string Prefix = "NCBRS-SLIP1.";

    public static string For(string brn) => Prefix + brn;

    public static bool TryRead(string? text, out string brn)
    {
        brn = "";
        var trimmed = text?.Trim() ?? "";
        if (!trimmed.StartsWith(Prefix, StringComparison.Ordinal) || trimmed.Length == Prefix.Length)
        {
            return false;
        }

        brn = trimmed[Prefix.Length..];
        return true;
    }
}

public static class PrintedDocuments
{
    /// <summary>
    /// The slip handed to the family at registration, before anyone leaves the
    /// room. A provisional number is printed loudly, because the family will
    /// be asked for it once the registry has given the birth its real one.
    /// </summary>
    public static PrintedDocument Slip(
        RegistrationDraft draft,
        RegisterBirthRequest birth,
        string facilityName,
        string? registrarName)
    {
        var lines = new List<PrintedLine>
        {
            new(draft.IsProvisional ? Strings.Print_ProvisionalNumber : Strings.Check_Brn, draft.Brn),
            new(Strings.Register_ChildName, birth.ChildFullName),
            new(Strings.Register_DateOfBirth, Language.Format(Strings.Check_Date, DateOnly.FromDateTime(UtcTime.AsUtc(birth.DateOfBirth)))),
            new(Strings.Register_Sex, Language.Name(birth.Sex)),
        };

        if (!string.IsNullOrWhiteSpace(birth.MotherFullName))
        {
            lines.Add(new(Strings.Register_Mother, birth.MotherFullName));
        }

        lines.Add(new(Strings.Print_Facility, facilityName));

        if (birth.RegisteredAtUtc is { } captured)
        {
            lines.Add(new(Strings.Print_RegisteredAt, Language.Format(Strings.Print_DateTime, UtcTime.AsUtc(captured).ToLocalTime())));
        }

        if (!string.IsNullOrWhiteSpace(registrarName))
        {
            lines.Add(new(Strings.Print_Registrar, registrarName));
        }

        return new PrintedDocument(
            DocumentKind.Slip,
            Strings.Print_Heading,
            draft.IsProvisional ? Strings.Print_SlipProvisionalTitle : Strings.Print_SlipTitle,
            lines,
            draft.IsProvisional ? Strings.Print_SlipProvisionalNotice : Strings.Print_SlipNotice,
            SlipCode.For(draft.Brn),
            draft.IsProvisional,
            Language.IsArabic);
    }

    /// <summary>
    /// A test page for setting a printer up: it names the tablet, and carries
    /// a code in the same place a slip does, so whoever holds it can see the
    /// text is legible and check the code scans. It names no birth.
    /// </summary>
    public static PrintedDocument Test(string facilityName, string deviceId)
        => new(
            DocumentKind.Slip,
            Strings.Print_Heading,
            Strings.Printer_TestTitle,
            [new PrintedLine(Strings.Print_Facility, facilityName), new PrintedLine(Strings.Printer_Tablet, deviceId)],
            Strings.Printer_TestNotice,
            "NCBRS-PRINTER-TEST." + deviceId,
            Provisional: false,
            Language.IsArabic);

    /// <summary>
    /// The certificate as the registry signed it. The facts printed are read
    /// from the <em>signed payload</em>, checked against the tablet's own
    /// bundle, never from the response's other fields: what the paper says
    /// and what its code proves cannot then differ. A code this tablet cannot
    /// verify, or one already withdrawn, is not printed at all; a family would
    /// otherwise leave holding a document the first check refuses.
    /// </summary>
    public static (PrintedDocument? Document, string? Problem) Certificate(
        CertificateResponse issued, OfflineVerification verified)
    {
        if (verified.Verdict is OfflineVerdict.Revoked)
        {
            return (null, Strings.Print_Withdrawn);
        }

        // Unknown with facts means the signature held and only the list of
        // withdrawn certificates is out of date: this one was issued moments
        // ago, so that list has nothing to say about it.
        if (verified.Brn is null || verified.Verdict is OfflineVerdict.NotGenuine
            || !string.Equals(verified.Brn, issued.Brn, StringComparison.Ordinal))
        {
            return (null, Strings.Print_NotGenuine);
        }

        var lines = new List<PrintedLine>
        {
            new(Strings.Check_Brn, verified.Brn),
            new(Strings.Register_ChildName, verified.ChildFullName ?? ""),
        };

        if (verified.DateOfBirth is { } born)
        {
            lines.Add(new(Strings.Register_DateOfBirth, Language.Format(Strings.Check_Date, born)));
        }

        if (verified.Sex is { } sex)
        {
            lines.Add(new(Strings.Register_Sex, Enum.TryParse<Sex>(sex, out var coded) ? Language.Name(coded) : sex));
        }

        lines.Add(new(Strings.Print_Facility, issued.FacilityName));

        if (verified.IssueDateUtc is { } issuedAt)
        {
            lines.Add(new(Strings.Check_Issued, Language.Format(Strings.Check_Date, issuedAt.ToLocalTime())));
        }

        return (new PrintedDocument(
            DocumentKind.Certificate,
            Strings.Print_Heading,
            Strings.Print_CertificateTitle,
            lines,
            Strings.Print_CertificateNotice,
            issued.QrPayload,
            Provisional: false,
            Language.IsArabic), null);
    }
}
