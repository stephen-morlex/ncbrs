import { type FormEvent, useCallback, useEffect, useState } from 'react'
import { useSearchParams } from 'react-router'
import { Ban, CircleAlert, FileSearch, Search, TriangleAlert } from 'lucide-react'
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import {
  Empty,
  EmptyDescription,
  EmptyHeader,
  EmptyMedia,
  EmptyTitle,
} from '@/components/ui/empty'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import { Skeleton } from '@/components/ui/skeleton'
import { Spinner } from '@/components/ui/spinner'
import { Textarea } from '@/components/ui/textarea'
import type { components } from '@/api/generated/api'
import { type NcbrsError, messagesFor, toNcbrsError, unreachableError } from '@/api/errors'
import { useApiClient } from '@/api/useApi'
import { PageHeader } from '@/shell/PageHeader'
import { formatDate } from '@/records/RecordDetail'

type BirthRecord = components['schemas']['BirthRecordResponse']
type AnnulmentReason = components['schemas']['AnnulmentReason']
type AnnulmentSummary = components['schemas']['AnnulmentSummary']

const BrnField = 'brn'

const ReasonLabels: Record<AnnulmentReason, string> = {
  RegisteredInError: 'Registered in error',
  FraudulentRegistration: 'Fraudulent registration',
  CourtOrdered: 'Court-ordered',
}

/**
 * Voiding a registration that should never have existed — ministry-level, and
 * the heaviest of the three acts a review can take on a record.
 *
 * The screen states the distinction it turns on, because choosing the wrong
 * act here is the mistake worth designing against:
 *   • an **amendment** says the register described a real birth wrongly — the
 *     record survives, corrected;
 *   • a **duplicate supersession** says two records describe one child — one
 *     survives;
 *   • an **annulment** says there was no such birth — nothing survives.
 *
 * Nothing is deleted: the record and its BRN are kept forever, because the
 * number may already be printed on a certificate or quoted in a school
 * register and must keep resolving to an explanation. Any valid certificate is
 * revoked. There is no un-annul — if an annulment was itself wrong, the remedy
 * is a fresh registration, which leaves both acts visible.
 */
export function AnnulRegistration() {
  const api = useApiClient()

  const [params, setParams] = useSearchParams()
  const linked = params.get('brn') ?? ''

  const [query, setQuery] = useState(linked)
  const [record, setRecord] = useState<BirthRecord | null>(null)
  const [error, setError] = useState<NcbrsError | null>(null)
  const [searching, setSearching] = useState(false)
  const [searched, setSearched] = useState(false)

  const fetchRecord = useCallback(
    async (brn: string) => {
      if (!brn) {
        return
      }

      setSearching(true)
      setError(null)
      setRecord(null)

      try {
        const { data, error: failure, response } = await api.GET('/api/BirthRecords/{brn}', {
          params: { path: { brn } },
        })

        if (failure || !response.ok) {
          setError(toNcbrsError(failure, response.status))
        } else {
          setRecord(data?.data ?? null)
        }
      } catch (cause) {
        setError(unreachableError(cause))
      } finally {
        setSearching(false)
        setSearched(true)
      }
    },
    [api],
  )

  useEffect(() => {
    if (linked) {
      void fetchRecord(linked)
    }
  }, [linked, fetchRecord])

  function onSubmit(event: FormEvent) {
    event.preventDefault()

    const brn = query.trim()
    if (!brn) {
      return
    }

    setParams(brn === linked ? params : { brn })
    if (brn === linked) {
      void fetchRecord(brn)
    }
  }

  const fieldMessages = messagesFor(error, BrnField)

  return (
    <div className="mx-auto w-full max-w-3xl space-y-6">
      <PageHeader
        title="Annulment"
        description="Void a registration that should never have existed. This withdraws a legal identity, so it is a ministry-level act."
      />

      <Card>
        <CardContent className="pt-6">
          <form onSubmit={onSubmit} className="grid gap-2">
            <Label htmlFor={BrnField}>Registration number</Label>
            <div className="flex flex-col gap-2 sm:flex-row">
              <Input
                id={BrnField}
                name={BrnField}
                value={query}
                onChange={(event) => setQuery(event.target.value)}
                placeholder="Find the record to annul"
                autoComplete="off"
                className="sm:flex-1"
                aria-invalid={fieldMessages.length > 0 || undefined}
                aria-describedby={fieldMessages.length > 0 ? `${BrnField}-error` : undefined}
              />
              <Button type="submit" disabled={searching || query.trim().length === 0}>
                {searching ? <Spinner /> : <Search />}
                Find
              </Button>
            </div>
            {fieldMessages.length > 0 ? (
              <p id={`${BrnField}-error`} className="text-destructive text-sm">
                {fieldMessages.join(' ')}
              </p>
            ) : null}
          </form>
        </CardContent>
      </Card>

      {searching ? <LoadingRecord /> : null}

      {!searching && error && fieldMessages.length === 0 ? <Failure error={error} /> : null}

      {!searching && record ? (
        <RecordSummary record={record} />
      ) : null}

      {!searching && record ? (
        record.annulment ? (
          <AnnulledNotice annulment={record.annulment} />
        ) : (
          <AnnulPanel
            record={record}
            onAnnulled={(summary) =>
              setRecord((current) =>
                current ? { ...current, status: 'Annulled', annulment: summary } : current,
              )
            }
          />
        )
      ) : null}

      {!searching && !error && !record && searched ? (
        <Empty className="border">
          <EmptyHeader>
            <EmptyMedia variant="icon">
              <FileSearch />
            </EmptyMedia>
            <EmptyTitle>No record for that number</EmptyTitle>
            <EmptyDescription>
              Check the number as printed. A number that has circulated always resolves.
            </EmptyDescription>
          </EmptyHeader>
        </Empty>
      ) : null}
    </div>
  )
}

function RecordSummary({ record }: { record: BirthRecord }) {
  return (
    <Card>
      <CardContent className="flex flex-wrap items-start justify-between gap-4 pt-6">
        <div>
          <p className="text-lg font-medium">{record.childFullName}</p>
          <p className="text-muted-foreground font-mono text-sm">{record.brn}</p>
          <p className="text-muted-foreground text-sm">Born {formatDate(record.dateOfBirth)}</p>
        </div>
        <Badge variant={record.status === 'Annulled' ? 'destructive' : 'secondary'}>
          {record.status}
        </Badge>
      </CardContent>
    </Card>
  )
}

/**
 * Shown when the record is already void. The BRN still resolves — that is the
 * point — so the screen explains what was done rather than offering to do it
 * again (which the server would refuse).
 */
function AnnulledNotice({ annulment }: { annulment: AnnulmentSummary }) {
  return (
    <Alert variant="destructive">
      <Ban />
      <AlertTitle>This registration has been annulled</AlertTitle>
      <AlertDescription>
        <p>
          {ReasonLabels[annulment.reason]} · {formatDate(annulment.annulledAtUtc)}
        </p>
        <p>{annulment.justification}</p>
        {annulment.authorityReference ? (
          <p className="text-muted-foreground">Authority: {annulment.authorityReference}</p>
        ) : null}
        <p className="text-muted-foreground mt-2">
          There is no un-annul. If this was itself wrong, the remedy is a fresh registration, which
          leaves both acts visible.
        </p>
      </AlertDescription>
    </Alert>
  )
}

/**
 * The annul form, shown only for a record that is not already void. It states
 * the three-way distinction before asking for a reason, because the mistake
 * this guards against is reaching for annulment when an amendment or a
 * duplicate supersession was the right act.
 */
function AnnulPanel({
  record,
  onAnnulled,
}: {
  record: BirthRecord
  onAnnulled: (summary: AnnulmentSummary) => void
}) {
  const api = useApiClient()

  const [reason, setReason] = useState<AnnulmentReason>('RegisteredInError')
  const [justification, setJustification] = useState('')
  const [authorityReference, setAuthorityReference] = useState('')
  const [showErrors, setShowErrors] = useState(false)
  const [confirming, setConfirming] = useState(false)
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState<NcbrsError | null>(null)

  const needsAuthority = reason === 'CourtOrdered'
  const justificationMissing = justification.trim().length === 0
  const authorityMissing = needsAuthority && authorityReference.trim().length === 0
  const invalid = justificationMissing || authorityMissing

  function openConfirm() {
    if (invalid) {
      setShowErrors(true)
      return
    }
    setShowErrors(false)
    setError(null)
    setConfirming(true)
  }

  const annul = useCallback(async () => {
    setSubmitting(true)
    setError(null)

    try {
      const { data, error: failure, response } = await api.POST(
        '/api/BirthRecords/{brn}/annulment',
        {
          params: { path: { brn: record.brn } },
          body: {
            data: {
              reason,
              justification: justification.trim(),
              authorityReference: authorityReference.trim() || null,
            },
          },
        },
      )

      if (response.ok) {
        setConfirming(false)
        onAnnulled({
          reason,
          justification: justification.trim(),
          authorityReference: authorityReference.trim() || null,
          annulledAtUtc: data?.data?.annulledAtUtc ?? new Date().toISOString(),
        })
        return
      }

      setError(toNcbrsError(failure, response.status))
      setConfirming(false)
    } catch (cause) {
      setError(unreachableError(cause))
      setConfirming(false)
    } finally {
      setSubmitting(false)
    }
  }, [api, record.brn, reason, justification, authorityReference, onAnnulled])

  return (
    <Card>
      <CardContent className="space-y-5 pt-6">
        <div className="text-muted-foreground space-y-1 text-sm">
          <p className="text-foreground font-medium">Is annulment the right act?</p>
          <p>
            <span className="text-foreground">Amendment</span> — the register described a real birth
            wrongly; the record survives, corrected.
          </p>
          <p>
            <span className="text-foreground">Duplicate supersession</span> — two records describe
            one child; one survives.
          </p>
          <p>
            <span className="text-foreground">Annulment</span> — there was no such birth; nothing
            survives. Only this last is done here.
          </p>
        </div>

        {error ? (
          <Alert variant="destructive">
            <CircleAlert />
            <AlertTitle>{error.title}</AlertTitle>
            <AlertDescription>
              {error.unreachable
                ? 'The registry did not answer. Nothing was changed; try again.'
                : error.fields.map((item) => item.message).join(' ') || 'Nothing was changed.'}
            </AlertDescription>
          </Alert>
        ) : null}

        <div className="grid gap-2">
          <Label htmlFor="annul-reason">Reason</Label>
          <Select value={reason} onValueChange={(value) => setReason(value as AnnulmentReason)}>
            <SelectTrigger id="annul-reason" className="w-full sm:w-80">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="RegisteredInError">Registered in error</SelectItem>
              <SelectItem value="FraudulentRegistration">Fraudulent registration</SelectItem>
              <SelectItem value="CourtOrdered">Court-ordered</SelectItem>
            </SelectContent>
          </Select>
        </div>

        {needsAuthority ? (
          <div className="grid gap-2">
            <Label htmlFor="annul-authority">Court order reference</Label>
            <Input
              id="annul-authority"
              value={authorityReference}
              onChange={(event) => setAuthorityReference(event.target.value)}
              placeholder="The order authorising the annulment"
              aria-invalid={(showErrors && authorityMissing) || undefined}
            />
            {showErrors && authorityMissing ? (
              <p className="text-destructive text-sm">
                A court-ordered annulment must cite the order authorising it.
              </p>
            ) : null}
          </div>
        ) : null}

        <div className="grid gap-2">
          <Label htmlFor="annul-justification">Justification</Label>
          <Textarea
            id="annul-justification"
            value={justification}
            onChange={(event) => setJustification(event.target.value)}
            placeholder="Why this registration is void. Kept as part of the record's permanent history."
            aria-invalid={(showErrors && justificationMissing) || undefined}
          />
          {showErrors && justificationMissing ? (
            <p className="text-destructive text-sm">
              A justification is required. An annulment withdraws a legal identity and must say why.
            </p>
          ) : null}
        </div>

        <Button variant="destructive" onClick={openConfirm}>
          Annul this registration
        </Button>
      </CardContent>

      <Dialog open={confirming} onOpenChange={(open) => (open ? undefined : setConfirming(false))}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Annul {record.brn}?</DialogTitle>
            <DialogDescription>
              {record.childFullName} · {ReasonLabels[reason]}
            </DialogDescription>
          </DialogHeader>

          <div className="text-muted-foreground space-y-2 text-sm">
            <p>This says there was no such birth. It cannot be undone.</p>
            <p>
              The record and its BRN are kept forever and keep resolving to this explanation; any
              valid certificate is revoked and will fail verification.
            </p>
          </div>

          <DialogFooter>
            <Button variant="outline" onClick={() => setConfirming(false)} disabled={submitting}>
              Cancel
            </Button>
            <Button variant="destructive" onClick={() => void annul()} disabled={submitting}>
              {submitting ? <Spinner /> : null}
              Annul the registration
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </Card>
  )
}

function LoadingRecord() {
  return (
    <Card aria-busy>
      <CardContent className="space-y-3 pt-6">
        <Skeleton className="h-6 w-56" />
        <Skeleton className="h-4 w-32" />
        <Skeleton className="h-24 w-full" />
      </CardContent>
    </Card>
  )
}

function Failure({ error }: { error: NcbrsError }) {
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
            : error.fields.map((item) => item.message).join(' ')}
        </EmptyDescription>
      </EmptyHeader>
    </Empty>
  )
}
