import { useAuth } from 'react-oidc-context'
import { ChevronsUpDown, LogOut, ShieldCheck } from 'lucide-react'
import { Avatar, AvatarFallback } from '@/components/ui/avatar'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import {
  SidebarMenu,
  SidebarMenuButton,
  SidebarMenuItem,
  useSidebar,
} from '@/components/ui/sidebar'
import { displayName, realmRoles } from '@/auth/claims'

/**
 * The signed-in user, in the sidebar footer.
 *
 * Follows shadcn's sidebar-07 block: a large menu button carrying the avatar
 * and a two-line label, opening a dropdown. Adapted in one way that matters
 * — the second line is the account's **roles**, not an email address.
 *
 * A registrar's roles decide what the register will let them do, and the
 * commonest confusion in a system like this is a user being certain they
 * should be able to do something the server refuses. Putting the roles where
 * they are always visible turns "why can't I approve this?" into a question
 * the user can answer themselves.
 */
export function NavUser() {
  const auth = useAuth()
  const { isMobile } = useSidebar()

  const name = displayName(auth.user) ?? 'Signed in'
  const roles = realmRoles(auth.user)
  const rolesLabel = roles.length > 0 ? roles.join(', ') : 'no roles assigned'

  return (
    <SidebarMenu>
      <SidebarMenuItem>
        <DropdownMenu>
          <DropdownMenuTrigger asChild>
            <SidebarMenuButton
              size="lg"
              className="data-[state=open]:bg-sidebar-accent data-[state=open]:text-sidebar-accent-foreground"
            >
              <Avatar className="size-8 rounded-lg">
                <AvatarFallback className="rounded-lg">{initials(name)}</AvatarFallback>
              </Avatar>
              <div className="grid flex-1 text-left text-sm leading-tight">
                <span className="truncate font-medium">{name}</span>
                <span className="text-muted-foreground truncate text-xs">{rolesLabel}</span>
              </div>
              <ChevronsUpDown className="ml-auto size-4" />
            </SidebarMenuButton>
          </DropdownMenuTrigger>

          <DropdownMenuContent
            className="w-56 rounded-lg"
            side={isMobile ? 'bottom' : 'right'}
            align="end"
            sideOffset={4}
          >
            <DropdownMenuLabel className="p-0 font-normal">
              <div className="flex items-center gap-2 px-1 py-1.5 text-left text-sm">
                <Avatar className="size-8 rounded-lg">
                  <AvatarFallback className="rounded-lg">{initials(name)}</AvatarFallback>
                </Avatar>
                <div className="grid flex-1 text-left text-sm leading-tight">
                  <span className="truncate font-medium">{name}</span>
                  <span className="text-muted-foreground truncate text-xs">
                    {auth.user?.profile.preferred_username ?? ''}
                  </span>
                </div>
              </div>
            </DropdownMenuLabel>

            <DropdownMenuSeparator />

            <DropdownMenuLabel className="text-muted-foreground text-xs font-normal">
              <span className="flex items-center gap-1.5">
                <ShieldCheck className="size-3.5" />
                {rolesLabel}
              </span>
            </DropdownMenuLabel>

            <DropdownMenuSeparator />

            {/* Ends the Keycloak session, not just this tab. Clearing local
                state alone would leave the SSO cookie standing and sign the
                next person in as the last one, which on a shared facility
                terminal is the opposite of signing out. */}
            <DropdownMenuItem onSelect={() => void auth.signoutRedirect()}>
              <LogOut />
              Sign out
            </DropdownMenuItem>
          </DropdownMenuContent>
        </DropdownMenu>
      </SidebarMenuItem>
    </SidebarMenu>
  )
}

function initials(name: string): string {
  return (
    name
      .split(/\s+/)
      .filter(Boolean)
      .slice(0, 2)
      .map((part) => part[0]?.toUpperCase() ?? '')
      .join('') || '?'
  )
}
