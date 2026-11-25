import { describe, expect, it } from 'vitest'
import { NcbrsRoles, isCrossFacility, satisfies } from '@/auth/roles'

/**
 * These assert the same decisions the API's policies make. They are a
 * tripwire rather than a proof: if the server widens a policy and this file
 * is not updated, these still pass while the UI quietly hides work the user
 * is allowed to do. What they do catch is this file being edited carelessly.
 */
describe('satisfies', () => {
  it('lets every registering role register a birth, including oversight', () => {
    for (const role of [
      NcbrsRoles.FacilityRegistrar,
      NcbrsRoles.CommunityHealthWorker,
      NcbrsRoles.DistrictOfficer,
      NcbrsRoles.RegistrarGeneral,
    ]) {
      expect(satisfies([role], 'CanRegisterBirths')).toBe(true)
    }
  })

  it('keeps review out of the hands of the people who file the records', () => {
    // A facility registrar adjudicating their own duplicate decides whether
    // a citizen has one legal identity or two.
    for (const policy of [
      'CanReviewDuplicates',
      'CanApproveAmendments',
      'CanApproveLateRegistrations',
    ] as const) {
      expect(satisfies([NcbrsRoles.FacilityRegistrar], policy)).toBe(false)
      expect(satisfies([NcbrsRoles.CommunityHealthWorker], policy)).toBe(false)
      expect(satisfies([NcbrsRoles.DistrictOfficer], policy)).toBe(true)
      expect(satisfies([NcbrsRoles.RegistrarGeneral], policy)).toBe(true)
    }
  })

  it('keeps annulment with the registrar-general, a level above the other reviews', () => {
    // Annulment withdraws a legal identity outright rather than deciding how
    // it reads, so a district officer is not enough.
    expect(satisfies([NcbrsRoles.DistrictOfficer], 'CanAnnulRegistrations')).toBe(false)
    expect(satisfies([NcbrsRoles.RegistrarGeneral], 'CanAnnulRegistrations')).toBe(true)
  })

  it('keeps statistics away from a facility registrar', () => {
    expect(satisfies([NcbrsRoles.FacilityRegistrar], 'CanReadReporting')).toBe(false)
    expect(satisfies([NcbrsRoles.DistrictOfficer], 'CanReadReporting')).toBe(true)
  })

  /** The web plan's role table: national figures and exports are the registrar-general's. */
  it('keeps the national view and the export with the registrar-general', () => {
    for (const policy of ['CanReadNationalReporting', 'CanExportReporting'] as const) {
      expect(satisfies([NcbrsRoles.DistrictOfficer], policy)).toBe(false)
      expect(satisfies([NcbrsRoles.RegistrarGeneral], policy)).toBe(true)
    }
  })

  it('grants nothing to an account with no roles', () => {
    for (const policy of [
      'CanRegisterBirths',
      'CanReviewDuplicates',
      'CanAnnulRegistrations',
      'CanReadReporting',
      'CanReadNationalReporting',
      'CanExportReporting',
    ] as const) {
      expect(satisfies([], policy)).toBe(false)
    }
  })

  it('ignores roles it does not know', () => {
    expect(satisfies(['some-other-realm-role'], 'CanRegisterBirths')).toBe(false)
  })

  it('is satisfied when any one of a user’s roles qualifies', () => {
    expect(satisfies(['unrelated', NcbrsRoles.RegistrarGeneral], 'CanAnnulRegistrations')).toBe(true)
  })
})


/** Decisions D1 and D2 (plan §17 item 29). */
describe('the role split', () => {
  it('keeps certificates from community health workers, who record births but do not certify them', () => {
    expect(satisfies([NcbrsRoles.CommunityHealthWorker], 'CanRegisterBirths')).toBe(true)
    expect(satisfies([NcbrsRoles.CommunityHealthWorker], 'CanIssueCertificates')).toBe(false)
    expect(satisfies([NcbrsRoles.FacilityRegistrar], 'CanIssueCertificates')).toBe(true)
  })

  it('gives the system admin the system and no record', () => {
    for (const policy of ['CanManageFacilities', 'CanManageRegistrars', 'CanEnrolDevices'] as const) {
      expect(satisfies([NcbrsRoles.SystemAdmin], policy)).toBe(true)
      expect(satisfies([NcbrsRoles.RegistrarGeneral], policy)).toBe(false)
    }

    for (const policy of [
      'CanReadRecords',
      'CanRegisterBirths',
      'CanAnnulRegistrations',
      'CanReadAuditTrail',
      'CanReadReporting',
    ] as const) {
      expect(satisfies([NcbrsRoles.SystemAdmin], policy)).toBe(false)
    }
  })
})
describe('isCrossFacility', () => {
  it('is true only for the roles that oversee more than one facility', () => {
    expect(isCrossFacility([NcbrsRoles.DistrictOfficer])).toBe(true)
    expect(isCrossFacility([NcbrsRoles.RegistrarGeneral])).toBe(true)
    expect(isCrossFacility([NcbrsRoles.SystemAdmin])).toBe(true)
    expect(isCrossFacility([NcbrsRoles.FacilityRegistrar])).toBe(false)
    expect(isCrossFacility([NcbrsRoles.CommunityHealthWorker])).toBe(false)
    expect(isCrossFacility([])).toBe(false)
  })
})
