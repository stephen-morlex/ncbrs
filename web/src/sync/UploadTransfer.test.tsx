import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import '@/i18n'
import { UploadTransfer } from './UploadTransfer'

const post = vi.fn()

vi.mock('@/api/useApi', () => ({
  useApiClient: () => ({ POST: post }),
}))

const sealed = {
  version: 'ncbrs-sealed-transfer-v1',
  keyId: 'moh-transfer-2026',
  ephemeralKey: 'AAAA',
  nonce: 'AAAA',
  ciphertext: 'AAAA',
  tag: 'AAAA',
}

function fileOf(content: string) {
  return new File([content], 'ncbrs-TAB-0A1B2C3D4E5F-20260928-0900.ncbrs-transfer.json', { type: 'application/json' })
}

function ok<T>(data: T) {
  return { data: { data }, error: undefined, response: { ok: true, status: 200 } }
}

async function upload(content: string) {
  const typist = userEvent.setup({ delay: null })
  render(
    <MemoryRouter initialEntries={['/records/transfer']}>
      <UploadTransfer />
    </MemoryRouter>,
  )
  await typist.upload(screen.getByLabelText(/transfer file from the tablet/i), fileOf(content))
  await typist.click(screen.getByRole('button', { name: /upload/i }))
}

beforeEach(() => {
  post.mockReset()
})

describe('UploadTransfer', () => {
  it('passes the sealed file on unread, in the usual envelope, and shows what became of each birth', async () => {
    post.mockResolvedValue(
      ok({
        syncBatchId: 'b', status: 'Reconciled', submitted: 3, registered: 1, duplicates: 1, rejected: 1,
        records: [
          { brn: '100001', status: 'Registered' },
          { brn: '100002', status: 'Duplicate' },
          { brn: '100003', status: 'Rejected', errors: [{ field: 'lateRegistration', message: 'Supporting evidence and a declarant are required.' }] },
        ],
      }),
    )

    await upload(JSON.stringify(sealed))

    const [path, { body }] = post.mock.calls[0]
    expect(path).toBe('/api/Sync/transfers')
    expect(body.data).toEqual(sealed)
    expect(body.meta.transactionId).toMatch(/^[0-9a-f-]{36}$/)
    expect(await screen.findByText('1 registered')).toBeInTheDocument()
    expect(screen.getByText('1 already held')).toBeInTheDocument()
    expect(screen.getByText('BRN 100003')).toBeInTheDocument()
    expect(screen.getByText(/Supporting evidence and a declarant are required/)).toBeInTheDocument()
    // Refused births are the tablet's to correct, and the officer is told so.
    expect(screen.getByText(/stay on the tablet/)).toBeInTheDocument()
  })

  it('names the wrong file before sending anything', async () => {
    await upload(JSON.stringify({ version: 'ncbrs-offline-transfer-v1' }))

    expect(await screen.findByText(/not a sealed transfer file/)).toBeInTheDocument()
    expect(post).not.toHaveBeenCalled()
  })

  it('says so when the file is not JSON at all', async () => {
    await upload('this is not a transfer file')

    expect(await screen.findByText(/could not be read/)).toBeInTheDocument()
    expect(post).not.toHaveBeenCalled()
  })

  it("shows the registry's reason when it refuses the file", async () => {
    post.mockResolvedValue({
      data: undefined,
      error: { meta: {}, data: { status: 400, title: 'Transfer file not opened.', errors: [{ field: 'file', message: 'The sealed file could not be opened: it has been altered.' }] } },
      response: { ok: false, status: 400 },
    })

    await upload(JSON.stringify(sealed))

    expect(await screen.findByText(/has been altered/)).toBeInTheDocument()
  })
})
