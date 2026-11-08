import { describe, expect, it } from 'vitest'
import { topologyRelationsCsv, type TopologyRelation } from './topologyExport'

const row: TopologyRelation = { profileId: 'p1', profileHost: '192.0.2.1', localDeviceId: 'd1', localInterface: 'GE1/0/1', neighborDevice: 'SW,"east"\nnext', neighborPort: 'GE1/0/2', siteId: 'site1', source: 'H3C_TASK_RAW:1', sourceAuthority: 'H3C_TASK_RAW', capturedScopeKey: '[1,"site1"]', capturedMs: 1800000000000, factRevision: 'f1', relationKey: 'r1', changeKind: 'UNCHANGED', scopeState: 'AUTHORIZED', claimsRevision: 'c1' }
const context = { loadedMs: 1800000000001, exportedMs: 1800000000002, failedProfiles: ['failed1'], potentiallyTruncatedProfiles: ['limited1'] }

describe('LLDP evidence CSV', () => {
  it('preserves quotes, multiline values, identity, scope and partial-load evidence', () => {
    const csv = topologyRelationsCsv([row], context)
    expect(csv.startsWith('\uFEFF"profile_id"')).toBe(true)
    expect(csv).toContain('"SW,""east""\nnext"')
    expect(csv).toContain('"H3C_TASK_RAW:1","f1","r1","UNCHANGED","AUTHORIZED","c1"')
    expect(csv).toContain('"[""failed1""]","[""limited1""]","CURRENT_FILTER_LOADED_CLAIMS_ONLY"')
    expect(csv).toContain(new Date(row.capturedMs).toISOString())
    expect(csv).toContain('\"source_authority\",\"captured_scope_key\"')
    expect(csv).toContain('\"H3C_TASK_RAW\",\"[1,\"\"site1\"\"]\"')
    expect(csv.endsWith('\r\n')).toBe(true)
  })
  it.each(['=HYPERLINK("evil")', '+SUM(1)', '-2+3', '@command', ' \t=command', '\nformula'])('neutralizes spreadsheet formula input %j', (value) => {
    expect(topologyRelationsCsv([{ ...row, neighborDevice: value }], context)).toContain('"\'' + value.replaceAll('"', '""') + '"')
  })
})
