import {
  BadgeCheck,
  BarChart3,
  FileSearch,
  GitCompareArrows,
  Hash,
  History,
  Hospital,
  Hourglass,
  ScrollText,
  Search,
  ShieldCheck,
  Smartphone,
  Users,
  type LucideIcon,
} from 'lucide-react'
import type { NcbrsPolicy } from '@/auth/roles'

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
  label: string
  to: string
  icon: LucideIcon
  policy: NcbrsPolicy | null

  /** Not built yet. Shown, but marked, so the shape of the system is legible. */
  pending?: boolean
}

export interface NavGroup {
  label: string
  items: NavItem[]
}

export const navigation: NavGroup[] = [
  {
    label: 'Register',
    items: [
      { label: 'Find by number', to: '/records', icon: Hash, policy: null },

      // A separate destination, not a mode of the lookup above. Looking up a
      // number a family is holding and searching the register by name are
      // different acts under different rules -- the second is confined to
      // the caller's district and recorded in the audit trail. One box that
      // quietly switched between them would hide that difference from the
      // person it applies to.
      { label: 'Search the register', to: '/records/search', icon: Search, policy: null },
      {
        label: 'Register a birth',
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
    label: 'Review',
    items: [
      {
        label: 'Amendments',
        to: '/review/amendments',
        icon: GitCompareArrows,
        policy: 'CanApproveAmendments',
      },
      {
        label: 'Duplicates',
        to: '/review/duplicates',
        icon: FileSearch,
        policy: 'CanReviewDuplicates',
      },
      {
        label: 'Late registrations',
        to: '/review/late-registrations',
        icon: Hourglass,
        policy: 'CanApproveLateRegistrations',
      },
      {
        label: 'Annulments',
        to: '/review/annulments',
        icon: BadgeCheck,
        policy: 'CanAnnulRegistrations',
      },
    ],
  },
  {
    label: 'Oversight',
    items: [
      // Not gated: a facility registrar needs to know their own post is
      // running low on numbers, because they are the one who will be handing
      // out provisional slips when it runs out.
      { label: 'Facilities', to: '/facilities', icon: Hospital, policy: null },
      // Listing who is provisioned is an oversight act — the same role that
      // enrols devices — so it names that policy, matching the list endpoint.
      {
        label: 'Registrars',
        to: '/registrars',
        icon: Users,
        policy: 'CanEnrolDevices',
      },
      {
        label: 'Devices',
        to: '/devices',
        icon: Smartphone,
        policy: 'CanEnrolDevices',
        pending: true,
      },
      // Not gated: checking a certificate a family presents is something any
      // signed-in officer does, and the verify endpoint is anonymous by
      // design — it is meant to be usable by anyone holding the document.
      { label: 'Verify a certificate', to: '/certificates/verify', icon: ShieldCheck, policy: null },
      // Under Oversight rather than beside the register: reading the trail
      // is checking on the work, not doing it.
      {
        label: 'Audit trail',
        to: '/audit',
        icon: History,
        policy: 'CanReadAuditTrail',
      },
      {
        label: 'Dashboard',
        to: '/dashboard',
        icon: BarChart3,
        policy: 'CanReadReporting',
        pending: true,
      },
    ],
  },
]
