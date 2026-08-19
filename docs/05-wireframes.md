# 05 — Key Screens

Low-fidelity layouts for the five screens that carry the product. Everything else is a variation on the list/detail pattern established here.

Global rules applied to all screens:

- Status is **never** conveyed by colour alone. Every status chip is `[icon] [word] [colour]`.
- Sensitive fields render as `••••••••` with a **Reveal** control. If the user lacks the permission, the field is absent entirely — not shown-and-disabled, which would leak the fact that a value exists.
- Every table supports column selection, multi-column sort, saved filters, and Excel export.
- Every empty state names the next action. "No services at this location — *Add a service* or *Import from CSV*."
- Confirmation dialogs for archive, bulk edit, and import commit. Nothing destructive is one click.

---

## 5.1 Portfolio dashboard

```
┌──────────────────────────────────────────────────────────────────────────────────────┐
│ FC Telecom Manager      [ 🔍 Search circuits, locations, IPs, accounts…        ]  JD ▾│
├────────────┬─────────────────────────────────────────────────────────────────────────┤
│            │  Portfolio                                    Region: All ▾   Export ▾  │
│ ▸ Dashboard│                                                                          │
│ ▸ Locations│  ┌───────────┬───────────┬───────────┬───────────┐                       │
│ ▸ Services │  │ LOCATIONS │ SERVICES  │  MONTHLY  │ ANNUALIZED│                       │
│ ▸ Vendors  │  │    124    │    317    │ $184,220  │ $2,210,640│                       │
│ ▸ Contracts│  │ active    │ active    │ recurring │  run rate │                       │
│ ▸ Costs    │  └───────────┴───────────┴───────────┴───────────┘                       │
│ ▸ Outages  │                                                                          │
│ ▸ Reports  │  ┌─ ⚠ NEEDS ATTENTION ────────────────────────────────────────────────┐ │
│ ▸ Imports  │  │                                                                     │ │
│ ▸ Admin    │  │  ⏰  6 contracts    notice deadline within 90 days      [ View → ]  │ │
│            │  │  🔴  2 services     currently down                      [ View → ]  │ │
│            │  │  ⚡  11 locations   no true carrier diversity           [ View → ]  │ │
│            │  │  📄  23 services    missing circuit ID or documents     [ View → ]  │ │
│            │  │  💵  4 invoices     variance over 10% vs expected       [ View → ]  │ │
│            │  │  ❓  8 services     no monitoring coverage              [ View → ]  │ │
│            │  │                                                                     │ │
│            │  └─────────────────────────────────────────────────────────────────────┘ │
│            │                                                                          │
│            │  ┌─ SPEND BY CARRIER ─────────┐  ┌─ AVAILABILITY (rolling 30d) ───────┐  │
│            │  │ Lumen       ████████ $62.1k│  │ Overall      99.94%   coverage 96% │  │
│            │  │ Spectrum    █████    $41.3k│  │ ─────────────────────────────────  │  │
│            │  │ AT&T        ████     $33.8k│  │ Lumen        99.98%   ▲            │  │
│            │  │ Comcast     ███      $24.0k│  │ Spectrum     99.91%   ▬            │  │
│            │  │ Verizon     ██       $14.2k│  │ AT&T         99.72%   ▼ below SLA  │  │
│            │  │ Other       █         $8.8k│  │ Comcast      99.89%   ▬            │  │
│            │  └────────────────────────────┘  └────────────────────────────────────┘  │
│            │                                                                          │
│            │  ┌─ RENEWAL PIPELINE — NEXT 180 DAYS ──────────────────────────────────┐ │
│            │  │ Contract     Vendor     Services  Annual    Notice by    Status     │ │
│            │  │ MSA-2291     Lumen         14     $412k     2026-09-02   ⚠ 14 days  │ │
│            │  │ SPEC-8841    Spectrum      31     $288k     2026-10-15   ⏰ 57 days  │ │
│            │  │ ATT-MA-77    AT&T           9     $196k     2026-11-30   ○ 103 days │ │
│            │  │ CMCST-4412   Comcast       22     $144k     2026-12-08   ○ 111 days │ │
│            │  └─────────────────────────────────────────────────────────────────────┘ │
└────────────┴─────────────────────────────────────────────────────────────────────────┘
```

**Design intent.** Every number on this page is a link into a filtered list. A dashboard tile that cannot be drilled into is a decoration, and people stop trusting it within a month.

The "Availability" tile shows **coverage percentage next to the availability percentage**, always. An availability figure without a coverage figure is not interpretable — 99.94% over 96% coverage and 99.94% over 40% coverage are completely different statements.

*Permission behaviour:* the spend tiles and the carrier-spend chart are absent for Help Desk. The availability panel is absent for Procurement unless separately granted. Tiles do not appear greyed out; they are not rendered.

---

## 5.2 Location detail

The page that has to answer all seven required questions without a scroll hunt.

```
┌──────────────────────────────────────────────────────────────────────────────────────┐
│ ← Locations                                                                           │
│                                                                                       │
│  ● Active   ST-0142 · Northgate Clinic                          [Edit] [Archive] [⋮] │
│  4820 N Broadway, Chicago IL 60640 · America/Chicago · Region: Midwest                │
│  Criticality: ◆ Critical  ·  Acceptable outage: 15 min  ·  CC: 4400-CLIN              │
│  IT Owner: M. Reyes · (312) 555-0143     Site contact: A. Okafor · (312) 555-0177     │
├───────────────────────────────────────────────────────────────────────────────────────┤
│  [ Services 4 ]  [ Costs ]  [ Contracts 2 ]  [ Documents 7 ]  [ Availability ]  [ History ] │
├───────────────────────────────────────────────────────────────────────────────────────┤
│                                                                                       │
│  ⚠  DIVERSITY RISK — Primary and backup share a last-mile provider (Everstream).      │
│     Confirmed via LOA, 2026-03-11.  A fiber cut in the building entrance takes both.  │
│                                                    [ Review dependencies → ]          │
│                                                                                       │
│  ┌─ SERVICES AT THIS LOCATION ──────────────────────────────────────────────────────┐ │
│  │                                                                                   │ │
│  │  ● UP   PRIMARY · Internet · Fiber DIA                                            │ │
│  │  Lumen  ·  Circuit ID  ORD/KFGS/123456/LMKT   ·  Acct 8-2K4H91                    │ │
│  │  1 Gbps ↓ / 1 Gbps ↑  ·  CIR 1 Gbps  ·  Handoff SMF-LC → MDF Rack 3               │ │
│  │  Static IPs  ••••••••••••  [Reveal]      Support  (877) 453-8353  P1              │ │
│  │  $2,480.00/mo  ·  Contract MSA-2291  ·  Notice by 2026-09-02  ⚠ 14 days           │ │
│  │  30-day availability  99.98%  (coverage 100%)                    [ Details → ]    │ │
│  │  ─────────────────────────────────────────────────────────────────────────────    │ │
│  │  ● UP   BACKUP · Internet · Coax                                                  │ │
│  │  Spectrum  ·  Circuit ID  60.LXFN.845512.CHI   ·  Acct 8245-1190-0034             │ │
│  │  600 Mbps ↓ / 35 Mbps ↑  ·  no CIR  ·  Handoff RJ45 → IDF-2                       │ │
│  │  Static IPs  ••••••••••••  [Reveal]      Support  (800) 314-7195  P2              │ │
│  │  $389.00/mo  ·  Contract SPEC-8841  ·  Notice by 2026-10-15  ⏰ 57 days            │ │
│  │  30-day availability  99.91%  (coverage 100%)                    [ Details → ]    │ │
│  │  ⚠ Shares last-mile (Everstream) with the primary circuit above                   │ │
│  │  ─────────────────────────────────────────────────────────────────────────────    │ │
│  │  ● UP   STANDALONE · SIP Trunk                                                    │ │
│  │  Intrado  ·  BTN (312) 555-0140  ·  24 channels  ·  Acct INT-77120                │ │
│  │  $612.00/mo  ·  Contract SPEC-8841                                                │ │
│  │  ? UNKNOWN — no monitoring configured                            [ Details → ]    │ │
│  │  ─────────────────────────────────────────────────────────────────────────────    │ │
│  │  ● UP   STANDALONE · Alarm Line (POTS)                                            │ │
│  │  AT&T  ·  WTN (312) 555-0198  ·  Acct 312-555-0198-1234                           │ │
│  │  $87.40/mo  ·  no contract on file  ⚠                            [ Details → ]    │ │
│  │                                                                                   │ │
│  └───────────────────────────────────────────────────────────────────────────────────┘ │
│                                                                                       │
│  ┌─ AT A GLANCE ────────────────────┬─ NEXT DEADLINES ─────────────────────────────┐  │
│  │ Total monthly       $3,568.40    │ 2026-09-02  Lumen MSA-2291 notice   ⚠ 14 days│  │
│  │ Annualized         $42,820.80    │ 2026-10-15  Spectrum SPEC-8841      ⏰ 57 days│  │
│  │ Cost per Mbps (WAN)     $1.79    │ 2026-12-31  Lumen contract end               │  │
│  │ Services 4 · monitored 2 of 4    │                                              │  │
│  └──────────────────────────────────┴──────────────────────────────────────────────┘  │
└───────────────────────────────────────────────────────────────────────────────────────┘
```

**The seven questions, answered in order of appearance:** what services are here (the service list), which is primary/backup (the role label leading each card), who is the carrier and how do we escalate (carrier name + support number + priority on every card), what are the circuit IDs and handoff details (line three of every card), what are we paying (per-card MRC and the At-a-glance panel), when do contracts renew and when is notice due (per-card and the Next deadlines panel), and is it up and how has it performed (the status chip and the 30-day figure with coverage).

**The diversity banner is the highest-value pixel on this page.** It converts a fact buried in `ServiceDependency` into something a person acts on. Note that it names the evidence and the date — an unsourced warning gets dismissed.

---

## 5.3 Circuit / service detail

```
┌──────────────────────────────────────────────────────────────────────────────────────┐
│ ← Northgate Clinic (ST-0142)                                                          │
│                                                                                       │
│  ● UP   Lumen Fiber DIA — 1 Gbps          PRIMARY      [Edit] [Outage view] [Archive] │
│  Circuit ID  ORD/KFGS/123456/LMKT                              [copy]                 │
├───────────────────────────────────────────────────────────────────────────────────────┤
│ [Overview] [Identifiers] [Addressing] [Costs] [Contract] [Dependencies] [Docs] [History]│
├───────────────────────────────────────────────────────────────────────────────────────┤
│                                                                                       │
│  ┌─ COMMERCIAL ────────────────────┬─ TECHNICAL ────────────────────────────────────┐ │
│  │ Carrier          Lumen          │ Service type    Internet (Dedicated)           │ │
│  │ Reseller         —              │ Media           Fiber                          │ │
│  │ Last-mile        Everstream ⚠   │ Handoff         SMF-LC (single-mode)           │ │
│  │ Network owner    Lumen          │ Demarc          MDF, Rack 3, Panel A, Port 12   │ │
│  │ Account          8-2K4H91       │ CPE             Adtran 834-5 · SN A4X99120     │ │
│  │ Billing acct     BAN 402-11940  │ CPE managed     Yes — by carrier               │ │
│  │ Status           Active         │ WAN interface   ether1 (FGT-60F-NGATE)         │ │
│  │ Role             Primary        │ Support         P1 · (877) 453-8353            │ │
│  │ Installed        2023-04-18     │ Ticket portal   control.lumen.com  ↗           │ │
│  │ Activated        2023-04-25     │                                                │ │
│  └─────────────────────────────────┴────────────────────────────────────────────────┘ │
│                                                                                       │
│  ┌─ BANDWIDTH & SLA ────────────────────────────────────────────────────────────────┐ │
│  │ Download 1 Gbps · Upload 1 Gbps · CIR 1 Gbps · No data cap                       │ │
│  │ SLA: 99.99% availability · ≤45 ms latency · ≤0.1% loss · 4 h MTTR                │ │
│  │ Actual (rolling 365d): 99.97% ▼ below SLA · 12 ms avg · 0.02% loss               │ │
│  │ ⚡ 1 potential service credit — 2026-02-14 outage, 3 h 42 m   [ Review claim → ]  │ │
│  └──────────────────────────────────────────────────────────────────────────────────┘ │
│                                                                                       │
│  ┌─ ADDRESSING  🔒 restricted ──────────────────────────────────────────────────────┐ │
│  │ IPv4 block      ••••••••••••••••          [ Reveal ]                             │ │
│  │ Gateway         ••••••••••••              Revealing is logged and attributed.    │ │
│  │ Usable range    ••••••••••••                                                     │ │
│  │ Routed /29      ••••••••••••                                                     │ │
│  └──────────────────────────────────────────────────────────────────────────────────┘ │
│                                                                                       │
│  ┌─ COST HISTORY ───────────────────────────────────────────────────────────────────┐ │
│  │ Effective            MRC      Taxes    Equip    Total    Source                  │ │
│  │ 2026-01-01 →       $2,480.00  $148.20  $0.00  $2,628.20  Invoice   ← current     │ │
│  │ 2024-05-01 → 2025-12-31 $2,340.00 $139.80 $0.00 $2,479.80  Contract              │ │
│  │ 2023-04-25 → 2024-04-30 $2,340.00 $131.40 $45.00 $2,516.40 Contract              │ │
│  │                                          [ Record a price change ]               │ │
│  └──────────────────────────────────────────────────────────────────────────────────┘ │
└───────────────────────────────────────────────────────────────────────────────────────┘
```

**"Record a price change" is not "Edit cost".** The label is chosen so the append-only behaviour is obvious before the user clicks. The form closes the current row and opens a new one; there is no path in the UI that overwrites a historical amount.

**Last-mile shows a ⚠ when it differs from the carrier** and another service at the same location shares it. The warning is contextual, not a static badge.

---

## 5.4 Contracts and renewals

```
┌──────────────────────────────────────────────────────────────────────────────────────┐
│  Contracts                              [ + New contract ]  [ Import ]  [ Export ▾ ] │
│                                                                                       │
│  Saved views:  ● Notice due ≤120d   ○ All active   ○ Auto-renew   ○ Missing terms     │
│  Vendor: All ▾   Status: Active ▾   Notice: Next 180 days ▾   Owner: All ▾   [Clear]  │
├───────────────────────────────────────────────────────────────────────────────────────┤
│                                                                                       │
│  ┌───────────────────────────────────────────────────────────────────────────────────┐│
│  │ ⚠ 14d │ MSA-2291 · Lumen                                        Owner: J. Dannehl ││
│  │       │ 14 services · $34,340/mo · $412,080/yr                                    ││
│  │       │ Term 2023-01-01 → 2026-12-31 (36 mo, auto-renews 12 mo)                   ││
│  │       │ Notice 120 days  →  deadline 2026-09-02  ✔ confirmed by M. Reyes          ││
│  │       │ Escalator 3% annual · Min commit $30,000/mo · ETF: 60% of remaining MRC   ││
│  │       │ [ Open ]  [ Start renewal review ]  [ Send notice reminder ]              ││
│  └───────────────────────────────────────────────────────────────────────────────────┘│
│  ┌───────────────────────────────────────────────────────────────────────────────────┐│
│  │ ⏰ 57d │ SPEC-8841 · Spectrum                                    Owner: unassigned ││
│  │       │ 31 services · $24,010/mo · $288,120/yr                                    ││
│  │       │ Term 2024-11-01 → 2026-10-31 (24 mo, evergreen month-to-month)            ││
│  │       │ Notice 30 days  →  deadline 2026-10-15  ⚠ NEEDS REVIEW — computed,        ││
│  │       │   not confirmed. Contract language is ambiguous.  [ Confirm date ]        ││
│  │       │ [ Open ]  [ Assign owner ]                                                ││
│  └───────────────────────────────────────────────────────────────────────────────────┘│
│  ┌───────────────────────────────────────────────────────────────────────────────────┐│
│  │ ⛔ ── │ POTS-LEGACY-04 · AT&T                                   Owner: unassigned ││
│  │       │ 9 services · $786/mo · $9,432/yr                                          ││
│  │       │ ⛔ NO CONTRACT TERMS ON FILE — end date, notice period, and renewal type  ││
│  │       │    are all unknown. These lines may be month-to-month or auto-renewing.   ││
│  │       │ [ Open ]  [ Add terms ]  [ Attach agreement ]                             ││
│  └───────────────────────────────────────────────────────────────────────────────────┘│
└───────────────────────────────────────────────────────────────────────────────────────┘
```

**Three visually distinct states, three different messages.** Confirmed deadline (act on it), unconfirmed deadline (act on it, but verify the date), and no terms at all (you are exposed and do not know how much). Most tools collapse the third into an empty cell, which is exactly how legacy POTS lines quietly auto-renew for a decade.

---

## 5.5 Outage view

Phone-first. This is the screen someone opens standing in a wiring closet at 6am.

```
┌─────────────────────────────────┐        Desktop: same content, three columns.
│ ← Outages                       │
│                                 │        Design constraints:
│ 🔴 DOWN  47 min                 │        • Renders correctly at 375 px
│ Northgate Clinic (ST-0142)      │        • Everything above the fold is what
│ Lumen Fiber DIA — PRIMARY       │          you read aloud to the carrier
│                                 │        • No modal, no hover-only content
│ ┌─────────────────────────────┐ │        • Survives a page reload with no
│ │ 📞  CALL LUMEN              │ │          state loss (it is read-mostly)
│ │     (877) 453-8353          │ │        • Works if the SignalR circuit drops
│ └─────────────────────────────┘ │
│ ┌─────────────────────────────┐ │
│ │ 📋  COPY SUPPORT SUMMARY    │ │  ← produces, on the clipboard:
│ └─────────────────────────────┘ │
│                                 │     Circuit ID: ORD/KFGS/123456/LMKT
│ Circuit ID                      │     Account: 8-2K4H91
│ ORD/KFGS/123456/LMKT      [copy]│     Location: Northgate Clinic, 4820 N
│                                 │       Broadway, Chicago IL 60640
│ Account                         │     Site contact: A. Okafor (312) 555-0177
│ 8-2K4H91                  [copy]│     Service: 1 Gbps Fiber DIA
│                                 │     Demarc: MDF Rack 3, Panel A, Port 12
│ Support priority   P1           │     CPE: Adtran 834-5, SN A4X99120,
│ Portal  control.lumen.com  ↗    │       carrier-managed
│                                 │     Down since: 2026-08-19 05:41 CDT
│ ── Demarc & CPE ──────────────  │     Confirmed from: 2 probes (Azure, DC-1)
│ MDF, Rack 3, Panel A, Port 12   │     Last good check: 05:40:12 CDT
│ Adtran 834-5 · SN A4X99120      │     Backup circuit: UP (Spectrum
│ Managed by carrier              │       60.LXFN.845512.CHI)
│ WAN iface  ether1 (FGT-60F)     │     ⚠ Backup shares last-mile provider
│                                 │       (Everstream) with this circuit
│ ── State ─────────────────────  │
│ Down since  05:41 CDT           │
│ Last good   05:40:12 CDT        │
│ Confirmed   2 of 2 probes       │
│ Classified  Carrier failure     │
│   (backup circuit at this site  │
│    is up → not a site outage)   │
│                                 │
│ ── Backup ────────────────────  │
│ ● UP  Spectrum 600/35 coax      │
│ ⚠ Shares last-mile: Everstream  │
│                                 │
│ ── Record ────────────────────  │
│ Carrier ticket  [___________]   │
│ Internal ticket [___________]   │
│ Cause           [ select ▾  ]   │
│ Notes           [___________]   │
│              [ Save ]           │
│                                 │
│ ── Recent at this location ───  │
│ 2026-02-14  3h 42m  Fiber cut   │
│ 2025-11-02  0h 18m  Carrier mx  │
└─────────────────────────────────┘
```

**"Copy support summary" is one small story with disproportionate value.** It is the difference between a five-minute call and a fifteen-minute one, every time, and it eliminates the transcription errors that send a carrier looking at the wrong circuit.

**The classification line explains itself.** "Carrier failure (backup circuit at this site is up → not a site outage)" tells the engineer *why* the system reached that conclusion, so they can disagree with it. A classification with no reasoning gets ignored.

**The backup warning appears here too**, because this is the moment it matters most — the engineer is about to reassure someone that the backup has them covered.

---

## 5.6 Patterns reused everywhere

**List view chrome**

```
┌──────────────────────────────────────────────────────────────────────────┐
│ Services                    [ + New ]  [ Import ]  [ Columns ▾ ] [ Export ▾ ]│
│ Saved: ● My region  ○ No contract  ○ Missing IPs  ○ All      [ Save view ] │
│ Location ▾  Carrier ▾  Type ▾  Status ▾  Role ▾  Contract ▾      [ Clear ] │
├──────────────────────────────────────────────────────────────────────────┤
│ ☐ │ Status │ Location   │ Type     │ Carrier  │ Circuit ID    │ MRC   │ ⋮ │
│ ☐ │ ● Up   │ ST-0142    │ Internet │ Lumen    │ ORD/KFGS/1234…│ $2,480│   │
│ ☐ │ ● Up   │ ST-0142    │ Internet │ Spectrum │ 60.LXFN.8455… │   $389│   │
│ ☑ │ ? Unk  │ ST-0143    │ SIP      │ Intrado  │ INT-77120     │   $612│   │
├──────────────────────────────────────────────────────────────────────────┤
│ 1 selected   [ Bulk edit ]  [ Archive ]  [ Export selection ]             │
│                                     ◀ 1 2 3 … 24 ▶   50 per page ▾       │
└──────────────────────────────────────────────────────────────────────────┘
```

**Status vocabulary** — icon, word, and colour together, always:

| | | |
|---|---|---|
| `● Up` | green | Confirmed up by the required probe quorum |
| `● Down` | red | Confirmed down, outage open |
| `◐ Suspect` | amber | Failing but below threshold or quorum — not yet an outage |
| `? Unknown` | grey | No coverage. **Not** counted as up |
| `◍ Maintenance` | blue | Inside a maintenance window |
| `⊘ Disabled` | grey outline | Monitoring intentionally off |

**Loading, empty, and error states** are specified per view, not left to a default spinner. A list that is loading shows skeleton rows of the correct shape; a list that is empty says why it is empty and what to do; a list that failed says what failed and offers retry.
