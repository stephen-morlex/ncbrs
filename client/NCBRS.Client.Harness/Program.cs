using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using NCBRS.Client;
using NCBRS.Client.Auth;
using NCBRS.Client.Brn;
using NCBRS.Client.Certificates;
using NCBRS.Client.Sync;
using NCBRS.Certificates;
using NCBRS.Devices;
using NCBRS.Models;

// A narrated end-to-end walk of the offline-first client-core. It runs the acts
// a village post performs with no connectivity, then the sync when it returns,
// printing what happens at each step. Nothing here is mocked — it drives the
// real FacilityClient and the shared Contracts crypto.

const string deviceId = "TABLET-TEREKEKA-01";
var facilityId = Guid.Parse("0199c000-0000-7000-8000-00000000f004");
var now = DateTime.UtcNow;
var jsonPreview = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

void Section(string title) => Console.WriteLine($"\n=== {title} ===");

// 1. Enrolment (online, once): the device makes its key; only the public half
//    is enrolled, the private half never leaves.
Section("1. Enrol the device (WS-B9)");
var signer = DeviceSigner.Generate();
Console.WriteLine("Generated an ECDSA P-256 device key. Enrolment key acceptable: "
    + DeviceSignature.ValidateEnrolmentKey(signer.PublicKeyPem).Valid);

// 2. Unlock (offline): a wrong PIN is refused and counts down; the right one
//    unlocks with no network.
Section("2. Unlock offline (WS-B3)");
var credential = OfflinePinLock.CreateCredential("2468", iterations: 10_000);
var pinLock = new OfflinePinLock(credential, maxAttempts: 3);
Console.WriteLine($"Wrong PIN: {pinLock.Unlock("0000", now).Outcome} "
    + $"(attempts remaining {pinLock.Unlock("0000", now).AttemptsRemaining})");
Console.WriteLine($"Right PIN: {pinLock.Unlock("2468", now).Outcome}");

// 3. Register offline. A deliberately tiny block (three numbers) shows the
//    low-block warning and the PROV- fallback when it runs dry.
Section("3. Register births offline (WS-B5 + B6)");
var client = new FacilityClient(
    deviceId, facilityId,
    new DeviceBrnAllocator(deviceId, blockStart: 200_000, blockEnd: 200_002),
    new SyncOutbox(deviceId, facilityId),
    signer,
    lowBlockThreshold: 1);

var names = new[] { "Ayen Deng", "Deng Majok", "Aluel Wani", "Nyandeng Lado", "Garang Kenyi" };
var drafts = new List<RegistrationDraft>();
foreach (var name in names)
{
    var draft = client.RegisterBirth(new RegisterBirthRequest { ChildFullName = name, Sex = Sex.Female });
    drafts.Add(draft);
    var flags = (draft.IsProvisional ? " [provisional]" : "") + (draft.BlockLow ? " [block low — top up]" : "");
    Console.WriteLine($"  {name,-16} -> {draft.Brn}{flags}");
}
Console.WriteLine($"Queued in outbox: {client.PendingCount}; block numbers left: {client.BlockRemaining}");

// 3b. The other side of B5: a device that reaches a connectivity window while
//     low fetches the next block and rolls straight over to it, so it never
//     falls back to a provisional identifier.
Section("3b. Top up the block before it runs dry (WS-B5)");
var toppedUp = new FacilityClient(
    deviceId, facilityId,
    new DeviceBrnAllocator(deviceId, blockStart: 300_000, blockEnd: 300_001),
    new SyncOutbox(deviceId, facilityId),
    signer,
    lowBlockThreshold: 1);
toppedUp.RegisterBirth(new RegisterBirthRequest { ChildFullName = "Nyakim Gatluak", Sex = Sex.Female });
Console.WriteLine($"After one birth: needs more numbers? {toppedUp.NeedsMoreNumbers}");
toppedUp.GrantNextBlock(300_100, 300_101); // request-brn-block granted the next range
Console.WriteLine($"Next block staged: needs more numbers? {toppedUp.NeedsMoreNumbers}");
foreach (var name in new[] { "Chol Bol", "Aluel Mayen" }) // drains 300_001, then rolls over
{
    var d = toppedUp.RegisterBirth(new RegisterBirthRequest { ChildFullName = name, Sex = Sex.Female });
    Console.WriteLine($"  {name,-14} -> {d.Brn}{(d.IsProvisional ? " [provisional]" : "")}");
}

// 4. Build the signed upload and confirm the centre would accept the signature.
Section("4. Build the signed upload (WS-B9)");
var upload = client.BuildSignedUpload();
var verified = DeviceSignature.Verify(signer.PublicKeyPem, upload.Body, upload.Signature);
Console.WriteLine($"Body {upload.Body.Length} bytes; header {upload.HeaderName}; signature verifies: {verified.Valid}");

// 5. No-network path: pack a transfer file, open it at a "sync point", and show
//    that a tampered file is refused.
Section("5. Signed offline transfer file (WS-H2)");
var file = client.BuildTransferFile();
var opened = OfflineTransferFile.Open(file, signer.PublicKeyPem);
Console.WriteLine($"Transfer file {file.Length} bytes; opened: {opened.Accepted} from {opened.DeviceId}");
// The body is base64 inside the envelope; corrupt that exact base64 (the same
// bytes signed in step 4) so the signature no longer matches.
var forgedBody = Convert.ToBase64String("{\"records\":[{\"brn\":\"999999\"}]}"u8.ToArray());
var tampered = System.Text.Encoding.UTF8.GetBytes(
    System.Text.Encoding.UTF8.GetString(file).Replace(Convert.ToBase64String(upload.Body), forgedBody));
Console.WriteLine($"Tampered file accepted: {OfflineTransferFile.Open(tampered, signer.PublicKeyPem).Accepted} (must be False)");

// 6. Sync: the centre answers per record. Registered/duplicate settle; a
//    rejection stays queued for attention.
Section("6. Settle the centre's response (WS-B6)");
var response = new SyncBatchResponse(
    Guid.NewGuid(), SyncBatchStatus.Reconciled, drafts.Count,
    Registered: 4, Duplicates: 0, Rejected: 1,
    Records:
    [
        new SyncRecordOutcome(drafts[0].Brn, SyncRecordStatus.Registered, BrnConfirmed: true),
        new SyncRecordOutcome(drafts[1].Brn, SyncRecordStatus.Registered, BrnConfirmed: true),
        new SyncRecordOutcome(drafts[2].Brn, SyncRecordStatus.Registered, BrnConfirmed: true),
        new SyncRecordOutcome(drafts[3].Brn, SyncRecordStatus.Registered, AssignedBrn: "200003", BrnConfirmed: true),
        new SyncRecordOutcome(drafts[4].Brn, SyncRecordStatus.Rejected),
    ]);
var settlement = client.Settle(response);
Console.WriteLine($"Settled {settlement.Settled.Count}, rejected {settlement.Rejected.Count}, still queued {settlement.RemainingCount}");
foreach (var assigned in settlement.Settled.Where(o => o.AssignedBrn is not null))
{
    Console.WriteLine($"  provisional {assigned.Brn} -> permanent {assigned.AssignedBrn}");
}

// 7. Offline certificate verification: no bundle cannot answer; a bundle
//    delegates to the centre's verifier and knows when it must refresh.
Section("7. Offline certificate verification (WS-B8)");
Console.WriteLine($"No bundle yet -> verdict {CachedVerificationBundle.Empty.Verify("any", now).Verdict}, refresh due {CachedVerificationBundle.Empty.RefreshDue(now)}");
// The verifier holds the Ministry's signing *certificate*, so stand up a
// self-signed one to play that role.
using var ministry = ECDsa.Create(ECCurve.NamedCurves.nistP256);
using var ministryCert = new CertificateRequest("CN=NCBRS Ministry", ministry, HashAlgorithmName.SHA256)
    .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
using var bundle = CachedVerificationBundle.From(
    [new VerificationKey("ministry-2026", ministryCert.ExportCertificatePem(), Active: true)], [], now);
Console.WriteLine($"With a bundle, a forged QR -> verdict {bundle.Verify("not.a.real.certificate", now).Verdict}");

Console.WriteLine("\nEnd-to-end run complete.");
