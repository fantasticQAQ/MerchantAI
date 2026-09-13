import { createApp } from 'vue'
import ElementPlus from 'element-plus'
import zhCn from 'element-plus/es/locale/lang/zh-cn'
import 'element-plus/dist/index.css'

import App from './App.vue'
import './style.css'

// 全量引入 Element Plus：这是个内部工具，没必要为省几百 KB 去配 unplugin 按需引入。
// 中文 locale 必须显式指定，否则分页/空态/确认框里的文案是英文。
createApp(App).use(ElementPlus, { locale: zhCn }).mount('#app')
