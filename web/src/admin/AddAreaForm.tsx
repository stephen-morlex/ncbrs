import { type FormEvent, useState } from 'react'
import { CircleAlert } from 'lucide-react'
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
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

type Area = components['schemas']['AdministrativeAreaResponse']
type Level = Area['level']

const Tier: Record<Level, number> = {
  Country: 0,
  State: 1,
  County: 2,
  Payam: 3,
  Block: 3,
  Boma: 4,
  Quarter: 4,
  Village: 5,
}

const Creatable: Level[] = ['Payam', 'Block', 'Boma', 'Quarter', 'Village']
const Rural: Level[] = ['Payam', 'Boma']
const Urban: Level[] = ['Block', 'Quarter']

/**
 * The levels that may be added inside `parent`. The registry's own rules
 * (`AdministrativeLevels` in Contracts), restated so the form offers only what
 * it would accept: below a county, a strictly lower tier, and never a rural
 * area inside an urban one or the reverse. The registry checks again.
 */
export function childLevels(parent: Level): Level[] {
  if (Tier[parent] < Tier.County) {
    return []
  }

  return Creatable.filter(
    (child) =>
      Tier[child] > Tier[parent] &&
      !(Rural.includes(parent) && Urban.includes(child)) &&
      !(Urban.includes(parent) && Rural.includes(child)),
  )
}

const Shown = ['data.name', 'data.level', 'data.parentId'] as const

/**
 * Add a payam, block, boma, quarter or village inside the chosen area (plan
 * §17 item 31). The official dataset stops at the payam, so bomas and villages
 * are recorded as they are met, which is what lets a village post be placed
 * where it actually is. The Ministry's act, like adding a facility.
 *
 * **The code is not asked for.** The registry gives it, with a hyphen no
 * official p-code has, so a recorded place can never collide with one
 * published later.
 */
export function AddAreaForm({ parent, onAdded }: { parent: Area; onAdded: (area: Area) => void }) {
  const api = useApiClient()
  const levels = childLevels(parent.level)
  const [level, setLevel] = useState<Level | ''>(levels.length === 1 ? levels[0] : '')
  const [name, setName] = useState('')
  const [error, setError] = useState<NcbrsError | null>(null)
  const [submitting, setSubmitting] = useState(false)

  if (levels.length === 0) {
    return null
  }

  async function submit(event: FormEvent) {
    event.preventDefault()
    if (level === '' || name.trim() === '') {
      return
    }

    setSubmitting(true)
    setError(null)

    try {
      const { data, error: failure, response } = await api.POST('/api/administrative-areas', {
        body: { data: { parentId: parent.administrativeAreaId, level, name: name.trim() } },
      })

      if (response.ok && data?.data) {
        setName('')
        onAdded(data.data)
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
  const levelMessages = messagesFor(error, 'data.level')
  const otherMessages = unattachedMessages(error, Shown).concat(messagesFor(error, 'data.parentId'))

  return (
    <form onSubmit={submit} className="space-y-4" aria-labelledby="add-area-title">
      <div className="space-y-1">
        <h2 id="add-area-title" className="font-medium">
          Add an area inside {parent.name}
        </h2>
        <p className="text-muted-foreground text-sm">
          For a place the official list does not hold. The registry gives it a code.
        </p>
      </div>

      {otherMessages.length > 0 ? (
        <Alert variant="destructive">
          <CircleAlert />
          <AlertTitle>{error?.title ?? 'Could not add the area'}</AlertTitle>
          <AlertDescription>{otherMessages.join(' ')}</AlertDescription>
        </Alert>
      ) : null}

      <div className="flex flex-wrap items-start gap-4">
        <div className="grid gap-2">
          <Label htmlFor="add-area-level">Level</Label>
          <Select value={level} onValueChange={(value) => setLevel(value as Level)}>
            <SelectTrigger id="add-area-level" className="w-full sm:w-44" aria-invalid={levelMessages.length > 0 || undefined}>
              <SelectValue placeholder="Choose" />
            </SelectTrigger>
            <SelectContent>
              {levels.map((option) => (
                <SelectItem key={option} value={option}>
                  {option}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
          {levelMessages.length > 0 ? (
            <p className="text-destructive text-sm">{levelMessages.join(' ')}</p>
          ) : null}
        </div>

        <div className="grid min-w-0 flex-1 gap-2">
          <Label htmlFor="add-area-name">Name</Label>
          <Input
            id="add-area-name"
            value={name}
            onChange={(event) => setName(event.target.value)}
            aria-invalid={nameMessages.length > 0 || undefined}
            aria-describedby={nameMessages.length > 0 ? 'add-area-name-error' : undefined}
          />
          {nameMessages.length > 0 ? (
            <p id="add-area-name-error" className="text-destructive text-sm">
              {nameMessages.join(' ')}
            </p>
          ) : null}
        </div>
      </div>

      <Button type="submit" disabled={level === '' || name.trim() === '' || submitting}>
        {submitting ? <Spinner /> : null}
        Add area
      </Button>
    </form>
  )
}
