<script setup>
import { CaretRight, Folder, FolderOpened, Top, Plus, MoreFilled, EditPen, Delete } from '@element-plus/icons-vue'
import SessionRow from './SessionRow.vue'

defineProps({
  section: { type: Object, required: true }, // { id, name, pinned, isGroup, items }
  collapsed: { type: Boolean, default: false },
  activeId: { type: String, default: '' },
  runningId: { type: String, default: '' },
  draggingId: { type: String, default: '' },
  dropHint: { type: Object, default: null } // { sessionId, position }
})

const emit = defineEmits([
  'toggle', 'group-command', 'select', 'rename', 'pin', 'delete',
  'new-session', 'dragstart', 'dragend', 'dragover', 'drop', 'drop-section'
])
</script>

<template>
  <section class="session-section">
    <!-- 整行可点：像目录树那样折叠/展开。右边的操作按钮都 stop 掉，免得误触发折叠。 -->
    <div
      class="section-head"
      :class="{ group: section.isGroup, collapsed }"
      @click="emit('toggle', section.id)"
    >
      <el-icon class="section-caret" :class="{ open: !collapsed }"><CaretRight /></el-icon>
      <el-icon class="section-folder">
        <FolderOpened v-if="!collapsed" />
        <Folder v-else />
      </el-icon>
      <span class="section-name">{{ section.name }}</span>
      <span class="section-count">{{ section.items.length }}</span>

      <!-- 空分组也要能操作：鼠标浮上来给一个「在此新建会话」的快捷按钮 -->
      <el-tooltip v-if="section.isGroup" content="在此新建会话" placement="top">
        <el-button
          class="row-more"
          text
          size="small"
          :icon="Plus"
          @click.stop="emit('new-session', section)"
        />
      </el-tooltip>

      <el-dropdown
        v-if="section.isGroup"
        trigger="click"
        placement="bottom-end"
        @command="(cmd) => emit('group-command', cmd, section)"
      >
        <el-button class="row-more" text size="small" :icon="MoreFilled" @click.stop />
        <template #dropdown>
          <el-dropdown-menu>
            <el-dropdown-item command="new">
              <el-icon><Plus /></el-icon>在此新建会话
            </el-dropdown-item>
            <el-dropdown-item command="rename">
              <el-icon><EditPen /></el-icon>重命名分组
            </el-dropdown-item>
            <el-dropdown-item command="pin">
              <el-icon><Top /></el-icon>{{ section.pinned ? '取消置顶' : '置顶分组' }}
            </el-dropdown-item>
            <el-dropdown-item command="delete" divided>
              <el-icon><Delete /></el-icon>删除分组
            </el-dropdown-item>
          </el-dropdown-menu>
        </template>
      </el-dropdown>
    </div>

    <el-collapse-transition>
      <div
        v-show="!collapsed"
        class="section-body"
        @dragover.prevent="emit('dragover', null, section.id)"
        @drop.prevent="emit('drop-section', section)"
      >
        <!-- 空分组也是有效的拖放目标，不然会话拖不进来 -->
        <div v-if="section.items.length === 0" class="section-empty">
          {{ draggingId ? '拖到这里' : '还没有会话' }}
        </div>

        <SessionRow
          v-for="s in section.items"
          :key="s.sessionId"
          :session="s"
          :active="s.sessionId === activeId"
          :running="s.sessionId === runningId"
          :dragging="s.sessionId === draggingId"
          :drop-hint="dropHint && dropHint.sessionId === s.sessionId ? dropHint.position : ''"
          @select="emit('select', $event)"
          @rename="emit('rename', $event)"
          @pin="emit('pin', $event)"
          @delete="emit('delete', $event)"
          @dragstart="emit('dragstart', $event)"
          @dragend="emit('dragend')"
          @dragover="emit('dragover', $event, section.id)"
          @drop="emit('drop', $event, section.id)"
        />
      </div>
    </el-collapse-transition>
  </section>
</template>
