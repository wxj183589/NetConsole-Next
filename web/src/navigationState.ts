import { shallowRef } from 'vue'

export interface NavigationErrorState {
  path: string
  message: string
  at: number
}

export const lastNavigationError = shallowRef<NavigationErrorState | null>(null)
