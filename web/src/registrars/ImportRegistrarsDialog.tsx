import type { components } from '@/api/generated/api'
import { useApiClient } from '@/api/useApi'
import { CsvImportDialog } from '@/import/CsvImportDialog'

type Row = components['schemas']['RegistrarImportRow']

const RoleLabels: Record<NonNullable<Row['role']>, string> = {
  FacilityRegistrar: 'Facility registrar',
  CommunityHealthWorker: 'Community health worker',
  DistrictOfficer: 'District officer',
  MinistryAdmin: 'Ministry admin',
}

/**
 * Add the people waiting in the queue from one CSV file (plan §17 item 38): on
 * a training day everyone signs in once, and this binds them all. Each row is
 * held to the same limits as adding one person (your county, facility staff
 * only unless you are the Ministry, a role they hold in Keycloak), checked
 * before anyone is added, and the file is added whole or not at all.
 */
export function ImportRegistrarsDialog({ onClose, onImported }: { onClose: () => void; onImported: (count: number) => void }) {
  const api = useApiClient()

  return (
    <CsvImportDialog<Row>
      title="Add people from a file"
      description={
        <>
          A CSV file with the columns <span className="font-mono">username, facility, role</span> and
          optionally <span className="font-mono">display_name</span>. The facility is its office code or its
          name. Each person must have signed in once, so they are waiting here.
        </>
      }
      noun={['person', 'people']}
      send={(csv, apply) => api.POST('/api/registrars/import', { body: { data: { csv, apply } } })}
      columns={[
        {
          header: 'Person',
          cell: (row) => (
            <>
              {row.displayName ?? row.username}
              <span className="text-muted-foreground block text-xs">{row.username}</span>
            </>
          ),
        },
        { header: 'Facility', cell: (row) => row.facilityName ?? '—' },
        { header: 'Role', cell: (row) => (row.role ? RoleLabels[row.role] : '—') },
      ]}
      done={() => 'Added'}
      onClose={onClose}
      onImported={onImported}
    />
  )
}
