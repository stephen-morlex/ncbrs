import { CircleAlert, TriangleAlert } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import {
  Empty,
  EmptyContent,
  EmptyDescription,
  EmptyHeader,
  EmptyMedia,
  EmptyTitle,
} from '@/components/ui/empty'
import { Skeleton } from '@/components/ui/skeleton'
import type { NcbrsError } from '@/api/errors'

/**
 * Pieces shared by the two amendment queues — approvals and conflicts.
 *
 * They read the same register, are worked by the same officer and fail the
 * same ways, so the age wording, the loading skeleton and the failure surface
 * are one implementation rather than two that drift.
 */

/**
 * How long an item has been waiting, in the coarse terms that matter for a
 * queue read top to bottom. The exact timestamp belongs on the row's title
 * attribute for anyone who needs it.
 */
export function ageInWords(iso: string): string {
  const then = new Date(iso).getTime()
  if (Number.isNaN(then)) {
    return ''
  }

  const days = Math.floor((Date.now() - then) / 86_400_000)
  if (days <= 0) {
    return 'today'
  }
  if (days === 1) {
    return 'yesterday'
  }
  if (days < 14) {
    return `${days} days ago`
  }

  const weeks = Math.floor(days / 7)
  if (weeks < 9) {
    return `${weeks} weeks ago`
  }

  const months = Math.floor(days / 30)
  return `${months} months ago`
}

export function QueueSkeleton() {
  return (
    <Card aria-busy>
      <CardContent className="space-y-3 pt-6">
        <Skeleton className="h-4 w-48" />
        <Skeleton className="h-12 w-full" />
        <Skeleton className="h-12 w-full" />
        <Skeleton className="h-12 w-full" />
      </CardContent>
    </Card>
  )
}

export function QueueFailure({
  error,
  onRetry,
  fallback,
}: {
  error: NcbrsError
  onRetry: () => void
  fallback: string
}) {
  return (
    <Empty className="border">
      <EmptyHeader>
        <EmptyMedia variant="icon">
          {error.unreachable ? <TriangleAlert /> : <CircleAlert />}
        </EmptyMedia>
        <EmptyTitle>{error.title}</EmptyTitle>
        <EmptyDescription>
          {error.unreachable
            ? 'The registry did not answer. Check the connection before trying again.'
            : error.fields.map((item) => item.message).join(' ') || fallback}
        </EmptyDescription>
      </EmptyHeader>
      <EmptyContent>
        <Button variant="outline" onClick={onRetry}>
          Try again
        </Button>
      </EmptyContent>
    </Empty>
  )
}
