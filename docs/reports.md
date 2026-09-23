# Evidence reports

The HTML reports are dated investigation artifacts, not current documentation.
They preserve the counts, proposals, corrections, and reasoning that led to
later plans and implementation work. Read their measurements against the named
revision; use the README and focused Markdown pages for current behavior.

All six reports were produced or reconciled on 2026-09-15 and are retained
together under `docs/history/reports/` so their cross-links continue to work.

| Report | Status | Evidence revision | Current replacement |
| --- | --- | --- | --- |
| [The Unfinished Cook](history/reports/blix-assets.html) | Historical measured snapshot | `2aa1faa`, 2026-09-15 | [Assets](assets.md) |
| [The Engine, Counted](history/reports/blix-engine.html) | Historical measured snapshot | `2aa1faa`, 2026-09-15 | [Architecture](architecture.md) |
| [What Blix Tooling Should Be](history/reports/blix-tooling-design.html) | Superseded proposal; retained for its corrections and open questions | authored at `fbbda33`, supersession recorded at `7c10841`, 2026-09-15 | [Workflow](workflow.md), [Demos](demos.md), and [Plan status](plans.md) |
| [Blix Module Topology](history/reports/blix-topology.html) | Historical measured snapshot; no longer the live register its footer claimed | `2aa1faa`, reconciled at `7c10841`, 2026-09-15 | [Architecture](architecture.md) |
| [What Blix Ships](history/reports/blix-what-it-ships.html) | Historical revision 3; its application/tool model has since shipped and evolved | `2aa1faa`, revision 3, 2026-09-15 | [README](../README.md), [Workflow](workflow.md), and [Demos](demos.md) |
| [The Blix Workbench](history/reports/blix-workbench.html) | Superseded proposal | authored at `fbbda33`, supersession recorded at `7c10841`, 2026-09-15 | [Workflow](workflow.md) and [Demos](demos.md) |

## How to read the revisions

- `2aa1faa` is the measured working-tree anchor named inside the four counted
  reports. It is not a claim that the current checkout has the same totals.
- `fbbda33` is the first committed version of the two early tooling proposals.
- `7c10841` added their visible supersession notices and reconciled the report
  family. It did not make the measurements evergreen.

Do not silently refresh a historical report. A new measurement should be a new
dated artifact with its own revision, methodology, and status row here.
