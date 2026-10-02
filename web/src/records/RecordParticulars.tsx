import { Lock } from 'lucide-react'
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert'
import { Item, ItemContent, ItemDescription, ItemGroup, ItemTitle } from '@/components/ui/item'
import type { components } from '@/api/generated/api'
import { DocumentLabels, PlaceLabels } from './RegistrationSections'

type BirthRecord = components['schemas']['BirthRecordResponse']
type ParentDetails = components['schemas']['ParentDetails']

/**
 * What the fuller registration recorded: the place of birth, the parents in
 * detail, their marriage and the proof of address.
 *
 * **Only what was given is shown.** Almost all of it is optional, and a screen
 * of "Not recorded" rows trains a registrar to stop reading the rows that
 * matter. A record registered before these fields existed, or by a tablet not
 * yet upgraded, has no details at all and shows the parents' names alone.
 *
 * **Withheld is said, not left blank.** The lookup by number is open to any
 * registrar, because a family carries the number between facilities; addresses
 * and document numbers are not. A blank address would read as "none given",
 * which is a different fact.
 */
export function RecordParticulars({ record }: { record: BirthRecord }) {
  const details = record.details

  return (
    <div className="space-y-4">
      {details?.restricted ? (
        <Alert>
          <Lock />
          <AlertTitle>Some details are held by the registering facility</AlertTitle>
          <AlertDescription>
            Addresses, identity document numbers and certificate references are shown only to
            those who may act for the facility where this birth was registered.
          </AlertDescription>
        </Alert>
      ) : null}

      <ItemGroup>
        {details?.placeOfBirthKind ? (
          <Row
            label="Place of birth"
            value={[PlaceLabels[details.placeOfBirthKind], details.placeOfBirth]
              .filter(Boolean)
              .join(': ')}
          />
        ) : null}
        {details?.childGivenNames || details?.childSurname ? (
          <Row
            label="Child’s given names and surname"
            value={`${details.childGivenNames ?? '—'} · ${details.childSurname ?? '—'}`}
          />
        ) : null}
      </ItemGroup>

      <Parent title="Mother" fullName={record.motherFullName} details={details?.mother} />
      <Parent title="Father" fullName={record.fatherFullName} details={details?.father} />

      {details?.marriage?.date || details?.marriage?.certificateNumber ? (
        <Group title="The parents’ marriage">
          <Row label="Date" value={details.marriage.date} />
          <Row label="Certificate number" value={details.marriage.certificateNumber} />
        </Group>
      ) : null}

      {details?.proofOfAddress?.kind || details?.proofOfAddress?.reference ? (
        <Group title="Proof of address">
          <Row label="What was shown" value={details.proofOfAddress.kind} />
          <Row label="Reference" value={details.proofOfAddress.reference} />
        </Group>
      ) : null}
    </div>
  )
}

function Parent({
  title,
  fullName,
  details,
}: {
  title: string
  fullName: string | null | undefined
  details: ParentDetails | null | undefined
}) {
  if (!fullName && !details) {
    return null
  }

  return (
    <Group title={title}>
      <Row label="Name" value={fullName} />
      <Row label="Maiden surname" value={details?.maidenSurname} />
      <Row label="Date of birth" value={details?.dateOfBirth} />
      <Row label="Place of birth" value={details?.placeOfBirth} />
      <Row label="Job" value={details?.occupation} />
      <Row label="Address" value={details?.address} />
      {details?.documentType ? (
        <Row
          label="Identity document"
          value={[DocumentLabels[details.documentType], details.documentNumber]
            .filter(Boolean)
            .join(' ')}
        />
      ) : null}
    </Group>
  )
}

function Group({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <section className="space-y-2" aria-label={title}>
      <h3 className="text-sm font-medium">{title}</h3>
      <ItemGroup>{children}</ItemGroup>
    </section>
  )
}

function Row({ label, value }: { label: string; value: string | null | undefined }) {
  if (!value) {
    return null
  }

  return (
    <Item variant="outline" role="listitem">
      <ItemContent>
        <ItemTitle className="text-muted-foreground text-xs font-normal uppercase tracking-wide">
          {label}
        </ItemTitle>
        <ItemDescription className="text-foreground text-sm">{value}</ItemDescription>
      </ItemContent>
    </Item>
  )
}
