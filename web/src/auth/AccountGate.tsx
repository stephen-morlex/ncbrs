import { type ReactNode, useCallback, useEffect, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useAuth } from 'react-oidc-context'
import { Hourglass, UserX } from 'lucide-react'
import { Button } from '@/components/ui/button'
import {
  Empty,
  EmptyContent,
  EmptyDescription,
  EmptyHeader,
  EmptyMedia,
  EmptyTitle,
} from '@/components/ui/empty'
import { Spinner } from '@/components/ui/spinner'
import type { components } from '@/api/generated/api'
import { useApiClient } from '@/api/useApi'

type Me = components['schemas']['MeResponse']

/**
 * After sign-in, asks the registry who this account is (`GET /api/me`), which
 * is also how a new account declares itself: an account not yet linked to a
 * facility is put on its county's list of waiting accounts, for a district
 * officer to add (pilot readiness §1).
 *
 * A waiting or withdrawn account is told so plainly, rather than meeting a
 * 403 on every screen with no idea why. If the registry cannot be asked, the
 * pages are shown anyway: each handles its own errors, and blocking the whole
 * site on this one call would turn a blip into an outage.
 */
export function AccountGate({ children }: { children: ReactNode }) {
  const api = useApiClient()
  const auth = useAuth()
  const { t } = useTranslation()
  const [me, setMe] = useState<Me | null>(null)
  const [checked, setChecked] = useState(false)

  const check = useCallback(async () => {
    try {
      const { data, response } = await api.GET('/api/me')
      setMe(response.ok ? (data?.data ?? null) : null)
    } catch {
      setMe(null)
    } finally {
      setChecked(true)
    }
  }, [api])

  useEffect(() => {
    void check()
  }, [check])

  if (!checked) {
    return (
      <div className="flex min-h-svh items-center justify-center">
        <Spinner />
      </div>
    )
  }

  if (me && !me.provisioned) {
    const name = auth.user?.profile.name ?? auth.user?.profile.preferred_username ?? ''

    return (
      <div className="flex min-h-svh items-center justify-center p-6">
        <Empty>
          <EmptyHeader>
            <EmptyMedia variant="icon">{me.withdrawn ? <UserX /> : <Hourglass />}</EmptyMedia>
            <EmptyTitle>
              {me.withdrawn ? t('auth.withdrawnTitle') : t('auth.pendingTitle')}
            </EmptyTitle>
            <EmptyDescription>
              {me.withdrawn ? t('auth.withdrawnBody') : t('auth.pendingBody', { name })}
            </EmptyDescription>
          </EmptyHeader>
          {me.withdrawn ? null : (
            <EmptyContent>
              <Button
                variant="outline"
                onClick={() => {
                  setChecked(false)
                  void check()
                }}
              >
                {t('auth.checkAgain')}
              </Button>
            </EmptyContent>
          )}
        </Empty>
      </div>
    )
  }

  return <>{children}</>
}
