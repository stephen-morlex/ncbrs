import { NavLink } from 'react-router'
import { useTranslation } from 'react-i18next'
import { useAuth } from 'react-oidc-context'
import { BookMarked } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import {
  Sidebar,
  SidebarContent,
  SidebarFooter,
  SidebarGroup,
  SidebarGroupContent,
  SidebarGroupLabel,
  SidebarHeader,
  SidebarMenu,
  SidebarMenuButton,
  SidebarMenuItem,
  SidebarRail,
} from '@/components/ui/sidebar'
import { realmRoles } from '@/auth/claims'
import { satisfies } from '@/auth/roles'
import { NavUser } from '@/shell/NavUser'
import { navigation } from '@/shell/navigation'

/**
 * Navigation, filtered to what this account may actually reach.
 *
 * "So a facility registrar is not shown four empty review queues" -- the
 * plan's own words, and the reason entries are removed rather than disabled.
 * A greyed-out queue still advertises work the registrar cannot do and
 * invites them to ask why; an absent one simply is not their job.
 *
 * Filtering is not enforcement. The route guards refuse the page and the API
 * refuses the call; this only decides what is worth offering.
 *
 * Laid out after shadcn's sidebar-07 block: branded header, grouped menu,
 * user dropdown in the footer, and a rail so it collapses to icons on a
 * narrow screen -- which a district office mini-PC often is.
 */
export function AppSidebar() {
  const auth = useAuth()
  const { t } = useTranslation()
  const roles = realmRoles(auth.user)

  const groups = navigation
    .map((group) => ({
      ...group,
      items: group.items.filter((item) => item.policy === null || satisfies(roles, item.policy)),
    }))
    // A group whose every item was filtered out leaves a heading over
    // nothing, which reads as a section that failed to load.
    .filter((group) => group.items.length > 0)

  return (
    <Sidebar collapsible="icon" variant="inset">
      <SidebarHeader>
        <SidebarMenu>
          <SidebarMenuItem>
            <SidebarMenuButton size="lg" asChild>
              <NavLink to="/records">
                <div className="bg-sidebar-primary text-sidebar-primary-foreground flex aspect-square size-8 items-center justify-center rounded-lg">
                  <BookMarked className="size-4" />
                </div>
                <div className="grid flex-1 text-left text-sm leading-tight">
                  <span className="truncate font-semibold">{t('app.name')}</span>
                  <span className="truncate text-xs">{t('app.subtitle')}</span>
                </div>
              </NavLink>
            </SidebarMenuButton>
          </SidebarMenuItem>
        </SidebarMenu>
      </SidebarHeader>

      <SidebarContent>
        {groups.map((group) => (
          <SidebarGroup key={group.id}>
            <SidebarGroupLabel>{t(`nav.groups.${group.id}`)}</SidebarGroupLabel>
            <SidebarGroupContent>
              <SidebarMenu>
                {group.items.map((item) => (
                  <SidebarMenuItem key={item.to}>
                    <NavLinkMenuButton item={item} />
                  </SidebarMenuItem>
                ))}
              </SidebarMenu>
            </SidebarGroupContent>
          </SidebarGroup>
        ))}
      </SidebarContent>

      <SidebarFooter>
        <NavUser />
      </SidebarFooter>

      {/* Drag handle for collapsing. Without it the only way to collapse is
          the header trigger, which is off-screen once you have scrolled. */}
      <SidebarRail />
    </Sidebar>
  )
}

function NavLinkMenuButton({ item }: { item: (typeof navigation)[number]['items'][number] }) {
  const { t } = useTranslation()
  const label = t(`nav.items.${item.id}`)

  return (
    <NavLink to={item.to} end={item.to === '/records'}>
      {({ isActive }) => (
        <SidebarMenuButton asChild isActive={isActive} tooltip={label}>
          <span>
            <item.icon />
            <span className="flex-1 truncate">{label}</span>
            {item.pending ? (
              <Badge
                variant="outline"
                className="ml-auto px-1 text-[10px] group-data-[collapsible=icon]:hidden"
              >
                {t('nav.soon')}
              </Badge>
            ) : null}
          </span>
        </SidebarMenuButton>
      )}
    </NavLink>
  )
}
