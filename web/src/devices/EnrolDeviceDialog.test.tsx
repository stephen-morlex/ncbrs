import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { EnrolDeviceDialog } from './EnrolDeviceDialog'

const post = vi.fn()

const client = { POST: post }

vi.mock('@/api/useApi', () => ({
  useApiClient: () => client,
}))

function created<T>(data: T) {
  return { data: { data }, error: undefined, response: { ok: true, status: 201 } }
}

function fail(status: number, body: unknown) {
  return { data: undefined, error: body, response: { ok: false, status } }
}

const user = () => userEvent.setup({ delay: null })

const facility = {
  facilityId: '0199a1b2-fac0-7000-8000-000000000001',
  name: 'Lusaka Central Clinic',
  tier: 'Hospital',
  districtId: 'lusaka',
  connectivityProfile: 'AlwaysOn',
  brnBlockStart: 100000,
  brnBlockEnd: 200000,
  brnBlockNextAvailable: 105000,
  brnRemaining: 95000,
  brnWarnBelow: 50,
  blockStatus: 'Healthy',
} as const

const onClose = vi.fn()
const onEnrolled = vi.fn()

function renderDialog() {
  render(
    <EnrolDeviceDialog
      facilities={[facility]}
      defaultFacilityId={facility.facilityId}
      onClose={onClose}
      onEnrolled={onEnrolled}
    />,
  )
}

beforeEach(() => {
  post.mockReset()
  onClose.mockReset()
  onEnrolled.mockReset()
})

describe('EnrolDeviceDialog', () => {
  it('warns against pasting a private key before anything is sent', () => {
    renderDialog()
    expect(screen.getByText(/never paste a private key/i)).toBeInTheDocument()
  })

  it('keeps enrol disabled until id and key are given', async () => {
    const typist = user()
    renderDialog()

    expect(screen.getByRole('button', { name: /enrol device/i })).toBeDisabled()

    await typist.type(screen.getByLabelText(/device id/i), 'tablet-003')
    await typist.type(screen.getByLabelText(/public key/i), '-----BEGIN PUBLIC KEY-----\nMFk...')

    expect(screen.getByRole('button', { name: /enrol device/i })).toBeEnabled()
  })

  it('enrols with the pasted public key', async () => {
    post.mockResolvedValue(created({ deviceId: 'tablet-003' }))

    const typist = user()
    renderDialog()

    await typist.type(screen.getByLabelText(/device id/i), 'tablet-003')
    await typist.type(screen.getByLabelText(/public key/i), '-----BEGIN PUBLIC KEY-----\nMFk...')
    await typist.click(screen.getByRole('button', { name: /enrol device/i }))

    await waitFor(() => expect(onEnrolled).toHaveBeenCalled())
    expect(post.mock.calls[0][0]).toBe('/api/devices')
    expect(post.mock.calls[0][1].body.data.deviceId).toBe('tablet-003')
    expect(post.mock.calls[0][1].body.data.publicKeyPem).toContain('PUBLIC KEY')
  })

  it('surfaces the refusal of a private key against the key field', async () => {
    post.mockResolvedValue(
      fail(400, {
        status: 400,
        title: 'Public key rejected.',
        errors: [
          {
            field: 'data.publicKeyPem',
            message: 'A private key was supplied. Enrol the public half only.',
          },
        ],
      }),
    )

    const typist = user()
    renderDialog()

    await typist.type(screen.getByLabelText(/device id/i), 'tablet-003')
    await typist.type(screen.getByLabelText(/public key/i), '-----BEGIN PRIVATE KEY-----')
    await typist.click(screen.getByRole('button', { name: /enrol device/i }))

    expect(await screen.findByText(/a private key was supplied/i)).toBeInTheDocument()
    expect(onEnrolled).not.toHaveBeenCalled()
  })

  it('surfaces a re-enrolment conflict against the device-id field', async () => {
    post.mockResolvedValue(
      fail(409, {
        status: 409,
        title: 'Device already enrolled.',
        errors: [
          {
            field: 'data.deviceId',
            message: "Device 'tablet-003' is already enrolled. Revoke the existing enrolment first.",
          },
        ],
      }),
    )

    const typist = user()
    renderDialog()

    await typist.type(screen.getByLabelText(/device id/i), 'tablet-003')
    await typist.type(screen.getByLabelText(/public key/i), '-----BEGIN PUBLIC KEY-----\nMFk...')
    await typist.click(screen.getByRole('button', { name: /enrol device/i }))

    expect(await screen.findByText(/already enrolled/i)).toBeInTheDocument()
    expect(screen.getByText(/revoke the existing enrolment first/i)).toBeInTheDocument()
  })
})
