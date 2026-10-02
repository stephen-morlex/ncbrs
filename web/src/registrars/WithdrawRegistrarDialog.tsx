import { type FormEvent, useState } from 'react'
import { CircleAlert } from 'lucide-react'
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { Label } from '@/components/ui/label'
import { Spinner } from '@/components/ui/spinner'
import { Textarea } from '@/components/ui/textarea'
import type { components } from '@/api/generated/api'
import { type NcbrsError, toNcbrsError, unreachableError } from '@/api/errors'
import { useApiClient } from '@/api/useApi'

type Registrar = components['schemas']['RegistrarResponse']

/**
 * Withdraw someone who has stopped working here. What it does is said before
 * it is done: they can no longer sign in to the registry, and their PIN stops
 * unlocking each tablet at that tablet's next sync -- not before, because a
 * tablet out of signal cannot be told. They stay in the directory, because
 * every record they registered still names them.
 */
export function WithdrawRegistrarDialog({
  registrar,
  onClose,
  onWithdrawn,
}: {
  registrar: Registrar
  onClose: () => void
  onWithdrawn: () => void
}) {
  const api = useApiClient()
  const [reason, setReason] = useState('')
  const [error, setError] = useState<NcbrsError | null>(null)
  const [submitting, setSubmitting] = useState(false)

  async function submit(event: FormEvent) {
    event.preventDefault()
    setSubmitting(true)
    setError(null)

    try {
      const { error: failure, response } = await api.POST('/api/registrars/{registrarId}/withdraw', {
        params: { path: { registrarId: registrar.registrarId } },
        body: { data: { reason: reason.trim() } },
      })

      if (response.ok) {
        onWithdrawn()
        return
      }

      setError(toNcbrsError(failure, response.status))
    } catch (cause) {
      setError(unreachableError(cause))
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <Dialog open onOpenChange={(open) => (open ? undefined : onClose())}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Withdraw {registrar.displayName}</DialogTitle>
          <DialogDescription>
            They will no longer be able to sign in to the registry. Their PIN stops unlocking each
            tablet at {registrar.facilityName} when that tablet next syncs. They stay listed here,
            because the records they registered still name them.
          </DialogDescription>
        </DialogHeader>

        <form onSubmit={submit} className="space-y-4">
          {error ? (
            <Alert variant="destructive">
              <CircleAlert />
              <AlertTitle>{error.title}</AlertTitle>
              <AlertDescription>
                {error.unreachable
                  ? 'The registry did not answer. Nothing was changed.'
                  : error.fields.map((item) => item.message).join(' ')}
              </AlertDescription>
            </Alert>
          ) : null}

          <div className="grid gap-2">
            <Label htmlFor="withdraw-reason">Why</Label>
            <Textarea
              id="withdraw-reason"
              value={reason}
              onChange={(event) => setReason(event.target.value)}
              placeholder="For example: moved to another county; left the health service."
            />
            <p className="text-muted-foreground text-xs">Kept in the registry. Not shown in the directory.</p>
          </div>

          <DialogFooter>
            <Button type="button" variant="outline" onClick={onClose}>
              Cancel
            </Button>
            <Button type="submit" variant="destructive" disabled={!reason.trim() || submitting}>
              {submitting ? <Spinner /> : null}
              Withdraw
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  )
}
