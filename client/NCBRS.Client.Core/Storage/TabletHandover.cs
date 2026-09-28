using NCBRS.Client.Network;
using NCBRS.Client.Sync;
using NCBRS.Models;

namespace NCBRS.Client.Storage;

/// <summary>What the centre's record of a device says about enrolling it here.</summary>
public enum EnrolmentVerdict
{
    /// <summary>Active, at the facility the officer chose. The only outcome that counts as enrolled.</summary>
    Enrolled,
    /// <summary>Active, but at another facility.</summary>
    EnrolledElsewhere,
    /// <summary>Withdrawn for good; the id can never be used again.</summary>
    Revoked,
    /// <summary>Withdrawn for now; a district officer can reinstate it.</summary>
    Suspended,
    /// <summary>The centre has no record of it.</summary>
    Unknown,
}

public sealed record HandoverResult(bool Enrolled, string? Problem = null, bool Rekeyed = false);

/// <summary>
/// Handing a tablet over, checked against the centre at every step rather than
/// inferred from a status code. A 409 says only that an id exists — enrolled
/// here, enrolled elsewhere, suspended or revoked — so after enrolling, the
/// device is read back with the officer's token and only an active enrolment
/// at the chosen facility counts.
///
/// An id the centre will not take back — revoked, or live at another facility —
/// is replaced, not argued with: a new key, so a new id, enrolled fresh, with
/// the old one revoked first if it was still live. Both acts stay in the
/// centre's trail. Every change of key is saved before it is sent, so an
/// interrupted handover retries as the device it last became.
/// </summary>
public static class TabletHandover
{
    public static EnrolmentVerdict Judge(DeviceResponse? device, Guid facilityId) => device switch
    {
        null => EnrolmentVerdict.Unknown,
        { Status: DeviceStatus.Revoked } => EnrolmentVerdict.Revoked,
        { Status: DeviceStatus.Suspended } => EnrolmentVerdict.Suspended,
        _ when device.FacilityId != facilityId => EnrolmentVerdict.EnrolledElsewhere,
        _ => EnrolmentVerdict.Enrolled,
    };

    public static async Task<HandoverResult> EnrolAsync(
        CentralClient asOfficer,
        DeviceState state,
        FacilitySummary facility,
        Uri centre,
        Uri realm,
        Uri? syncVia,
        string? label,
        Func<Task> save,
        CancellationToken cancellationToken = default)
    {
        var rekeyed = false;

        // Twice at most: as the device it is, then, if the centre will not take
        // that id back, once more under a new key.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (state.DevicePrivateKeyPem is null)
            {
                state.DevicePrivateKeyPem = DeviceSigner.Generate().ExportPrivateKeyPem();
                await save();
            }

            var signer = DeviceSigner.FromPrivateKey(state.DevicePrivateKeyPem);
            var deviceId = DeviceIdentity.IdFor(signer.PublicKeyPem);

            var enrolled = await asOfficer.EnrolDeviceAsync(new EnrolDeviceRequest
            {
                DeviceId = deviceId,
                FacilityId = facility.FacilityId,
                PublicKeyPem = signer.PublicKeyPem,
                Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim(),
            }, cancellationToken);

            if (!enrolled.Succeeded && enrolled.StatusCode != 409)
            {
                return new HandoverResult(false, Describe("The tablet was not enrolled", enrolled));
            }

            // Read back, never inferred: what the centre now holds for this id.
            var record = await asOfficer.GetDeviceAsync(deviceId, cancellationToken);
            if (!record.Succeeded)
            {
                return new HandoverResult(false, Describe("The enrolment could not be confirmed", record));
            }

            switch (Judge(record.Value, facility.FacilityId))
            {
                case EnrolmentVerdict.Enrolled:
                    state.Identity = new DeviceIdentity(deviceId, facility.FacilityId, centre, syncVia, realm, facility.Name);
                    await save();
                    return new HandoverResult(true, Rekeyed: rekeyed);

                case EnrolmentVerdict.Suspended:
                    return new HandoverResult(false,
                        $"This tablet ({deviceId}) is suspended at the registry: {record.Value!.StatusReason}. "
                        + "A district officer reinstates it from the web app, or revokes it so it can be handed over again.");

                case EnrolmentVerdict.EnrolledElsewhere:
                    var revoked = await asOfficer.RevokeDeviceAsync(deviceId,
                        $"Handed over again, to {facility.Name}; replaced by a new key.", cancellationToken);
                    if (!revoked.Succeeded)
                    {
                        return new HandoverResult(false, Describe("The tablet's enrolment elsewhere could not be withdrawn", revoked));
                    }

                    break;

                case EnrolmentVerdict.Revoked:
                case EnrolmentVerdict.Unknown:
                    break;
            }

            // The centre will not take this id back: a new key, so a new id.
            state.DevicePrivateKeyPem = DeviceSigner.Generate().ExportPrivateKeyPem();
            state.Identity = null;
            await save();
            rekeyed = true;
        }

        return new HandoverResult(false, "The tablet could not be enrolled even under a new key. Contact the district office.");
    }

    /// <summary>
    /// Before handing over again: withdraw this device at the centre, and
    /// confirm it is withdrawn. Only then may the tablet discard its key.
    /// </summary>
    public static async Task<string?> RevokeForHandoverAsync(
        CentralClient asOfficer, DeviceIdentity identity, CancellationToken cancellationToken = default)
    {
        var revoked = await asOfficer.RevokeDeviceAsync(identity.DeviceId,
            $"Handed over to the wrong facility ({identity.FacilityName ?? identity.FacilityId.ToString()}); to be handed over again under a new key.",
            cancellationToken);

        if (revoked.Succeeded)
        {
            return null;
        }

        // 409 may be "revoked already" — or something else. Read it back.
        var record = await asOfficer.GetDeviceAsync(identity.DeviceId, cancellationToken);
        return record.Value is { Status: DeviceStatus.Revoked }
            ? null
            : Describe("The tablet was not withdrawn, so it cannot be handed over again", revoked);
    }

    /// <summary>
    /// Straight after a registrar signs in: may this account act for the
    /// tablet's facility? Asked by fetching the staff PINs, which the centre
    /// only gives an account that can. Refused, the sign-in must be ended at
    /// once — kept, it would sit on a tablet it can never use.
    /// </summary>
    public static async Task<AccountCheck> CheckAccountAsync(
        CentralClient asRegistrar, DeviceState state, Func<Task> save, CancellationToken cancellationToken = default)
    {
        var identity = state.Identity!;
        var staff = await asRegistrar.FetchStaffCredentialsAsync(identity.FacilityId, identity.DeviceId, cancellationToken);
        var facility = identity.FacilityName ?? identity.FacilityId.ToString();

        if (staff.Value is { } bundle)
        {
            StaffUnlock.Provision(state, bundle);
            await save();
            return new AccountCheck(true);
        }

        if (staff.RefusedTheAccountHere)
        {
            return new AccountCheck(false, EndSignIn: true,
                Problem: $"This account is not permitted at {facility}, which this tablet is enrolled to. "
                         + $"Sign in with an account from {facility}. If the tablet was handed to the wrong facility, "
                         + "a district officer hands it over again.");
        }

        if (staff.RefusedTheDevice)
        {
            return new AccountCheck(false,
                Problem: $"The registry does not accept this tablet: {Detail(staff)}. A district officer must hand it over again.");
        }

        if (staff.Outcome == CentralOutcome.Unauthorized)
        {
            return new AccountCheck(false, EndSignIn: true, Problem: "The registry did not accept the sign-in. Sign in again.");
        }

        // No signal is not a refusal: the account is checked again next window.
        return new AccountCheck(true, Problem: staff.Outcome == CentralOutcome.Unreachable
            ? "Signed in, but the registry could not be reached to check the account. It is checked again when there is signal."
            : Describe("The account could not be checked", staff));
    }

    private static string Detail<T>(CentralResult<T> result)
        => result.Errors is { Count: > 0 } errors ? string.Join("; ", errors.Select(error => error.Message)) : result.Detail ?? result.Outcome.ToString();

    private static string Describe<T>(string what, CentralResult<T> result) => $"{what}: {Detail(result)}";
}

/// <summary>Whether a signed-in account may use this tablet, and whether its sign-in must be ended.</summary>
public sealed record AccountCheck(bool Permitted, string? Problem = null, bool EndSignIn = false);
