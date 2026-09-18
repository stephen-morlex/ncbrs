import { useState } from 'react'
import { Badge } from '@/components/ui/badge'
import { Card, CardContent } from '@/components/ui/card'
import type { components } from '@/api/generated/api'
import { PageHeader } from '@/shell/PageHeader'
import { AreaPicker } from './AreaPicker'

type Area = components['schemas']['AdministrativeAreaResponse']

/**
 * Browse South Sudan's administrative geography by drilling down the tree.
 * Also the working home of the dependent picker the facility and location
 * flows reuse — selecting a state filters the counties, a county its payams or
 * blocks, and so on, as far as the hierarchy is recorded.
 */
export function AdministrativeAreas() {
  const [selected, setSelected] = useState<Area | null>(null)

  return (
    <div className="mx-auto w-full max-w-3xl space-y-6">
      <PageHeader
        title="Administrative areas"
        description="South Sudan's geography — state, county, and the areas beneath. Select down the tree to find one."
      />

      <Card>
        <CardContent className="pt-6">
          <AreaPicker onSelect={setSelected} />
        </CardContent>
      </Card>

      {selected ? (
        <Card>
          <CardContent className="space-y-1 pt-6">
            <p className="text-lg font-medium">{selected.name}</p>
            <Badge variant="secondary">{selected.level}</Badge>
            <p className="text-muted-foreground font-mono text-xs">{selected.code}</p>
          </CardContent>
        </Card>
      ) : null}
    </div>
  )
}
