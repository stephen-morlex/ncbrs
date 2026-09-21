import { Fragment, useRef } from 'react'
import { Outlet, useLocation } from 'react-router'
import {
  Breadcrumb,
  BreadcrumbItem,
  BreadcrumbLink,
  BreadcrumbList,
  BreadcrumbPage,
  BreadcrumbSeparator,
} from '@/components/ui/breadcrumb'
import { Separator } from '@/components/ui/separator'
import { SidebarInset, SidebarProvider, SidebarTrigger } from '@/components/ui/sidebar'
import { TooltipProvider } from '@/components/ui/tooltip'
import { SessionExpiry } from '@/auth/SessionExpiry'
import { MainContentId, SkipLink, useFocusOnRouteChange, usePageTitle } from '@/shell/a11y'
import { AppSidebar } from '@/shell/AppSidebar'
import { navigation } from '@/shell/navigation'

/**
 * The frame every signed-in page renders inside, following shadcn's
 * sidebar-07 block: inset sidebar, sticky header, breadcrumbs.
 *
 * `TooltipProvider` is required rather than decorative. Collapsed to icons,
 * the sidebar labels each one with a `Tooltip`, and without a provider those
 * throw -- and collapsed is exactly the state a district office on a small
 * screen will be in.
 */
export function AppLayout() {
  const trail = useBreadcrumbs()
  const mainContent = useRef<HTMLDivElement>(null)

  usePageTitle()
  useFocusOnRouteChange(mainContent)

  return (
    <TooltipProvider delayDuration={300}>
      <SidebarProvider>
        {/* First in the tab order, before the sidebar's twenty-odd links. */}
        <SkipLink />
        <AppSidebar />
        <SidebarInset>
          {/* Sticky: on a long review queue the trigger and the trail are
              the way back, and scrolling to the top to find them is friction
              on every single record. */}
          <header className="bg-background sticky top-0 z-10 flex h-16 shrink-0 items-center gap-2 border-b transition-[width,height] ease-linear group-has-data-[collapsible=icon]/sidebar-wrapper:h-12">
            <div className="flex items-center gap-2 px-4">
              <SidebarTrigger className="-ml-1" />
              <Separator orientation="vertical" className="mr-2 data-[orientation=vertical]:h-4" />
              <Breadcrumb>
                <BreadcrumbList>
                  {/* Keyed by position, not by `to`. Both crumbs point at the
                      same destination -- the group and the page within it --
                      so keying on the href gives React two identical keys and
                      it keeps a stale crumb from the previous route. */}
                  {trail.map((crumb, index) => (
                    <Fragment key={`${index}:${crumb.label}`}>
                      {index > 0 ? <BreadcrumbSeparator /> : null}
                      <BreadcrumbItem>
                        {index === trail.length - 1 ? (
                          <BreadcrumbPage>{crumb.label}</BreadcrumbPage>
                        ) : (
                          <BreadcrumbLink href={crumb.to}>{crumb.label}</BreadcrumbLink>
                        )}
                      </BreadcrumbItem>
                    </Fragment>
                  ))}
                </BreadcrumbList>
              </Breadcrumb>
            </div>
          </header>

          {/* tabIndex -1 so it can receive focus from the skip link and from a
              route change without becoming a tab stop of its own. */}
          <div
            id={MainContentId}
            ref={mainContent}
            tabIndex={-1}
            className="flex flex-1 flex-col gap-4 p-4 outline-none md:p-6"
          >
            {/* Above the page, inside the frame: the warning has to be visible
                without scrolling on whichever page the registrar is on when
                the session starts running out. */}
            <SessionExpiry />
            <Outlet />
          </div>
        </SidebarInset>
      </SidebarProvider>
    </TooltipProvider>
  )
}

/**
 * The trail for the current route, read from the same navigation list the
 * sidebar uses.
 *
 * Derived rather than declared per page: a page that forgot to declare its
 * own trail would show none, and a header that is sometimes empty reads as
 * broken rather than as a page without a parent.
 */
function useBreadcrumbs(): { label: string; to: string }[] {
  const { pathname } = useLocation()

  // Longest match wins. `/records/search` is a prefix match for `/records`
  // too, and taking the first hit would label the search page "Find by
  // number" -- naming the wrong act on the page whose whole point is that it
  // is a different one.
  const candidates = navigation
    .flatMap((group) => group.items.map((item) => ({ group: group.label, item })))
    .filter(({ item }) => pathname === item.to || pathname.startsWith(`${item.to}/`))
    .sort((a, b) => b.item.to.length - a.item.to.length)

  const best = candidates[0]

  if (best) {
    return [
      { label: best.group, to: best.item.to },
      { label: best.item.label, to: best.item.to },
    ]
  }

  return [{ label: 'NCBRS', to: '/records' }]
}
