import assert from 'node:assert/strict'
import { mkdtempSync, mkdirSync, rmSync, symlinkSync } from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { WORKSPACE_ROOT_ENV, workspacePath } from '../tools/workspace_paths.mjs'

const previous = process.env[WORKSPACE_ROOT_ENV]
const temporary = mkdtempSync(path.join(os.tmpdir(), 'netconsole-next-workspace-test-'))
process.env[WORKSPACE_ROOT_ENV] = path.join(temporary, 'NetConsole-Next-Workspace')
try {
  assert.equal(workspacePath('diagnostic', 'run.json'), path.join(process.env[WORKSPACE_ROOT_ENV], 'diagnostic', 'run.json'))
  assert.throws(() => workspacePath('build', '..', 'outside.txt'), /escapes/)
  const build = path.join(process.env[WORKSPACE_ROOT_ENV], 'build')
  const outside = path.join(temporary, 'outside')
  mkdirSync(build, { recursive: true })
  mkdirSync(outside)
  assert.throws(() => workspacePath('build', outside), /escapes/)
  assert.equal(workspacePath('build', path.join(build, 'allowed.png')), path.join(build, 'allowed.png'))
  symlinkSync(outside, path.join(build, 'browser'), process.platform === 'win32' ? 'junction' : 'dir')
  assert.throws(() => workspacePath('build', 'browser', 'capture.png'), /reparse point/)

  const actual = path.join(temporary, 'actual')
  mkdirSync(actual)
  const ancestor = path.join(temporary, 'ancestor-link')
  symlinkSync(actual, ancestor, process.platform === 'win32' ? 'junction' : 'dir')
  process.env[WORKSPACE_ROOT_ENV] = path.join(ancestor, 'NetConsole-Next-Workspace')
  mkdirSync(process.env[WORKSPACE_ROOT_ENV])
  assert.throws(() => workspacePath('build', 'capture.png'), /reparse point/)
} finally {
  if (previous === undefined) delete process.env[WORKSPACE_ROOT_ENV]
  else process.env[WORKSPACE_ROOT_ENV] = previous
  rmSync(temporary, { recursive: true, force: true })
}
