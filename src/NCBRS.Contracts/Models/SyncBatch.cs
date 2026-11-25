namespace NCBRS.Models;

public enum SyncBatchStatus
{
    Pending,
    Processing,
    Reconciled,
    Failed
}

/// <summary>
/// One device's outbox transmission -- a batch of offline-created records
/// pushed to the district/central tier once connectivity is available.
/// Section 6.3 of the NCBRS draft: sync is transactional per record, so a
/// partial sync never leaves an orphaned certificate.
/// </summary>
public class SyncBatch
{
    public Guid SyncBatchId { get; set; } = Guid.CreateVersion7();

    public required string DeviceId { get; set; }

    public Guid FacilityId { get; set; }
    public Facility? Facility { get; set; }

    /// <summary>
    /// Who pushed the batch, as distinct from who authored each record in
    /// it. Both are kept: the uploader is verified by their token, the
    /// per-record author is claimed by the device, and conflating them would
    /// hide which of the two a given attribution rests on.
    /// </summary>
    public Guid? UploadedByRegistrarId { get; set; }

    public DateTime SubmittedAtUtc { get; set; }

    public int RecordCount { get; set; }

    public SyncBatchStatus Status { get; set; } = SyncBatchStatus.Pending;

    /// <summary>
    /// What became of the batch's records, kept on the batch (plan §17 item
    /// 35). The refusal rate is how a district sees a post whose births keep
    /// bouncing, and until this it existed only in the event stream. Null on
    /// batches processed before the counts were kept: "not recorded", never
    /// zero, which would claim those batches went cleanly.
    /// </summary>
    public int? RegisteredCount { get; set; }

    public int? DuplicateCount { get; set; }

    public int? RejectedCount { get; set; }
}
