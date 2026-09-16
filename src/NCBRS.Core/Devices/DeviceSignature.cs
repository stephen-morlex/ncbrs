using System.Security.Cryptography;

namespace NCBRS.Devices;

public record DeviceSignatureResult(bool Valid, string? Reason = null);

/// <summary>
/// Proof that a sync batch came from the device it claims to (WS-B9).
///
/// **What is signed is the raw request body, byte for byte.** Not a
/// canonical projection of its fields, and that choice is load-bearing:
///
/// A canonical form is a second description of the payload, and the day it
/// disagrees with the parser -- a field added, a number formatted
/// differently, a duplicate key -- a genuine batch fails or a tampered one
/// passes. Signing the bytes removes that gap entirely: the server verifies
/// exactly what it will parse.
///
/// It also survives the district tier, which stores a batch's raw text and
/// forwards it unchanged. A signature over the bytes is still valid after a
/// week in a district queue, which a signature over a re-serialised object
/// would not be. That the two designs fit is not a coincidence -- both come
/// from the same rule, that an intermediary must not need to understand a
/// batch to carry it.
///
/// The transaction id travels inside the signed body, so replaying the bytes
/// replays a transaction the centre already answers idempotently. There is
/// nothing to gain by capturing and resending one.
/// </summary>
public static class DeviceSignature
{
    /// <summary>
    /// Header carrying the base64 ECDSA P-256 signature over the request
    /// body, in IEEE P1363 (r||s) form.
    /// </summary>
    public const string HeaderName = "X-NCBRS-Device-Signature";

    public static DeviceSignatureResult Verify(
        string publicKeyPem,
        ReadOnlySpan<byte> body,
        string? base64Signature)
    {
        if (string.IsNullOrWhiteSpace(base64Signature))
        {
            return new DeviceSignatureResult(false, $"The {HeaderName} header is missing.");
        }

        byte[] signature;

        try
        {
            signature = Convert.FromBase64String(base64Signature);
        }
        catch (FormatException)
        {
            return new DeviceSignatureResult(false, $"The {HeaderName} header is not valid base64.");
        }

        using var ecdsa = ECDsa.Create();

        try
        {
            ecdsa.ImportFromPem(publicKeyPem);
        }
        catch (Exception exception) when (exception is ArgumentException or CryptographicException)
        {
            // The enrolled key itself is unusable. Reported as its own
            // failure rather than as a bad signature: nothing the device did
            // is wrong, and telling a field officer "signature invalid" would
            // send them to replace a working tablet.
            return new DeviceSignatureResult(false, "The enrolled public key for this device could not be read.");
        }

        var valid = ecdsa.VerifyData(body, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        return valid
            ? new DeviceSignatureResult(true)
            : new DeviceSignatureResult(false, "The signature does not match the request body for this device's enrolled key.");
    }

    /// <summary>
    /// Checks that a key offered at enrolment is one this system can verify
    /// against later.
    ///
    /// Done at enrolment because the alternative is discovering it at the
    /// first sync -- which is weeks later, in the field, from a post that
    /// has just spent that time filling an outbox it now cannot deliver.
    /// </summary>
    public static DeviceSignatureResult ValidateEnrolmentKey(string publicKeyPem)
    {
        if (string.IsNullOrWhiteSpace(publicKeyPem))
        {
            return new DeviceSignatureResult(false, "A public key is required.");
        }

        if (publicKeyPem.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase))
        {
            // Refused outright rather than trimmed to the public half. A
            // device whose private key has been sent to a server is a device
            // whose signature proves nothing, and quietly accepting it would
            // leave everyone believing otherwise.
            return new DeviceSignatureResult(false,
                "A private key was supplied. Enrol the public half only -- the private key must never leave the device.");
        }

        using var ecdsa = ECDsa.Create();

        try
        {
            ecdsa.ImportFromPem(publicKeyPem);
        }
        catch (Exception exception) when (exception is ArgumentException or CryptographicException)
        {
            return new DeviceSignatureResult(false, "The public key is not a readable PEM-encoded key.");
        }

        var curve = ecdsa.KeySize;

        return curve == 256
            ? new DeviceSignatureResult(true)
            : new DeviceSignatureResult(false, $"An ECDSA P-256 key is required; this key is {curve}-bit.");
    }
}
