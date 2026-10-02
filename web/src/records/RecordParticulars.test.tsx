import { render, screen, within } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import type { components } from '@/api/generated/api'
import { RecordParticulars } from './RecordParticulars'

type BirthRecord = components['schemas']['BirthRecordResponse']
type RegistrationDetails = components['schemas']['RegistrationDetails']

function aRecord(details: Partial<RegistrationDetails> | null, overrides: Partial<BirthRecord> = {}): BirthRecord {
  return {
    birthRecordId: '0199a1b2-0001-7000-8000-000000000001',
    brn: '100001',
    childFullName: 'Ayen Akol Deng',
    motherFullName: 'Achol Deng',
    dateOfBirth: '2026-06-01T00:00:00Z',
    sex: 'Female',
    status: 'Confirmed',
    details:
      details === null
        ? null
        : {
            childGivenNames: 'Ayen Akol',
            childSurname: 'Deng',
            placeOfBirthKind: 'Home',
            placeOfBirth: 'Gumbo, near the borehole',
            mother: null,
            father: null,
            marriage: null,
            proofOfAddress: null,
            restricted: false,
            ...details,
          },
    ...overrides,
  } as BirthRecord
}

describe('RecordParticulars', () => {
  it('shows where the birth happened and the parents as recorded', () => {
    render(
      <RecordParticulars
        record={aRecord({
          mother: {
            givenNames: 'Achol',
            surname: 'Deng',
            maidenSurname: 'Garang',
            occupation: 'Teacher',
            address: 'Gumbo, Juba',
            documentType: 'NationalId',
            documentNumber: 'SS1234567',
          },
          marriage: { date: '2023-01-14', certificateNumber: 'M-22/2023' },
        })}
      />,
    )

    expect(screen.getByText('At home: Gumbo, near the borehole')).toBeInTheDocument()

    const mother = screen.getByRole('region', { name: 'Mother' })
    expect(within(mother).getByText('Achol Deng')).toBeInTheDocument()
    expect(within(mother).getByText('Garang')).toBeInTheDocument()
    expect(within(mother).getByText('Teacher')).toBeInTheDocument()
    expect(within(mother).getByText('National ID SS1234567')).toBeInTheDocument()

    expect(screen.getByText('M-22/2023')).toBeInTheDocument()
  })

  it('shows only what was given, not a screen of empty rows', () => {
    render(<RecordParticulars record={aRecord({})} />)

    // The mother is named on the record, so she appears -- with her name alone.
    const mother = screen.getByRole('region', { name: 'Mother' })
    expect(within(mother).getAllByRole('listitem')).toHaveLength(1)

    expect(screen.queryByRole('region', { name: 'Father' })).not.toBeInTheDocument()
    expect(screen.queryByRole('region', { name: /marriage/i })).not.toBeInTheDocument()
    expect(screen.queryByText(/not recorded/i)).not.toBeInTheDocument()
  })

  it('says details were withheld rather than leaving a blank that reads as none given', () => {
    render(
      <RecordParticulars
        record={aRecord({
          restricted: true,
          mother: { givenNames: 'Achol', surname: 'Deng', documentType: 'NationalId' },
        })}
      />,
    )

    expect(screen.getByText(/held by the registering facility/i)).toBeInTheDocument()
    expect(screen.getByText('National ID')).toBeInTheDocument()
  })

  it('shows a record registered in the original form with the parents’ names alone', () => {
    render(<RecordParticulars record={aRecord(null, { fatherFullName: 'Deng Garang' })} />)

    expect(within(screen.getByRole('region', { name: 'Father' })).getByText('Deng Garang')).toBeInTheDocument()
    expect(screen.queryByText(/place of birth/i)).not.toBeInTheDocument()
  })
})
