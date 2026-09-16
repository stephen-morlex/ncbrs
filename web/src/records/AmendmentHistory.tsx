import { useCallback, useEffect, useState } from 'react'
import { CircleAlert, FileClock } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import {
  Empty,
  EmptyDescription,
  EmptyHeader,
  EmptyMedia,
  EmptyTitle,
} from '@/components/ui/empty'
import { Skeleton } from '@/components/ui/skeleton'
import {
  Table,
  TableBody,
  TableCaption,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table'
import type { components } from '@/api/generated/api'
import { type NcbrsError, toNcbrsError, unreachableError } from '@/api/errors'
import { useApiClient } from '@/api/useApi'
import { formatDate } from './RecordDetail'

type Amendment = components['schemas']['AmendmentHistoryEntry']

/**
 * What changed on this record, from what, by whom and why.
 *
 * This is the view an auditor or a court needs, and the reason amendments
 * store the previous value rather than overwriting it. Two things it shows
 * that a naive "current state" view cannot:
 *
 * **Refused corrections are listed.** A change someone proposed and a reviewer
 * turned down is part of a record's history too, and is often the part a
 * dispute turns on — an attempt to alter a date of birth that was caught is
 * more interesting than one that was routine.
 *
 * **Pending corrections are listed as pending.** A correction to a name or a
 * date of birth waits for a reviewer who is not the submitter, and during that
 * wait the record still says what it said. Showing a queued change as though
 * it had been applied would misdescribe the register.
 */
export function AmendmentHistory({ brn }: { brn: string }) {
  const api = useApiClient()

  const [amendments, setAmendments] = useState<Amendment[] | null>(null)
  const [error, setError] = useState<NcbrsError | null>(null)

  const load = useCallback(async () => {
    setError(null)

    try {
      const { data, error: failure, response } = await api.GET(
        '/api/BirthRecords/{brn}/amendments',
        { params: { path: { brn } } },
      )

      if (failure || !response.ok) {
        setError(toNcbrsError(failure, response.status))
        setAmendments([])
      } else {
        setAmendments(data?.data ?? [])
      }
    } catch (cause) {
      setError(unreachableError(cause))
      setAmendments([])
    }
  }, [api, brn])

  useEffect(() => {
    void load()
  }, [load])

  if (amendments === null) {
    return (
      <div className="space-y-2" aria-busy>
        <Skeleton className="h-9 w-full" />
        <Skeleton className="h-9 w-full" />
      </div>
    )
  }

  if (error) {
    return (
      <Empty className="border">
        <EmptyHeader>
          <EmptyMedia variant="icon">
            <CircleAlert />
          </EmptyMedia>
          <EmptyTitle>The correction history could not be loaded</EmptyTitle>
          <EmptyDescription>
            {error.unreachable
              ? 'The registry did not answer. The record above is still accurate.'
              : error.fields.map((item) => item.message).join(' ')}
          </EmptyDescription>
        </EmptyHeader>
      </Empty>
    )
  }

  if (amendments.length === 0) {
    return (
      <Empty className="border">
        <EmptyHeader>
          <EmptyMedia variant="icon">
            <FileClock />
          </EmptyMedia>
          <EmptyTitle>Never corrected</EmptyTitle>
          <EmptyDescription>
            This record reads as it was first registered.
          </EmptyDescription>
        </EmptyHeader>
      </Empty>
    )
  }

  return (
    <Table>
      <TableCaption>
        Includes corrections that were refused or are still awaiting a reviewer.
      </TableCaption>
      <TableHeader>
        <TableRow>
          <TableHead>Field</TableHead>
          <TableHead>Was</TableHead>
          <TableHead>Became</TableHead>
          <TableHead>Reason</TableHead>
          <TableHead>By</TableHead>
          <TableHead>When</TableHead>
          <TableHead>Status</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {amendments.map((amendment) => (
          <TableRow key={amendment.amendmentId}>
            <TableCell className="font-medium">{amendment.field}</TableCell>
            {/* The real previous value, not what anyone believed it was. An
                audit trail asserting a transition that never happened is worse
                than no trail. */}
            <TableCell className="text-muted-foreground">
              {amendment.previousValue ?? '—'}
            </TableCell>
            <TableCell>{amendment.newValue ?? '—'}</TableCell>
            <TableCell className="max-w-56 truncate" title={amendment.reason}>
              {amendment.reason}
            </TableCell>
            <TableCell>{amendment.amendedByRegistrarName}</TableCell>
            <TableCell className="whitespace-nowrap">
              {formatDate(amendment.amendedAtUtc)}
            </TableCell>
            <TableCell>
              <StatusBadge amendment={amendment} />
            </TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  )
}

/**
 * Applied, awaiting a reviewer, or refused — and the reviewer's note where
 * there is one, because "refused" without a reason tells the next person
 * nothing they can act on.
 */
function StatusBadge({ amendment }: { amendment: Amendment }) {
  const title = amendment.reviewNote ?? undefined

  switch (amendment.status) {
    case 'Applied':
      return <Badge variant="secondary">applied</Badge>

    case 'Rejected':
      return (
        <Badge variant="destructive" title={title}>
          refused
        </Badge>
      )

    default:
      return (
        <Badge variant="outline" title={title}>
          awaiting review
        </Badge>
      )
  }
}
