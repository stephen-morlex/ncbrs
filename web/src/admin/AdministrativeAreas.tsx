import { useState } from 'react'
import { useAuth } from 'react-oidc-context'
import { CircleCheck } from 'lucide-react'
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Card, CardContent } from '@/components/ui/card'
import type { components } from '@/api/generated/api'
import { realmRoles } from '@/auth/claims'
import { satisfies } from '@/auth/roles'
import { PageHeader } from '@/shell/PageHeader'
import { AddAreaForm } from './AddAreaForm'
import { AreaPicker } from './AreaPicker'

type Area = components['schemas']['AdministrativeAreaResponse']

/**
 * Browse South Sudan's administrative geography by drilling down the tree.
 * Also the working home of the dependent picker the facility and location
 * flows reuse — selecting a state filters the counties, a county its payams or
 * blocks, and so on, as far as the hierarchy is recorded.
 *
 * The Ministry can add an area inside the one chosen. The server enforces who
 * may; hiding the form from everyone else is a courtesy.
 */
export function AdministrativeAreas() {
  const auth = useAuth()
  const canAdd = satisfies(realmRoles(auth.user), 'CanManageFacilities')
  const [selected, setSelected] = useState<Area | null>(null)
  const [added, setAdded] = useState<Area | null>(null)

  // The picker is rebuilt after an addition so the new area appears in it.
  const [pickerKey, setPickerKey] = useState(0)

  function onAdded(area: Area) {
    setAdded(area)
    setSelected(null)
    setPickerKey((key) => key + 1)
  }

  return (
    <div className="mx-auto w-full max-w-3xl space-y-6">
      <PageHeader
        title="Administrative areas"
        description="South Sudan's geography — state, county, and the areas beneath. Select down the tree to find one."
      />

      {added ? (
        <Alert>
          <CircleCheck />
          <AlertTitle>
            {added.level} {added.name} added
          </AlertTitle>
          <AlertDescription>
            Its code is <span className="font-mono">{added.code}</span>. Choose it below to add places
            inside it, or to place a facility there.
          </AlertDescription>
        </Alert>
      ) : null}

      <Card>
        <CardContent className="pt-6">
          <AreaPicker key={pickerKey} onSelect={setSelected} />
        </CardContent>
      </Card>

      {selected ? (
        <Card>
          <CardContent className="space-y-6 pt-6">
            <div className="space-y-1">
              <p className="text-lg font-medium">{selected.name}</p>
              <Badge variant="secondary">{selected.level}</Badge>
              <p className="text-muted-foreground font-mono text-xs">{selected.code}</p>
            </div>
            {canAdd ? <AddAreaForm key={selected.administrativeAreaId} parent={selected} onAdded={onAdded} /> : null}
          </CardContent>
        </Card>
      ) : null}
    </div>
  )
}
