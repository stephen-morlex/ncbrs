import { type FormEvent, useCallback, useEffect, useState } from 'react'
import { CircleAlert, Search, TriangleAlert, Users } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import {
  Empty,
  EmptyContent,
  EmptyDescription,
  EmptyHeader,
  EmptyMedia,
  EmptyTitle,
} from '@/components/ui/empty'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import { Skeleton } from '@/components/ui/skeleton'
import { Spinner } from '@/components/ui/spinner'
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table'
import type { components } from '@/api/generated/api'
import { type NcbrsError, toNcbrsError, unreachableError } from '@/api/errors'
import { useApiClient } from '@/api/useApi'
import { PageHeader } from '@/shell/PageHeader'

type Registrar = components['schemas']['RegistrarResponse']
type RegistrarRole = components['schemas']['RegistrarRole']
type Facility = components['schemas']['FacilityResponse']

const AllFacilities = 'all'

const RoleLabels: Record<RegistrarRole, string> = {
  FacilityRegistrar: 'Facility registrar',
  CommunityHealthWorker: 'Community health worker',
  DistrictOfficer: 'District officer',
  MinistryAdmin: 'Ministry admin',
}

/**
 * Who the people acting on the register are, in the caller's district — or
 * nationally, for the Ministry.
 *
 * The queues and histories already name the registrar beside each id, so this
 * is not there to decode them. It answers the other question: **who is
 * provisioned here** — which a district officer has to be able to ask before
 * they can notice an account that should have been withdrawn. Listing is an
 * oversight act, so the screen sits behind the same role as device enrolment.
 */
export function RegistrarDirectory() {
  const api = useApiClient()

  const [rows, setRows] = useState<Registrar[] | null>(null)
  const [total, setTotal] = useState(0)
  const [nextCursor, setNextCursor] = useState<string | null>(null)
  const [error, setError] = useState<NcbrsError | null>(null)
  const [loading, setLoading] = useState(true)
  const [loadingMore, setLoadingMore] = useState(false)

  const [facilities, setFacilities] = useState<Facility[]>([])
  const [facilityId, setFacilityId] = useState<string>(AllFacilities)

  // The applied search term, separate from what is being typed: the query only
  // runs on submit, so a directory of hundreds is not refetched on every key.
  const [nameInput, setNameInput] = useState('')
  const [name, setName] = useState('')

  const load = useCallback(
    async (after: string | null, facility: string, search: string) => {
      if (after) {
        setLoadingMore(true)
      } else {
        setLoading(true)
      }
      setError(null)

      try {
        const { data, error: failure, response } = await api.GET('/api/registrars', {
          params: {
            query: {
              limit: 25,
              ...(after ? { after } : {}),
              ...(facility !== AllFacilities ? { facilityId: facility } : {}),
              ...(search ? { name: search } : {}),
            },
          },
        })

        if (failure || !response.ok) {
          setError(toNcbrsError(failure, response.status))
          return
        }

        const page = data?.data
        setRows((current) =>
          after ? [...(current ?? []), ...(page?.items ?? [])] : (page?.items ?? []),
        )
        setTotal(page?.total ?? 0)
        setNextCursor(page?.nextCursor ?? null)
      } catch (cause) {
        setError(unreachableError(cause))
      } finally {
        setLoading(false)
        setLoadingMore(false)
      }
    },
    [api],
  )

  useEffect(() => {
    let cancelled = false

    void (async () => {
      try {
        const { data, response } = await api.GET('/api/facilities', {
          params: { query: { limit: 200 } },
        })

        if (!cancelled && response.ok) {
          setFacilities(data?.data?.items ?? [])
        }
      } catch {
        // The filter is a convenience; the directory works without it.
      }
    })()

    return () => {
      cancelled = true
    }
  }, [api])

  // Reloaded from the top whenever the applied search or facility changes — a
  // cursor drawn against one query is meaningless against another.
  useEffect(() => {
    void load(null, facilityId, name)
  }, [load, facilityId, name])

  function onSearch(event: FormEvent) {
    event.preventDefault()
    setName(nameInput.trim())
  }

  return (
    <div className="mx-auto w-full max-w-5xl space-y-6">
      <PageHeader
        title="Registrars"
        description="Who holds an account on the register — so an account that should have been withdrawn can be noticed."
      />

      <Card>
        <CardContent className="flex flex-wrap items-end gap-4 pt-6">
          <form onSubmit={onSearch} className="grid gap-2">
            <Label htmlFor="registrar-name">Name</Label>
            <div className="flex gap-2">
              <Input
                id="registrar-name"
                value={nameInput}
                onChange={(event) => setNameInput(event.target.value)}
                placeholder="Search by name"
                autoComplete="off"
                className="sm:w-64"
              />
              <Button type="submit" variant="outline">
                <Search />
                Search
              </Button>
            </div>
          </form>

          <div className="grid gap-2">
            <Label htmlFor="registrar-facility">Facility</Label>
            <Select value={facilityId} onValueChange={setFacilityId}>
              <SelectTrigger id="registrar-facility" className="w-64">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value={AllFacilities}>All facilities</SelectItem>
                {facilities.map((facility) => (
                  <SelectItem key={facility.facilityId} value={facility.facilityId}>
                    {facility.name}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>
        </CardContent>
      </Card>

      {loading && rows === null ? <LoadingDirectory /> : null}

      {error ? <Failure error={error} onRetry={() => void load(null, facilityId, name)} /> : null}

      {rows !== null && !error ? (
        rows.length === 0 ? (
          <Empty className="border">
            <EmptyHeader>
              <EmptyMedia variant="icon">
                <Users />
              </EmptyMedia>
              <EmptyTitle>
                {name || facilityId !== AllFacilities
                  ? 'No registrars match'
                  : 'No registrars provisioned'}
              </EmptyTitle>
              <EmptyDescription>
                {name || facilityId !== AllFacilities
                  ? 'Nobody in scope matches this search. Clear it to see everyone in your district.'
                  : 'No accounts are provisioned in your district, which is worth checking rather than accepting.'}
              </EmptyDescription>
            </EmptyHeader>
          </Empty>
        ) : (
          <Card>
            <CardContent className="pt-6">
              <p className="text-muted-foreground mb-3 text-sm">
                {plural(total)}
                {name ? ` matching “${name}”` : ''}.
              </p>

              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>Name</TableHead>
                    <TableHead>Role</TableHead>
                    <TableHead>Facility</TableHead>
                    <TableHead>District</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {rows.map((registrar) => (
                    <TableRow key={registrar.registrarId}>
                      <TableCell className="font-medium">{registrar.displayName}</TableCell>
                      <TableCell>
                        <Badge variant="secondary">{RoleLabels[registrar.role]}</Badge>
                      </TableCell>
                      <TableCell>{registrar.facilityName}</TableCell>
                      <TableCell className="text-muted-foreground">{registrar.districtId}</TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>

              {nextCursor ? (
                <div className="mt-4 flex justify-center">
                  <Button
                    variant="outline"
                    onClick={() => void load(nextCursor, facilityId, name)}
                    disabled={loadingMore}
                  >
                    {loadingMore ? <Spinner /> : null}
                    Show more
                  </Button>
                </div>
              ) : null}
            </CardContent>
          </Card>
        )
      ) : null}
    </div>
  )
}

function LoadingDirectory() {
  return (
    <Card aria-busy>
      <CardContent className="space-y-3 pt-6">
        <Skeleton className="h-4 w-48" />
        <Skeleton className="h-10 w-full" />
        <Skeleton className="h-10 w-full" />
        <Skeleton className="h-10 w-full" />
      </CardContent>
    </Card>
  )
}

function Failure({ error, onRetry }: { error: NcbrsError; onRetry: () => void }) {
  return (
    <Empty className="border">
      <EmptyHeader>
        <EmptyMedia variant="icon">
          {error.unreachable ? <TriangleAlert /> : <CircleAlert />}
        </EmptyMedia>
        <EmptyTitle>{error.title}</EmptyTitle>
        <EmptyDescription>
          {error.unreachable
            ? 'The registry did not answer. Check the connection before trying again.'
            : error.fields.map((item) => item.message).join(' ') ||
              'The directory could not be loaded.'}
        </EmptyDescription>
      </EmptyHeader>
      <EmptyContent>
        <Button variant="outline" onClick={onRetry}>
          Try again
        </Button>
      </EmptyContent>
    </Empty>
  )
}

function plural(count: number): string {
  return `${count.toLocaleString()} ${count === 1 ? 'registrar' : 'registrars'}`
}
