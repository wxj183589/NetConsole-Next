# Device Management P2-B UI Design

## Information architecture

The existing Device Management page remains the entry point. The device detail view gains separate tabs/cards:

1. Identity and legacy primary management address.
2. All addresses, grouped by network domain and address role.
3. Network memberships and Site/Station/Line/Section context.
4. VLAN plan versus observed/imported values.
5. Review history, source provenance and audit timeline.

The page must never collapse planned and observed values into one unlabeled field. Each row shows source kind, source reference, revision, last observed time, validity and conflict state.

## States and interactions

- Empty state: explain that no address/domain/VLAN has been recorded; offer a bounded add/import action.
- Planned-only state: show the plan and label observed data as unavailable, not as matching.
- Observed-only state: show telemetry as observed and offer “create plan from observed” as an explicit action.
- Conflict state: show side-by-side values and exact revisions; offer review/resolve, never silent overwrite.
- Stale edit: keep the user draft, show current server value and require refresh/merge/submit-as-new-source.
- Offline target: preserve last observed time and source; do not render it as current reachability.
- Import preview: show accepted/skipped/conflict/error counts before confirm; allow cancel.

## Multi-IP presentation

The address table supports family, normalized address, prefix, role, interface, domain, source, primary candidate and lifecycle filters. The legacy primary address is marked as a compatibility projection, not a special authority. IPv6 formatting is normalized for comparison but displayed in operator-friendly form.

## VLAN presentation

VLAN plans are grouped by Network Domain and scope. The comparison view uses explicit columns for planned, observed, imported and manually verified values. Mismatches use warning/conflict states with accessible text, not color alone. VLAN IDs are displayed with domain and tagging context so the same ID in two domains is not misleading.

## Permissions and safety

Read-only users can inspect provenance and differences. Admin users can prepare plans/import previews only where authorized; superadmin or a future explicitly scoped operator permission is required for any write gate. Disabled controls must not be the only security boundary; the API response is authoritative. Destructive/archive actions require a confirmation dialog that names the exact scope and revision.

## Responsive and visual acceptance

The design must preserve light/dark themes and be verified at 1920×1080, 1440×900 and 1366×768. Tables become horizontally scrollable or use a detail drawer; important source/conflict labels remain visible. Dialogs stay inside the viewport with bounded scrollable bodies. Keyboard focus, accessible names, empty/loading/error states and reduced motion are part of acceptance.
