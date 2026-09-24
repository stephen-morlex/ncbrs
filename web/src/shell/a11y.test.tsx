import { useRef } from 'react'
import { MemoryRouter, useNavigate } from 'react-router'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import {
  MainContentId,
  SkipLink,
  navItemFor,
  useFocusOnRouteChange,
  usePageTitle,
} from '@/shell/a11y'

describe('SkipLink', () => {
  it('offers a link straight to the main content', () => {
    render(<SkipLink />)

    const link = screen.getByRole('link', { name: /skip to main content/i })
    expect(link).toHaveAttribute('href', `#${MainContentId}`)
  })

  /**
   * Hidden until focused is the whole design: it must be the first thing a
   * keyboard user reaches and invisible to everyone else.
   */
  it('is visually hidden until it takes focus', () => {
    render(<SkipLink />)

    const link = screen.getByRole('link', { name: /skip to main content/i })
    expect(link.className).toContain('sr-only')
    expect(link.className).toContain('focus:not-sr-only')
  })
})

describe('navItemFor', () => {
  it('finds the destination for a route', () => {
    expect(navItemFor('/devices')?.id).toBe('devices')
  })

  /**
   * `/records/search` is a prefix match for `/records` too. Taking the first
   * hit would title the search page "Find by number" -- naming the wrong act
   * on the page whose whole point is that it is a different one.
   */
  it('prefers the longest match over a prefix', () => {
    expect(navItemFor('/records/search')?.id).toBe('searchRegister')
    expect(navItemFor('/records')?.id).toBe('findByNumber')
  })

  it('has no destination for a route that is not one', () => {
    expect(navItemFor('/nowhere')).toBeNull()
  })
})

/** Drives both hooks the way AppLayout does, with a way to change route. */
function Harness() {
  const main = useRef<HTMLDivElement>(null)
  const navigate = useNavigate()

  usePageTitle()
  useFocusOnRouteChange(main)

  return (
    <>
      <button onClick={() => navigate('/devices')}>Go to devices</button>
      <div id={MainContentId} ref={main} tabIndex={-1} data-testid="main">
        content
      </div>
    </>
  )
}

function renderAt(path: string) {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <Harness />
    </MemoryRouter>,
  )
}

describe('usePageTitle', () => {
  it('titles the document for the current route', async () => {
    renderAt('/records')

    await waitFor(() => expect(document.title).toBe('Find by number · NCBRS'))
  })

  it('retitles when the route changes', async () => {
    renderAt('/records')
    await waitFor(() => expect(document.title).toBe('Find by number · NCBRS'))

    await userEvent.click(screen.getByRole('button', { name: /go to devices/i }))

    await waitFor(() => expect(document.title).toBe('Devices · NCBRS'))
  })

  it('falls back to the service name on an unknown route', async () => {
    renderAt('/nowhere')

    await waitFor(() => expect(document.title).toBe('NCBRS'))
  })
})

describe('useFocusOnRouteChange', () => {
  /**
   * Focus on load belongs to the browser; stealing it would fight the address
   * bar and any in-page anchor.
   */
  it('leaves focus alone on first paint', () => {
    renderAt('/records')

    expect(screen.getByTestId('main')).not.toHaveFocus()
  })

  /**
   * Without this, clicking a nav link leaves focus on the link: a screen
   * reader announces nothing and the next Tab carries on through the sidebar
   * rather than into the content the user just asked for.
   */
  it('moves focus to the main content when the route changes', async () => {
    renderAt('/records')

    await userEvent.click(screen.getByRole('button', { name: /go to devices/i }))

    await waitFor(() => expect(screen.getByTestId('main')).toHaveFocus())
  })
})
