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
      NcbrsRoles.MinistryAdmin,
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
      expect(satisfies([NcbrsRoles.MinistryAdmin], policy)).toBe(true)
    }
  })

  it('keeps annulment at the ministry, a level above the other reviews', () => {
    // Annulment withdraws a legal identity outright rather than deciding how
    // it reads, so a district officer is not enough.
    expect(satisfies([NcbrsRoles.DistrictOfficer], 'CanAnnulRegistrations')).toBe(false)
    expect(satisfies([NcbrsRoles.MinistryAdmin], 'CanAnnulRegistrations')).toBe(true)
  })

  it('keeps national statistics away from a facility registrar', () => {
    expect(satisfies([NcbrsRoles.FacilityRegistrar], 'CanReadReporting')).toBe(false)
    expect(satisfies([NcbrsRoles.DistrictOfficer], 'CanReadReporting')).toBe(true)
  })

  it('grants nothing to an account with no roles', () => {
    for (const policy of [
      'CanRegisterBirths',
      'CanReviewDuplicates',
      'CanAnnulRegistrations',
      'CanReadReporting',
    ] as const) {
      expect(satisfies([], policy)).toBe(false)
    }
  })

  it('ignores roles it does not know', () => {
    expect(satisfies(['some-other-realm-role'], 'CanRegisterBirths')).toBe(false)
  })

  it('is satisfied when any one of a user’s roles qualifies', () => {
    expect(satisfies(['unrelated', NcbrsRoles.MinistryAdmin], 'CanAnnulRegistrations')).toBe(true)
  })
})

describe('isCrossFacility', () => {
  it('is true only for the roles that oversee more than one facility', () => {
    expect(isCrossFacility([NcbrsRoles.DistrictOfficer])).toBe(true)
    expect(isCrossFacility([NcbrsRoles.MinistryAdmin])).toBe(true)
    expect(isCrossFacility([NcbrsRoles.FacilityRegistrar])).toBe(false)
    expect(isCrossFacility([NcbrsRoles.CommunityHealthWorker])).toBe(false)
    expect(isCrossFacility([])).toBe(false)
  })
})
