using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using NCBRS.Certificates;
using Xunit;

namespace NCBRS.Tests;

/// <summary>
/// The two ways to change a certificate's printed signature without the
/// Ministry's key, built here independently of the code that defends against
/// them, so a test cannot pass by sharing the fix's own arithmetic.
///
/// Found on the tablet's check screen: a certificate with the last character
/// of its code changed still verified. A withdrawn certificate is listed by a
/// digest of its signature's text, so each of these, re-printed as a new QR
/// code, used to take a withdrawn certificate past the list.
/// </summary>
public static class ReprintedCertificate
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

    private static readonly BigInteger P256Order = BigInteger.Parse(
        "0FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551",
        NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    /// <summary>
    /// The last character changed within the bits a lenient decoder ignores: a
    /// 64-byte signature is 86 characters, and the last holds 2 real bits.
    /// </summary>
    public static string WithPaddingBitChanged(string qr)
    {
        var last = Alphabet.IndexOf(qr[^1]);
        return qr[..^1] + Alphabet[last ^ 1];
    }

    /// <summary>The same payload under (r, n − s): a second valid ECDSA signature.</summary>
    public static string WithTwinSignature(string qr)
    {
        var parts = qr.Split('.');
        var signature = Decode(parts[3]);
        Assert.Equal(64, signature.Length);

        var s = new BigInteger(signature.AsSpan(32), isUnsigned: true, isBigEndian: true);
        var flipped = (P256Order - s).ToByteArray(isUnsigned: true, isBigEndian: true);
        var twin = new byte[64];
        signature.AsSpan(0, 32).CopyTo(twin);
        flipped.CopyTo(twin, 64 - flipped.Length);

        parts[3] = Convert.ToBase64String(twin).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return string.Join('.', parts);
    }

    private static byte[] Decode(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        return Convert.FromBase64String(padded);
    }
}

public class SignatureMalleabilityTests
{
    /// <summary>
    /// The group orders are constants; a wrong one would silently stop catching
    /// the twin on that curve. Each is proved by the twin it produces verifying.
    /// </summary>
    [Theory]
    [InlineData("nistP256")]
    [InlineData("nistP384")]
    [InlineData("nistP521")]
    public void TheTwinOfARealSignatureVerifies_OnEveryCurveASignerMightUse(string curve)
    {
        using var key = ECDsa.Create(ECCurve.CreateFromFriendlyName(curve));
        var data = Encoding.UTF8.GetBytes("NCBRS|v1|100102|Garang Deng|2026-09-29|Male|x|2026-09-29T20:29:56Z");

        for (var attempt = 0; attempt < 8; attempt++)
        {
            var signature = CertificateSignatureForms.Encode(key.SignData(data, HashAlgorithmName.SHA256));
            var forms = CertificateSignatureForms.Equivalents(signature);

            Assert.Equal(2, forms.Count);
            Assert.Equal(signature, forms[0]);
            Assert.NotEqual(forms[0], forms[1]);
            Assert.True(CertificateSignatureForms.TryDecode(forms[1], out var twin));
            Assert.True(key.VerifyData(data, twin, HashAlgorithmName.SHA256));
        }
    }

    [Fact]
    public void OnlyTheSignersOwnEncodingDecodes()
    {
        var bytes = RandomNumberGenerator.GetBytes(64);
        var text = CertificateSignatureForms.Encode(bytes);

        Assert.True(CertificateSignatureForms.TryDecode(text, out var decoded));
        Assert.Equal(bytes, decoded);

        var alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        var sameBytes = text[..^1] + alphabet[alphabet.IndexOf(text[^1]) ^ 1];

        Assert.False(CertificateSignatureForms.TryDecode(sameBytes, out _));
        Assert.False(CertificateSignatureForms.TryDecode(text + "==", out _));
        Assert.False(CertificateSignatureForms.TryDecode(" " + text, out _));
    }
}
