import { type FormEvent, useState } from 'react'
import { CircleAlert } from 'lucide-react'
import { AreaPicker } from '@/admin/AreaPicker'
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
type Area = components['schemas']['AdministrativeAreaResponse']
type Tier = components['schemas']['FacilityTier']
type Connectivity = components['schemas']['ConnectivityProfile']

const Tiers: { value: Tier; label: string }[] = [
  { value: 'Hospital', label: 'Hospital' },
  { value: 'Clinic', label: 'Clinic' },
  { value: 'VillageHealthPost', label: 'Village health post' },
]

const Profiles: { value: Connectivity; label: string }[] = [
  { value: 'AlwaysOn', label: 'Always on' },
  { value: 'Intermittent', label: 'Intermittent' },
  { value: 'OfflineFirst', label: 'Offline first (weeks without signal)' },
]

const Shown = ['data.name', 'data.administrativeAreaId'] as const

/**
 * Bring a facility into the registry. The Ministry's act.
 *
 * **Two things are not asked for, on purpose.** The county comes from where
 * the facility sits in the administrative tree, and the range of registration
 * numbers is allocated by the registry above every range already given. Both
 * are what keep a BRN unique while devices issue them offline; neither is
 * something to type.
 */
export function CreateFacilityDialog({
  onClose,
  onCreated,
}: {
  onClose: () => void
  onCreated: (facility: Facility) => void
}) {
  const api = useApiClient()
  const [name, setName] = useState('')
  const [tier, setTier] = useState<Tier | ''>('')
  const [profile, setProfile] = useState<Connectivity | ''>('')
  const [area, setArea] = useState<Area | null>(null)
  const [error, setError] = useState<NcbrsError | null>(null)
  const [submitting, setSubmitting] = useState(false)

  async function submit(event: FormEvent) {
    event.preventDefault()
    if (!area || !tier || !profile) {
      return
    }

    setSubmitting(true)
    setError(null)

    try {
      const { data, error: failure, response } = await api.POST('/api/facilities', {
        body: {
          data: {
            name: name.trim(),
            tier,
            connectivityProfile: profile,
            administrativeAreaId: area.administrativeAreaId,
          },
        },
      })

      if (response.ok && data?.data) {
        onCreated(data.data)
        return
      }

      setError(toNcbrsError(failure, response.status))
    } catch (cause) {
      setError(unreachableError(cause))
    } finally {
      setSubmitting(false)
    }
  }

  const nameMessages = messagesFor(error, 'data.name')
  const areaMessages = messagesFor(error, 'data.administrativeAreaId')
  const otherMessages = unattachedMessages(error, Shown)
  const canSubmit = name.trim().length > 0 && tier !== '' && profile !== '' && area !== null

  return (
    <Dialog open onOpenChange={(open) => (open ? undefined : onClose())}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Add a facility</DialogTitle>
          <DialogDescription>
            The registry places it in its county from where you put it, and gives it a range of
            registration numbers no other facility holds.
          </DialogDescription>
        </DialogHeader>

        <form onSubmit={submit} className="space-y-4">
          {otherMessages.length > 0 ? (
            <Alert variant="destructive">
              <CircleAlert />
              <AlertTitle>{error?.title ?? 'Could not add the facility'}</AlertTitle>
              <AlertDescription>{otherMessages.join(' ')}</AlertDescription>
            </Alert>
          ) : null}

          <div className="grid gap-2">
            <Label htmlFor="facility-name">Name</Label>
            <Input
              id="facility-name"
              value={name}
              onChange={(event) => setName(event.target.value)}
              aria-invalid={nameMessages.length > 0 || undefined}
              aria-describedby={nameMessages.length > 0 ? 'facility-name-error' : undefined}
            />
            {nameMessages.length > 0 ? (
              <p id="facility-name-error" className="text-destructive text-sm">
                {nameMessages.join(' ')}
              </p>
            ) : null}
          </div>

          <div className="grid gap-2">
            <Label htmlFor="facility-tier">Tier</Label>
            <Select value={tier} onValueChange={(value) => setTier(value as Tier)}>
              <SelectTrigger id="facility-tier">
                <SelectValue placeholder="Choose a tier" />
              </SelectTrigger>
              <SelectContent>
                {Tiers.map((option) => (
                  <SelectItem key={option.value} value={option.value}>
                    {option.label}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>

          <div className="grid gap-2">
            <Label htmlFor="facility-connectivity">Connectivity</Label>
            <Select value={profile} onValueChange={(value) => setProfile(value as Connectivity)}>
              <SelectTrigger id="facility-connectivity">
                <SelectValue placeholder="How often does it have signal?" />
              </SelectTrigger>
              <SelectContent>
                {Profiles.map((option) => (
                  <SelectItem key={option.value} value={option.value}>
                    {option.label}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
            <p className="text-muted-foreground text-xs">
              Decides how long it may be silent before its district is alerted.
            </p>
          </div>

          <div className="grid gap-2">
            <AreaPicker onSelect={setArea} />
            {areaMessages.length > 0 ? (
              <p className="text-destructive text-sm">{areaMessages.join(' ')}</p>
            ) : null}
          </div>

          <DialogFooter>
            <Button type="button" variant="outline" onClick={onClose}>
              Cancel
            </Button>
            <Button type="submit" disabled={!canSubmit || submitting}>
              {submitting ? <Spinner /> : null}
              Add facility
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  )
}
