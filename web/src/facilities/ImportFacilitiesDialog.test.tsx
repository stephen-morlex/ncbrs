import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ImportFacilitiesDialog } from './ImportFacilitiesDialog'

const post = vi.fn()
const client = { POST: post }

vi.mock('@/api/useApi', () => ({
  useApiClient: () => client,
}))

function ok<T>(data: T) {
  return { data: { data }, error: undefined, response: { ok: true, status: 200 } }
}

const user = () => userEvent.setup({ delay: null })

const csv = 'name,tier,connectivity,area\nRejaf PHCU,Clinic,OfflineFirst,SS010101\n'

const readyRow = {
  line: 2, name: 'Rejaf PHCU', areaName: 'Rejaf', countyCode: 'SS0101', officeCode: null,
  problems: [], facilityId: null, brnBlockStart: null, brnBlockEnd: null,
}

async function chooseFile(typist: ReturnType<typeof user>) {
  await typist.upload(screen.getByLabelText('File'), new File([csv], 'juba.csv', { type: 'text/csv' }))
}

beforeEach(() => {
  post.mockReset()
})

describe('ImportFacilitiesDialog', () => {
  it('checks the file first, and only then offers to add it', async () => {
    post.mockResolvedValueOnce(ok({ applied: false, valid: 1, invalid: 0, rows: [readyRow] }))
    post.mockResolvedValueOnce(ok({
      applied: true, valid: 1, invalid: 0,
      rows: [{ ...readyRow, facilityId: 'fac-1', brnBlockStart: 100000, brnBlockEnd: 199999 }],
    }))
    const onImported = vi.fn()
    const typist = user()
    render(<ImportFacilitiesDialog onClose={vi.fn()} onImported={onImported} />)

    await chooseFile(typist)
    await typist.click(screen.getByRole('button', { name: 'Check the file' }))

    expect(await screen.findByText('juba.csv: all 1 rows can be added')).toBeInTheDocument()
    expect(post.mock.calls[0][1].body.data).toEqual({ csv, apply: false })

    await typist.click(screen.getByRole('button', { name: 'Add 1 facility' }))

    await waitFor(() => expect(onImported).toHaveBeenCalledWith(1))
    expect(post.mock.calls[1][1].body.data).toEqual({ csv, apply: true })
    expect(screen.getByText('Added: 100,000–199,999')).toBeInTheDocument()
  })

  it('shows each row that needs correcting, and offers nothing to add', async () => {
    post.mockResolvedValue(ok({
      applied: false, valid: 0, invalid: 1,
      rows: [{ ...readyRow, problems: ["No administrative area has the code 'SS9999'."] }],
    }))
    const typist = user()
    render(<ImportFacilitiesDialog onClose={vi.fn()} onImported={vi.fn()} />)

    await chooseFile(typist)
    await typist.click(screen.getByRole('button', { name: 'Check the file' }))

    expect(await screen.findByText(/No administrative area has the code/)).toBeInTheDocument()
    expect(screen.getByText('1 of 1 rows need correcting')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /^Add / })).toBeNull()
  })

  it('cannot check before a file is chosen', () => {
    render(<ImportFacilitiesDialog onClose={vi.fn()} onImported={vi.fn()} />)

    expect(screen.getByRole('button', { name: 'Check the file' })).toBeDisabled()
  })
})
