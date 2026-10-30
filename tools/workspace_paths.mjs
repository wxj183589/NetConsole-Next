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
  if (fs.existsSync(absolute) && fs.lstatSync(absolute).isSymbolicLink()) throw new Error(`workspace root must not be a symlink: ${absolute}`)
  return absolute
}

export function workspacePath(category, ...parts) {
  if (!Object.hasOwn(LAYOUT, category)) throw new Error(`unknown NetConsole-Next workspace category: ${category}`)
  const base = path.resolve(workspaceRoot(), LAYOUT[category])
  const candidate = path.resolve(base, ...parts)
  const relative = path.relative(base, candidate)
  if (relative.startsWith('..') || path.isAbsolute(relative)) throw new Error(`workspace path escapes ${base}: ${candidate}`)
  if (fs.existsSync(base) && fs.lstatSync(base).isSymbolicLink()) throw new Error(`workspace category must not be a symlink: ${base}`)
  return candidate
}
