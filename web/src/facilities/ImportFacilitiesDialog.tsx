import type { components } from '@/api/generated/api'
import { useApiClient } from '@/api/useApi'
import { CsvImportDialog } from '@/import/CsvImportDialog'

type Row = components['schemas']['FacilityImportRow']

/**
 * Add a district's facilities from one CSV file (plan §17 item 38). The
 * registry checks every row exactly as "Add a facility" would, plus what only a
 * file gets wrong (the same name or office code twice), before anything is
 * added; then adds the file whole or not at all.
 */
export function ImportFacilitiesDialog({ onClose, onImported }: { onClose: () => void; onImported: (count: number) => void }) {
  const api = useApiClient()

  return (
    <CsvImportDialog<Row>
      title="Add facilities from a file"
      description={
        <>
          A CSV file with the columns <span className="font-mono">name, tier, connectivity, area</span> and
          optionally <span className="font-mono">office_code</span>. The area is the code of its county, payam,
          boma or village. The registry checks every row before anything is added.
        </>
      }
      noun={['facility', 'facilities']}
      send={(csv, apply) => api.POST('/api/facilities/import', { body: { data: { csv, apply } } })}
      columns={[
        {
          header: 'Name',
          cell: (row) => (
            <>
              {row.name}
              {row.officeCode ? <span className="text-muted-foreground block font-mono text-xs">{row.officeCode}</span> : null}
            </>
          ),
        },
        {
          header: 'Where',
          cell: (row) => (
            <>
              {row.areaName ?? '—'}
              {row.countyCode ? <span className="text-muted-foreground block font-mono text-xs">{row.countyCode}</span> : null}
            </>
          ),
        },
      ]}
      done={(row) =>
        row.brnBlockStart != null && row.brnBlockEnd != null
          ? `Added: ${row.brnBlockStart.toLocaleString()}–${row.brnBlockEnd.toLocaleString()}`
          : 'Added'
      }
      onClose={onClose}
      onImported={onImported}
    />
  )
}
