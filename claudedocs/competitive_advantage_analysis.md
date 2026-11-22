# Competitive Advantage Analysis — Potion (2026-11-22)

Method: competitive-advantage program. Competitor survey (paid and free) →
money-flow decomposition → Musk-method + Socratic iteration until a
meaningful insight emerged (2 rounds). Judged from the creator's principles:
the creator holds no personal data / software-only provision (the creator
bears no operating cost) / the product itself is free.

## Market and money flow

| Competitor | Form | Revenue model |
|---|---|---|
| NinjaOne | Cloud RMM | Per-endpoint SaaS: $3.75/endpoint/month at ≤50 endpoints down to $1.50 at 10,000; quote-based; add-on modules extra |
| Kaseya VSA | Cloud/on-prem RMM | ~$4–5/endpoint/month (negotiated); sold through MSPs |
| Atera | Cloud RMM | Per-technician $149–219/month, unlimited devices |
| Nexthink | DEX platform | Quote-only; third-party estimates ~$6–10/endpoint/month; endpoint telemetry aggregated in the vendor cloud |
| Ivanti Neurons | Endpoint platform | Quote-only; "self-healing bots" as a platform feature |
| 1E → TeamViewer DEX | DEX platform | Acquired by TeamViewer (2025); quote-only; autonomous remediation library |
| Microsoft Hotpatching (Windows Server 2025) | OS feature | $1.50/core/month via Azure Arc — even the OS vendor meters "repair without reboot" |
| Tactical RMM | Self-hosted RMM | Free core, but a source-available (non-OSI) license; code-signed agents and SSO behind sponsorship tiers from $55/month |
| MeshCentral | Self-hosted remote control | Apache-2.0, free; remote-control-centric, thin on guarded remediation |

Where the money pools:

1. **Rent per endpoint.** Every commercial competitor meters the fleet —
   per endpoint per month (NinjaOne, Kaseya, Nexthink) or per technician
   (Atera). Remediation is never sold alone; it is a retention feature
   inside a subscription platform.
2. **Telemetry aggregation.** DEX vendors collect experience and telemetry
   data from every endpoint into the vendor cloud, where it becomes
   benchmark data and AI training assets. The customer's fleet data is
   part of the vendor's moat.
3. **The MSP channel.** RMM vendors sell through MSPs, who resell the same
   per-endpoint rent downstream. The control plane stays the vendor's.
4. The structural consequence: to earn this money, a vendor **must**
   concentrate a privileged remote-execution path into its cloud control
   plane. That concentration is exactly what the 2021 Kaseya VSA
   supply-chain attack turned into a blast radius (≈60 MSPs, 800–1,500
   downstream businesses, a $70M ransom demand) — the management path
   itself became the attack surface.

## Iteration record

### Round 1 — Questioning "autonomous remediation is the product"

- Assumption questioned: self-healing / autonomous remediation is the
  headline value, as the README's first line presents it.
- What disappeared: autonomous repair as a differentiator. It is the arena
  where Ivanti, 1E/TeamViewer and Nexthink compete with enterprise
  capital — and Potion's own implementation already demoted it: the
  repair-execution services (`AutoRecoveryManager`,
  `PerformanceOptimizer`, `EventDrivenRemediationService`,
  `PredictiveRemediationService`) sit behind
  `FeatureFlags:RepairExecutionEnabled`, **off by default**, and every
  repair command passes an allowlist, argument sanitization and rate
  limiting. The repository's own audit history (CHANGELOG) keeps proving
  the same point: what an operator of an admin-privileged service
  actually needs is not "it fixes things on its own" but "what it *can*
  execute is enumerable, it does not execute by default, and execution
  leaves a record."
- What survived: monitoring + anomaly detection + **restrained**
  remediation (allowlisted, off by default, auditable).

### Round 2 — Questioning "the competitors are RMM/DEX vendors" (assumption collapsed)

- Assumption questioned: Potion competes with RMM/DEX platforms on
  features and fleet scale.
- The real competitors are (a) doing nothing — built-in sfc/dism, Task
  Scheduler and Event Viewer are free and already installed — and
  (b) **distrust of management tooling itself**. The Kaseya incident
  repriced this market: the most expensive failure is not missing
  features but the management path being turned against the fleet.
- Vendors cannot escape the concentration: per-endpoint rent and
  telemetry aggregation require a cloud control plane holding privileged
  execution paths into every customer endpoint. Potion has no such
  plane — it runs on a single machine, its only outbound observability
  target is an OTLP endpoint the operator configures, and its repair
  vocabulary is a finite allowlist of OS-standard commands.
- The axis of competition therefore moves from "how smart is the repair"
  to "can you prove the management path has no blast radius."

## Structural advantage

Potion's remediation is off by default and confined to an allowlist of
OS-standard repair commands inside a single-machine, self-hosted service,
so it has no vendor cloud control plane concentrating privileged
remote-execution paths into customer fleets — the asset RMM/DEX vendors
must build to collect per-endpoint rent and aggregate telemetry, and the
asset whose compromise produced the Kaseya blast radius. A vendor cannot
copy this shape without dismantling its billing model; an operator can
verify it from the source and the config, for free, forever.

Supporting facts already in the repository:

- Repair execution is gated behind `FeatureFlags:RepairExecutionEnabled`
  (default `false`); the always-on surface is monitoring, anomaly
  detection, compliance reporting and observability.
- Repair commands pass a command allowlist, argument sanitization,
  SQL-injection guards and rate limiting; spawned remediation processes
  run under a memory ceiling (a Job Object on Windows).
- Operator configuration lives outside the install directory
  (`%ProgramData%\Potion\config`), so upgrades never wipe operator
  edits — the operator, not the vendor, owns the deployment.
- The only telemetry destination is the operator-configured OTLP endpoint
  (default `http://localhost:4317`). The creator receives nothing: no
  account, no phone-home, no per-endpoint fee.
- MIT license, with a Windows CI pipeline (build, tests, CodeQL, NuGet
  audit) — unlike the best-known self-hosted RMM, whose license is
  source-available and non-OSI.

## Remaining opportunities (ranked)

1. **README signage** (implemented with this document): the first line
   advertised "autonomous remediation capabilities" unconditionally,
   while the implementation deliberately defaults repair execution to
   off. The intro now leads with restrained remediation and the
   no-vendor-cloud shape.
2. **Verify the hand-written test count.** README states "318/318 tests";
   the CHANGELOG's audit trail shows the count moving (293 → 297 → 298
   → …) as tests are pinned. Either re-measure and restate it, or drop
   the number and let CI speak — hand-written measured values in
   documents go stale.
3. **Gather the security model in one place.** A short "Security model"
   section (or a SECURITY.md expansion) enumerating: no inbound
   remote-execution path, the allowlist contents, the flag default, and
   the outbound endpoints. The facts exist across README and CHANGELOG
   but are not yet collected where a security reviewer looks first.
4. **Fleet-scope honesty.** Potion is a single-machine service; RMM
   buyers manage fleets. State the intended scope explicitly (a
   per-machine guardian, composable with the operator's own tooling)
   rather than letting the comparison be assumed — the single-machine
   shape is the source of the advantage, not a gap to apologize for.
5. **Do not chase the DEX arena.** Experience scores, sentiment surveys
   and AI repair libraries are the vendors' capital game and would
   require the telemetry aggregation Potion structurally refuses. Last
   priority by design.
