import {
  BadgeCheck,
  BarChart3,
  BellRing,
  FileDown,
  FileSearch,
  GitCompareArrows,
  Hash,
  History,
  Hospital,
  Hourglass,
  ListX,
  MapPinned,
  ScrollText,
  Search,
  ShieldCheck,
  Smartphone,
  Users,
  type LucideIcon,
} from 'lucide-react'
import type { NcbrsPolicy } from '@/auth/roles'
import type common from '@/i18n/locales/en/common.json'

/** A navigation group, named by its key in the `nav.groups` resources. */
export type NavGroupId = keyof typeof common.nav.groups

/** A destination, named by its key in the `nav.items` resources. */
export type NavItemId = keyof typeof common.nav.items

/**
 * The destinations, and who each is for.
 *
 * Every entry names the policy that gates it, so navigation is derived from
 * the same list the route guards use rather than assembled separately. Two
 * lists would drift, and the way they drift is that a link appears for a page
 * that immediately refuses the user -- or, worse, a page the user is entitled
 * to never appears at all and they conclude the system does not do it.
 *
 * `policy: null` means signed in is enough.
 */
export interface NavItem {
  /** The display name is `t(`nav.items.${id}`)`; the id never changes with the language. */
  id: NavItemId
  to: string
  icon: LucideIcon
  policy: NcbrsPolicy | null

  /** Not built yet. Shown, but marked, so the shape of the system is legible. */
  pending?: boolean
}

export interface NavGroup {
  id: NavGroupId
  items: NavItem[]
}

export const navigation: NavGroup[] = [
  {
    id: 'register',
    items: [
      { id: 'findByNumber', to: '/records', icon: Hash, policy: null },

      // A separate destination, not a mode of the lookup above. Looking up a
      // number a family is holding and searching the register by name are
      // different acts under different rules -- the second is confined to
      // the caller's district and recorded in the audit trail. One box that
      // quietly switched between them would hide that difference from the
      // person it applies to.
      { id: 'searchRegister', to: '/records/search', icon: Search, policy: null },
      {
        id: 'registerBirth',
        to: '/records/new',
        icon: ScrollText,
        policy: 'CanRegisterBirths',
      },
    ],
  },
  {
    // Four separate queues rather than one, because they are four different
    // decisions with four different thresholds -- and because a district
    // officer needs to know which kind of backlog they have.
    id: 'review',
    items: [
      {
        id: 'amendments',
        to: '/review/amendments',
        icon: GitCompareArrows,
        policy: 'CanApproveAmendments',
      },
      {
        id: 'duplicates',
        to: '/review/duplicates',
        icon: FileSearch,
        policy: 'CanReviewDuplicates',
      },
      {
        id: 'lateRegistrations',
        to: '/review/late-registrations',
        icon: Hourglass,
        policy: 'CanApproveLateRegistrations',
      },
      {
        id: 'annulments',
        to: '/review/annulments',
        icon: BadgeCheck,
        policy: 'CanAnnulRegistrations',
      },
    ],
  },
  {
    id: 'oversight',
    items: [
      // Not gated: a facility registrar needs to know their own post is
      // running low on numbers, because they are the one who will be handing
      // out provisional slips when it runs out.
      { id: 'facilities', to: '/facilities', icon: Hospital, policy: null },
      // Not gated: the geography is reference data naming no person, and a
      // registrar recording a facility's location needs the same lists an
      // oversight officer browsing the tree does.
      { id: 'administrativeAreas', to: '/admin/areas', icon: MapPinned, policy: null },
      // Listing who is provisioned is an oversight act — the same role that
      // enrols devices — so it names that policy, matching the list endpoint.
      {
        id: 'registrars',
        to: '/registrars',
        icon: Users,
        policy: 'CanEnrolDevices',
      },
      {
        id: 'devices',
        to: '/devices',
        icon: Smartphone,
        policy: 'CanEnrolDevices',
      },
      // The queue of devices gone quiet — a silent post is not a post with no
      // births, and only one of those needs someone to drive out.
      {
        id: 'deviceAlerts',
        to: '/devices/alerts',
        icon: BellRing,
        policy: 'CanEnrolDevices',
      },
      // Not gated: checking a certificate a family presents is something any
      // signed-in officer does, and the verify endpoint is anonymous by
      // design — it is meant to be usable by anyone holding the document.
      { id: 'verifyCertificate', to: '/certificates/verify', icon: ShieldCheck, policy: null },
      // Not gated: the list names no person — every entry is an opaque digest —
      // and the endpoint is anonymous so verifiers can mirror it.
      { id: 'revocationList', to: '/certificates/revocations', icon: ListX, policy: null },
      // Under Oversight rather than beside the register: reading the trail
      // is checking on the work, not doing it.
      {
        id: 'auditTrail',
        to: '/audit',
        icon: History,
        policy: 'CanReadAuditTrail',
      },
      {
        id: 'dashboard',
        to: '/dashboard',
        icon: BarChart3,
        policy: 'CanReadReporting',
      },
      {
        id: 'dhis2Export',
        to: '/exports/dhis2',
        icon: FileDown,
        policy: 'CanReadReporting',
      },
    ],
  },
]
