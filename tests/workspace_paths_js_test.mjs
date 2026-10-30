import assert from 'node:assert/strict'
import { mkdtempSync, rmSync } from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { WORKSPACE_ROOT_ENV, workspacePath } from '../tools/workspace_paths.mjs'

const previous = process.env[WORKSPACE_ROOT_ENV]
const temporary = mkdtempSync(path.join(os.tmpdir(), 'netconsole-next-workspace-test-'))
process.env[WORKSPACE_ROOT_ENV] = path.join(temporary, 'NetConsole-Next-Workspace')
try {
  assert.equal(workspacePath('diagnostic', 'run.json'), path.join(process.env[WORKSPACE_ROOT_ENV], 'diagnostic', 'run.json'))
  assert.throws(() => workspacePath('build', '..', 'outside.txt'), /escapes/)
} finally {
  if (previous === undefined) delete process.env[WORKSPACE_ROOT_ENV]
  else process.env[WORKSPACE_ROOT_ENV] = previous
  rmSync(temporary, { recursive: true, force: true })
}
