/*
 * Design-level, dependency-free scenarios for the proposed wireless identity
 * and multi-AC state contracts. This is intentionally not a collector test:
 * it makes the assumptions executable before a runtime schema exists.
 */

import assert from 'node:assert/strict'

const TRUSTED = 'TRUSTED_H3C_EVIDENCE'

function normalizeMac(value) {
  const compact = String(value || '').replace(/[.:-]/g, '').trim().toLowerCase()
  return /^[0-9a-f]{12}$/.test(compact) ? compact : null
}

function observation({ ac, site = 'site-a', serial = null, mac = null, raw = 'I', observedAt = 100, expiresAt = 1000, revision = `${ac}-${observedAt}`, semantics = TRUSTED, online = true }) {
  return { ac, site, serial: serial?.trim() || null, mac: normalizeMac(mac), raw, observedAt, expiresAt, revision, semantics, online }
}

function reconcileIdentity(observations, replacements = new Map(), identityNamespace = '') {
  const identities = []
  const serialMap = new Map()
  const macMap = new Map()
  const output = []

  for (const item of observations) {
    const serialMatch = item.serial ? serialMap.get(item.serial) : null
    const macMatch = item.mac ? macMap.get(item.mac) : null
    const candidates = new Set([serialMatch, macMatch].filter(Boolean))
    const siteConflict = [...candidates].some(id => identities.find(identity => identity.id === id)?.site !== item.site)
    const serialIdentity = serialMatch ? identities.find(identity => identity.id === serialMatch) : null
    const macIdentity = macMatch ? identities.find(identity => identity.id === macMatch) : null
    const fieldConflict = (serialMatch && macMatch && serialMatch !== macMatch)
      || (serialIdentity?.mac && item.mac && serialIdentity.mac !== item.mac)
      || (macIdentity?.serial && item.serial && macIdentity.serial !== item.serial)
    if (!item.serial && !item.mac) {
      output.push({ ...item, identityId: null, conflict: 'UNRESOLVED' })
      continue
    }
    if (siteConflict || fieldConflict) {
      output.push({ ...item, identityId: null, conflict: siteConflict ? 'SITE_CONFLICT' : 'IDENTITY_CONFLICT' })
      continue
    }

    const id = [...candidates][0] || `fit-ap-${identityNamespace ? `${identityNamespace}-` : ''}${identities.length + 1}`
    if (!identities.some(identity => identity.id === id)) {
      const replacementOf = replacements.get(item.serial || item.mac) || null
      identities.push({ id, site: item.site, serial: item.serial, mac: item.mac, replacementOf })
    }
    if (item.serial) serialMap.set(item.serial, id)
    if (item.mac) macMap.set(item.mac, id)
    output.push({ ...item, identityId: id, conflict: null })
  }
  return { identities, observations: output }
}

function resolveState(observations, { now, semanticsTrusted = false } = {}) {
  const conflicts = observations.filter(item => item.conflict)
  if (conflicts.length) return { state: 'CONFLICT', reason: conflicts[0].conflict }
  const fresh = observations.filter(item => item.expiresAt == null || item.expiresAt >= now)
  if (!fresh.length) return { state: 'STALE', reason: 'NO_FRESH_OBSERVATION' }
  if (!semanticsTrusted || fresh.some(item => item.semantics !== TRUSTED)) return { state: 'UNKNOWN', reason: 'ASSUMPTION_PENDING_H3C_EVIDENCE' }

  const primary = fresh.filter(item => item.raw === 'R/M')
  const backup = fresh.filter(item => item.raw === 'R/B')
  if (primary.length > 1) return { state: 'CONFLICT', reason: 'MULTIPLE_PRIMARY_CANDIDATES', sources: primary.map(item => item.ac) }
  if (primary.length === 1) return { state: 'PRIMARY', primaryAc: primary[0].ac, sourceDegraded: fresh.some(item => item.online === false) }
  if (backup.length) return { state: 'BACKUP', primaryAc: null, backupAc: backup[0].ac }
  return { state: 'UNKNOWN', reason: 'UNMAPPED_RAW_STATE' }
}

function assertScenario(name, fn) {
  fn()
  console.log(`PASS: ${name}`)
}

assertScenario('single AC and single AP', () => {
  const result = reconcileIdentity([observation({ ac: 'ac-a', serial: 'AP-001', mac: 'aa:bb:cc:00:00:01', raw: 'R/M' })])
  assert.equal(result.identities.length, 1)
  assert.equal(resolveState(result.observations, { now: 200, semanticsTrusted: true }).state, 'PRIMARY')
})

assertScenario('single AC with multiple AP groups keeps AP identities separate', () => {
  const result = reconcileIdentity([
    observation({ ac: 'ac-a', serial: 'AP-001', mac: 'aa:bb:cc:00:00:01' }),
    observation({ ac: 'ac-a', serial: 'AP-002', mac: 'aa:bb:cc:00:00:02' }),
  ])
  assert.equal(result.identities.length, 2)
})

assertScenario('dual AC same serial resolves to one FIT-AP with two observations', () => {
  const result = reconcileIdentity([
    observation({ ac: 'ac-a', serial: 'AP-001', mac: 'aa:bb:cc:00:00:01' }),
    observation({ ac: 'ac-b', serial: 'AP-001', mac: 'aa:bb:cc:00:00:01' }),
  ])
  assert.equal(result.identities.length, 1)
  assert.equal(new Set(result.observations.map(item => item.identityId)).size, 1)
})

assertScenario('dual AC same trusted primary MAC resolves without serial', () => {
  const result = reconcileIdentity([
    observation({ ac: 'ac-a', mac: 'aa-bb-cc-00-00-03' }),
    observation({ ac: 'ac-b', mac: 'aabb.cc00.0003' }),
  ])
  assert.equal(result.identities.length, 1)
})

assertScenario('AC-A R/M and AC-B R/B resolves primary and preserves backup source', () => {
  const result = reconcileIdentity([
    observation({ ac: 'ac-a', serial: 'AP-005', mac: 'aa:bb:cc:00:00:05', raw: 'R/M' }),
    observation({ ac: 'ac-b', serial: 'AP-005', mac: 'aa:bb:cc:00:00:05', raw: 'R/B' }),
  ])
  const state = resolveState(result.observations, { now: 200, semanticsTrusted: true })
  assert.equal(state.state, 'PRIMARY')
  assert.equal(state.primaryAc, 'ac-a')
})

assertScenario('primary/backup switch accepts the newer primary after the old observation expires', () => {
  const result = reconcileIdentity([
    observation({ ac: 'ac-a', serial: 'AP-006', mac: 'aa:bb:cc:00:00:06', raw: 'R/M', observedAt: 100, expiresAt: 150 }),
    observation({ ac: 'ac-b', serial: 'AP-006', mac: 'aa:bb:cc:00:00:06', raw: 'R/M', observedAt: 200, expiresAt: 1000 }),
  ])
  const state = resolveState(result.observations, { now: 250, semanticsTrusted: true })
  assert.equal(state.state, 'PRIMARY')
  assert.equal(state.primaryAc, 'ac-b')
})

assertScenario('two fresh R/M observations are a conflict', () => {
  const result = reconcileIdentity([
    observation({ ac: 'ac-a', serial: 'AP-007', mac: 'aa:bb:cc:00:00:07', raw: 'R/M' }),
    observation({ ac: 'ac-b', serial: 'AP-007', mac: 'aa:bb:cc:00:00:07', raw: 'R/M' }),
  ])
  assert.equal(resolveState(result.observations, { now: 200, semanticsTrusted: true }).state, 'CONFLICT')
})

assertScenario('one AC offline does not turn an AP offline', () => {
  const result = reconcileIdentity([
    observation({ ac: 'ac-a', serial: 'AP-008', mac: 'aa:bb:cc:00:00:08', raw: 'R/M', online: false }),
    observation({ ac: 'ac-b', serial: 'AP-008', mac: 'aa:bb:cc:00:00:08', raw: 'R/B', online: true }),
  ])
  const state = resolveState(result.observations, { now: 200, semanticsTrusted: true })
  assert.notEqual(state.state, 'OFFLINE')
  assert.equal(state.sourceDegraded, true)
})

assertScenario('serial conflict is quarantined', () => {
  const result = reconcileIdentity([
    observation({ ac: 'ac-a', serial: 'AP-009', mac: 'aa:bb:cc:00:00:09' }),
    observation({ ac: 'ac-b', serial: 'AP-009', mac: 'aa:bb:cc:00:00:99' }),
  ])
  assert.equal(result.observations[1].conflict, 'IDENTITY_CONFLICT')
})

assertScenario('MAC conflict is quarantined', () => {
  const result = reconcileIdentity([
    observation({ ac: 'ac-a', serial: 'AP-010', mac: 'aa:bb:cc:00:00:10' }),
    observation({ ac: 'ac-b', serial: 'AP-011', mac: 'aa:bb:cc:00:00:10' }),
  ])
  assert.equal(result.observations[1].conflict, 'IDENTITY_CONFLICT')
})

assertScenario('AP replacement creates a new identity and links replacementOf', () => {
  const old = reconcileIdentity([observation({ ac: 'ac-a', serial: 'AP-012', mac: 'aa:bb:cc:00:00:12' })])
  const replacement = reconcileIdentity(
    [observation({ ac: 'ac-a', serial: 'AP-012A', mac: 'aa:bb:cc:00:00:13' })],
    new Map([['AP-012A', old.identities[0].id]]),
    'replacement',
  )
  assert.notEqual(replacement.identities[0].id, old.identities[0].id)
  assert.equal(replacement.identities[0].replacementOf, old.identities[0].id)
})

assertScenario('expired observations are ignored', () => {
  const result = reconcileIdentity([
    observation({ ac: 'ac-a', serial: 'AP-013', mac: 'aa:bb:cc:00:00:13', raw: 'R/M', expiresAt: 150 }),
    observation({ ac: 'ac-b', serial: 'AP-013', mac: 'aa:bb:cc:00:00:13', raw: 'R/B', expiresAt: 1000 }),
  ])
  const state = resolveState(result.observations, { now: 200, semanticsTrusted: true })
  assert.equal(state.state, 'BACKUP')
  assert.equal(state.backupAc, 'ac-b')
})

assertScenario('cross-site identity relation is rejected', () => {
  const result = reconcileIdentity([
    observation({ ac: 'ac-a', site: 'site-a', serial: 'AP-014', mac: 'aa:bb:cc:00:00:14' }),
    observation({ ac: 'ac-b', site: 'site-b', serial: 'AP-014', mac: 'aa:bb:cc:00:00:14' }),
  ])
  assert.equal(result.observations[1].conflict, 'SITE_CONFLICT')
})

assertScenario('stack or cluster members stay as separate AC sources', () => {
  const members = [
    { ac: 'ac-stack-1', cluster: 'cluster-1', role: 'STACK_MEMBER' },
    { ac: 'ac-stack-2', cluster: 'cluster-1', role: 'STACK_MEMBER' },
  ]
  assert.equal(new Set(members.map(member => member.ac)).size, 2)
  assert.equal(new Set(members.map(member => member.cluster)).size, 1)
})

console.log('PASS: wireless architecture scenarios complete (14 scenarios)')
