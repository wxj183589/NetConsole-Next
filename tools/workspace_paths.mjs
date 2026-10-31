import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'

export const WORKSPACE_ROOT_ENV = 'NETCONSOLE_NEXT_WORKSPACE_ROOT'
const DEFAULT_WINDOWS_WORKSPACE_ROOT = 'D:\\study\\NetConsole-Next-Workspace'
const LAYOUT = {
  repository: 'NetConsole-Next',
  worktrees: 'worktrees',
  'test-data': 'test-data',
  build: 'build',
  diagnostic: 'diagnostic',
  release: 'release',
  poc: 'poc',
  reports: 'reports',
}
const LEGACY_ROOTS = [
  'D:\\study\\NetConsole-Next',
  'D:\\study\\NetConsole-Next-PoC',
  'D:\\study\\build\\NetConsole-Next',
  'D:\\study\\diagnostic\\NetConsole-Next',
  'D:\\study\\test-data\\NetConsole-Next',
  'D:\\study\\worktrees\\NetConsole-Next',
]

const normalized = value => path.normalize(path.resolve(value)).replace(/[\\/]+$/, '').toLowerCase()

function isReparsePoint(value) {
  try {
    // On Windows, lstat reports junctions as symbolic links as well.
    return fs.lstatSync(value).isSymbolicLink()
  } catch (error) {
    if (error?.code === 'ENOENT' || error?.code === 'ENOTDIR') return false
    throw error
  }
}

function assertNoReparsePoints(value) {
  let probe = value
  while (true) {
    if (isReparsePoint(probe)) throw new Error(`workspace path traverses a reparse point: ${probe}`)
    const parent = path.dirname(probe)
    if (parent === probe) break
    probe = parent
  }
}

export function workspaceRoot() {
  const configured = process.env[WORKSPACE_ROOT_ENV]?.trim()
  const root = configured || (process.platform === 'win32' ? DEFAULT_WINDOWS_WORKSPACE_ROOT : path.join(os.tmpdir(), 'netconsole-next-workspace'))
  const absolute = path.resolve(root)
  const key = normalized(absolute)
  if (LEGACY_ROOTS.some(item => normalized(item) === key)) throw new Error(`legacy NetConsole-Next workspace root: ${absolute}`)
  if (process.platform === 'win32' && key !== normalized(DEFAULT_WINDOWS_WORKSPACE_ROOT)) {
    const study = normalized('D:\\study')
    if (key === study || path.dirname(key) === study) throw new Error(`workspace root must not be a D:\\study child: ${absolute}`)
  }
  assertNoReparsePoints(absolute)
  return absolute
}

export function workspacePath(category, ...parts) {
  if (!Object.hasOwn(LAYOUT, category)) throw new Error(`unknown NetConsole-Next workspace category: ${category}`)
  const base = path.resolve(workspaceRoot(), LAYOUT[category])
  const candidate = path.resolve(base, ...parts)
  const relative = path.relative(base, candidate)
  if (relative.startsWith('..') || path.isAbsolute(relative)) throw new Error(`workspace path escapes ${base}: ${candidate}`)
  // Reject links at all existing levels, including workspace root ancestors.
  assertNoReparsePoints(candidate)
  return candidate
}
