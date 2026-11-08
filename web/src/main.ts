import { createApp } from 'vue'
import { createPinia } from 'pinia'
import { ElAlert, ElButton, ElDialog, ElIcon, ElLoading, ElPagination } from 'element-plus'
import 'element-plus/dist/index.css'
import App from './App.vue'
import router from './router'
import './styles/theme.css'

createApp(App)
  .use(createPinia())
  .use(router)
  .use(ElAlert)
  .use(ElButton)
  .use(ElDialog)
  .use(ElIcon)
  .use(ElLoading)
  .use(ElPagination)
  .mount('#app')

