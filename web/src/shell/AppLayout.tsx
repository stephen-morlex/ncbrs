import { Fragment } from 'react'
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

  return (
    <TooltipProvider delayDuration={300}>
      <SidebarProvider>
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
                  {trail.map((crumb, index) => (
                    <Fragment key={crumb.to}>
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

          <div className="flex flex-1 flex-col gap-4 p-4 md:p-6">
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

  for (const group of navigation) {
    const match = group.items.find((item) => pathname === item.to || pathname.startsWith(`${item.to}/`))

    if (match) {
      return [
        { label: group.label, to: match.to },
        { label: match.label, to: match.to },
      ]
    }
  }

  return [{ label: 'NCBRS', to: '/records' }]
}
