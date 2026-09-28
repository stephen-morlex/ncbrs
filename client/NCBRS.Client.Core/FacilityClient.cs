using System.Text.Json;
using NCBRS.Client.Brn;
using NCBRS.Client.Network;
using NCBRS.Client.Sync;
using NCBRS.Models;

namespace NCBRS.Client;

/// <summary>What one offline registration produced: its number, whether it is provisional, and whether the block is running low.</summary>
public sealed record RegistrationDraft(string Brn, bool IsProvisional, bool BlockLow);

/// <summary>
/// A signed upload ready to POST: the exact body bytes, the signature, the
/// header to carry it in, and the transaction id inside the body.
///
/// Persist it until it is settled and retry <em>it</em>, not a rebuilt one: the
/// same bytes under the same transaction id are what let the centre (and a
/// District node) recognise a retry after a lost response as work it has
/// already done, and answer with the result it already produced.
/// </summary>
public sealed record SignedUpload(byte[] Body, string Signature, string HeaderName, Guid TransactionId);

/// <summary>
/// The offline-first workflow of the Tier-1 client, composing the tested
/// client-core pieces into the acts a registrar actually performs. This is the
/// surface the MAUI shell drives; the shell adds the screens and the local
/// store, and holds no registration logic of its own.
///
/// The composition is the whole registration story from the device's side:
/// allocate a number from the granted block (or a <c>PROV-</c> fallback when it
/// runs dry, WS-B5), stage the birth in the local outbox (B6), and — when
/// connectivity returns — build the batch and sign its raw bytes (B9) so the
/// centre can prove it came from this device, or pack the same signed bytes into
/// a transfer file (H2) when there is no network even to upload. The response is
/// reconciled back through the outbox, leaving only rejected records queued.
///
/// Unlocking the registrar (B3) gates all of this and is the shell's
/// responsibility; a <see cref="FacilityClient"/> is only constructed for an
/// unlocked session.
/// </summary>
public sealed class FacilityClient
{
    // The body is signed and uploaded byte-for-byte, so it is serialised once,
    // here, in the shape the centre parses. Re-serialising it anywhere else
    // would break the signature — the same rule the raw-body signing rests on.
    private static readonly JsonSerializerOptions BodyJson = ClientJson.Options;

    private readonly string _deviceId;
    private readonly Guid _facilityId;
    private readonly DeviceBrnAllocator _brn;
    private readonly SyncOutbox _outbox;
    private readonly DeviceSigner _signer;
    private readonly long _lowBlockThreshold;

    public FacilityClient(
        string deviceId,
        Guid facilityId,
        DeviceBrnAllocator brn,
        SyncOutbox outbox,
        DeviceSigner signer,
        long lowBlockThreshold = 50)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentNullException.ThrowIfNull(brn);
        ArgumentNullException.ThrowIfNull(outbox);
        ArgumentNullException.ThrowIfNull(signer);

        _deviceId = deviceId;
        _facilityId = facilityId;
        _brn = brn;
        _outbox = outbox;
        _signer = signer;
        _lowBlockThreshold = lowBlockThreshold;
    }

    public string DeviceId => _deviceId;

    public Guid FacilityId => _facilityId;

    /// <summary>Every birth not yet settled with the centre, refused ones included.</summary>
    public int PendingCount => _outbox.Count;

    /// <summary>What the next upload would carry: queued births not held for correction.</summary>
    public int SendableCount => _outbox.SendableCount;

    /// <summary>The births the centre refused, with its reasons, held until corrected.</summary>
    public IReadOnlyList<(SyncBirthRecord Record, IReadOnlyList<ApiError> Reasons)> Refused
        => [.. _outbox.Pending
            .Where(record => _outbox.Refused.ContainsKey(record.Birth.Brn))
            .Select(record => (record, _outbox.Refused[record.Birth.Brn]))];

    /// <summary>
    /// Correct a refused birth and release it for the next upload. Its BRN and
    /// capture time stay what they were (<see cref="SyncOutbox.Correct"/>).
    /// </summary>
    public void Correct(string brn, RegisterBirthRequest corrected) => _outbox.Correct(brn, corrected);

    public long BlockRemaining => _brn.Remaining;

    /// <summary>
    /// Whether the device should fetch another BRN block while it has
    /// connectivity — running low, or already dry with nothing staged. False once
    /// a next block is in hand, so it does not keep asking. Drives the shell's
    /// top-up on a connectivity window.
    /// </summary>
    public bool NeedsMoreNumbers => !_brn.HasPendingBlock && (_brn.IsLow(_lowBlockThreshold) || _brn.IsExhausted);

    /// <summary>
    /// Stage the block the centre just granted (<c>request-brn-block</c>) to roll
    /// over to when the current one runs dry, so registration keeps issuing real
    /// BRNs instead of falling back to provisional identifiers.
    /// </summary>
    public void GrantNextBlock(long blockStart, long blockEnd) => _brn.GrantNextBlock(blockStart, blockEnd);

    /// <summary>
    /// Register a birth offline: draw its number, stamp the facility and device
    /// on it, and stage it. The caller supplies the birth details; the number,
    /// facility and device are the client's to set, so a caller cannot assert
    /// them.
    /// </summary>
    public RegistrationDraft RegisterBirth(RegisterBirthRequest birth, Guid? registeredByRegistrarId = null)
    {
        ArgumentNullException.ThrowIfNull(birth);

        var allocation = _brn.Allocate();

        _outbox.Enqueue(new SyncBirthRecord
        {
            RegisteredByRegistrarId = registeredByRegistrarId,
            Birth = birth with { Brn = allocation.Value, FacilityId = _facilityId, DeviceId = _deviceId },
        });

        return new RegistrationDraft(allocation.Value, allocation.IsProvisional, _brn.IsLow(_lowBlockThreshold));
    }

    /// <summary>
    /// Build and sign the current outbox for upload, under a new transaction
    /// id. Persist the result and POST <see cref="SignedUpload.Body"/> verbatim
    /// with the signature header (<see cref="Network.CentralClient.UploadAsync"/>
    /// does both); retry that same upload until it is settled.
    /// </summary>
    public SignedUpload BuildSignedUpload()
    {
        var transactionId = Guid.CreateVersion7();
        var body = SerializeBatch(transactionId);
        return new SignedUpload(body, _signer.Sign(body), DeviceSigner.HeaderName, transactionId);
    }

    /// <summary>
    /// Pack the current outbox as a signed transfer file for a post with no
    /// network at all (WS-H2). The body inside is the same envelope an upload
    /// sends, transaction id included, so the sync point that opens it can
    /// forward it to the centre verbatim.
    /// </summary>
    public byte[] BuildTransferFile() => OfflineTransferFile.Pack(_deviceId, SerializeBatch(Guid.CreateVersion7()), _signer);

    /// <summary>Apply the centre's response, settling accepted records and leaving rejected ones queued.</summary>
    public OutboxSettlement Settle(SyncBatchResponse response) => _outbox.Settle(response);

    /// <summary>
    /// The batch in the <c>{ meta, data }</c> envelope every centre endpoint
    /// takes, naming the transaction in <c>meta.transactionId</c>.
    ///
    /// Until the network layer this serialised the bare batch. The centre binds
    /// <c>ApiRequest&lt;SyncBatchRequest&gt;</c> and refused that shape with
    /// "data is required", and a District node needs the transaction id to
    /// hold and deduplicate it — so no upload the device built could ever have
    /// been accepted. Found the first time the client was pointed at the API.
    /// </summary>
    private byte[] SerializeBatch(Guid transactionId)
        => JsonSerializer.SerializeToUtf8Bytes(
            new ApiRequest<SyncBatchRequest>
            {
                Meta = new RequestMeta { TransactionId = transactionId },
                Data = _outbox.BuildBatch(),
            },
            BodyJson);
}
