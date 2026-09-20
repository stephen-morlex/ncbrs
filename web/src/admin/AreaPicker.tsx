import { useCallback, useEffect, useState } from 'react'
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

  const fetchAreas = useCallback(
    async (query: { level?: Area['level']; parentId?: string }): Promise<Area[]> => {
      try {
        const { data, response } = await api.GET('/api/administrative-areas', { params: { query } })
        return response.ok ? (data?.data ?? []) : []
      } catch {
        return []
      }
    },
    [api],
  )

  // The top of the tree the caller starts from is the states (and the three
  // state-equivalent administrative areas), not the country root itself.
  useEffect(() => {
    let cancelled = false
    void (async () => {
      const states = await fetchAreas({ level: 'State' })
      if (!cancelled) {
        setLevels(states.length > 0 ? [{ options: states, selected: '' }] : [])
        setLoading(false)
      }
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
