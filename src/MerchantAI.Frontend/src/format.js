/** 会话列表里的时间显示：像 DSH 那样用相对时间，不占地方也更好扫。 */
export function relativeTime(value) {
  if (!value) return ''
  const d = new Date(value)
  if (Number.isNaN(d.getTime())) return ''

  // 时钟回拨之类的极端情况，别显示成 "-3 分"
  const minutes = Math.max(0, Math.floor((Date.now() - d.getTime()) / 60000))

  if (minutes < 1) return '刚刚'
  if (minutes < 60) return `${minutes} 分`
  const hours = Math.floor(minutes / 60)
  if (hours < 24) return `${hours} 小时`
  const days = Math.floor(hours / 24)
  if (days < 30) return `${days} 天`
  return d.toLocaleDateString('zh-CN', { month: '2-digit', day: '2-digit' })
}

/** 完整时间，鼠标悬浮时的 tooltip 用。 */
export function fullTime(value) {
  if (!value) return '时间未知'
  const d = new Date(value)
  if (Number.isNaN(d.getTime())) return '时间未知'
  return d.toLocaleString('zh-CN', {
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit'
  })
}
