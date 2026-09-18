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
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import { Spinner } from '@/components/ui/spinner'
import { Textarea } from '@/components/ui/textarea'
import type { components } from '@/api/generated/api'
import {
  type NcbrsError,
  messagesFor,
  toNcbrsError,
  unattachedMessages,
  unreachableError,
} from '@/api/errors'
import { useApiClient } from '@/api/useApi'

type Facility = components['schemas']['FacilityResponse']

const Shown = ['data.deviceId', 'data.facilityId', 'data.publicKeyPem'] as const

/**
 * Enrol a device against a facility.
 *
 * **The public key only.** Enrolment records the public half of the device's
 * key so the centre can later check a signature; a private key reaching a
 * server proves nothing and is refused outright by the API — this form says so
 * before the paste, and surfaces the refusal plainly if it happens anyway.
 *
 * Re-enrolling an existing device id is refused too (409): silently replacing
 * the key would take over an identity every record is attributed to. Replacing
 * a device means revoking the old enrolment first, which the message says.
 */
export function EnrolDeviceDialog({
  facilities,
  defaultFacilityId,
  onClose,
  onEnrolled,
}: {
  facilities: Facility[]
  defaultFacilityId: string
  onClose: () => void
  onEnrolled: () => void
}) {
  const api = useApiClient()

  const [facilityId, setFacilityId] = useState(defaultFacilityId)
  const [deviceId, setDeviceId] = useState('')
  const [label, setLabel] = useState('')
  const [publicKeyPem, setPublicKeyPem] = useState('')
  const [error, setError] = useState<NcbrsError | null>(null)
  const [submitting, setSubmitting] = useState(false)

  async function submit(event: FormEvent) {
    event.preventDefault()
    setSubmitting(true)
    setError(null)

    try {
      const { error: failure, response } = await api.POST('/api/devices', {
        body: {
          data: {
            deviceId: deviceId.trim(),
            facilityId,
            publicKeyPem: publicKeyPem.trim(),
            label: label.trim() || null,
          },
        },
      })

      if (response.ok) {
        onEnrolled()
        return
      }

      setError(toNcbrsError(failure, response.status))
    } catch (cause) {
      setError(unreachableError(cause))
    } finally {
      setSubmitting(false)
    }
  }

  const deviceMessages = messagesFor(error, 'data.deviceId')
  const facilityMessages = messagesFor(error, 'data.facilityId')
  const keyMessages = messagesFor(error, 'data.publicKeyPem')
  const otherMessages = unattachedMessages(error, Shown)

  const canSubmit =
    facilityId.length > 0 && deviceId.trim().length > 0 && publicKeyPem.trim().length > 0

  return (
    <Dialog open onOpenChange={(open) => (open ? undefined : onClose())}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Enrol a device</DialogTitle>
          <DialogDescription>
            Register a tablet or terminal so its births can be attributed and verified.
          </DialogDescription>
        </DialogHeader>

        <form onSubmit={submit} className="space-y-4">
          {otherMessages.length > 0 ? (
            <Alert variant="destructive">
              <CircleAlert />
              <AlertTitle>{error?.title ?? 'Could not enrol'}</AlertTitle>
              <AlertDescription>{otherMessages.join(' ')}</AlertDescription>
            </Alert>
          ) : null}

          <div className="grid gap-2">
            <Label htmlFor="enrol-facility">Facility</Label>
            <Select value={facilityId} onValueChange={setFacilityId}>
              <SelectTrigger id="enrol-facility" aria-invalid={facilityMessages.length > 0 || undefined}>
                <SelectValue placeholder="Choose a facility" />
              </SelectTrigger>
              <SelectContent>
                {facilities.map((facility) => (
                  <SelectItem key={facility.facilityId} value={facility.facilityId}>
                    {facility.name}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
            {facilityMessages.length > 0 ? (
              <p className="text-destructive text-sm">{facilityMessages.join(' ')}</p>
            ) : null}
          </div>

          <div className="grid gap-2">
            <Label htmlFor="enrol-device-id">Device id</Label>
            <Input
              id="enrol-device-id"
              value={deviceId}
              onChange={(event) => setDeviceId(event.target.value)}
              placeholder="The identifier the device will sync under"
              autoComplete="off"
              aria-invalid={deviceMessages.length > 0 || undefined}
            />
            {deviceMessages.length > 0 ? (
              <p className="text-destructive text-sm">{deviceMessages.join(' ')}</p>
            ) : null}
          </div>

          <div className="grid gap-2">
            <Label htmlFor="enrol-label">
              Label <span className="text-muted-foreground font-normal">— optional</span>
            </Label>
            <Input
              id="enrol-label"
              value={label}
              onChange={(event) => setLabel(event.target.value)}
              placeholder="e.g. Maternity ward tablet"
              autoComplete="off"
            />
          </div>

          <div className="grid gap-2">
            <Label htmlFor="enrol-key">Public key (PEM)</Label>
            <Textarea
              id="enrol-key"
              value={publicKeyPem}
              onChange={(event) => setPublicKeyPem(event.target.value)}
              placeholder="-----BEGIN PUBLIC KEY-----"
              className="font-mono text-xs"
              rows={5}
              aria-invalid={keyMessages.length > 0 || undefined}
            />
            {/* Said before the paste, not only after the server refuses one. */}
            <p className="text-muted-foreground text-xs">
              The device's public key only. Never paste a private key — it must never leave the
              device.
            </p>
            {keyMessages.length > 0 ? (
              <p className="text-destructive text-sm">{keyMessages.join(' ')}</p>
            ) : null}
          </div>

          <DialogFooter>
            <Button type="button" variant="outline" onClick={onClose} disabled={submitting}>
              Cancel
            </Button>
            <Button type="submit" disabled={submitting || !canSubmit}>
              {submitting ? <Spinner /> : null}
              Enrol device
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  )
}
