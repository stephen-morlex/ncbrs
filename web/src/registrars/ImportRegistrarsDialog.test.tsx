import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ImportRegistrarsDialog } from './ImportRegistrarsDialog'

const post = vi.fn()
const client = { POST: post }

vi.mock('@/api/useApi', () => ({
  useApiClient: () => client,
}))

function ok<T>(data: T) {
  return { data: { data }, error: undefined, response: { ok: true, status: 200 } }
}

const user = () => userEvent.setup({ delay: null })

const csv = 'username,facility,role\nachol.garang,RPH,FacilityRegistrar\n'

const row = {
  line: 2, username: 'achol.garang', displayName: 'Achol Garang', facilityName: 'Rejaf PHCU',
  role: 'FacilityRegistrar', problems: [], registrarId: null,
}

beforeEach(() => {
  post.mockReset()
})

describe('ImportRegistrarsDialog', () => {
  it('checks the file against the registrar endpoint, then adds the people', async () => {
    post.mockResolvedValueOnce(ok({ applied: false, valid: 1, invalid: 0, rows: [row] }))
    post.mockResolvedValueOnce(ok({ applied: true, valid: 1, invalid: 0, rows: [{ ...row, registrarId: 'reg-1' }] }))
    const onImported = vi.fn()
    const typist = user()
    render(<ImportRegistrarsDialog onClose={vi.fn()} onImported={onImported} />)

    await typist.upload(screen.getByLabelText('File'), new File([csv], 'staff.csv', { type: 'text/csv' }))
    await typist.click(screen.getByRole('button', { name: 'Check the file' }))

    expect(await screen.findByText('Facility registrar')).toBeInTheDocument()
    expect(screen.getByText('Rejaf PHCU')).toBeInTheDocument()
    expect(post).toHaveBeenCalledWith('/api/registrars/import', { body: { data: { csv, apply: false } } })

    await typist.click(screen.getByRole('button', { name: 'Add 1 person' }))

    await waitFor(() => expect(onImported).toHaveBeenCalledWith(1))
    expect(screen.getByText('1 person added')).toBeInTheDocument()
  })

  it('shows the limits a row breaks, and adds nobody', async () => {
    post.mockResolvedValue(ok({
      applied: false, valid: 0, invalid: 1,
      rows: [{ ...row, problems: ['The account does not hold the facility-registrar role in Keycloak.'] }],
    }))
    const typist = user()
    render(<ImportRegistrarsDialog onClose={vi.fn()} onImported={vi.fn()} />)

    await typist.upload(screen.getByLabelText('File'), new File([csv], 'staff.csv', { type: 'text/csv' }))
    await typist.click(screen.getByRole('button', { name: 'Check the file' }))

    expect(await screen.findByText(/does not hold the facility-registrar role/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /^Add / })).toBeNull()
  })
})
