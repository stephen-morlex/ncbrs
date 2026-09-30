using System.Globalization;
using System.Numerics;

namespace NCBRS.Certificates;

/// <summary>
/// The texts one certificate's signature can be printed as and still verify.
///
/// A withdrawn certificate is published by a digest of its signature's text
/// (<see cref="OfflineCertificateVerifier.SerialFor"/>). A digest of text is
/// only as fixed as the text, and two things let anyone change it without the
/// key while the signature still holds. Each would let a withdrawn certificate,
/// re-printed with a new QR code, verify as valid:
///
/// <list type="bullet">
/// <item>Base64 padding bits. A 64-byte signature is 86 characters, and the last
/// carries 2 bits of signature and 4 that a decoder ignores, so
/// <c>…XMA</c> and <c>…XMB</c> are one signature. Closed by
/// <see cref="TryDecode"/>, which accepts only the one encoding the signer
/// produces.</item>
/// <item>ECDSA itself. If (r, s) verifies, so does (r, n − s). Refusing one of
/// the two would stop about half of all certificates already issued from
/// verifying, because the signer never chose. So the list is checked under
/// both instead (<see cref="Equivalents"/>): the text actually issued is
/// always one of them.</item>
/// </list>
///
/// Neither changes what is issued or what the published lists contain; a
/// verifier simply stops being fooled.
/// </summary>
public static class CertificateSignatureForms
{
    // Group orders, by signature length (two field-sized integers). A wrong
    // constant would quietly stop catching the second form for that curve, so
    // the tests sign under each curve and verify the flipped signature.
    private static readonly Dictionary<int, BigInteger> Orders = new()
    {
        [64] = Hex("FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551"),
        [96] = Hex("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFC7634D81F4372DDF581A0DB248B0A77AECEC196ACCC52973"),
        [132] = Hex("01FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFA51868783BF2F966B7FCC0148F709A5D03BB5C9B8899C47AEBB6FB71E91386409"),
    };

    /// <summary>Unpadded base64url, exactly as the signer writes it.</summary>
    public static string Encode(ReadOnlySpan<byte> bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// Decodes <paramref name="text"/> only if it is the one encoding
    /// <see cref="Encode"/> gives for its bytes: no padding, no stray bits, no
    /// whitespace. Anything else was not written by the signer.
    /// </summary>
    public static bool TryDecode(string text, out byte[] bytes)
    {
        bytes = [];
        var padded = text.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };

        try
        {
            var decoded = Convert.FromBase64String(padded);
            if (!string.Equals(Encode(decoded), text, StringComparison.Ordinal))
            {
                return false;
            }

            bytes = decoded;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Every text the signature on a certificate could have been issued as:
    /// itself, and its (r, n − s) twin, each as the signer encodes it.
    /// </summary>
    public static IReadOnlyList<string> Equivalents(string signature)
    {
        if (!TryDecode(signature, out var bytes) || !Orders.TryGetValue(bytes.Length, out var order))
        {
            return [signature];
        }

        var half = bytes.Length / 2;
        var s = new BigInteger(bytes.AsSpan(half), isUnsigned: true, isBigEndian: true);
        if (s.IsZero || s >= order)
        {
            return [signature];
        }

        var twin = (byte[])bytes.Clone();
        var flipped = (order - s).ToByteArray(isUnsigned: true, isBigEndian: true);
        Array.Clear(twin, half, half);
        flipped.CopyTo(twin, bytes.Length - flipped.Length);

        return [signature, Encode(twin)];
    }

    private static BigInteger Hex(string hex)
        => BigInteger.Parse("0" + hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
}
