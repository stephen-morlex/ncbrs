import { useCallback, useEffect, useState } from 'react'
import { Button } from '@/components/ui/button'
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
import { useApiClient } from '@/api/useApi'

type Area = components['schemas']['AdministrativeAreaResponse']

type Level = { options: Area[]; selected: string }

/** The rural and urban names at the same tier read as one label on the dropdown that offers both. */
function labelFor(options: Area[]): string {
  const levels = [...new Set(options.map((option) => option.level))]
  return levels.join(' / ')
}

/**
 * A dependent administrative-area picker: State → County → Payam/Block →
 * Boma/Quarter → Village, one dropdown per level, each fetched from the
 * children of the selection above it. It stops when a selection has no
 * children, so a branch that ends early simply shows fewer dropdowns — the
 * flexibility of the hierarchy, surfaced.
 *
 * Emits the deepest area chosen (or null while nothing is selected), so a
 * caller can attach a facility to wherever in the tree it actually sits.
 */
export function AreaPicker({ onSelect }: { onSelect?: (area: Area | null) => void }) {
  const api = useApiClient()

  const [levels, setLevels] = useState<Level[]>([])
  const [loading, setLoading] = useState(true)
  const [failed, setFailed] = useState(false)

  /**
   * Areas, or a failure — the two kept apart deliberately.
   *
   * Returning an empty list for a failed request would render "no
   * administrative areas are available yet", which tells a registrar the
   * country has no recorded geography when the truth is that we could not ask.
   * It is the same rule the dashboard follows for a null indicator: absence of
   * an answer is not the answer zero.
   */
  const fetchAreas = useCallback(
    async (query: { level?: Area['level']; parentId?: string }): Promise<Area[] | null> => {
      try {
        const { data, response } = await api.GET('/api/administrative-areas', { params: { query } })
        return response.ok ? (data?.data ?? []) : null
      } catch {
        return null
      }
    },
    [api],
  )

  // The top of the tree the caller starts from is the states (and the three
  // state-equivalent administrative areas), not the country root itself.
  const loadStates = useCallback(async () => {
    setLoading(true)
    setFailed(false)

    const states = await fetchAreas({ level: 'State' })

    if (states === null) {
      setFailed(true)
      setLevels([])
    } else {
      setLevels(states.length > 0 ? [{ options: states, selected: '' }] : [])
    }

    setLoading(false)
  }, [fetchAreas])

  useEffect(() => {
    let cancelled = false
    void (async () => {
      const states = await fetchAreas({ level: 'State' })
      if (cancelled) {
        return
      }

      if (states === null) {
        setFailed(true)
        setLevels([])
      } else {
        setLevels(states.length > 0 ? [{ options: states, selected: '' }] : [])
      }

      setLoading(false)
    })()
    return () => {
      cancelled = true
    }
  }, [fetchAreas])

  const selectAt = useCallback(
    async (index: number, areaId: string) => {
      const chosen = levels[index]?.options.find((option) => option.administrativeAreaId === areaId) ?? null

      // Keep everything down to this level, set the selection, and drop any
      // deeper dropdowns — the choices under them no longer apply.
      const trimmed = levels
        .slice(0, index + 1)
        .map((level, i) => (i === index ? { ...level, selected: areaId } : level))

      setLevels(trimmed)
      onSelect?.(chosen)

      const children = await fetchAreas({ parentId: areaId })

      // A failed lookup must not look like "this area has nothing beneath it",
      // which is a real and ordinary answer here — plenty of branches end
      // early. Saying so is the difference between a picker that stops because
      // the tree stops and one that stopped because the network did.
      if (children === null) {
        setFailed(true)
        return
      }

      if (children.length > 0) {
        setLevels((current) =>
          // Only append if the selection is still the current deepest one.
          current.length === index + 1 ? [...current, { options: children, selected: '' }] : current,
        )
      }
    },
    [levels, onSelect, fetchAreas],
  )

  if (loading) {
    return <Spinner />
  }

  if (failed) {
    return (
      <div className="space-y-2" role="alert">
        <p className="text-sm">The administrative areas could not be loaded.</p>
        <p className="text-muted-foreground text-sm">
          This is a connection problem, not an empty register — do not read it as the areas being
          missing.
        </p>
        <Button variant="outline" size="sm" onClick={() => void loadStates()}>
          Try again
        </Button>
      </div>
    )
  }

  if (levels.length === 0) {
    return <p className="text-muted-foreground text-sm">No administrative areas are available yet.</p>
  }

  return (
    <div className="flex flex-wrap gap-4">
      {levels.map((level, index) => (
        <div key={index} className="grid gap-2">
          <Label htmlFor={`area-level-${index}`}>{labelFor(level.options)}</Label>
          <Select value={level.selected} onValueChange={(value) => void selectAt(index, value)}>
            <SelectTrigger id={`area-level-${index}`} className="w-56">
              <SelectValue placeholder="Choose" />
            </SelectTrigger>
            <SelectContent>
              {level.options.map((option) => (
                <SelectItem key={option.administrativeAreaId} value={option.administrativeAreaId}>
                  {option.name}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </div>
      ))}
    </div>
  )
}
