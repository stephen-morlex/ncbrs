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
import { Spinner } from '@/components/ui/spinner'
import type { components } from '@/api/generated/api'
import { type NcbrsError, toNcbrsError, unreachableError } from '@/api/errors'
import { useApiClient } from '@/api/useApi'

type Registrar = components['schemas']['RegistrarResponse']

/**
 * Clear the offline PIN of someone who has forgotten it. What happens is said
 * before it is done: the officer never learns or sets the new PIN. The
 * registrar sets it themselves, signed in on a tablet with their own account
 * while there is signal, and the old one stops unlocking each tablet at that
 * tablet's next sync -- not before, because a tablet out of signal cannot be
 * told.
 */
export function ResetPinDialog({
  registrar,
  onClose,
  onReset,
}: {
  registrar: Registrar
  onClose: () => void
  onReset: () => void
}) {
  const api = useApiClient()
  const [error, setError] = useState<NcbrsError | null>(null)
  const [submitting, setSubmitting] = useState(false)

  async function submit(event: FormEvent) {
    event.preventDefault()
    setSubmitting(true)
    setError(null)

    try {
      const { error: failure, response } = await api.POST('/api/registrars/{registrarId}/reset-device-pin', {
        params: { path: { registrarId: registrar.registrarId } },
      })

      if (response.ok) {
        onReset()
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
          <DialogTitle>Reset the PIN of {registrar.displayName}</DialogTitle>
          <DialogDescription>
            Their PIN is cleared. You do not choose the new one: they set it themselves on a tablet,
            signed in with their own account while there is signal. Their old PIN stops unlocking
            each tablet at {registrar.facilityName} when that tablet next syncs.
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

          <DialogFooter>
            <Button type="button" variant="outline" onClick={onClose}>
              Cancel
            </Button>
            <Button type="submit" disabled={submitting}>
              {submitting ? <Spinner /> : null}
              Reset PIN
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  )
}
