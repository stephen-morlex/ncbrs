using System.Text.Json;

namespace NCBRS.Devices;

/// <summary>The on-disk shape of a transfer file: a versioned envelope around the signed batch body.</summary>
public sealed record TransferEnvelope(string Version, string DeviceId, string BodyBase64, string Signature);

/// <summary>What opening a transfer envelope produced: the verified body, or why it was refused.</summary>
public sealed record TransferEnvelopeResult(bool Accepted, string? DeviceId, byte[]? Body, string? Signature, string? Reason)
{
    public static TransferEnvelopeResult Refuse(string reason) => new(false, null, null, null, reason);
}

/// <summary>
/// WS-H2's signed transfer envelope: a batch body, the device's signature over
/// those exact bytes, and the device id. In Contracts because both ends read
/// it — the tablet writes it, and the registry opens it once a sealed transfer
/// file (<see cref="SealedTransfer"/>) has been decrypted — so its format, like
/// the signature's, exists once.
/// </summary>
public static class TransferEnvelopes
{
    public const string CurrentVersion = "ncbrs-offline-transfer-v1";

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Wrap signed bytes. The body is the exact bytes that were signed — never a re-serialisation.</summary>
    public static byte[] Pack(string deviceId, ReadOnlySpan<byte> body, string signature)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            throw new ArgumentException("A device id is required.", nameof(deviceId));
        }

        return JsonSerializer.SerializeToUtf8Bytes(
            new TransferEnvelope(CurrentVersion, deviceId, Convert.ToBase64String(body), signature), Json);
    }

    /// <summary>
    /// Read an envelope, without verifying it: the registry verifies the
    /// signature against the device's <em>enrolled</em> key, through the same
    /// check a sync upload goes through, so there is one place that decides.
    /// </summary>
    public static TransferEnvelopeResult Read(ReadOnlySpan<byte> file)
    {
        TransferEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<TransferEnvelope>(file, Json);
        }
        catch (JsonException)
        {
            return TransferEnvelopeResult.Refuse("The transfer file is not a readable NCBRS transfer envelope.");
        }

        if (envelope is null)
        {
            return TransferEnvelopeResult.Refuse("The transfer file is empty.");
        }

        if (envelope.Version != CurrentVersion)
        {
            return TransferEnvelopeResult.Refuse(
                $"Unrecognised transfer file version '{envelope.Version}'; this reader expects '{CurrentVersion}'.");
        }

        if (string.IsNullOrWhiteSpace(envelope.DeviceId) || string.IsNullOrWhiteSpace(envelope.Signature))
        {
            return TransferEnvelopeResult.Refuse("The transfer file names no device, or carries no signature.");
        }

        try
        {
            return new TransferEnvelopeResult(true, envelope.DeviceId, Convert.FromBase64String(envelope.BodyBase64), envelope.Signature, null);
        }
        catch (FormatException)
        {
            return TransferEnvelopeResult.Refuse("The transfer file body is not valid base64.");
        }
    }

    /// <summary>Read and verify against a known public key: a genuine file yields its body, anything else is refused.</summary>
    public static TransferEnvelopeResult Open(ReadOnlySpan<byte> file, string publicKeyPem)
    {
        var read = Read(file);
        if (!read.Accepted)
        {
            return read;
        }

        var verified = DeviceSignature.Verify(publicKeyPem, read.Body!, read.Signature!);
        return verified.Valid ? read : TransferEnvelopeResult.Refuse(verified.Reason!);
    }
}
