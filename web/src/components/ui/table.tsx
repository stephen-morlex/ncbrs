import * as React from "react"
import { useTranslation } from "react-i18next"
import { cn } from "cn"

/**
 * Whether the element's content is wider than the element, kept current as
 * either resizes.
 */
function useOverflowsX(element: React.RefObject<HTMLElement | null>) {
  const [overflows, setOverflows] = React.useState(false)

  React.useEffect(() => {
    const node = element.current
    if (!node) return

    const measure = () => setOverflows(node.scrollWidth > node.clientWidth)
    measure()

    // Where ResizeObserver is missing (an old embedded browser, or jsdom in
    // the unit tests) fall back to the window: it misses a table that grows
    // after load, never a phone turned sideways.
    if (typeof ResizeObserver === "undefined") {
      window.addEventListener("resize", measure)
      return () => window.removeEventListener("resize", measure)
    }

    const observer = new ResizeObserver(measure)
    observer.observe(node)
    if (node.firstElementChild) observer.observe(node.firstElementChild)
    return () => observer.disconnect()
  }, [element])

  return overflows
}

/**
 * The container scrolls sideways when the table is wider than the screen --
 * on a phone, most of them are. A region that scrolls must be reachable from
 * the keyboard (WCAG 2.1.1), and a table of plain text has nothing focusable
 * inside it to scroll with. So while it overflows, and only then, the
 * container itself takes focus, as a named region: a tab stop on every table
 * whether or not it scrolls would be noise on the desktop, where most fit.
 */
function Table({ className, ...props }: React.ComponentProps<"table">) {
  const { t } = useTranslation()
  const container = React.useRef<HTMLDivElement>(null)
  const scrolls = useOverflowsX(container)

  return (
    <div
      ref={container}
      data-slot="table-container"
      className="focus-visible:ring-ring/50 relative w-full overflow-x-auto rounded-md outline-none focus-visible:ring-[3px]"
      {...(scrolls ? { tabIndex: 0, role: "region", "aria-label": t("a11y.scrollableTable") } : {})}
    >
      <table
        data-slot="table"
        className={cn("w-full caption-bottom text-sm", className)}
        {...props}
      />
    </div>
  )
}

function TableHeader({ className, ...props }: React.ComponentProps<"thead">) {
  return (
    <thead
      data-slot="table-header"
      className={cn("[&_tr]:border-b", className)}
      {...props}
    />
  )
}

function TableBody({ className, ...props }: React.ComponentProps<"tbody">) {
  return (
    <tbody
      data-slot="table-body"
      className={cn("[&_tr:last-child]:border-0", className)}
      {...props}
    />
  )
}

function TableFooter({ className, ...props }: React.ComponentProps<"tfoot">) {
  return (
    <tfoot
      data-slot="table-footer"
      className={cn(
        "border-t bg-muted/50 font-medium [&>tr]:last:border-b-0",
        className
      )}
      {...props}
    />
  )
}

function TableRow({ className, ...props }: React.ComponentProps<"tr">) {
  return (
    <tr
      data-slot="table-row"
      className={cn(
        "border-b transition-colors hover:bg-muted/50 has-aria-expanded:bg-muted/50 data-[state=selected]:bg-muted",
        className
      )}
      {...props}
    />
  )
}

function TableHead({ className, ...props }: React.ComponentProps<"th">) {
  return (
    <th
      data-slot="table-head"
      className={cn(
        "h-10 px-2 text-left align-middle font-medium whitespace-nowrap text-foreground [&:has([role=checkbox])]:pr-0",
        className
      )}
      {...props}
    />
  )
}

function TableCell({ className, ...props }: React.ComponentProps<"td">) {
  return (
    <td
      data-slot="table-cell"
      className={cn(
        "p-2 align-middle whitespace-nowrap [&:has([role=checkbox])]:pr-0",
        className
      )}
      {...props}
    />
  )
}

function TableCaption({
  className,
  ...props
}: React.ComponentProps<"caption">) {
  return (
    <caption
      data-slot="table-caption"
      className={cn("mt-4 text-sm text-muted-foreground", className)}
      {...props}
    />
  )
}

export {
  Table,
  TableHeader,
  TableBody,
  TableFooter,
  TableHead,
  TableRow,
  TableCell,
  TableCaption,
}
