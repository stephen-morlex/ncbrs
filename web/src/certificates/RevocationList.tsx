import { useCallback, useEffect, useState } from 'react'
import { CircleAlert, ShieldCheck, TriangleAlert } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
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
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table'
import type { components } from '@/api/generated/api'
import { type NcbrsError, toNcbrsError, unreachableError } from '@/api/errors'
import { useApiClient } from '@/api/useApi'
import { PageHeader } from '@/shell/PageHeader'
import { formatDate } from '@/records/RecordDetail'

type RevocationList = components['schemas']['CertificateRevocationList']
type RevocationReason = components['schemas']['RevocationReason']

const ReasonLabels: Record<NonNullable<RevocationReason>, string> = {
  Amended: 'Register corrected',
  SupersededAsDuplicate: 'Duplicate',
  RegistrationAnnulled: 'Annulled',
}

/**
 * The signed list of withdrawn certificates.
 *
 * A certificate cannot know the register was corrected after it was printed,
 * so a withdrawn one is published here and verification checks both the
 * signature and this list. The whole point is that it can be distributed as
 * widely as it must be: every entry is an **opaque digest of the printed
 * signature**, so the list names no child, no BRN and no facility — which is
 * why this view is safe for any signed-in officer, and the endpoint behind it
 * is anonymous.
 *
 * `nextUpdateUtc` is load-bearing, not decoration: past it, a certificate
 * absent from a cached copy is *unknown*, not valid — a verifier that treats a
 * stale list as authoritative has re-opened the hole the list closes.
 */
export function RevocationList() {
  const api = useApiClient()

  const [list, setList] = useState<RevocationList | null>(null)
  const [error, setError] = useState<NcbrsError | null>(null)
  const [loading, setLoading] = useState(true)

  const load = useCallback(async () => {
    setLoading(true)
    setError(null)

    try {
      const { data, error: failure, response } = await api.GET('/api/certificates/revocations', {})

      if (failure || !response.ok) {
        setError(toNcbrsError(failure, response.status))
        return
      }

      setList(data?.data ?? null)
    } catch (cause) {
      setError(unreachableError(cause))
    } finally {
      setLoading(false)
    }
  }, [api])

  useEffect(() => {
    void load()
  }, [load])

  const entries = list?.entries ?? []

  return (
    <div className="mx-auto w-full max-w-4xl space-y-6">
      <PageHeader
        title="Revocation list"
        description="Certificates that have been withdrawn. A certificate is valid only if signed and not on this list."
      />

      {loading && list === null ? <LoadingList /> : null}

      {error ? <Failure error={error} onRetry={() => void load()} /> : null}

      {list && !error ? (
        <>
          <Card>
            <CardContent className="grid gap-x-6 gap-y-3 pt-6 sm:grid-cols-2">
              <Meta label="Issued" value={formatDate(list.issuedAtUtc)} />
              <Meta
                label="Next update"
                value={formatDate(list.nextUpdateUtc)}
                hint="Past this, a certificate absent from a cached copy is unknown, not valid."
              />
              <Meta label="Entries" value={list.count.toLocaleString()} />
              <Meta label="Signing key" value={list.keyId} />
            </CardContent>
          </Card>

          {entries.length === 0 ? (
            <Empty className="border">
              <EmptyHeader>
                <EmptyMedia variant="icon">
                  <ShieldCheck />
                </EmptyMedia>
                <EmptyTitle>No certificates have been withdrawn</EmptyTitle>
                <EmptyDescription>
                  Every certificate ever issued still stands. The list is published and signed all
                  the same, so a verifier can tell an empty list from a missing one.
                </EmptyDescription>
              </EmptyHeader>
            </Empty>
          ) : (
            <Card>
              <CardContent className="pt-6">
                <Table>
                  <TableHeader>
                    <TableRow>
                      <TableHead>Signature digest</TableHead>
                      <TableHead>Reason</TableHead>
                      <TableHead>Withdrawn</TableHead>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {entries.map((entry) => (
                      <TableRow key={entry.serialHash}>
                        {/* The opaque digest, not a BRN or a name — which is
                            what lets this list be distributed as widely as it
                            must be. Full value on hover; the row stays legible. */}
                        <TableCell
                          className="max-w-72 truncate font-mono text-xs"
                          title={entry.serialHash}
                        >
                          {entry.serialHash}
                        </TableCell>
                        <TableCell>
                          <Badge variant="outline">
                            {entry.reason ? ReasonLabels[entry.reason] : 'Withdrawn'}
                          </Badge>
                        </TableCell>
                        <TableCell className="whitespace-nowrap">
                          {formatDate(entry.revokedAtUtc)}
                        </TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              </CardContent>
            </Card>
          )}
        </>
      ) : null}
    </div>
  )
}

function Meta({ label, value, hint }: { label: string; value: string; hint?: string }) {
  return (
    <div>
      <p className="text-muted-foreground text-xs uppercase tracking-wide">{label}</p>
      <p className="text-sm">{value}</p>
      {hint ? <p className="text-muted-foreground text-xs">{hint}</p> : null}
    </div>
  )
}

function LoadingList() {
  return (
    <Card aria-busy>
      <CardContent className="space-y-3 pt-6">
        <Skeleton className="h-4 w-48" />
        <Skeleton className="h-10 w-full" />
        <Skeleton className="h-10 w-full" />
        <Skeleton className="h-10 w-full" />
      </CardContent>
    </Card>
  )
}

function Failure({ error, onRetry }: { error: NcbrsError; onRetry: () => void }) {
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
            : error.fields.map((item) => item.message).join(' ') ||
              'The revocation list could not be loaded.'}
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
