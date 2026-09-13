<script setup>
import { computed } from 'vue'
import { ChatLineRound, MoreFilled, EditPen, Top, Delete } from '@element-plus/icons-vue'
import { relativeTime } from './format.js'

const props = defineProps({
  session: { type: Object, required: true },
  active: { type: Boolean, default: false },
  running: { type: Boolean, default: false },
  dragging: { type: Boolean, default: false },
  /** 'before' / 'after'：拖动时给一行插入位置的高亮线 */
  dropHint: { type: String, default: '' }
})

const emit = defineEmits(['select', 'rename', 'pin', 'delete', 'dragstart', 'dragend', 'dragover', 'dragleave', 'drop'])

/** 悬浮浮层里那句状态。卡着没确认的操作最该被看见，其次才是「正在跑」。 */
const status = computed(() => {
  if (props.session.pendingCount > 0) {
    return { kind: 'warn', text: `待确认 ${props.session.pendingCount} 项` }
  }
  if (props.running) return { kind: 'running', text: '运行中' }
  return { kind: 'idle', text: '空闲' }
})

/** 列表里「21 天」够用，展开看的时候想看到「21 天前」。 */
const fullRelative = computed(() => {
  const text = relativeTime(props.session.lastActiveAt)
  if (!text) return '时间未知'
  return text === '刚刚' ? text : text + '前'
})
</script>

<template>
  <!-- 悬浮浮层：列表里空间有限，标题会被省略号截掉；这里给全文 + 完整时间 + 状态。
       拖动时要关掉，否则浮层跟着鼠标挡视线。 -->
  <el-tooltip
    placement="right"
    effect="light"
    :show-after="350"
    :disabled="dragging"
    popper-class="session-tip"
  >
    <template #content>
      <div class="tip-title">{{ session.title }}</div>
      <div class="tip-time">{{ fullRelative }}</div>
      <div class="tip-status">
        <span class="tip-dot" :class="status.kind"></span>{{ status.text }}
      </div>
    </template>

    <div
      class="session-item"
      :class="[
        { current: active, dragging },
        dropHint ? 'drop-' + dropHint : ''
      ]"
      draggable="true"
      @click="emit('select', session)"
      @dragstart="emit('dragstart', session)"
      @dragend="emit('dragend')"
      @dragover.prevent="emit('dragover', session)"
      @dragleave="emit('dragleave', session)"
      @drop.prevent="emit('drop', session)"
    >
      <div class="session-line">
        <el-icon class="session-icon"><ChatLineRound /></el-icon>

        <span class="session-title">{{ session.title }}</span>

        <span v-if="session.pendingCount" class="session-pending" />

        <span class="session-time">{{ relativeTime(session.lastActiveAt) }}</span>

        <!-- 鼠标浮上来时三点盖住时间那个位置，不占额外宽度 -->
        <el-dropdown trigger="click" placement="bottom-end" @command="(cmd) => emit(cmd, session)">
          <el-button class="row-more" text size="small" :icon="MoreFilled" @click.stop />
          <template #dropdown>
            <el-dropdown-menu>
              <el-dropdown-item command="rename">
                <el-icon><EditPen /></el-icon>重命名
              </el-dropdown-item>
              <el-dropdown-item command="pin">
                <el-icon><Top /></el-icon>{{ session.pinned ? '取消置顶' : '置顶' }}
              </el-dropdown-item>
              <el-dropdown-item command="delete" divided>
                <el-icon><Delete /></el-icon>删除
              </el-dropdown-item>
            </el-dropdown-menu>
          </template>
        </el-dropdown>
      </div>
    </div>
  </el-tooltip>
</template>
