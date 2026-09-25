import { useCallback, useEffect, useState } from 'react'
import { Link, useSearchParams } from 'react-router'
import { BadgeCheck, CircleAlert, Printer, ShieldOff, TriangleAlert } from 'lucide-react'
import { WebChannelDeviceId } from '@/auth/channel'
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import {
  Empty,
  EmptyDescription,
  EmptyHeader,
  EmptyMedia,
  EmptyTitle,
} from '@/components/ui/empty'
import { Skeleton } from '@/components/ui/skeleton'
import { Spinner } from '@/components/ui/spinner'
import type { components } from '@/api/generated/api'
import { type NcbrsError, toNcbrsError, unreachableError } from '@/api/errors'
import { useApiClient } from '@/api/useApi'
import { PageHeader } from '@/shell/PageHeader'
import { formatDate } from './RecordDetail'

type BirthRecord = components['schemas']['BirthRecordResponse']
type Certificate = components['schemas']['CertificateResponse']

/**
 * Issue or reprint the birth certificate for one record.
 *
 * Reached from the record, not the navigation — a certificate is an act on a
 * particular registration. The screen's job is to make the **refusals** legible
 * as much as the issuing: a certificate is withheld on a fetal death, an
 * unverified late registration, an unreconciled provisional number and an
 * annulled record, and a registrar with a family at the counter needs to be
 * told which, not shown a button that fails.
 *
 * Reprint is deliberately distinct from issue: the signature and the issue
 * date do not change — the document is the same one, printed again — and the
 * count is kept, because a certificate reprinted repeatedly is worth noticing.
 */
export function CertificateManage() {
  const api = useApiClient()

  const [params] = useSearchParams()
  const brn = params.get('brn') ?? ''

  const [record, setRecord] = useState<BirthRecord | null>(null)
  const [certificate, setCertificate] = useState<Certificate | null>(null)
  const [loadError, setLoadError] = useState<NcbrsError | null>(null)
  const [loading, setLoading] = useState(true)

  const [acting, setActing] = useState(false)
  const [actionError, setActionError] = useState<NcbrsError | null>(null)
  const [reprinted, setReprinted] = useState(false)

  const load = useCallback(async () => {
    if (!brn) {
      setLoading(false)
      return
    }

    setLoading(true)
    setLoadError(null)

    try {
      const { data, error: failure, response } = await api.GET('/api/BirthRecords/{brn}', {
        params: { path: { brn } },
      })

      if (failure || !response.ok) {
        setLoadError(toNcbrsError(failure, response.status))
        return
      }

      const loaded = data?.data ?? null
      setRecord(loaded)

      // A record that already has a certificate: fetch the full one for its
      // QR payload and signature, which the record's summary state omits.
      if (loaded?.certificate) {
        const cert = await api.GET('/api/BirthRecords/{brn}/certificate', {
          params: { path: { brn } },
        })
        if (cert.response.ok) {
          setCertificate(cert.data?.data ?? null)
        }
      } else {
        setCertificate(null)
      }
    } catch (cause) {
      setLoadError(unreachableError(cause))
    } finally {
      setLoading(false)
    }
  }, [api, brn])

  useEffect(() => {
    void load()
  }, [load])

  const act = useCallback(
    async (reprint: boolean) => {
      setActing(true)
      setActionError(null)

      const body = { params: { path: { brn } }, body: { data: { deviceId: WebChannelDeviceId } } }

      try {
        // Two literal paths rather than a union variable: the generated client
        // can only resolve the response type from a concrete endpoint.
        const { data, error: failure, response } = reprint
          ? await api.POST('/api/BirthRecords/{brn}/certificate/reprint', body)
          : await api.POST('/api/BirthRecords/{brn}/certificate', body)

        if (failure || !response.ok) {
          setActionError(toNcbrsError(failure, response.status))
          return
        }

        setCertificate(data?.data ?? null)
        setReprinted(reprint)
      } catch (cause) {
        setActionError(unreachableError(cause))
      } finally {
        setActing(false)
      }
    },
    [api, brn],
  )

  if (!brn) {
    return (
      <div className="mx-auto w-full max-w-3xl space-y-6">
        <PageHeader title="Certificate" description="Open a record first." />
        <Alert variant="destructive">
          <CircleAlert />
          <AlertTitle>No record named</AlertTitle>
          <AlertDescription>
            A certificate is issued for a particular registration. Find the record first.
          </AlertDescription>
        </Alert>
      </div>
    )
  }

  return (
    <div className="mx-auto w-full max-w-3xl space-y-6">
      <PageHeader
        title="Certificate"
        description="Issue or reprint the birth certificate for this record."
      />

      {loading ? <LoadingCard /> : null}

      {loadError ? <LoadFailure error={loadError} onRetry={() => void load()} /> : null}

      {!loading && record ? (
        <>
          <RecordSummary record={record} />

          {record.annulment ? (
            <Alert variant="destructive">
              <ShieldOff />
              <AlertTitle>No certificate for an annulled registration</AlertTitle>
              <AlertDescription>
                The register records no such birth. Nothing can be issued.{' '}
                <BackToRecord brn={brn} />
              </AlertDescription>
            </Alert>
          ) : certificate && (record.certificate?.isValid ?? true) ? (
            <IssuedCertificate
              certificate={certificate}
              reprinted={reprinted}
              acting={acting}
              onReprint={() => void act(true)}
              actionError={actionError}
            />
          ) : record.certificate && !record.certificate.isValid ? (
            <Alert variant="destructive">
              <ShieldOff />
              <AlertTitle>This certificate has been withdrawn</AlertTitle>
              <AlertDescription>
                Issued {formatDate(record.certificate.issuedAtUtc)} and later withdrawn. A copy in
                circulation will fail verification, and a new one cannot be issued over it.{' '}
                <BackToRecord brn={brn} />
              </AlertDescription>
            </Alert>
          ) : (
            <IssuePanel
              record={record}
              acting={acting}
              actionError={actionError}
              onIssue={() => void act(false)}
            />
          )}
        </>
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
        <Badge variant={record.annulment ? 'destructive' : 'secondary'}>{record.status}</Badge>
      </CardContent>
    </Card>
  )
}

/**
 * The issue action, and — when the register refuses — the reason in plain
 * words. The server is the authority on certifiability (it knows the vital
 * event type, the late-registration state and the BRN reconciliation this
 * screen cannot see), so the refusal it returns is shown as-is rather than
 * guessed at here.
 */
function IssuePanel({
  record,
  acting,
  actionError,
  onIssue,
}: {
  record: BirthRecord
  acting: boolean
  actionError: NcbrsError | null
  onIssue: () => void
}) {
  const lateNotApproved =
    record.lateRegistration != null && record.lateRegistration.status !== 'Approved'

  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-base">No certificate issued yet</CardTitle>
      </CardHeader>
      <CardContent className="space-y-4">
        {lateNotApproved ? (
          <Alert>
            <TriangleAlert />
            <AlertTitle>
              {record.lateRegistration?.status === 'Rejected'
                ? 'The late-registration evidence was refused'
                : 'The late registration is not yet verified'}
            </AlertTitle>
            <AlertDescription>
              A birth registered outside the statutory window cannot be certified until a district
              registrar has verified the evidence. The registration itself stands.
            </AlertDescription>
          </Alert>
        ) : null}

        {actionError ? (
          <Alert variant="destructive">
            <CircleAlert />
            <AlertTitle>{actionError.title}</AlertTitle>
            <AlertDescription>
              {actionError.unreachable
                ? 'The registry did not answer. Nothing was issued; try again.'
                : actionError.fields.map((item) => item.message).join(' ') ||
                  'This record cannot be certified.'}
            </AlertDescription>
          </Alert>
        ) : null}

        <p className="text-muted-foreground text-sm">
          Issuing signs the certificate with the Ministry's key. It can then be printed, and a
          verifier can check it from the QR code even offline.
        </p>

        <Button onClick={onIssue} disabled={acting}>
          {acting ? <Spinner /> : <BadgeCheck />}
          Issue certificate
        </Button>
      </CardContent>
    </Card>
  )
}

function IssuedCertificate({
  certificate,
  reprinted,
  acting,
  actionError,
  onReprint,
}: {
  certificate: Certificate
  reprinted: boolean
  acting: boolean
  actionError: NcbrsError | null
  onReprint: () => void
}) {
  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-base">Certificate issued</CardTitle>
      </CardHeader>
      <CardContent className="space-y-4">
        <Alert>
          <BadgeCheck />
          <AlertTitle>Issued {formatDate(certificate.issueDateUtc)}</AlertTitle>
          <AlertDescription>
            {certificate.reprintCount > 0
              ? `Reprinted ${certificate.reprintCount} ${certificate.reprintCount === 1 ? 'time' : 'times'}.`
              : 'Not reprinted.'}
          </AlertDescription>
        </Alert>

        {reprinted ? (
          <Alert>
            <Printer />
            <AlertTitle>Reprinted</AlertTitle>
            <AlertDescription>
              The same certificate, printed again — its signature and issue date are unchanged. The
              reprint count is now {certificate.reprintCount}.
            </AlertDescription>
          </Alert>
        ) : null}

        {actionError ? (
          <Alert variant="destructive">
            <CircleAlert />
            <AlertTitle>{actionError.title}</AlertTitle>
            <AlertDescription>
              {actionError.unreachable
                ? 'The registry did not answer. Try again.'
                : actionError.fields.map((item) => item.message).join(' ') || 'Could not reprint.'}
            </AlertDescription>
          </Alert>
        ) : null}

        <div>
          <p className="text-muted-foreground mb-1 text-xs">QR payload</p>
          {/* The signed payload a verifier scans. Shown so it can be printed or
              checked, and monospaced because every character is load-bearing. */}
          <pre className="bg-muted overflow-x-auto rounded-md p-3 font-mono text-xs">
            {certificate.qrPayload}
          </pre>
        </div>

        <Button variant="outline" onClick={onReprint} disabled={acting}>
          {acting ? <Spinner /> : <Printer />}
          Reprint
        </Button>
      </CardContent>
    </Card>
  )
}

function BackToRecord({ brn }: { brn: string }) {
  return (
    <Link to={`/records?brn=${encodeURIComponent(brn)}`} className="underline underline-offset-4">
      Back to the record
    </Link>
  )
}

function LoadingCard() {
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

function LoadFailure({ error, onRetry }: { error: NcbrsError; onRetry: () => void }) {
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
            : error.fields.map((item) => item.message).join(' ') || 'The record could not be loaded.'}
        </EmptyDescription>
      </EmptyHeader>
      <div className="mt-2">
        <Button variant="outline" onClick={onRetry}>
          Try again
        </Button>
      </div>
    </Empty>
  )
}
