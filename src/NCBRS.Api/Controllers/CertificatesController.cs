using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NCBRS.Middleware;
using NCBRS.Models;
using NCBRS.Services;

namespace NCBRS.Controllers;

[ApiController]
[Route("api/BirthRecords/{brn}/certificate")]
[Authorize(Policy = NcbrsRoles.CanRegisterBirths)]
[Produces("application/json")]
public class CertificatesController(
    CertificateService certificates,
    CurrentRegistrarService currentRegistrar,
    DeviceChannelGate channelGate) : ControllerBase
{
    /// <summary>
    /// Issues the birth certificate for a registered live birth, signing it
    /// with the Ministry's X.509 key.
    /// </summary>
    [HttpPost(Name = "IssueCertificate")]
    [ProducesResponseType(typeof(CertificateResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CertificateResponse>> Issue(
        string brn,
        ApiRequest<IssueCertificateRequest> envelope)
    {
        var registrar = await currentRegistrar.GetAsync(HttpContext.RequestAborted);
        if (registrar is null)
        {
            return NotProvisioned();
        }

        var refused = await channelGate.RefuseUnlessPermittedForRecordAsync(
            HttpContext, registrar, envelope.Data.DeviceId, brn);
        if (refused is not null)
        {
            return refused;
        }

        var result = await certificates.IssueAsync(
            brn,
            registrar,
            envelope.Data.DeviceId,
            TransactionContext.Get(HttpContext)?.TransactionId,
            HttpContext.RequestAborted);

        return result.Succeeded
            ? StatusCode(StatusCodes.Status201Created, result.Response)
            : MapFailure(result);
    }

    [HttpGet(Name = "GetCertificate")]
    [ProducesResponseType(typeof(CertificateResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CertificateResponse>> Get(string brn)
    {
        var certificate = await certificates.GetAsync(brn, HttpContext.RequestAborted);

        return certificate is null
            ? ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status404NotFound, "Certificate not found.",
                "brn", $"No certificate has been issued for BRN '{brn}'."))
            : certificate;
    }

    /// <summary>
    /// Reprints an already-issued certificate. Separate from issuing on
    /// purpose: reprints are counted and audited, because a certificate
    /// reprinted repeatedly is worth being able to notice.
    /// </summary>
    [HttpPost("reprint", Name = "ReprintCertificate")]
    [ProducesResponseType(typeof(CertificateResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CertificateResponse>> Reprint(
        string brn,
        ApiRequest<IssueCertificateRequest> envelope)
    {
        var registrar = await currentRegistrar.GetAsync(HttpContext.RequestAborted);
        if (registrar is null)
        {
            return NotProvisioned();
        }

        var refused = await channelGate.RefuseUnlessPermittedForRecordAsync(
            HttpContext, registrar, envelope.Data.DeviceId, brn);
        if (refused is not null)
        {
            return refused;
        }

        var result = await certificates.ReprintAsync(
            brn,
            registrar,
            envelope.Data.DeviceId,
            TransactionContext.Get(HttpContext)?.TransactionId,
            HttpContext.RequestAborted);

        return result.Succeeded ? result.Response! : MapFailure(result);
    }

    private static ObjectResult NotProvisioned()
        => ApiErrors.Result(ApiErrors.Single(
            StatusCodes.Status403Forbidden, "Account not provisioned.",
            "registrar", "This account is not linked to a registrar in the registry."));

    private static ObjectResult MapFailure(CertificateOutcome result)
        => result.Result switch
        {
            CertificateResult.BirthRecordNotFound => ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status404NotFound, "Not found.", "brn", result.Detail!)),

            CertificateResult.AlreadyIssued => ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status409Conflict, "Certificate already issued.", "brn", result.Detail!)),

            CertificateResult.NotPermitted => ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status403Forbidden, "Not permitted for this facility.", "brn", result.Detail!)),

            // A state conflict, not a malformed request: the caller asked a
            // reasonable thing at a moment the register cannot answer it.
            // Retrying after verification is exactly the right response.
            CertificateResult.LateRegistrationNotVerified => ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status409Conflict, "Late registration not verified.", "brn", result.Detail!)),

            CertificateResult.RecordAnnulled => ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status409Conflict, "Registration annulled.", "brn", result.Detail!)),

            CertificateResult.AwaitingBrnReconciliation => ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status409Conflict, "Awaiting BRN reconciliation.", "brn", result.Detail!)),

            _ => ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status400BadRequest, "Not certifiable.", "brn", result.Detail!))
        };
}

/// <summary>
/// Checking a certificate someone is holding.
///
/// All three endpoints are deliberately anonymous. A birth certificate is
/// presented to banks, schools and employers who have no account here, and
/// the whole point of signing it is that they can check it themselves. A
/// revocation list behind a login would reach none of them, which would make
/// it worthless.
///
/// None of them can be used to look up citizens. The facts returned come
/// from the signed payload the caller already holds; the only registry read
/// is a revocation lookup keyed by a digest of that payload's signature,
/// which cannot be produced without a genuine certificate to ask about.
/// </summary>
[ApiController]
[Route("api/certificates")]
[AllowAnonymous]
[Produces("application/json")]
public class CertificateVerificationController(
    CertificateSigner signer,
    CertificateRevocationService revocations) : ControllerBase
{
    /// <summary>
    /// Checks a scanned certificate: the signature, then the revocation
    /// list. A signature alone only proves the document was once issued --
    /// it cannot know the register was corrected afterwards.
    /// </summary>
    [HttpPost("verify", Name = "VerifyCertificate")]
    [ProducesResponseType(typeof(VerifyCertificateResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<VerifyCertificateResponse>> Verify(
        ApiRequest<VerifyCertificateRequest> envelope)
        => await revocations.VerifyAsync(envelope.Data.QrPayload, HttpContext.RequestAborted);

    /// <summary>
    /// The signed revocation list, for verifiers that check offline.
    ///
    /// Pass <c>since</c> (the IssuedAtUtc of a copy already held) to fetch
    /// only what changed after it -- a village device on a metered link
    /// should not re-download the national list to learn nothing changed.
    /// The window is inside the signature, so a delta cannot be replayed as
    /// a complete list.
    ///
    /// Callers must honour NextUpdateUtc: past it, a certificate absent from
    /// the cached list is unknown, not valid. A verifier that treats a stale
    /// list as authoritative has re-created the exact hole this closes.
    ///
    /// Safe to serve anonymously, and to cache or mirror: every entry is an
    /// opaque digest, so the list names no child, no BRN and no facility.
    /// </summary>
    [HttpGet("revocations", Name = "GetCertificateRevocations")]
    [ProducesResponseType(typeof(CertificateRevocationList), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CertificateRevocationList>> GetRevocations(
        [FromQuery] DateTime? since = null)
    {
        if (since is { } from && from.ToUniversalTime() > DateTime.UtcNow)
        {
            return ApiErrors.Result(ApiErrors.Single(
                StatusCodes.Status400BadRequest, "Invalid window.",
                "since", "since cannot be in the future."));
        }

        return await revocations.BuildAsync(since?.ToUniversalTime(), HttpContext.RequestAborted);
    }

    /// <summary>
    /// Everything a device needs to verify certificates offline: the public
    /// key and the current revocation list, in one fetch.
    ///
    /// This is what a facility tablet pulls whenever it has connectivity,
    /// alongside its BRN block. Two separate calls would let a device end up
    /// holding a list it has no key for, or a key newer than its list --
    /// and it would find out only once it was already offline.
    /// </summary>
    [HttpGet("offline-bundle", Name = "GetOfflineVerificationBundle")]
    [ProducesResponseType(typeof(OfflineVerificationBundle), StatusCodes.Status200OK)]
    public async Task<ActionResult<OfflineVerificationBundle>> OfflineBundle()
        => new OfflineVerificationBundle(
            signer.KeyId,
            "ECDSA-P256-SHA256",
            signer.PublicKeyPem(),
            await revocations.BuildAsync(cancellationToken: HttpContext.RequestAborted),
            Keys(signer));

    /// <summary>
    /// The public half of the signing key, so a verifier can be provisioned
    /// once and then check certificates entirely offline -- which is the
    /// situation a rural district office is usually in.
    /// </summary>
    [HttpGet("signing-key", Name = "GetCertificateSigningKeys")]
    [ProducesResponseType(typeof(SigningKeyResponse), StatusCodes.Status200OK)]
    public ActionResult<SigningKeyResponse> SigningKey()
        => new SigningKeyResponse(
            signer.KeyId, "ECDSA-P256-SHA256", signer.PublicKeyPem(), Keys(signer));

    /// <summary>
    /// The whole key set, current first. Retired keys are published because a
    /// certificate signed under one is still genuine -- rotation says nothing
    /// about the birth it certifies.
    /// </summary>
    private static IReadOnlyList<VerificationKeyResponse> Keys(CertificateSigner signer)
        => [.. signer.VerificationKeys()
            .OrderByDescending(key => key.Active)
            .Select(key => new VerificationKeyResponse(key.KeyId, key.CertificatePem, key.Active))];
}
