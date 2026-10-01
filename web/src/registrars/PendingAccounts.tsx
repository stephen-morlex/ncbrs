import { type FormEvent, useCallback, useEffect, useState } from 'react'
import { CircleAlert, UserPlus } from 'lucide-react'
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
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
import {
  type NcbrsError,
  messagesFor,
  toNcbrsError,
  unattachedMessages,
  unreachableError,
} from '@/api/errors'
import { useApiClient } from '@/api/useApi'

type Pending = components['schemas']['PendingAccountResponse']
type Facility = components['schemas']['FacilityResponse']
type RegistrarRole = components['schemas']['RegistrarRole']

/** The registry role each realm role corresponds to, in the order an officer meets them. */
const RolesByRealmRole: { realmRole: string; role: RegistrarRole; label: string; staff: boolean }[] = [
  { realmRole: 'facility-registrar', role: 'FacilityRegistrar', label: 'Facility registrar', staff: true },
  { realmRole: 'community-health-worker', role: 'CommunityHealthWorker', label: 'Community health worker', staff: true },
  { realmRole: 'district-officer', role: 'DistrictOfficer', label: 'District officer', staff: false },
  { realmRole: 'ministry-admin', role: 'MinistryAdmin', label: 'Ministry admin', staff: false },
]

const Shown = ['data.facilityId', 'data.role'] as const

/**
 * Accounts that have signed in and are waiting to be added (pilot readiness
 * §1). An account declares itself on first sign-in, so an officer picks it
 * from here rather than copying an id out of Keycloak. A district officer sees
 * the accounts placed in their county and adds facility staff; the Ministry
 * sees every account and may give any role.
 */
export function PendingAccounts({
  facilities,
  ministry,
  onBound,
}: {
  facilities: Facility[]
  ministry: boolean
  onBound: () => void
}) {
  const api = useApiClient()
  const [accounts, setAccounts] = useState<Pending[] | null>(null)
  const [binding, setBinding] = useState<Pending | null>(null)

  const load = useCallback(async () => {
    try {
      const { data, response } = await api.GET('/api/registrars/pending')
      setAccounts(response.ok ? (data?.data ?? []) : [])
    } catch {
      setAccounts([])
    }
  }, [api])

  useEffect(() => {
    void load()
  }, [load])

  // Nothing waiting is the usual state; say nothing rather than add a card.
  if (!accounts || accounts.length === 0) {
    return null
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>Waiting to be added</CardTitle>
        <CardDescription>
          These people have signed in and are not yet linked to a facility. Add each to the
          facility they work at.
        </CardDescription>
      </CardHeader>
      <CardContent>
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>Name</TableHead>
              <TableHead>Account</TableHead>
              <TableHead>County</TableHead>
              <TableHead className="text-right">Add</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {accounts.map((account) => (
              <TableRow key={account.pendingAccountId}>
                <TableCell className="font-medium">{account.displayName}</TableCell>
                <TableCell className="text-muted-foreground">
                  {account.username ?? account.email ?? '—'}
                </TableCell>
                <TableCell className="text-muted-foreground">
                  {account.countyCode ?? 'not set'}
                </TableCell>
                <TableCell className="text-right">
                  <Button size="sm" variant="outline" onClick={() => setBinding(account)}>
                    <UserPlus />
                    Add
                  </Button>
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </CardContent>

      {binding ? (
        <BindDialog
          account={binding}
          facilities={facilities}
          ministry={ministry}
          onClose={() => setBinding(null)}
          onBound={() => {
            setBinding(null)
            void load()
            onBound()
          }}
        />
      ) : null}
    </Card>
  )
}

function BindDialog({
  account,
  facilities,
  ministry,
  onClose,
  onBound,
}: {
  account: Pending
  facilities: Facility[]
  ministry: boolean
  onClose: () => void
  onBound: () => void
}) {
  const api = useApiClient()

  // Only roles the account holds in Keycloak, and only staff roles unless the
  // Ministry is adding them. The server refuses the rest; this saves asking.
  const roles = RolesByRealmRole.filter(
    (option) => account.realmRoles.includes(option.realmRole) && (ministry || option.staff),
  )

  // An account placed in a county belongs at a facility there.
  const offered = account.countyCode
    ? facilities.filter((facility) => facility.countyCode === account.countyCode)
    : facilities

  const [facilityId, setFacilityId] = useState('')
  const [role, setRole] = useState<RegistrarRole | ''>(roles.length === 1 ? roles[0].role : '')
  const [displayName, setDisplayName] = useState(account.displayName)
  const [error, setError] = useState<NcbrsError | null>(null)
  const [submitting, setSubmitting] = useState(false)

  async function submit(event: FormEvent) {
    event.preventDefault()
    if (!facilityId || !role) {
      return
    }

    setSubmitting(true)
    setError(null)

    try {
      const { error: failure, response } = await api.POST('/api/registrars', {
        body: {
          data: {
            pendingAccountId: account.pendingAccountId,
            facilityId,
            role,
            displayName: displayName.trim() || null,
          },
        },
      })

      if (response.ok) {
        onBound()
        return
      }

      setError(toNcbrsError(failure, response.status))
    } catch (cause) {
      setError(unreachableError(cause))
    } finally {
      setSubmitting(false)
    }
  }

  const facilityMessages = messagesFor(error, 'data.facilityId')
  const roleMessages = messagesFor(error, 'data.role')
  const otherMessages = unattachedMessages(error, Shown)

  return (
    <Dialog open onOpenChange={(open) => (open ? undefined : onClose())}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Add {account.displayName}</DialogTitle>
          <DialogDescription>
            Link this account to the facility they work at. They can sign in and work there at
            once.
          </DialogDescription>
        </DialogHeader>

        <form onSubmit={submit} className="space-y-4">
          {otherMessages.length > 0 ? (
            <Alert variant="destructive">
              <CircleAlert />
              <AlertTitle>{error?.title ?? 'Could not add them'}</AlertTitle>
              <AlertDescription>{otherMessages.join(' ')}</AlertDescription>
            </Alert>
          ) : null}

          {roles.length === 0 ? (
            <Alert variant="destructive">
              <CircleAlert />
              <AlertTitle>No role you can give</AlertTitle>
              <AlertDescription>
                The account holds no role you may give it in Keycloak. Give it one there, have them
                sign in again, then add them.
              </AlertDescription>
            </Alert>
          ) : null}

          <div className="grid gap-2">
            <Label htmlFor="bind-name">Name in the registry</Label>
            <Input
              id="bind-name"
              value={displayName}
              onChange={(event) => setDisplayName(event.target.value)}
            />
          </div>

          <div className="grid gap-2">
            <Label htmlFor="bind-facility">Facility</Label>
            <Select value={facilityId} onValueChange={setFacilityId}>
              <SelectTrigger
                id="bind-facility"
                aria-invalid={facilityMessages.length > 0 || undefined}
              >
                <SelectValue placeholder="Choose their facility" />
              </SelectTrigger>
              <SelectContent>
                {offered.map((facility) => (
                  <SelectItem key={facility.facilityId} value={facility.facilityId}>
                    {facility.name}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
            {facilityMessages.length > 0 ? (
              <p className="text-destructive text-sm">{facilityMessages.join(' ')}</p>
            ) : null}
          </div>

          <div className="grid gap-2">
            <Label htmlFor="bind-role">Role</Label>
            <Select value={role} onValueChange={(value) => setRole(value as RegistrarRole)}>
              <SelectTrigger id="bind-role" aria-invalid={roleMessages.length > 0 || undefined}>
                <SelectValue placeholder="Choose their role" />
              </SelectTrigger>
              <SelectContent>
                {roles.map((option) => (
                  <SelectItem key={option.role} value={option.role}>
                    {option.label}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
            {roleMessages.length > 0 ? (
              <p className="text-destructive text-sm">{roleMessages.join(' ')}</p>
            ) : null}
          </div>

          <DialogFooter>
            <Button type="button" variant="outline" onClick={onClose}>
              Cancel
            </Button>
            <Button type="submit" disabled={!facilityId || !role || submitting}>
              {submitting ? <Spinner /> : null}
              Add
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  )
}
