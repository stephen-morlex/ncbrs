import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { PageHeader } from '@/shell/PageHeader'
import { AmendmentConflicts } from './AmendmentConflicts'
import { AmendmentQueue } from './AmendmentQueue'

/**
 * The two amendment review queues under one destination, because the
 * navigation names one "Amendments" and they are two facets of the same job:
 * corrections a district officer works through.
 *
 * They are kept as separate tabs rather than one list because they answer
 * different questions. **Approvals** asks whether a correction to a person's
 * identity should be made at all. **Conflicts** asks about a correction that
 * has already been resolved by last-writer-wins, where the wrong value may
 * have won. Folding them together would bury the second — a possibly-wrong
 * value standing on a legal record — inside the first.
 */
export function AmendmentsReview() {
  return (
    <div className="mx-auto w-full max-w-5xl space-y-6">
      <PageHeader
        title="Amendments"
        description="Corrections to a person's identity, and the clashes flagged when an offline device corrected a field the centre already had."
      />

      <Tabs defaultValue="approvals">
        <TabsList>
          <TabsTrigger value="approvals">Approvals</TabsTrigger>
          <TabsTrigger value="conflicts">Conflicts</TabsTrigger>
        </TabsList>

        <TabsContent value="approvals" className="pt-4">
          <AmendmentQueue />
        </TabsContent>

        <TabsContent value="conflicts" className="pt-4">
          <AmendmentConflicts />
        </TabsContent>
      </Tabs>
    </div>
  )
}
