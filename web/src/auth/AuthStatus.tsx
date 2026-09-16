import type { ReactNode } from 'react'
import { Empty } from '@/components/ui/empty'

/**
 * A full-height page holding one <Empty>.
 *
 * Not a component in its own right so much as a page frame: `Empty` centres
 * its own contents and handles the spacing, but it fills its parent rather
 * than the viewport, so something has to give it the height. That is all this
 * does.
 *
 * It exists because the three auth states -- signing in, refused, failed --
 * must look identical to each other. A registrar who sees one of them rarely
 * should not have to work out which kind of screen they are on.
 */
export function AuthStatus({ children }: { children: ReactNode }) {
  return (
    <main className="flex min-h-svh items-center justify-center p-6">
      <Empty className="max-w-md">{children}</Empty>
    </main>
  )
}
