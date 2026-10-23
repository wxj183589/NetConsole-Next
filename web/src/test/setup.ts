import { beforeEach } from 'vitest'

beforeEach(() => {
  sessionStorage.clear()
  document.documentElement.dataset.theme = 'light'
})

