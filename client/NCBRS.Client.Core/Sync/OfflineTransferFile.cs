using System.Text.Json;
using NCBRS.Devices;

namespace NCBRS.Client.Sync;

/// <summary>The on-disk shape of a transfer file: a versioned envelope around the signed batch body.</summary>
public sealed record OfflineTransferEnvelope(string Version, string DeviceId, string BodyBase64, string Signature);

/// <summary>What opening a transfer file produced: the verified body, or why it was refused.</summary>
public sealed record OfflineTransferResult(bool Accepted, string? DeviceId, byte[]? Body, string? Reason)
{
    public static OfflineTransferResult Refuse(string reason) => new(false, null, null, reason);
}

/// <summary>
/// WS-H2. A birth batch carried on removable media when a post has no network
/// at all — not even enough to sync directly — to a sync point that does
/// (draft: connectivity fallbacks).
///
/// The file is the outbox batch's raw bytes, the device signature over those
/// exact bytes (WS-B9), and the device id, in a versioned envelope. The point
/// of the exercise is the exit condition: **a tampered transfer file is
/// rejected.** Because the signature is over the raw bytes — the same bytes the
/// centre will ultimately parse — any edit to the body in transit fails
/// verification, and the sync point refuses it rather than forwarding a batch
/// no one signed. Verification reuses <see cref="DeviceSignature"/> (Contracts),
/// so a file accepted here is a file the centre will accept, and the body is
/// forwarded byte-for-byte, exactly as the district tier forwards a batch.
/// </summary>
public static class OfflineTransferFile
{
    public const string CurrentVersion = "ncbrs-offline-transfer-v1";

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>
    /// Package a signed batch body for transfer. <paramref name="body"/> is the
    /// exact bytes to be uploaded — sign then carry the same bytes, never a
    /// re-serialisation.
    /// </summary>
    public static byte[] Pack(string deviceId, ReadOnlySpan<byte> body, DeviceSigner signer)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            throw new ArgumentException("A device id is required.", nameof(deviceId));
        }

        var envelope = new OfflineTransferEnvelope(
            CurrentVersion, deviceId, Convert.ToBase64String(body), signer.Sign(body));

        return JsonSerializer.SerializeToUtf8Bytes(envelope, Json);
    }

    /// <summary>
    /// Read a transfer file and verify it against the device's enrolled public
    /// key. A malformed file, an unrecognised version, or a signature that does
    /// not match the body is refused; only a genuine file yields its body for
    /// forwarding.
    /// </summary>
    public static OfflineTransferResult Open(ReadOnlySpan<byte> file, string publicKeyPem)
    {
        OfflineTransferEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<OfflineTransferEnvelope>(file, Json);
        }
        catch (JsonException)
        {
            return OfflineTransferResult.Refuse("The transfer file is not a readable NCBRS transfer envelope.");
        }

        if (envelope is null)
        {
            return OfflineTransferResult.Refuse("The transfer file is empty.");
        }

        if (envelope.Version != CurrentVersion)
        {
            return OfflineTransferResult.Refuse(
                $"Unrecognised transfer file version '{envelope.Version}'; this reader expects '{CurrentVersion}'.");
        }

        byte[] body;
        try
        {
            body = Convert.FromBase64String(envelope.BodyBase64);
        }
        catch (FormatException)
        {
            return OfflineTransferResult.Refuse("The transfer file body is not valid base64.");
        }

        var verified = DeviceSignature.Verify(publicKeyPem, body, envelope.Signature);
        return verified.Valid
            ? new OfflineTransferResult(true, envelope.DeviceId, body, null)
            : OfflineTransferResult.Refuse(verified.Reason!);
    }
}
