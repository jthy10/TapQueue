# Reports

**Reports** in the admin console, and `tapqueue-admin report`, add up the pages people printed
over a period: by person, group, printer, queue, day or month.

```sh
tapqueue-admin report                                   # this month so far, by person
tapqueue-admin report --by printer --from 2026-09-01 --to 2026-09-30
tapqueue-admin report --by month --from 2026-01-01
tapqueue-admin report --by group --csv > pages.csv      # for a spreadsheet
```

In the console, pick a period and what to add up by; **Download CSV** saves what's shown. Anyone
with a role in the Jobs area can see reports.

## What's counted

The same thing page limits count:

- Only jobs that were **released** to a printer. Held, canceled and expired jobs printed nothing.
- A job is its **pages times its copies**, counted from the document when it arrived (PDF, PWG and
  Apple raster, JPEG). A job whose pages couldn't be counted is 1 page a copy; reports say how many
  of those there were.
- The date is when the job was released. **Days and months are the server's**: they start at
  midnight in the server's time zone, which the report names. That's the server machine's own
  zone unless you set one (**Server → Settings → Time zone**, or
  `tapqueue-admin server set time-zone Europe/Oslo`).
- Each row also says how many of its pages were sent to print in **colour**, in **black and
  white** and on **both sides**. That's what the job asked for: the choice in the print dialog,
  or, when the job didn't say, what a raster document says about itself. A PDF sent with colour
  left on automatic to a colour queue is neither colour nor black and white in the report (the
  printer decided, and TapQueue doesn't know which way); so are jobs printed before 0.16.

Things to know when reading one:

- **Groups** are the ones people are in today, not when they printed. Someone in two groups counts
  in both, so a group report's rows can add up to more than its total. People in no group are one
  row, "(no group)".
- A **deleted user's** pages stay under their username, marked "(deleted user)". Removed printers
  and queues keep their ids.
- Pages are what was sent to the printer, not sheets of paper: a 2-page job printed on both sides
  is 2 pages.
- Job history is kept for ever unless you say otherwise, so reports go back to when the server
  was set up. If you set a limit (**Server → Settings → Job history**, or
  `tapqueue-admin server set job-history 365`), finished jobs older than that are deleted, with
  their document names, owners and source PCs, and reports reach back only that far. The least
  is 32 days, so a monthly page limit can still be counted. The activity log is kept 90 days
  (`server set activity-log <days>`; 0 keeps it for ever).

## From a script

`GET /api/v1/admin/reports/usage?from=2026-10-01&to=2026-10-31&by=user` with the admin token
returns JSON; add `&format=csv` for CSV. `by` is `user`, `group`, `printer`, `queue`, `day` or
`month`; without dates it's this month so far.

```sh
curl -H "Authorization: Bearer $TAPQUEUE_ADMIN_TOKEN" \
    "https://tapqueue.example.com:8632/api/v1/admin/reports/usage?by=group&format=csv"
```

Not there yet: emailing reports on a schedule, and prices per page ([roadmap](roadmap.md)).
