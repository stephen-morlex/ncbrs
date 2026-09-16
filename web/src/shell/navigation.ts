import {
  BadgeCheck,
  BarChart3,
  FileSearch,
  GitCompareArrows,
  Hourglass,
  ScrollText,
  Search,
  Smartphone,
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
      { label: 'Find a record', to: '/records', icon: Search, policy: null },
      {
        label: 'Register a birth',
        to: '/records/new',
        icon: ScrollText,
        policy: 'CanRegisterBirths',
        pending: true,
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
        pending: true,
      },
      {
        label: 'Duplicates',
        to: '/review/duplicates',
        icon: FileSearch,
        policy: 'CanReviewDuplicates',
        pending: true,
      },
      {
        label: 'Late registrations',
        to: '/review/late-registrations',
        icon: Hourglass,
        policy: 'CanApproveLateRegistrations',
        pending: true,
      },
      {
        label: 'Annulments',
        to: '/review/annulments',
        icon: BadgeCheck,
        policy: 'CanAnnulRegistrations',
        pending: true,
      },
    ],
  },
  {
    label: 'Oversight',
    items: [
      {
        label: 'Devices',
        to: '/devices',
        icon: Smartphone,
        policy: 'CanEnrolDevices',
        pending: true,
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
