import { type FormEvent, useState } from 'react'
import { BadgeCheck, CircleAlert, ShieldOff, ShieldX } from 'lucide-react'
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { Item, ItemContent, ItemDescription, ItemGroup, ItemTitle } from '@/components/ui/item'
import { Label } from '@/components/ui/label'
import { Spinner } from '@/components/ui/spinner'
import { Textarea } from '@/components/ui/textarea'
import type { components } from '@/api/generated/api'
import { type NcbrsError, toNcbrsError, unreachableError } from '@/api/errors'
import { useApiClient } from '@/api/useApi'
import { PageHeader } from '@/shell/PageHeader'
import { formatDate } from '@/records/RecordDetail'

type VerifyResult = components['schemas']['VerifyCertificateResponse']
type RevocationReason = components['schemas']['RevocationReason']

const RevocationLabels: Record<NonNullable<RevocationReason>, string> = {
  Amended: 'The register was corrected after this was issued',
  SupersededAsDuplicate: 'Superseded as a duplicate of another registration',
  RegistrationAnnulled: 'The registration was annulled',
}

/**
 * Check a certificate someone is holding, from the QR payload printed on it.
 *
 * **A signature alone is not enough.** It proves the document was genuinely
 * issued, but nothing printed on paper can know the register was corrected
 * afterwards — so verification is two checks, the signature and the revocation
 * list, and a revoked certificate reads as revoked however sound its
 * signature. The facts shown come from the payload the holder already
 * presents; nothing here looks a citizen up.
 */
export function VerifyCertificate() {
  const api = useApiClient()

  const [payload, setPayload] = useState('')
  const [result, setResult] = useState<VerifyResult | null>(null)
  const [error, setError] = useState<NcbrsError | null>(null)
  const [verifying, setVerifying] = useState(false)

  async function onSubmit(event: FormEvent) {
    event.preventDefault()

    const scanned = payload.trim()
    if (!scanned) {
      return
    }

    setVerifying(true)
    setError(null)
    setResult(null)

    try {
      const { data, error: failure, response } = await api.POST('/api/certificates/verify', {
        body: { data: { qrPayload: scanned } },
      })

      if (failure || !response.ok) {
        setError(toNcbrsError(failure, response.status))
        return
      }

      setResult(data?.data ?? null)
    } catch (cause) {
      setError(unreachableError(cause))
    } finally {
      setVerifying(false)
    }
  }

  return (
    <div className="mx-auto w-full max-w-3xl space-y-6">
      <PageHeader
        title="Verify a certificate"
        description="Paste the QR payload from a certificate to check it against the Ministry's signature and the revocation list."
      />

      <Card>
        <CardContent className="pt-6">
          <form onSubmit={onSubmit} className="grid gap-3">
            <Label htmlFor="qr-payload">Scanned QR payload</Label>
            <Textarea
              id="qr-payload"
              value={payload}
              onChange={(event) => setPayload(event.target.value)}
              placeholder="Paste the contents of the certificate's QR code"
              className="font-mono text-xs"
              rows={4}
            />
            <div>
              <Button type="submit" disabled={verifying || payload.trim().length === 0}>
                {verifying ? <Spinner /> : <BadgeCheck />}
                Verify
              </Button>
            </div>
          </form>
        </CardContent>
      </Card>

      {error ? (
        <Alert variant="destructive">
          <CircleAlert />
          <AlertTitle>{error.title}</AlertTitle>
          <AlertDescription>
            {error.unreachable
              ? 'The registry did not answer. Check the connection before trying again.'
              : error.fields.map((item) => item.message).join(' ') ||
                'That payload could not be read. Check it was pasted whole.'}
          </AlertDescription>
        </Alert>
      ) : null}

      {result ? <Verdict result={result} /> : null}
    </div>
  )
}

function Verdict({ result }: { result: VerifyResult }) {
  const facts = <CertificateFacts result={result} />

  // Revocation is checked whatever the signature says: a genuinely-signed
  // document whose register entry was later corrected must still read as no
  // longer valid.
  if (result.revoked) {
    return (
      <Alert variant="destructive">
        <ShieldOff />
        <AlertTitle>This certificate has been revoked</AlertTitle>
        <AlertDescription className="space-y-2">
          <p>
            {result.revocationReason ? RevocationLabels[result.revocationReason] : 'It is no longer valid.'}
            {result.revokedAtUtc ? ` (${formatDate(result.revokedAtUtc)})` : ''} Do not accept this
            document; a current certificate can be reissued from the register.
          </p>
          {facts}
        </AlertDescription>
      </Alert>
    )
  }

  if (result.valid) {
    return (
      <Alert>
        <BadgeCheck />
        <AlertTitle>Valid certificate</AlertTitle>
        <AlertDescription className="space-y-2">
          <p>The signature checks out and the certificate has not been revoked.</p>
          {facts}
        </AlertDescription>
      </Alert>
    )
  }

  return (
    <Alert variant="destructive">
      <ShieldX />
      <AlertTitle>This could not be verified</AlertTitle>
      <AlertDescription>
        {result.reason ||
          'The signature did not check out. This may not be a genuine certificate, or the payload was altered or incomplete.'}
      </AlertDescription>
    </Alert>
  )
}

/**
 * The facts read from the signed payload — not a registry lookup. Shown for a
 * valid or revoked result so the person at the counter can check the document
 * in their hand against what it claims.
 */
function CertificateFacts({ result }: { result: VerifyResult }) {
  if (!result.brn && !result.childFullName) {
    return null
  }

  return (
    <ItemGroup className="mt-2">
      {result.childFullName ? <Fact label="Name" value={result.childFullName} /> : null}
      {result.brn ? <Fact label="Registration number" value={result.brn} /> : null}
      {result.dateOfBirth ? <Fact label="Date of birth" value={formatDate(result.dateOfBirth)} /> : null}
      {result.sex ? <Fact label="Sex" value={result.sex} /> : null}
      {result.issueDateUtc ? <Fact label="Issued" value={formatDate(result.issueDateUtc)} /> : null}
    </ItemGroup>
  )
}

function Fact({ label, value }: { label: string; value: string }) {
  return (
    <Item variant="outline">
      <ItemContent>
        <ItemTitle className="text-muted-foreground text-xs font-normal uppercase tracking-wide">
          {label}
        </ItemTitle>
        <ItemDescription className="text-foreground text-sm">{value}</ItemDescription>
      </ItemContent>
    </Item>
  )
}
