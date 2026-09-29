import { type FormEvent, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { CircleAlert, FileUp, PackageCheck } from 'lucide-react'
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Item, ItemContent, ItemDescription, ItemGroup, ItemTitle } from '@/components/ui/item'
import { Label } from '@/components/ui/label'
import { Spinner } from '@/components/ui/spinner'
import type { components } from '@/api/generated/api'
import { type NcbrsError, toNcbrsError, unreachableError } from '@/api/errors'
import { useApiClient } from '@/api/useApi'
import { PageHeader } from '@/shell/PageHeader'

type SealedTransferFile = components['schemas']['SealedTransferFile']
type SyncBatchResponse = components['schemas']['SyncBatchResponse']

/** The version every sealed file carries (`SealedTransfer.CurrentVersion`). */
const SealedVersion = 'ncbrs-sealed-transfer-v1'

/**
 * Upload a sealed transfer file a tablet saved to a USB stick or card (WS-H2).
 *
 * The file is sealed to the registry: this page cannot read it, and neither
 * can anyone who carried it. It is passed on as it is, and the registry opens
 * it and treats the births inside exactly as a sync from the tablet — the
 * tablet's own signature is what vouches for them, not whoever uploads.
 *
 * What comes back is the per-birth answer. Refused births cannot be corrected
 * here: they stay on the tablet, which is where the registrar corrects them,
 * so the page says so rather than leaving the officer to guess.
 */
export function UploadTransfer() {
  const { t } = useTranslation()
  const api = useApiClient()

  const [file, setFile] = useState<File | null>(null)
  const [result, setResult] = useState<SyncBatchResponse | null>(null)
  const [error, setError] = useState<NcbrsError | null>(null)
  const [problem, setProblem] = useState<string | null>(null)
  const [uploading, setUploading] = useState(false)

  async function onSubmit(event: FormEvent) {
    event.preventDefault()
    if (!file) {
      return
    }

    setUploading(true)
    setResult(null)
    setError(null)
    setProblem(null)

    try {
      let sealed: SealedTransferFile
      try {
        sealed = JSON.parse(await file.text()) as SealedTransferFile
      } catch {
        setProblem(t('transfer.unreadable'))
        return
      }

      // Checked here only to name the mistake early (the wrong file chosen);
      // the registry decides whether it opens.
      if (sealed?.version !== SealedVersion) {
        setProblem(t('transfer.notSealed'))
        return
      }

      const { data, error: failure, response } = await api.POST('/api/Sync/transfers', {
        body: { meta: { transactionId: crypto.randomUUID() }, data: sealed },
      })

      if (failure || !response.ok) {
        setError(toNcbrsError(failure, response.status))
        return
      }

      setResult(data?.data ?? null)
    } catch (cause) {
      setError(unreachableError(cause))
    } finally {
      setUploading(false)
    }
  }

  return (
    <div className="mx-auto w-full max-w-3xl space-y-6">
      <PageHeader title={t('transfer.title')} description={t('transfer.description')} />

      <Card>
        <CardContent className="pt-6">
          <form onSubmit={onSubmit} className="grid gap-3">
            <Label htmlFor="transfer-file">{t('transfer.fileLabel')}</Label>
            <Input
              id="transfer-file"
              type="file"
              accept=".json,application/json"
              aria-describedby="transfer-file-hint"
              onChange={(event) => setFile(event.target.files?.[0] ?? null)}
            />
            <p id="transfer-file-hint" className="text-muted-foreground text-sm">
              {t('transfer.fileHint')}
            </p>
            <div>
              <Button type="submit" disabled={uploading || !file}>
                {uploading ? <Spinner /> : <FileUp />}
                {t('transfer.upload')}
              </Button>
            </div>
          </form>
        </CardContent>
      </Card>

      {problem ? (
        <Alert variant="destructive">
          <CircleAlert />
          <AlertTitle>{t('transfer.refusedTitle')}</AlertTitle>
          <AlertDescription>{problem}</AlertDescription>
        </Alert>
      ) : null}

      {error ? (
        <Alert variant="destructive">
          <CircleAlert />
          <AlertTitle>{error.unreachable ? error.title : t('transfer.refusedTitle')}</AlertTitle>
          <AlertDescription>
            {error.unreachable ? t('transfer.unreachable') : error.fields.map((item) => item.message).join(' ')}
          </AlertDescription>
        </Alert>
      ) : null}

      {result ? <Outcome result={result} /> : null}
    </div>
  )
}

function Outcome({ result }: { result: SyncBatchResponse }) {
  const { t } = useTranslation()
  const refused = result.records.filter((record) => record.status === 'Rejected')

  return (
    <div className="space-y-4">
      <Alert>
        <PackageCheck />
        <AlertTitle>{t('transfer.resultTitle')}</AlertTitle>
        <AlertDescription className="space-y-2">
          <div className="flex flex-wrap gap-2">
            <Badge>{t('transfer.registered', { count: result.registered })}</Badge>
            <Badge variant="secondary">{t('transfer.duplicates', { count: result.duplicates })}</Badge>
            <Badge variant={result.rejected > 0 ? 'destructive' : 'secondary'}>
              {t('transfer.rejected', { count: result.rejected })}
            </Badge>
          </div>
          {result.duplicates > 0 ? <p>{t('transfer.duplicatesNote')}</p> : null}
        </AlertDescription>
      </Alert>

      {refused.length > 0 ? (
        <section aria-labelledby="refused-births" className="space-y-2">
          <h2 id="refused-births" className="font-medium">
            {t('transfer.rejectedTitle')}
          </h2>
          <p className="text-muted-foreground text-sm">{t('transfer.rejectedNote')}</p>
          <ItemGroup>
            {refused.map((record) => (
              <Item key={record.brn} variant="outline" role="listitem">
                <ItemContent>
                  <ItemTitle>{t('transfer.rejectedItem', { brn: record.brn })}</ItemTitle>
                  <ItemDescription>{(record.errors ?? []).map((error) => error.message).join(' ')}</ItemDescription>
                </ItemContent>
              </Item>
            ))}
          </ItemGroup>
        </section>
      ) : null}
    </div>
  )
}
