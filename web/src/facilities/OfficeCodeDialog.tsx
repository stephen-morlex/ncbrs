import { type FormEvent, useState } from 'react'
import { CircleAlert, TriangleAlert } from 'lucide-react'
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
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Spinner } from '@/components/ui/spinner'
import type { components } from '@/api/generated/api'
import { type NcbrsError, toNcbrsError, unreachableError } from '@/api/errors'
import { useApiClient } from '@/api/useApi'

type Facility = components['schemas']['FacilityResponse']

/** Two to six capitals or digits: the registry's rule, checked there too. */
const OfficeCodeShape = /^[A-Z0-9]{2,6}$/

/**
 * Give a facility its office code. The Ministry's act, and a one-way one.
 *
 * **It is said plainly before it is done.** Every registration number the
 * facility issues afterwards carries the code, so the registry refuses to
 * change it: a changed code would leave numbers in families' hands naming an
 * office the register no longer knows. A typo here is permanent.
 */
export function OfficeCodeDialog({
  facility,
  onClose,
  onSet,
}: {
  facility: Facility
  onClose: () => void
  onSet: (facility: Facility) => void
}) {
  const api = useApiClient()
  const [code, setCode] = useState('')
  const [error, setError] = useState<NcbrsError | null>(null)
  const [submitting, setSubmitting] = useState(false)

  const valid = OfficeCodeShape.test(code)

  async function submit(event: FormEvent) {
    event.preventDefault()
    if (!valid) {
      return
    }

    setSubmitting(true)
    setError(null)

    try {
      const { data, error: failure, response } = await api.PUT('/api/facilities/{facilityId}/office-code', {
        params: { path: { facilityId: facility.facilityId } },
        body: { data: { officeCode: code } },
      })

      if (response.ok && data?.data) {
        onSet(data.data)
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
          <DialogTitle>Give {facility.name} an office code</DialogTitle>
          <DialogDescription>
            Its registration numbers will read SS-{code || 'CODE'}-{new Date().getFullYear()}-000001-…
          </DialogDescription>
        </DialogHeader>

        <form onSubmit={submit} className="space-y-4">
          {error ? (
            <Alert variant="destructive">
              <CircleAlert />
              <AlertTitle>{error.title}</AlertTitle>
              <AlertDescription>
                {error.unreachable
                  ? 'The registry did not answer. Nothing was changed; try again.'
                  : error.fields.map((item) => item.message).join(' ')}
              </AlertDescription>
            </Alert>
          ) : null}

          <div className="grid gap-2">
            <Label htmlFor="office-code">Office code</Label>
            <Input
              id="office-code"
              value={code}
              onChange={(event) => setCode(event.target.value.trim().toUpperCase())}
              maxLength={6}
              className="w-40 font-mono uppercase"
              placeholder="JTH01"
              aria-invalid={(code !== '' && !valid) || undefined}
              aria-describedby="office-code-help"
            />
            <p id="office-code-help" className="text-muted-foreground text-xs">
              Two to six letters or digits, not used by any other facility.
            </p>
          </div>

          <Alert>
            <TriangleAlert />
            <AlertTitle>This cannot be changed later</AlertTitle>
            <AlertDescription>
              Every number the facility issues from now on carries this code. Numbers it has already
              issued keep working as they are.
            </AlertDescription>
          </Alert>

          <DialogFooter>
            <Button type="button" variant="outline" onClick={onClose} disabled={submitting}>
              Cancel
            </Button>
            <Button type="submit" disabled={!valid || submitting}>
              {submitting ? <Spinner /> : null}
              Give the code
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  )
}
