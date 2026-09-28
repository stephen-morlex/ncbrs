using NCBRS.Devices;

namespace NCBRS.Client.Sync;

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
/// exact bytes (WS-B9), and the device id, in a versioned envelope. **A
/// tampered transfer file is rejected:** because the signature is over the raw
/// bytes — the same bytes the centre will ultimately parse — any edit to the
/// body in transit fails verification.
///
/// The envelope's format is <see cref="TransferEnvelopes"/> in Contracts, since
/// the registry reads it too. What leaves the tablet is that envelope
/// <b>sealed</b> to the registry (<see cref="SealedTransfer"/>), so a stick
/// that is lost, copied or passed through several hands exposes nobody.
/// </summary>
public static class OfflineTransferFile
{
    public const string CurrentVersion = TransferEnvelopes.CurrentVersion;

    /// <summary>
    /// Package a signed batch body. <paramref name="body"/> is the exact bytes
    /// to be uploaded — sign then carry the same bytes, never a re-serialisation.
    /// </summary>
    public static byte[] Pack(string deviceId, ReadOnlySpan<byte> body, DeviceSigner signer)
        => TransferEnvelopes.Pack(deviceId, body, signer.Sign(body));

    /// <summary>
    /// Package and seal: the signed envelope, encrypted to the registry's
    /// transfer key so only the registry can read the births inside.
    /// </summary>
    public static byte[] PackSealed(
        string deviceId, ReadOnlySpan<byte> body, DeviceSigner signer, string registryPublicKeyPem, string registryKeyId)
        => SealedTransfer.Seal(Pack(deviceId, body, signer), registryPublicKeyPem, registryKeyId);

    /// <summary>
    /// Read a transfer file and verify it against the device's enrolled public
    /// key. A malformed file, an unrecognised version, or a signature that does
    /// not match the body is refused; only a genuine file yields its body.
    /// </summary>
    public static OfflineTransferResult Open(ReadOnlySpan<byte> file, string publicKeyPem)
    {
        var opened = TransferEnvelopes.Open(file, publicKeyPem);
        return opened.Accepted
            ? new OfflineTransferResult(true, opened.DeviceId, opened.Body, null)
            : OfflineTransferResult.Refuse(opened.Reason!);
    }
}
