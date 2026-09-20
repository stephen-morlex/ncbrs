import { Link } from 'react-router'
import { Activity, Award, CircleCheck, FileWarning, History, Info, PenLine, ShieldOff, TriangleAlert } from 'lucide-react'
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Item, ItemContent, ItemDescription, ItemGroup, ItemTitle } from '@/components/ui/item'
import { Separator } from '@/components/ui/separator'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import type { components } from '@/api/generated/api'
import { AmendmentHistory } from './AmendmentHistory'
import { heldDays } from './held'

type BirthRecord = components['schemas']['BirthRecordResponse']

/**
 * Everything about one birth record, on the screen a registrar reads while a
 * family is standing in front of them.
 *
 * The order is not cosmetic. What is *wrong* with a record comes before what
 * is in it: an annulled registration, a certificate being withheld, a
 * certificate that has been withdrawn. Each of those changes what the person
 * at the counter should be told, and a detail grid that buried them under the
 * date of birth would let someone read the whole screen and still say the
 * wrong thing.
 */
export function RecordDetail({ record }: { record: BirthRecord }) {
  const held = heldDays(record.registeredAtUtc, record.receivedAtUtc)

  return (
    <Card>
      <CardHeader>
        <div className="flex flex-wrap items-start justify-between gap-3">
          <div className="space-y-1">
            <CardTitle className="text-xl">{record.childFullName ?? 'Name not recorded'}</CardTitle>
            <CardDescription className="font-mono">{record.brn}</CardDescription>
          </div>
          <div className="flex flex-wrap gap-1.5">
            <Badge variant={record.annulment ? 'destructive' : 'secondary'}>{record.status}</Badge>
            {/* Confirmed means the centre reconciled the BRN against the block
                it actually granted. Unconfirmed is not an error -- the birth
                happened and the number may already be printed on a slip in a
                family's hands. */}
            {record.confirmedAtUtc ? null : <Badge variant="outline">BRN unconfirmed</Badge>}
            {record.lateRegistration ? <Badge variant="outline">late registration</Badge> : null}
            {held > 0 ? (
              <Badge variant="outline">reached the centre {held} days later</Badge>
            ) : null}
          </div>
        </div>

        <div className="mt-2 flex flex-wrap gap-2">
          <Button asChild variant="outline" size="sm">
            <Link to={`/audit?brn=${encodeURIComponent(record.brn)}`}>
              <History />
              This record's history
            </Link>
          </Button>

          {/* Not offered on an annulled record. Every acting path refuses one
              with a 409 — there is no such birth to correct — and an enabled
              button that always fails is worse than none. */}
          {record.annulment ? null : (
            <Button asChild variant="outline" size="sm">
              <Link to={`/records/correct?brn=${encodeURIComponent(record.brn)}`}>
                <PenLine />
                Request a correction
              </Link>
            </Button>
          )}

          {/* Hidden on an annulled record, which can carry no certificate.
              What is certifiable otherwise is the server's call, made on the
              certificate screen itself. */}
          {record.annulment ? null : (
            <Button asChild variant="outline" size="sm">
              <Link to={`/records/certificate?brn=${encodeURIComponent(record.brn)}`}>
                <Award />
                Certificate
              </Link>
            </Button>
          )}

          {/* A death after a live birth is a second vital event, recorded
              against the record rather than editing it. Not offered on an
              annulled record — every acting path refuses one. */}
          {record.annulment ? null : (
            <Button asChild variant="outline" size="sm">
              <Link to={`/records/outcome?brn=${encodeURIComponent(record.brn)}`}>
                <Activity />
                Record an outcome
              </Link>
            </Button>
          )}
        </div>
      </CardHeader>

      <CardContent className="space-y-4">
        <Annulled record={record} />
        <Withheld record={record} />
        <CertificateState record={record} />

        <Tabs defaultValue="details">
          <TabsList>
            <TabsTrigger value="details">Details</TabsTrigger>
            <TabsTrigger value="corrections">Corrections</TabsTrigger>
          </TabsList>

          <TabsContent value="details" className="pt-4">
            <ItemGroup>
              <Detail label="Date of birth" value={formatDate(record.dateOfBirth)} />
              <Detail label="Sex" value={record.sex ?? 'Not recorded'} />
              <Detail
                label="Registered by"
                value={record.registeredByRegistrarName ?? 'Not recorded'}
              />
              {/* The two are one fact and are shown together deliberately. On
                  their own, "received 3 October" on a birth in June reads as a
                  registration filed three months late. */}
              <Detail
                label="Registered on the device"
                value={formatDate(record.registeredAtUtc ?? undefined)}
              />
              <Detail
                label="Received by the centre"
                value={formatDate(record.receivedAtUtc ?? undefined)}
              />
              {/* Shown only when there is one. Most records were never
                  registered under a provisional number, and a row reading
                  "None" on every screen trains people to stop reading it. */}
              {record.provisionalIdentifier ? (
                <Detail
                  label="Also registered under"
                  value={record.provisionalIdentifier}
                  hint="The number a device issued before this record was reconciled. A family may still be holding the slip it was printed on."
                />
              ) : null}
              <Detail
                label="BRN confirmed"
                value={
                  record.confirmedAtUtc
                    ? formatDate(record.confirmedAtUtc)
                    : 'Not yet reconciled against the facility’s block'
                }
              />
            </ItemGroup>
          </TabsContent>

          <TabsContent value="corrections" className="pt-4">
            <AmendmentHistory brn={record.brn} />
          </TabsContent>
        </Tabs>
      </CardContent>
    </Card>
  )
}

/**
 * An annulled record still resolves and still shows why: a number that has
 * circulated must keep answering with an explanation rather than falling
 * silent.
 */
function Annulled({ record }: { record: BirthRecord }) {
  if (!record.annulment) {
    return null
  }

  return (
    <>
      <Alert variant="destructive">
        <ShieldOff />
        <AlertTitle>This registration was annulled</AlertTitle>
        <AlertDescription>
          The register records no such birth. The number is kept so that it keeps resolving to
          this explanation. {record.annulment.reason ? `Reason: ${record.annulment.reason}.` : null}
        </AlertDescription>
      </Alert>
      <Separator />
    </>
  )
}

/**
 * A late registration whose evidence has not yet been verified.
 *
 * The registration itself succeeded — a child registered late is still a child
 * who exists — but no certificate can be issued until a district registrar has
 * checked the evidence. **This is the message a family must not be sent away
 * without**, and until the API started returning `lateRegistration` on a
 * lookup there was no way for this screen to know.
 */
function Withheld({ record }: { record: BirthRecord }) {
  const late = record.lateRegistration

  if (!late) {
    return null
  }

  if (late.status === 'Rejected') {
    return (
      <Alert variant="destructive">
        <FileWarning />
        <AlertTitle>The late-registration evidence was refused</AlertTitle>
        <AlertDescription>
          Filed {late.daysLate} days after the birth, outside the {late.windowDaysAtFiling}-day
          window. The registration stands; no certificate can be issued on this evidence.
        </AlertDescription>
      </Alert>
    )
  }

  if (late.status === 'PendingApproval') {
    return (
      <Alert>
        <TriangleAlert />
        <AlertTitle>No certificate until the evidence is verified</AlertTitle>
        <AlertDescription>
          Registered {late.daysLate} days after the birth, outside the{' '}
          {late.windowDaysAtFiling}-day statutory window. A district registrar other than the
          person who filed it must verify the evidence first. Do not tell the family a
          certificate is available.
        </AlertDescription>
      </Alert>
    )
  }

  return (
    <Alert>
      <CircleCheck />
      <AlertTitle>Late registration, evidence verified</AlertTitle>
      <AlertDescription>
        Registered {late.daysLate} days after the birth and since verified by a district
        registrar. A certificate can be issued.
      </AlertDescription>
    </Alert>
  )
}

/**
 * Whether a certificate exists, and whether it still stands.
 *
 * A certificate is valid only if signed **and** not revoked, and the paper in
 * a family's hands does not change when the register is corrected. So a
 * withdrawn certificate has to be visible to whoever is being asked about it —
 * showing only "issued 3 March" would state something false about a legal
 * document.
 */
function CertificateState({ record }: { record: BirthRecord }) {
  const certificate = record.certificate

  if (!certificate) {
    return null
  }

  if (!certificate.isValid) {
    return (
      <Alert variant="destructive">
        <ShieldOff />
        <AlertTitle>This certificate has been withdrawn</AlertTitle>
        <AlertDescription>
          Issued {formatDate(certificate.issuedAtUtc)} and withdrawn{' '}
          {formatDate(certificate.withdrawnAtUtc ?? undefined)}.{' '}
          {certificate.withdrawnReason ?? ''} A copy may still be in circulation; it will fail
          verification.
        </AlertDescription>
      </Alert>
    )
  }

  return (
    <Alert>
      <Info />
      <AlertTitle>Certificate issued {formatDate(certificate.issuedAtUtc)}</AlertTitle>
      <AlertDescription>
        {certificate.reprintCount > 0
          ? `Reprinted ${certificate.reprintCount} ${certificate.reprintCount === 1 ? 'time' : 'times'}.`
          : 'Not reprinted.'}
      </AlertDescription>
    </Alert>
  )
}

function Detail({ label, value, hint }: { label: string; value: string; hint?: string }) {
  return (
    <Item variant="outline">
      <ItemContent>
        <ItemTitle className="text-muted-foreground text-xs font-normal uppercase tracking-wide">
          {label}
        </ItemTitle>
        <ItemDescription className="text-foreground text-sm">{value}</ItemDescription>
        {hint ? <ItemDescription className="text-xs">{hint}</ItemDescription> : null}
      </ItemContent>
    </Item>
  )
}

/**
 * A date of birth is a calendar date, not an instant. Rendering it in the
 * viewer's timezone would shift it a day either way depending on where they
 * are sitting, and a date of birth decides school entry and age of majority.
 */
export function formatDate(value: string | undefined): string {
  if (!value) {
    return 'Not recorded'
  }

  const [date] = value.split('T')

  return date ?? value
}
