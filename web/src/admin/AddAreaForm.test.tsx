import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { AddAreaForm, childLevels } from './AddAreaForm'

const post = vi.fn()
const client = { POST: post }

vi.mock('@/api/useApi', () => ({
  useApiClient: () => client,
}))

const rejaf = { administrativeAreaId: 'area-rejaf', name: 'Rejaf', level: 'Payam' as const, code: 'SS010105', parentId: 'area-juba' }
const gumbo = { administrativeAreaId: 'area-gumbo', name: 'Gumbo', level: 'Boma' as const, code: 'SS010105-B01', parentId: 'area-rejaf' }

const user = () => userEvent.setup({ delay: null })

beforeEach(() => {
  post.mockReset()
})

describe('childLevels', () => {
  it('offers only what the registry accepts: below a county, lower, and never rural with urban', () => {
    expect(childLevels('State')).toEqual([])
    expect(childLevels('County')).toEqual(['Payam', 'Block', 'Boma', 'Quarter', 'Village'])
    expect(childLevels('Payam')).toEqual(['Boma', 'Village'])
    expect(childLevels('Block')).toEqual(['Quarter', 'Village'])
    expect(childLevels('Boma')).toEqual(['Village'])
    expect(childLevels('Village')).toEqual([])
  })
})

describe('AddAreaForm', () => {
  it('sends the parent, level and name, and never a code', async () => {
    post.mockResolvedValue({ data: { data: gumbo }, error: undefined, response: { ok: true, status: 201 } })
    const onAdded = vi.fn()
    const typist = user()
    render(<AddAreaForm parent={rejaf} onAdded={onAdded} />)

    await typist.click(screen.getByLabelText('Level'))
    await typist.click(await screen.findByRole('option', { name: 'Boma' }))
    await typist.type(screen.getByLabelText('Name'), ' Gumbo ')
    await typist.click(screen.getByRole('button', { name: 'Add area' }))

    await waitFor(() => expect(onAdded).toHaveBeenCalledWith(gumbo))
    expect(post.mock.calls[0][1].body.data).toEqual({ parentId: 'area-rejaf', level: 'Boma', name: 'Gumbo' })
  })

  it('chooses the level when there is only one', () => {
    render(<AddAreaForm parent={gumbo} onAdded={vi.fn()} />)

    expect(screen.getByLabelText('Level')).toHaveTextContent('Village')
  })

  it('is not offered where nothing can be added', () => {
    const { container } = render(
      <AddAreaForm parent={{ ...gumbo, level: 'Village', name: 'Lologo' }} onAdded={vi.fn()} />,
    )

    expect(container).toBeEmptyDOMElement()
  })

  it("puts the registry's refusal beside the name", async () => {
    post.mockResolvedValue({
      data: undefined,
      error: { status: 409, title: 'Name already used here.', errors: [{ field: 'data.name', message: "Rejaf already has an area called 'Gumbo'." }] },
      response: { ok: false, status: 409 },
    })
    const typist = user()
    render(<AddAreaForm parent={gumbo} onAdded={vi.fn()} />)

    await typist.type(screen.getByLabelText('Name'), 'Gumbo')
    await typist.click(screen.getByRole('button', { name: 'Add area' }))

    expect(await screen.findByText(/already has an area called/)).toBeInTheDocument()
    expect(screen.getByLabelText('Name')).toHaveAttribute('aria-invalid', 'true')
  })
})
