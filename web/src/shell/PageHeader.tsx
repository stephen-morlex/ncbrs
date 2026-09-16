/**
 * The title and one-line explanation at the top of a page.
 *
 * Shared so every page announces itself the same way, and so the `h1` is
 * present exactly once per page — a screen reader's first question is what
 * this page is, and the breadcrumb in the header is navigation rather than
 * an answer to that.
 */
export function PageHeader({ title, description }: { title: string; description?: string }) {
  return (
    <div className="space-y-1">
      <h1 className="text-2xl font-semibold tracking-tight">{title}</h1>
      {description ? <p className="text-muted-foreground text-sm">{description}</p> : null}
    </div>
  )
}
