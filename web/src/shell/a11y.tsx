import { type RefObject, useEffect, useRef } from 'react'
import { useLocation } from 'react-router'
import { useTranslation } from 'react-i18next'
import { type NavItem, navigation } from '@/shell/navigation'

/** Where the skip link lands, and what route changes move focus to. */
export const MainContentId = 'main-content'

/**
 * WCAG 2.2 2.4.1 (Bypass Blocks, Level A).
 *
 * Every page renders the sidebar — around twenty links — before its content,
 * so without this a keyboard user tabs through the whole navigation on every
 * single page to reach the thing they came for. Visually hidden until focused,
 * which is the point: it is the first thing a keyboard user reaches and
 * invisible to everyone else.
 */
export function SkipLink() {
  const { t } = useTranslation()

  return (
    <a
      href={`#${MainContentId}`}
      className="bg-background ring-ring sr-only focus:not-sr-only focus:fixed focus:top-3 focus:left-3 focus:z-50 focus:rounded-md focus:px-4 focus:py-2 focus:text-sm focus:font-medium focus:shadow-md focus:ring-2 focus:outline-none"
    >
      {t('a11y.skipToContent')}
    </a>
  )
}

/**
 * The destination for the current route, matched the way the breadcrumbs
 * match: longest first, so `/records/search` is the search page rather than
 * being claimed by the `/records` prefix. Returns the item, not its words —
 * the words depend on the language.
 */
export function navItemFor(pathname: string): NavItem | null {
  const matches = navigation
    .flatMap((group) => group.items)
    .filter((item) => pathname === item.to || pathname.startsWith(`${item.to}/`))
    .sort((a, b) => b.to.length - a.to.length)

  return matches[0] ?? null
}

/**
 * WCAG 2.2 2.4.2 (Page Titled, Level A).
 *
 * A single-page app never reloads, so without this every route keeps whatever
 * title the document was served with. A screen reader announces the title on
 * navigation, and the browser's history and tab list are how anyone finds their
 * way back — all three say the same thing for every page until this runs.
 */
export function usePageTitle() {
  const { pathname } = useLocation()
  const { t } = useTranslation()
  const item = navItemFor(pathname)

  const app = t('app.name')
  const title = item ? t('app.pageTitle', { page: t(`nav.items.${item.id}`), app }) : app

  useEffect(() => {
    document.title = title
  }, [title])
}

/**
 * Moves focus to the main region when the route changes.
 *
 * Clicking a navigation link in a single-page app leaves focus on the link and
 * the new page unannounced: a screen reader user hears nothing, and a keyboard
 * user's next Tab continues through the sidebar rather than into the content
 * they just asked for.
 *
 * Deliberately skipped on first paint — focus on load belongs to the browser,
 * and stealing it would fight the address bar and any in-page anchor.
 */
export function useFocusOnRouteChange(target: RefObject<HTMLElement | null>) {
  const { pathname } = useLocation()
  const firstPaint = useRef(true)

  useEffect(() => {
    if (firstPaint.current) {
      firstPaint.current = false
      return
    }

    target.current?.focus()
  }, [pathname, target])
}
