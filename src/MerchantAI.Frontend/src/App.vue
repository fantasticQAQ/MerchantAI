<script setup>
import { ref, reactive, computed, onMounted, nextTick } from 'vue'
import { ElMessageBox } from 'element-plus'
import { Refresh, FolderAdd, Search, Fold, Expand, CirclePlus } from '@element-plus/icons-vue'
import SessionSection from './SessionSection.vue'
import { marked } from 'marked'
import DOMPurify from 'dompurify'

const TOKEN_KEY = 'merchant-ai:token'
const SESSION_KEY = 'merchant-ai:sessionId'
const MODE_KEY = 'merchant-ai:mode'
const COLLAPSE_KEY = 'merchant-ai:collapsed'

// ---------------------------------------------------------------- 登录态

const token = ref(localStorage.getItem(TOKEN_KEY) || '')
const currentUser = ref(null)
const loginForm = reactive({ userName: '', password: '' })
const loginError = ref('')
const loginLoading = ref(false)
const booting = ref(true)

// ---------------------------------------------------------------- AI 权限级别

// 用 el-select 而不是原生 <select>：原生 option 的弹出列表由操作系统绘制，
// CSS 完全管不到，永远是直角加系统默认字体的白底方框，和这套界面割裂。
const permissionMode = ref(localStorage.getItem(MODE_KEY) || 'approve')

const modeOptions = [
  { value: 'readonly', label: '仅可查看', hint: '写操作一律拒绝' },
  { value: 'approve', label: '需确认', hint: '写操作点确认后才执行（推荐）' },
  { value: 'full', label: '完全权限', hint: '写操作直接执行，不再弹确认' }
]
const modeHints = Object.fromEntries(modeOptions.map((m) => [m.value, m.hint]))
const currentMode = computed(
  () => modeOptions.find((m) => m.value === permissionMode.value) || modeOptions[1]
)

/** 统一走 Element Plus 的确认框，所有调用点不用关心它是怎么弹的。 */
async function askConfirm({ title, message, confirmText = '确定', danger = false }) {
  try {
    await ElMessageBox.confirm(message, title, {
      confirmButtonText: confirmText,
      cancelButtonText: '取消',
      type: danger ? 'warning' : 'info',
      // 换行靠 white-space: pre-line，ElMessageBox 默认会把换行吃掉
      customClass: 'ai-confirm'
    })
    return true
  } catch (e) {
    return false // 取消 / 关闭都走 reject
  }
}

/** el-select 用的是 :model-value，点了取消不需要回滚，显示值一直跟着 permissionMode。 */
async function changeMode(next) {
  if (!next || next === permissionMode.value) return

  if (next === 'full') {
    const ok = await askConfirm({
      title: '开启「完全权限」？',
      message:
        'AI 的写操作（下单 / 删单 / 改商品 / 退款…）会不经确认直接执行，' +
        '点错一次就没有挽回余地。\n\n建议只在批量操作时临时开启，用完切回「需确认」。',
      confirmText: '我明白，开启',
      danger: true
    })
    if (!ok) return
  }

  permissionMode.value = next
  localStorage.setItem(MODE_KEY, next)
}

// ---------------------------------------------------------------- 主视图状态

const view = ref('chat') // 'chat' | 'trace'
const sessions = ref([])
const groups = ref([])
const sessionsLoading = ref(false)
const traceEntries = ref([])
const traceLoading = ref(false)
const expanded = ref({}) // 轨迹条目的展开状态，按索引存
const panelError = ref('')
/** 新建会话时它该落进哪个分组（会话要等第一条消息发出去才真正存在，所以先记在这儿）。 */
const pendingGroupId = ref(null)

const messages = ref([]) // { role, text, html?, pending: [{actionId, summary}] | null }
const input = ref('')
const loading = ref(false)
const sessionId = ref('')
const listEl = ref(null)

// ---------------------------------------------------------------- 分组折叠

// 存 localStorage：折叠是「我怎么看这个列表」的偏好，刷新一次就全展开会很难受。
const collapsedSections = ref(new Set(readCollapsed()))

function readCollapsed() {
  try {
    const raw = JSON.parse(localStorage.getItem(COLLAPSE_KEY) || '[]')
    return Array.isArray(raw) ? raw : []
  } catch (e) {
    return []
  }
}

function isCollapsed(id) {
  return collapsedSections.value.has(id)
}

function persistCollapsed(set) {
  try {
    localStorage.setItem(COLLAPSE_KEY, JSON.stringify([...set]))
  } catch (e) {
    // 存不进去只影响下次打开时的展开状态，不值得打断用户
  }
}

function toggleCollapse(id) {
  // 换一个新 Set 而不是原地改：ref 里放 Set 时原地改不会触发更新
  const next = new Set(collapsedSections.value)
  if (next.has(id)) next.delete(id)
  else next.add(id)

  collapsedSections.value = next
  persistCollapsed(next)
}

// ---------------------------------------------------------------- 列表分区

/** 搜索框按标题过滤。 */
const searchText = ref('')

const visibleSessions = computed(() => {
  const q = searchText.value.trim().toLowerCase()
  if (!q) return sessions.value
  return sessions.value.filter((s) => (s.title || '').toLowerCase().includes(q))
})

/** 置顶的会话从各自分组里拎出来，单独排在最上面。 */
const pinnedSessions = computed(() => visibleSessions.value.filter((s) => s.pinned))

/** 分组区：分组（置顶的在前）+ 末尾一个「未分组」。空分组也保留，否则用户建完就找不到了。 */
const groupedSections = computed(() => {
  const rest = visibleSessions.value.filter((s) => !s.pinned)

  const sections = groups.value.map((g) => ({
    id: g.groupId,
    name: g.name,
    pinned: g.pinned,
    isGroup: true,
    items: rest.filter((s) => s.groupId === g.groupId)
  }))

  const ungrouped = rest.filter((s) => !s.groupId)
  if (ungrouped.length > 0 || sections.length > 0) {
    sections.push({
      id: '__ungrouped__',
      name: '未分组',
      pinned: false,
      isGroup: false,
      items: ungrouped
    })
  }

  return sections
})

/** 页面上所有可拖放的区（含置顶区），拖动时按 id 找目标。 */
const allSections = computed(() => {
  const pinned = {
    id: '__pinned__',
    name: '置顶',
    pinned: true,
    isGroup: false,
    items: pinnedSessions.value
  }
  return pinnedSessions.value.length ? [pinned, ...groupedSections.value] : groupedSections.value
})

/** 一键全部折叠/展开（以「是否还有没折的」来定方向，和常见的目录树一致）。 */
const allCollapsed = computed(() => {
  const ids = allSections.value.map((s) => s.id)
  return ids.length > 0 && ids.every((id) => collapsedSections.value.has(id))
})

function toggleAllCollapsed() {
  const next = allCollapsed.value ? new Set() : new Set(allSections.value.map((s) => s.id))
  collapsedSections.value = next
  persistCollapsed(next)
}

// ---------------------------------------------------------------- 拖动排序

// 用原生 HTML5 拖放：只是列表内重排，为这个引一个拖拽库不划算。
const dragSession = ref(null)
const dropHint = ref(null) // { sectionId, sessionId, position }

function onDragStart(s) {
  dragSession.value = s
}

function onDragEnd() {
  dragSession.value = null
  dropHint.value = null
}

function onDragOver(target, sectionId) {
  if (!dragSession.value) return
  if (!target) {
    // 落在空白处 = 追加到这个区的末尾
    dropHint.value = { sectionId, sessionId: null, position: 'end' }
    return
  }
  if (target.sessionId === dragSession.value.sessionId) {
    dropHint.value = null
    return
  }
  dropHint.value = { sectionId, sessionId: target.sessionId, position: 'before' }
}

/** 算一次「拖到这儿之后这个区的新顺序」，返回 null 表示这次拖动无效。 */
function computeNewOrder(section, target) {
  const dragged = dragSession.value
  if (!dragged || !section) return null

  const ids = section.items.map((s) => s.sessionId).filter((id) => id !== dragged.sessionId)
  const at = target ? ids.indexOf(target.sessionId) : -1

  if (at < 0) ids.push(dragged.sessionId) // 落在空白处，放到最后
  else ids.splice(at, 0, dragged.sessionId)

  return ids
}

async function patchSession(id, body) {
  try {
    await api(`/api/ai/sessions/${encodeURIComponent(id)}`, {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body)
    })
  } catch (e) {
    if (e.message !== 'UNAUTHORIZED') panelError.value = '移动会话失败：' + e.message
  }
}

async function persistOrder(sessionIds) {
  try {
    await api('/api/ai/sessions/order', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ sessionIds })
    })
  } catch (e) {
    if (e.message !== 'UNAUTHORIZED') panelError.value = '保存顺序失败：' + e.message
  }
}

/** 把会话挪到目标区（跨区时才需要改归属；置顶区靠 pinned 表示）。 */
async function moveToSection(session, sectionId) {
  if (sectionId === '__pinned__') {
    if (!session.pinned) await patchSession(session.sessionId, { pinned: true })
    return
  }

  const body = { pinned: false }
  if (sectionId === '__ungrouped__') body.groupId = '' // 空字符串 = 移出分组
  else body.groupId = sectionId

  const moved = session.pinned || (session.groupId || '') !== (body.groupId || '')
  if (moved) await patchSession(session.sessionId, body)
}

async function onDrop(target, sectionId) {
  const dragged = dragSession.value
  const section = allSections.value.find((x) => x.id === sectionId)
  const ids = computeNewOrder(section, target)

  dragSession.value = null
  dropHint.value = null
  if (!dragged || !ids) return

  await moveToSection(dragged, sectionId)
  await persistOrder(ids)
  await loadSessions()
}

/** 拖到分组标题下的空白区 = 挪进这个分组并排到最后。 */
async function onDropSection(section) {
  if (!dropHint.value || dropHint.value.sectionId !== section.id) return
  await onDrop(null, section.id)
}

// ---------------------------------------------------------------- 通用

marked.setOptions({ breaks: true, gfm: true })

/** 模型输出是不可信内容，渲染成 HTML 之前必须 sanitize。 */
function renderMarkdown(text) {
  try {
    return DOMPurify.sanitize(marked.parse(text || ''))
  } catch (e) {
    return null
  }
}

function scrollToBottom() {
  nextTick(() => {
    if (listEl.value) listEl.value.scrollTop = listEl.value.scrollHeight
  })
}

function pushAi(text, pending = null) {
  messages.value.push({ role: 'ai', text: text || '', html: renderMarkdown(text), pending })
}

function rememberSession(id) {
  sessionId.value = id || ''
  if (id) localStorage.setItem(SESSION_KEY, id)
  else localStorage.removeItem(SESSION_KEY)
}

/** 统一带上 Bearer；401 一律当作登录失效，回到登录页。 */
async function api(path, options = {}) {
  const headers = { ...(options.headers || {}) }
  if (token.value) headers.Authorization = `Bearer ${token.value}`

  const res = await fetch(path, { ...options, headers })

  if (res.status === 401) {
    logout('登录已过期，请重新登录')
    throw new Error('UNAUTHORIZED')
  }
  return res
}

async function jsonBody(res, fallback = {}) {
  return await res.json().catch(() => fallback)
}

/** 轨迹时间戳要带上日期，一个会话可能跨天。 */
function formatStamp(value) {
  if (!value) return ''
  const d = new Date(value)
  if (Number.isNaN(d.getTime())) return ''
  return d.toLocaleString('zh-CN', {
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit'
  })
}

// ---------------------------------------------------------------- 登录

async function doLogin() {
  if (loginLoading.value) return
  loginError.value = ''
  loginLoading.value = true
  try {
    const res = await fetch('/api/ai/login', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ userName: loginForm.userName, password: loginForm.password })
    })
    const data = await jsonBody(res)

    if (!res.ok || !data.token) {
      loginError.value = data.message || data.title || `登录失败（HTTP ${res.status}）`
      return
    }

    token.value = data.token
    localStorage.setItem(TOKEN_KEY, data.token)
    loginForm.password = ''

    await bootstrap()
  } catch (e) {
    loginError.value = '登录失败：' + e.message
  } finally {
    loginLoading.value = false
  }
}

function logout(message) {
  token.value = ''
  currentUser.value = null
  localStorage.removeItem(TOKEN_KEY)
  // 下一个人用这台机器时，不该继承上一个人的会话
  localStorage.removeItem(SESSION_KEY)
  sessionId.value = ''
  messages.value = []
  sessions.value = []
  traceEntries.value = []
  view.value = 'chat'
  if (message) loginError.value = message
}

// ---------------------------------------------------------------- 启动

async function bootstrap() {
  // 先确认 token 还有效，并拿到当前用户
  const meRes = await api('/api/ai/me')
  if (!meRes.ok) return
  currentUser.value = await jsonBody(meRes)

  await loadSessions()

  const saved = localStorage.getItem(SESSION_KEY)
  if (!saved) return
  await loadSession(saved, { silent: true })
}

// ---------------------------------------------------------------- 会话列表

async function loadSessions() {
  sessionsLoading.value = true
  try {
    const res = await api('/api/ai/sessions?limit=50')
    const data = await jsonBody(res)
    if (res.ok) {
      sessions.value = data.sessions || []
      groups.value = data.groups || []
    }
  } catch (e) {
    if (e.message !== 'UNAUTHORIZED') panelError.value = '加载会话列表失败：' + e.message
  } finally {
    sessionsLoading.value = false
  }
}

const viewOptions = [
  { label: '会话', value: 'chat' },
  { label: '轨迹', value: 'trace' }
]

/** 页签切换。切到轨迹页要先把这个会话的轨迹拉下来。 */
function switchView(next) {
  view.value = next
  if (next === 'trace') loadTrace()
}

function selectSession(s) {
  view.value = 'chat'
  loadSession(s.sessionId)
}

function newSession() {
  resetSession()
  view.value = 'chat'
  pendingGroupId.value = null
}

// ---------------------------------------------------------------- 会话

async function loadSession(id, { silent = false } = {}) {
  if (!silent) loading.value = true
  panelError.value = ''
  try {
    const res = await api(`/api/ai/sessions/${encodeURIComponent(id)}`)
    const data = await jsonBody(res)

    if (!res.ok) {
      rememberSession('')
      messages.value = []
      if (!silent) pushAi(data.message || '这个会话已过期或不存在，已为你开一个新会话')
      return
    }

    rememberSession(id)
    messages.value = []

    for (const m of data.messages || []) {
      // 聊天视图只显示用户输入和助手回复；工具调用/结果留给「审计轨迹」看
      if (m.role === 'user' && m.text) {
        messages.value.push({ role: 'user', text: m.text })
      } else if (m.role === 'assistant' && m.text) {
        pushAi(m.text)
      }
    }

    // 把还没处理的确认卡片恢复出来，否则刷新后就没法继续确认了
    if (data.pendingActions?.length) {
      messages.value.push({
        role: 'ai',
        text: '',
        html: '',
        pending: data.pendingActions.map((p) => ({ actionId: p.actionId, summary: p.summary }))
      })
    }
  } catch (e) {
    if (e.message !== 'UNAUTHORIZED') panelError.value = '加载会话失败：' + e.message
  } finally {
    loading.value = false
    scrollToBottom()
  }
}

function resetSession() {
  if (loading.value) return
  rememberSession('')
  messages.value = []
  input.value = ''
  traceEntries.value = []
  pendingGroupId.value = null
}

async function removeSession(s) {
  const id = s.sessionId ?? s
  // 提示是给店主看的，不是给开发看的：说清「删了会怎样」就够了，
  // 不用交代软删除、存储实现、审计留痕这些细节。
  const ok = await askConfirm({
    title: '删除这个会话？',
    message: '删除后，这个会话和它的审计轨迹都不再显示。',
    confirmText: '删除',
    danger: true
  })
  if (!ok) return

  try {
    const res = await api(`/api/ai/sessions/${encodeURIComponent(id)}`, { method: 'DELETE' })
    const data = await jsonBody(res)
    if (!res.ok) {
      panelError.value = data.message || `删除失败（HTTP ${res.status}）`
      return
    }
    sessions.value = sessions.value.filter((x) => x.sessionId !== id)
    if (sessionId.value === id) newSession()
  } catch (e) {
    if (e.message !== 'UNAUTHORIZED') panelError.value = '删除失败：' + e.message
  }
}

// ---------------------------------------------------------------- 重命名 / 置顶 / 分组

/** 弹一个输入框。用户取消时返回 null。 */
async function askText({ title, message, value = '', maxlength = 40 }) {
  try {
    const result = await ElMessageBox.prompt(message, title, {
      inputValue: value,
      confirmButtonText: '保存',
      cancelButtonText: '取消',
      inputValidator: (v) => {
        const text = (v || '').trim()
        if (!text) return '不能为空'
        if (text.length > maxlength) return `最长 ${maxlength} 个字符`
        return true
      }
    })
    return result.value.trim()
  } catch (e) {
    return null // 取消
  }
}

/** 改会话的名字。改完标记为手动命名，之后模型就不会再自动覆盖它了。 */
async function renameSession(s) {
  const title = await askText({
    title: '重命名会话',
    message: '给这个会话起个好认的名字',
    value: s.title === '（空会话）' ? '' : s.title
  })
  if (!title) return

  try {
    const res = await api(`/api/ai/sessions/${encodeURIComponent(s.sessionId)}`, {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ title })
    })
    if (!res.ok) {
      const data = await jsonBody(res)
      panelError.value = data.message || '重命名失败'
      return
    }
    sessions.value = sessions.value.map((x) => (x.sessionId === s.sessionId ? { ...x, title } : x))
  } catch (e) {
    if (e.message !== 'UNAUTHORIZED') panelError.value = '重命名失败：' + e.message
  }
}

async function togglePinSession(s) {
  const pinned = !s.pinned
  try {
    const res = await api(`/api/ai/sessions/${encodeURIComponent(s.sessionId)}`, {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ pinned })
    })
    if (!res.ok) return
    sessions.value = sessions.value.map((x) => (x.sessionId === s.sessionId ? { ...x, pinned } : x))
  } catch (e) {
    if (e.message !== 'UNAUTHORIZED') panelError.value = '置顶失败：' + e.message
  }
}

async function createGroup() {
  const name = await askText({ title: '新建分组', message: '给分组起个名字', maxlength: 20 })
  if (!name) return

  try {
    const res = await api('/api/ai/groups', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ name })
    })
    const data = await jsonBody(res)
    if (!res.ok) {
      panelError.value = data.message || '新建分组失败'
      return
    }
    await loadSessions()
  } catch (e) {
    if (e.message !== 'UNAUTHORIZED') panelError.value = '新建分组失败：' + e.message
  }
}

async function patchGroup(groupId, body) {
  try {
    const res = await api(`/api/ai/groups/${encodeURIComponent(groupId)}`, {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body)
    })
    const data = await jsonBody(res)
    if (!res.ok) {
      panelError.value = data.message || '分组操作失败'
      return
    }
    await loadSessions()
  } catch (e) {
    if (e.message !== 'UNAUTHORIZED') panelError.value = '分组操作失败：' + e.message
  }
}

async function renameGroup(sec) {
  const name = await askText({
    title: '重命名分组',
    message: '给分组起个新名字',
    value: sec.name,
    maxlength: 20
  })
  if (!name) return
  await patchGroup(sec.id, { name })
}

async function toggleGroupPin(sec) {
  await patchGroup(sec.id, { pinned: !sec.pinned })
}

async function deleteGroup(sec) {
  const ok = await askConfirm({
    title: `删除分组「${sec.name}」？`,
    message: `组里的 ${sec.items.length} 个会话不会被删除，只是回到「未分组」。`,
    confirmText: '删除分组',
    danger: true
  })
  if (!ok) return

  try {
    const res = await api(`/api/ai/groups/${encodeURIComponent(sec.id)}`, { method: 'DELETE' })
    if (!res.ok) {
      const data = await jsonBody(res)
      panelError.value = data.message || '删除分组失败'
      return
    }
    await loadSessions()
  } catch (e) {
    if (e.message !== 'UNAUTHORIZED') panelError.value = '删除分组失败：' + e.message
  }
}

/** 分组三点菜单统一入口，菜单项和操作一一对应，加一项只改这儿和模板。 */
function onGroupCommand(cmd, sec) {
  if (cmd === 'new') newSessionInGroup(sec)
  else if (cmd === 'rename') renameGroup(sec)
  else if (cmd === 'pin') toggleGroupPin(sec)
  else if (cmd === 'delete') deleteGroup(sec)
}

/** 在这个分组里开一个新会话。会话要等第一条消息发出去才真正存在，所以先记着分组 id。 */
function newSessionInGroup(sec) {
  resetSession()
  view.value = 'chat'
  pendingGroupId.value = sec.id
}

/** 会话刚被创建出来时，把它落到之前选好的分组里。 */
async function applyPendingGroup(id) {
  const groupId = pendingGroupId.value
  pendingGroupId.value = null
  if (!groupId) return

  try {
    await api(`/api/ai/sessions/${encodeURIComponent(id)}`, {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ groupId })
    })
  } catch (e) {
    // 分不进去不算致命，会话本身是好的
  }
}

// ---------------------------------------------------------------- 轨迹（按会话）

// 轨迹不提供单独删除，它是会话的审计记录，跟着会话一起走。
// 删掉会话，它的轨迹也就不再展示了；单独把轨迹抹掉只会让审计链断掉。

/** 拉当前会话的轨迹。后端按时间正序返回，读起来就是一条流程。 */
async function loadTrace() {
  if (!sessionId.value) {
    traceEntries.value = []
    return
  }

  traceLoading.value = true
  panelError.value = ''
  expanded.value = {}
  try {
    const res = await api(
      `/api/ai/traces?sessionId=${encodeURIComponent(sessionId.value)}&limit=2000`
    )
    const data = await jsonBody(res)
    if (res.ok) traceEntries.value = data.entries || []
    else panelError.value = data.message || '加载轨迹失败'
  } catch (e) {
    if (e.message !== 'UNAUTHORIZED') panelError.value = '加载轨迹失败：' + e.message
  } finally {
    traceLoading.value = false
  }
}

function toggleTrace(index) {
  expanded.value = { ...expanded.value, [index]: !expanded.value[index] }
}

const eventLabel = {
  user_input: '用户输入',
  tool_pre: '调用工具',
  tool_post: '工具返回',
  tool_denied: '角色不足',
  tool_blocked: '策略拦截',
  approval_pending: '登记待确认',
  approval_invalid: '参数被拒',
  approval_denied: '越权确认',
  approval_cancelled: '取消操作',
  loop_guard: '循环护栏',
  agent_timeout: '超时中止',
  agent_error: '调用失败',
  turn_summary: '本轮耗时',
  session_hidden: '移除会话',
  traces_hidden: '移除轨迹',
  answer: '回答'
}

/** 事件归类，只用来决定时间线上那颗点的颜色。 */
function eventKind(type) {
  if (type === 'user_input') return 'input'
  if (type === 'answer') return 'answer'
  if (type === 'approval_pending') return 'pending'
  if (type === 'tool_pre' || type === 'tool_post') return 'tool'
  if (
    type === 'tool_denied' ||
    type === 'tool_blocked' ||
    type === 'loop_guard' ||
    type === 'approval_invalid' ||
    type === 'approval_denied' ||
    type === 'agent_error' ||
    type === 'agent_timeout'
  ) {
    return 'warn'
  }
  return 'other'
}

/** el-timeline 圆点的颜色 */
const kindColors = {
  input: '#3b82f6',
  answer: '#10b981',
  tool: '#0ea5e9',
  pending: '#f59e0b',
  warn: '#ef4444',
  other: '#cbd5e1'
}
const traceColor = (type) => kindColors[eventKind(type)]

/** el-tag 的类型。Element Plus 没有 cyan，工具类用 primary 代替。 */
const kindTagTypes = {
  input: 'primary',
  answer: 'success',
  tool: 'info',
  pending: 'warning',
  warn: 'danger',
  other: 'info'
}
const traceTagType = (type) => kindTagTypes[eventKind(type)]

/** 轨迹页的筛选：默认「全部」，其余按类型收窄。 */
const traceFilters = [
  { value: 'all', label: '全部' },
  { value: 'tool', label: '工具调用' },
  { value: 'approval', label: '审批' },
  { value: 'warn', label: '异常 / 拦截' }
]
const traceFilter = ref('all')

const visibleTraces = computed(() => {
  if (traceFilter.value === 'all') return traceEntries.value
  return traceEntries.value.filter((e) => {
    const kind = eventKind(e.eventType)
    if (traceFilter.value === 'tool') return kind === 'tool'
    if (traceFilter.value === 'approval') return kind === 'pending'
    if (traceFilter.value === 'warn') return kind === 'warn'
    return true
  })
})

/**
 * 轨迹页顶部那一排体检数据。
 * 「本轮耗时」事件是专门为此加的，它带 durationMs 和 token 用量，
 * 光看事件条数是看不出慢在哪的。
 */
const traceStats = computed(() => {
  const list = traceEntries.value
  const sums = list.filter((e) => e.eventType === 'turn_summary')
  const readNumber = (payload, key) => {
    try {
      const obj = typeof payload === 'string' ? JSON.parse(payload) : payload
      const v = obj?.[key]
      return typeof v === 'number' ? v : null
    } catch (e) {
      return null
    }
  }

  let durationMs = 0
  let tokens = 0
  let hasTokens = false
  for (const e of sums) {
    durationMs += readNumber(e.payload, 'durationMs') || 0
    const t = readNumber(e.payload, 'totalTokens')
    if (t !== null) {
      tokens += t
      hasTokens = true
    }
  }

  return {
    turns: sums.length,
    toolCalls: list.filter((e) => e.eventType === 'tool_post').length,
    approvals: list.filter((e) => e.eventType === 'approval_pending').length,
    blocked: list.filter((e) => eventKind(e.eventType) === 'warn').length,
    durationText: durationMs > 0 ? (durationMs / 1000).toFixed(1) + 's' : '—',
    tokenText: hasTokens ? tokens.toLocaleString('zh-CN') : '—'
  }
})

function summarizePayload(payload) {
  if (payload === null || payload === undefined) return ''
  const text = typeof payload === 'string' ? payload : JSON.stringify(payload)
  return text.length > 180 ? text.slice(0, 180) + '…' : text
}

/** 展开时把 JSON 排版好再显示，一行糊在一起的 payload 根本读不了。 */
function prettyPayload(payload) {
  if (payload === null || payload === undefined) return ''
  if (typeof payload === 'string') {
    try {
      return JSON.stringify(JSON.parse(payload), null, 2)
    } catch (e) {
      return payload
    }
  }
  return JSON.stringify(payload, null, 2)
}

/** 页头显示当前会话的标题。列表还没加载出来时退回第一条用户消息。 */
const sessionTitle = computed(() => {
  const found = sessions.value.find((s) => s.sessionId === sessionId.value)
  if (found) return found.title
  const first = messages.value.find((m) => m.role === 'user' && m.text)
  return first ? first.text.slice(0, 24) : '（新会话）'
})

// ---------------------------------------------------------------- 对话

// 一次用户输入可能要跑很久（「建 1000 个商品」约 17 轮乘 60 次工具调用）。
// 后端每轮有调用额度，跑满就把 hasMore 交回来；这里自动再发一轮，
// 用户不用手打「继续」。每轮都是独立的短请求，不会把单个 HTTP 请求拖到超时。
const MAX_AUTO_ROUNDS = 40
const autoRound = ref(0)

// ---- 右侧消息刻度条 ----

// 每渲染一条消息就找一下当前滚到哪条。消息数不多（几十条），遍历一次的开销可以忽略；
// 真到几百条再考虑用 IntersectionObserver。
const activeMessage = ref(0)

/**
 * 只给我自己发的消息打点，AI 的回复一条接一条，全打上就成一堵墙了，反而找不到定位。
 * 每根刻度同时带上「这条提问」和「紧随其后的那条回复」，
 * 悬浮时两块都能看到 —— 光有提问的话，还是认不出这轮聊了什么。
 */
const userTicks = computed(() => {
  const list = messages.value
  return list
    .map((m, index) => ({ m, index }))
    .filter((x) => x.m.role === 'user')
    .map((x) => {
      let reply = ''
      for (let i = x.index + 1; i < list.length; i++) {
        if (list[i].role === 'user') break // 下一轮提问了，说明这轮还没回复
        if (list[i].role === 'ai' && list[i].text) {
          reply = list[i].text
          break
        }
      }

      return {
        index: x.index,
        question: (x.m.text || '').replace(/\s+/g, ' ').slice(0, 60) || '你的提问',
        // 回复是 Markdown，压成一行再去掉标记符号，浮层里才读得下去
        reply: reply
          .replace(/[#*`>|]/g, '')
          .replace(/\s+/g, ' ')
          .trim()
          .slice(0, 90)
      }
    })
})

/** 当前看的这段里，最后一条属于我的提问。 */
const activeTick = computed(() => {
  let active = 0
  userTicks.value.forEach((t, k) => {
    if (t.index <= activeMessage.value) active = k
  })
  return active
})

function onListScroll() {
  const el = listEl.value
  if (!el) return

  const rows = el.querySelectorAll('.row')
  if (rows.length === 0) return

  const top = el.scrollTop
  let index = 0
  rows.forEach((row, i) => {
    if (row.offsetTop - el.offsetTop <= top + 48) index = i
  })
  activeMessage.value = index
}

function scrollToMessage(index) {
  const el = listEl.value
  if (!el) return
  const row = el.querySelectorAll('.row')[index]
  if (!row) return

  el.scrollTo({ top: Math.max(0, row.offsetTop - el.offsetTop - 16), behavior: 'smooth' })
  activeMessage.value = index
}

// ---- 发送 ----

async function send() {
  const text = input.value.trim()
  if (!text || loading.value) return
  input.value = ''
  messages.value.push({ role: 'user', text })
  loading.value = true
  autoRound.value = 0
  scrollToBottom()

  try {
    await runChat(text)
  } catch (e) {
    if (e.message !== 'UNAUTHORIZED') pushAi('网络错误：' + e.message)
  } finally {
    loading.value = false
    autoRound.value = 0
    scrollToBottom()
    // 新会话、标题、消息数、轨迹条数都会变，顺手刷新左侧列表
    loadSessions()
  }
}

/** 一轮 /chat 请求。返回后端响应体；HTTP 不成功时把错误显示出来并返回 null。 */
async function postChat(message, auto) {
  const res = await api('/api/ai/chat', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      message,
      sessionId: sessionId.value || null,
      mode: permissionMode.value,
      autoContinue: auto || null
    })
  })
  const data = await jsonBody(res)
  if (!res.ok) {
    pushAi(data.message || `请求失败（HTTP ${res.status}）`)
    return null
  }
  return data
}

/** 把一轮响应显示出来，返回这轮的待确认卡片（没有就是 null）。 */
function applyChatResult(data) {
  if (data.sessionId && data.sessionId !== sessionId.value) {
    rememberSession(data.sessionId)
    // 新会话刚诞生，如果之前是「在某分组里新建」，这会儿把它落进那个分组
    applyPendingGroup(data.sessionId)
  }

  const pending = data.pendingActions?.length
    ? data.pendingActions
    : data.pendingAction
      ? [data.pendingAction]
      : null
  pushAi(data.reply, pending)

  // 模型给会话起了个新标题，列表里立刻换掉，不用等这轮全部跑完
  if (data.title) {
    const id = data.sessionId || sessionId.value
    if (sessions.value.some((x) => x.sessionId === id)) {
      sessions.value = sessions.value.map((x) =>
        x.sessionId === id ? { ...x, title: data.title } : x
      )
    }
  }

  return pending
}

/** 一直发到任务做完为止。startAuto=true 表示第一发就是续跑（确认后接着做时用）。 */
async function runChat(firstMessage, startAuto = false) {
  let message = firstMessage
  let auto = startAuto

  for (let round = 0; ; round++) {
    const data = await postChat(message, auto)
    if (!data) return

    const pending = applyChatResult(data)

    // 有卡片要停下等用户点确认；没有 hasMore 说明任务真的做完了
    if (pending || !data.hasMore) return

    if (round + 1 >= MAX_AUTO_ROUNDS) {
      pushAi(`（已连续自动执行 ${MAX_AUTO_ROUNDS} 轮，先停在这里。还要继续就跟我说一声。）`)
      return
    }

    auto = true
    autoRound.value = round + 1
    scrollToBottom()
  }
}

async function confirm(message) {
  const pendings = message.pending
  if (!pendings?.length || loading.value) return
  loading.value = true
  autoRound.value = 0
  try {
    const res = await api('/api/ai/confirm', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        actionIds: pendings.map((p) => p.actionId),
        sessionId: sessionId.value,
        mode: permissionMode.value
      })
    })
    const data = await jsonBody(res)
    if (res.ok) clearPending(message)
    pushAi((res.ok && data.reply) || data.message || `已确认（HTTP ${res.status}）`)

    // 后端会自动让模型接着做下一步，这里把后续回复和新的确认卡片一起显示出来
    if (res.ok && data.continuation) {
      const pending = data.pendingActions?.length ? data.pendingActions : null
      pushAi(data.continuation, pending)

      // 确认这一波之后模型还没做完（比如还有下一批要接着跑），继续自动驱动，别让用户再打「继续」
      if (!pending && data.hasMore) await runChat('', true)
    }
  } catch (e) {
    if (e.message !== 'UNAUTHORIZED') pushAi('确认失败：' + e.message)
  } finally {
    loading.value = false
    autoRound.value = 0
    scrollToBottom()
    loadSessions()
  }
}

async function cancel(message) {
  const pendings = message.pending
  if (!pendings?.length) return
  clearPending(message)
  try {
    await api('/api/ai/cancel', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        actionIds: pendings.map((p) => p.actionId),
        sessionId: sessionId.value
      })
    })
  } catch (e) {
    // 取消失败不影响本地卡片已经收起；服务端那条会自然过期
  }
  pushAi('已取消该操作')
  scrollToBottom()
}

function clearPending(message) {
  message.pending = null
}

function onKeydown(e) {
  if (e.key === 'Enter' && !e.shiftKey) {
    e.preventDefault()
    send()
  }
}

onMounted(async () => {
  try {
    if (token.value) await bootstrap()
  } catch (e) {
    // bootstrap 里已经处理了 401；其它错误让它停在登录页
  } finally {
    booting.value = false
  }
})
</script>

<template>
  <div class="chat">
    <div v-if="booting" class="boot" v-loading="true" element-loading-text="加载中…"></div>

    <!-- 登录 -->
    <div v-else-if="!currentUser" class="login">
      <el-card class="login-card" shadow="never">
        <div class="login-title">商户 AI 助手</div>
        <div class="login-sub">请使用商户后台账号登录</div>

        <el-input
          v-model="loginForm.userName"
          size="large"
          placeholder="用户名"
          autocomplete="username"
          @keydown.enter="doLogin"
        />
        <el-input
          v-model="loginForm.password"
          size="large"
          type="password"
          placeholder="密码"
          show-password
          autocomplete="current-password"
          @keydown.enter="doLogin"
        />

        <el-alert v-if="loginError" :title="loginError" type="error" :closable="false" show-icon />

        <el-button
          type="primary"
          size="large"
          class="login-btn"
          :loading="loginLoading"
          :disabled="!loginForm.userName.trim() || !loginForm.password"
          @click="doLogin"
        >
          登录
        </el-button>
      </el-card>
    </div>

    <template v-else>
      <div class="app-body">
        <!-- 整体左右分栏：左边一列管「有哪些会话」，右边一列管「当前这个会话」。
             品牌名从顶部横栏挪进来了，顶部横栏撤掉之后右侧的内容整体上移。 -->
        <aside class="sidebar">
          <div class="sidebar-brand">商户 AI 助手</div>

          <div class="sidebar-head">
            <button class="new-session-btn" :disabled="loading" @click="newSession">
              <el-icon><CirclePlus /></el-icon>
              <span>新会话</span>
            </button>
          </div>

          <div class="sidebar-tools">
            <el-input
              v-model="searchText"
              class="session-search"
              size="small"
              placeholder="搜索会话"
              :prefix-icon="Search"
              clearable
            />
            <el-tooltip content="新建分组" placement="top">
              <el-button class="btn-icon" circle :icon="FolderAdd" @click="createGroup" />
            </el-tooltip>
            <el-tooltip :content="allCollapsed ? '全部展开' : '全部折叠'" placement="top">
              <el-button
                class="btn-icon"
                circle
                :icon="allCollapsed ? Expand : Fold"
                @click="toggleAllCollapsed"
              />
            </el-tooltip>
          </div>

          <el-scrollbar class="session-scroll">
            <el-empty
              v-if="!sessionsLoading && sessions.length === 0 && groups.length === 0"
              description="还没有会话记录"
              :image-size="56"
            />
            <div v-else-if="visibleSessions.length === 0" class="panel-hint">没有匹配的会话</div>

            <!-- 置顶区：置顶的会话从各自分组里拎出来单独排在最上面 -->
            <SessionSection
              v-if="pinnedSessions.length"
              :section="{
                id: '__pinned__',
                name: '置顶',
                pinned: true,
                isGroup: false,
                items: pinnedSessions
              }"
              :collapsed="isCollapsed('__pinned__')"
              :active-id="sessionId"
              :running-id="loading ? sessionId : ''"
              :dragging-id="dragSession?.sessionId || ''"
              :drop-hint="dropHint"
              @toggle="toggleCollapse"
              @select="selectSession"
              @rename="renameSession"
              @pin="togglePinSession"
              @delete="removeSession"
              @dragstart="onDragStart"
              @dragend="onDragEnd"
              @dragover="onDragOver"
              @drop="onDrop"
              @drop-section="onDropSection"
            />

            <!-- 分组 + 未分组，每个都能像目录一样折叠 -->
            <SessionSection
              v-for="sec in groupedSections"
              :key="sec.id"
              :section="sec"
              :collapsed="isCollapsed(sec.id)"
              :active-id="sessionId"
              :running-id="loading ? sessionId : ''"
              :dragging-id="dragSession?.sessionId || ''"
              :drop-hint="dropHint"
              @toggle="toggleCollapse"
              @group-command="onGroupCommand"
              @new-session="newSessionInGroup"
              @select="selectSession"
              @rename="renameSession"
              @pin="togglePinSession"
              @delete="removeSession"
              @dragstart="onDragStart"
              @dragend="onDragEnd"
              @dragover="onDragOver"
              @drop="onDrop"
              @drop-section="onDropSection"
            />
          </el-scrollbar>

          <!-- 用户信息沉到侧栏底部。顶部横栏已经撤掉，这里也得有个身份提示。 -->
          <div class="sidebar-foot">
            <el-tag type="info" effect="plain" round size="small">
              {{ currentUser.userName }}
              <template v-if="currentUser.roles && currentUser.roles.length">
                ·{{ currentUser.roles.join('/') }}
              </template>
            </el-tag>
            <el-button text size="small" @click="logout()">退出</el-button>
          </div>
        </aside>

        <!-- 右侧主区 -->
        <main class="main-pane">
          <!-- 顶部一行显示当前会话名 -->
          <div class="pane-title" :title="sessionTitle">{{ sessionTitle }}</div>

          <!-- 下划线式页签，左对齐贴在内容区顶部 -->
          <div class="pane-tabs">
            <button
              v-for="tab in viewOptions"
              :key="tab.value"
              :class="['pane-tab', { active: view === tab.value }]"
              @click="switchView(tab.value)"
            >
              {{ tab.label }}
            </button>
          </div>

          <el-alert
            v-if="panelError"
            class="pane-error"
            :title="panelError"
            type="error"
            :closable="false"
            show-icon
          />

          <!-- ===== 会话 ===== -->
          <template v-if="view === 'chat'">
            <div class="chat-scroll">
              <div class="chat-list" ref="listEl" @scroll="onListScroll">
                <el-empty
                  v-if="messages.length === 0"
                  description="试试问我：「有哪些商品？」「最近的订单情况怎么样？」「本店经营数据如何？」"
                >
                  <template #description>
                    <div class="empty-title">你好，我是商户 AI 助手</div>
                    <div class="empty-tips">
                      试试问我：「有哪些商品？」「最近的订单情况怎么样？」「本店经营数据如何？」
                    </div>
                  </template>
                </el-empty>

                <div v-for="(m, i) in messages" :key="i" class="row" :class="m.role">
                  <div v-if="m.role === 'ai' && m.html" class="bubble md" v-html="m.html"></div>
                  <div v-else-if="m.role === 'user'" class="bubble">{{ m.text }}</div>

                  <div v-if="m.pending && m.pending.length" class="pending-card">
                    <div class="pending-body">
                      <div class="pending-title">
                        待确认操作
                        <template v-if="m.pending.length > 1">（{{ m.pending.length }} 项）</template>
                      </div>
                      <div v-for="p in m.pending" :key="p.actionId" class="pending-desc">
                        {{ p.summary }}
                      </div>
                    </div>
                    <div class="pending-actions">
                      <el-button type="primary" :loading="loading" @click="confirm(m)">
                        确认执行并继续
                      </el-button>
                      <el-button :disabled="loading" @click="cancel(m)">取消</el-button>
                    </div>
                  </div>
                </div>

                <div v-if="loading" class="row ai">
                  <div class="typing-wrap">
                    <div class="bubble typing"><span></span><span></span><span></span></div>
                    <div v-if="autoRound" class="auto-round">自动继续 · 第 {{ autoRound }} 轮</div>
                  </div>
                </div>
              </div>

              <!-- 右侧消息刻度条：只给我发的那几条打点，它在滚动条左边。
                   浮层用 el-tooltip 是为了能指定方向 —— 原生的 title 只能跟着鼠标走，
                   贴在屏幕右边缘时会挡住刻度本身。 -->
              <div v-if="userTicks.length > 1" class="minimap">
                <el-tooltip
                  v-for="(t, k) in userTicks"
                  :key="t.index"
                  placement="left"
                  :show-after="120"
                  popper-class="tick-tip"
                >
                  <template #content>
                    <div class="tick-tip-q">{{ t.question }}</div>
                    <div v-if="t.reply" class="tick-tip-a">{{ t.reply }}</div>
                  </template>
                  <span
                    :class="['tick', { active: k === activeTick }]"
                    @click="scrollToMessage(t.index)"
                  />
                </el-tooltip>
              </div>
            </div>

            <!-- 输入区：权限下拉就放在输入框内部的左下角 -->
            <footer class="composer">
              <el-input
                v-model="input"
                type="textarea"
                :rows="2"
                resize="none"
                placeholder="输入你的问题…"
                @keydown="onKeydown"
              />

              <div class="composer-bar">
                <el-select
                  class="mode-select"
                  :class="'mode-' + permissionMode"
                  :model-value="permissionMode"
                  size="default"
                  placement="top-start"
                  :offset="14"
                  :fit-input-width="false"
                  popper-class="mode-dropdown"
                  @change="changeMode"
                >
                  <!-- 一个小圆点跟着权限级别变色，比纯文字更容易一眼认出当前档位 -->
                  <template #prefix><span class="mode-dot"></span></template>
                  <el-option
                    v-for="opt in modeOptions"
                    :key="opt.value"
                    :label="opt.label"
                    :value="opt.value"
                  >
                    <span class="mode-opt">
                      <span class="mode-opt-label">
                        {{ opt.label }}
                        <span v-if="opt.value === permissionMode" class="mode-opt-check">✓</span>
                      </span>
                      <span class="mode-opt-hint">{{ opt.hint }}</span>
                    </span>
                  </el-option>
                </el-select>

                <span class="composer-hint">Enter 发送 · Shift+Enter 换行</span>

                <el-button
                  type="primary"
                  :loading="loading"
                  :disabled="!input.trim()"
                  @click="send"
                >
                  发送
                </el-button>
              </div>
            </footer>
          </template>

          <!-- ===== 轨迹：当前会话的时间线（从早到晚） ===== -->
          <template v-else>
            <div class="trace-head">
              <div>
                <div class="trace-title">审计轨迹</div>
                <div class="trace-sub">
                  <template v-if="sessionId">{{ sessionTitle }}</template>
                  <template v-else>先在左侧选一个会话</template>
                </div>
              </div>
              <el-button
                :icon="Refresh"
                :loading="traceLoading"
                :disabled="!sessionId"
                @click="loadTrace"
              >
                刷新
              </el-button>
            </div>

            <el-scrollbar class="trace-scroll">
              <el-empty
                v-if="!sessionId"
                description="左侧点任意一个会话，这里会列出它完整的轨迹：什么时候调用了哪个工具、参数是什么、返回了什么、有没有被护栏拦下"
              />
              <el-empty
                v-else-if="!traceLoading && traceEntries.length === 0"
                description="这个会话还没有轨迹"
              />

              <template v-else>
                <!-- 体检条：这个会话花了多久、调了多少次工具、烧了多少 token、有没有被拦过 -->
                <div class="trace-stats">
                  <div class="stat">
                    <span class="stat-value">{{ traceStats.turns }}</span>
                    <span class="stat-label">轮次</span>
                  </div>
                  <div class="stat">
                    <span class="stat-value">{{ traceStats.toolCalls }}</span>
                    <span class="stat-label">工具调用</span>
                  </div>
                  <div class="stat">
                    <span class="stat-value">{{ traceStats.approvals }}</span>
                    <span class="stat-label">待确认</span>
                  </div>
                  <div class="stat" :class="{ danger: traceStats.blocked > 0 }">
                    <span class="stat-value">{{ traceStats.blocked }}</span>
                    <span class="stat-label">异常 / 拦截</span>
                  </div>
                  <div class="stat">
                    <span class="stat-value">{{ traceStats.durationText }}</span>
                    <span class="stat-label">模型耗时</span>
                  </div>
                  <div class="stat">
                    <span class="stat-value">{{ traceStats.tokenText }}</span>
                    <span class="stat-label">Token</span>
                  </div>

                  <el-radio-group v-model="traceFilter" size="small" class="trace-filter">
                    <el-radio-button v-for="f in traceFilters" :key="f.value" :value="f.value">
                      {{ f.label }}
                    </el-radio-button>
                  </el-radio-group>
                </div>

                <div v-if="visibleTraces.length === 0" class="panel-hint">这个筛选下没有记录</div>

                <el-timeline class="trace-timeline">
                  <el-timeline-item
                    v-for="(e, i) in visibleTraces"
                    :key="e.time + '-' + i"
                    :timestamp="formatStamp(e.time)"
                    :color="traceColor(e.eventType)"
                    placement="top"
                  >
                    <div class="trace-row">
                      <el-tag size="small" :type="traceTagType(e.eventType)" effect="light">
                        {{ eventLabel[e.eventType] || e.eventType }}
                      </el-tag>
                      <code v-if="e.functionName" class="trace-fn">{{ e.functionName }}</code>
                    </div>
                    <pre
                      v-if="e.payload"
                      class="trace-payload"
                      :class="{ expanded: expanded[i] }"
                      :title="expanded[i] ? '点击收起' : '点击展开全文'"
                      @click="toggleTrace(i)"
                    >{{ expanded[i] ? prettyPayload(e.payload) : summarizePayload(e.payload) }}</pre>
                  </el-timeline-item>
                </el-timeline>
              </template>
            </el-scrollbar>
          </template>
        </main>
      </div>
    </template>
  </div>
</template>
