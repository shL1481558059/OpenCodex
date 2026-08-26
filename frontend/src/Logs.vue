<template>
  <div>
    <div class="toolbar">
      <div>
        <h2>请求日志</h2>
        <div class="text-muted">表格分页展示，详情中查看完整请求与响应</div>
      </div>
      <div class="toolbar-actions">
        <el-popover placement="bottom-end" width="320" trigger="click">
          <template #reference>
            <el-button :icon="Setting">列设置</el-button>
          </template>
          <div class="log-column-settings">
            <div class="log-column-settings__header">
              <span>显示列</span>
              <el-button link type="primary" @click="resetLogColumns">恢复默认</el-button>
            </div>
            <el-checkbox-group v-model="visibleLogColumnKeys" class="log-column-settings__list">
              <div v-for="(column, index) in orderedLogColumns" :key="column.key" class="log-column-settings__item">
                <el-checkbox :label="column.key" :value="column.key">{{ column.label }}</el-checkbox>
                <div class="log-column-settings__actions">
                  <el-button size="small" text :disabled="index === 0" @click="moveLogColumn(index, -1)">上移</el-button>
                  <el-button size="small" text :disabled="index === orderedLogColumns.length - 1" @click="moveLogColumn(index, 1)">下移</el-button>
                </div>
              </div>
            </el-checkbox-group>
          </div>
    </el-popover>
        <el-dropdown trigger="click" @command="setLogSseMode">
          <el-button :type="logSseEnabled ? 'primary' : 'default'" :icon="Lightning">
            {{ logSseLabel }}
          </el-button>
          <template #dropdown>
            <el-dropdown-menu class="log-auto-refresh-menu">
              <div class="log-auto-refresh-menu__title">实时更新</div>
              <el-dropdown-item :command="true">
                <span class="log-auto-refresh-menu__item">
                  <span>开启</span>
                  <el-icon v-if="logSseEnabled"><Check /></el-icon>
                </span>
              </el-dropdown-item>
              <el-dropdown-item :command="false">
                <span class="log-auto-refresh-menu__item">
                  <span>关闭</span>
                  <el-icon v-if="!logSseEnabled"><Check /></el-icon>
                </span>
              </el-dropdown-item>
            </el-dropdown-menu>
          </template>
        </el-dropdown>
  <el-button :icon="Refresh" :loading="refreshLoading" @click="refreshLogPageData()">刷新</el-button>
        <el-popconfirm
          v-if="isSuperadmin"
          title="确定清除全部请求日志？此操作不可恢复，将删除所有日志、内容引用及 SSE 流。"
          confirm-button-text="清除"
          cancel-button-text="取消"
          confirm-button-type="danger"
          width="320"
          @confirm="clearAllLogs"
        >
          <template #reference>
            <el-button type="danger" :icon="Delete" :loading="clearingLogs">清除全部日志</el-button>
          </template>
        </el-popconfirm>
      </div>
    </div>

    <div class="log-filter-shell">
      <el-form class="log-filter-form log-filter-quick" @submit.prevent="submitLogFilters(1)">
          <el-form-item label="时间范围" class="log-filter-quick__range">
            <el-select v-model="draftLogTimePreset" @change="handleLogTimePresetChange">
              <el-option
                v-for="item in logTimePresetOptions"
                :key="item.value"
                :label="item.label"
                :value="item.value"
              />
            </el-select>
          </el-form-item>

          <el-form-item label="标识" class="log-filter-quick__identity">
            <div class="log-filter-identity-control">
              <el-select
                v-model="quickIdentifierField"
                class="log-filter-identity-control__type"
                @change="handleIdentifierFieldChange"
              >
                <el-option
                  v-for="item in identifierFieldOptions"
                  :key="item.value"
                  :label="item.label"
                  :value="item.value"
                />
              </el-select>
              <el-autocomplete
                v-model="identifierValue"
                class="log-filter-identity-control__value"
                :fetch-suggestions="identifierSuggestions"
                :debounce="300"
                :trigger-on-focus="false"
                clearable
                placeholder="输入标识值"
                @keyup.enter.stop.prevent="submitLogFilters(1)"
              />
            </div>
          </el-form-item>

          <el-form-item label="状态">
            <el-select v-model="draftLogFilters.request_status" clearable>
              <el-option
                v-for="item in filterOptions.request_statuses"
                :key="item"
                :label="formatRequestStatus(item)"
                :value="item"
              />
            </el-select>
          </el-form-item>

          <el-form-item label="模型">
            <el-autocomplete
              v-model="draftLogFilters.model"
              :fetch-suggestions="modelSuggestions"
              :debounce="300"
              :trigger-on-focus="false"
              clearable
              placeholder="全部模型"
              @keyup.enter.stop.prevent="submitLogFilters(1)"
            />
          </el-form-item>

          <el-form-item label="渠道">
            <el-select
              v-model="draftLogFilters.channel_id"
              clearable
              filterable
              remote
              :remote-method="(query) => loadFilterOptions('channel_id', query)"
              :loading="filterOptionsLoading.channel_id"
              @visible-change="(visible) => handleFilterVisible('channel_id', visible)"
            >
              <el-option
                v-for="item in filterOptions.channel_ids"
                :key="channelOptionValue(item)"
                :label="channelOptionLabel(item)"
                :value="channelOptionValue(item)"
              />
            </el-select>
          </el-form-item>

          <div class="log-filter-quick__actions log-filter-actions">
            <el-button
              type="primary"
              :icon="Search"
              native-type="submit"
              :loading="refreshLoading"
            >
              查询
            </el-button>
            <el-button link @click="resetLogFilters">重置</el-button>
            <el-button
              class="log-filter-advanced-trigger"
              :icon="Filter"
              @click="advancedFiltersVisible = true"
            >
              更多筛选
              <el-tag v-if="appliedAdvancedFilterCount" size="small" effect="plain">
                {{ appliedAdvancedFilterCount }}
              </el-tag>
            </el-button>
          </div>
      </el-form>

      <div
        v-if="activeLogFilterChips.length || logFiltersDirty"
        class="log-active-filters"
        aria-live="polite"
      >
        <span class="log-active-filters__label">已应用</span>
        <div class="log-active-filters__list">
          <el-tag
            v-for="chip in activeLogFilterChips"
            :key="chip.key"
            closable
            effect="plain"
            @close="clearAppliedFilterChip(chip.key)"
          >
            {{ chip.label }}
          </el-tag>
        </div>
        <span v-if="logFiltersDirty" class="log-active-filters__pending">
          有未应用更改
        </span>
      </div>
    </div>

    <el-drawer
      v-model="advancedFiltersVisible"
      title="更多筛选"
      direction="rtl"
      size="min(520px, 100vw)"
      class="log-filter-drawer"
    >
      <el-alert
        v-if="logFiltersDirty"
        class="log-filter-drawer__pending"
        title="条件修改后点击“应用筛选”才会刷新列表"
        type="info"
        :closable="false"
      />

      <el-form class="log-filter-advanced-form log-filter-advanced-form--drawer" label-position="top">
        <div v-if="draftLogTimePreset === 'custom'" class="log-filter-section">
          <div class="log-filter-section__title">自定义时间</div>
          <el-form-item label="开始时间">
            <el-date-picker
              v-model="draftLogFilters.created_from"
              type="datetime"
              :clearable="false"
            />
          </el-form-item>
          <el-form-item label="结束时间">
            <el-date-picker
              v-model="draftLogFilters.created_to"
              type="datetime"
              :clearable="false"
            />
          </el-form-item>
        </div>

        <div class="log-filter-section">
          <div class="log-filter-section__title">关联链路</div>
          <el-form-item v-if="quickIdentifierField !== 'request_id'" label="请求 ID">
            <el-autocomplete
              v-model="draftLogFilters.request_id"
              :fetch-suggestions="requestIdSuggestions"
              :debounce="300"
              :trigger-on-focus="false"
              clearable
              @keyup.enter.stop.prevent="applyAdvancedFilters"
            />
          </el-form-item>
          <el-form-item v-if="quickIdentifierField !== 'conversation_key'" label="会话键">
            <el-autocomplete
              v-model="draftLogFilters.conversation_key"
              :fetch-suggestions="conversationKeySuggestions"
              :debounce="300"
              :trigger-on-focus="false"
              clearable
              @keyup.enter.stop.prevent="applyAdvancedFilters"
            />
          </el-form-item>
          <el-form-item v-if="quickIdentifierField !== 'conversation_turn_id'" label="Turn ID">
            <el-autocomplete
              v-model="draftLogFilters.conversation_turn_id"
              :fetch-suggestions="conversationTurnIdSuggestions"
              :debounce="300"
              :trigger-on-focus="false"
              clearable
              @keyup.enter.stop.prevent="applyAdvancedFilters"
            />
          </el-form-item>
          <el-form-item v-if="quickIdentifierField !== 'conversation_window_id'" label="窗口 ID">
            <el-autocomplete
              v-model="draftLogFilters.conversation_window_id"
              :fetch-suggestions="conversationWindowIdSuggestions"
              :debounce="300"
              :trigger-on-focus="false"
              clearable
              @keyup.enter.stop.prevent="applyAdvancedFilters"
            />
          </el-form-item>
          <el-form-item v-if="quickIdentifierField !== 'previous_response_id'" label="上一响应 ID">
            <el-autocomplete
              v-model="draftLogFilters.previous_response_id"
              :fetch-suggestions="previousResponseIdSuggestions"
              :debounce="300"
              :trigger-on-focus="false"
              clearable
              @keyup.enter.stop.prevent="applyAdvancedFilters"
            />
          </el-form-item>
        </div>

        <div class="log-filter-section">
          <div class="log-filter-section__title">请求属性</div>
          <el-form-item label="路径">
            <el-select
              v-model="draftLogFilters.path"
              clearable
              filterable
              remote
              :remote-method="(query) => loadFilterOptions('path', query)"
              :loading="filterOptionsLoading.path"
              @visible-change="(visible) => handleFilterVisible('path', visible)"
            >
              <el-option
                v-for="item in filterOptions.paths"
                :key="item"
                :label="item"
                :value="item"
              />
            </el-select>
          </el-form-item>
          <el-form-item label="日志类型">
            <el-select v-model="draftLogFilters.request_type" clearable>
              <el-option
                v-for="item in filterOptions.request_types"
                :key="item"
                :label="formatRequestType(item)"
                :value="item"
              />
            </el-select>
            <div class="log-filter-help">未选择时默认不含渠道尝试日志</div>
          </el-form-item>
        </div>

        <div class="log-filter-section">
          <div class="log-filter-section__title">结果与权限</div>
          <el-form-item label="状态码">
            <el-select
              v-model="draftLogFilters.status_code"
              clearable
              filterable
              remote
              :remote-method="(query) => loadFilterOptions('status_code', query)"
              :loading="filterOptionsLoading.status_code"
              @visible-change="(visible) => handleFilterVisible('status_code', visible)"
            >
              <el-option
                v-for="item in filterOptions.status_codes"
                :key="item"
                :label="item"
                :value="String(item)"
              />
            </el-select>
          </el-form-item>
          <el-form-item v-if="isSuperadmin" label="用户">
            <el-select
              v-model="draftLogFilters.owner_username"
              clearable
              filterable
              remote
              :remote-method="(query) => loadFilterOptions('owner_username', query)"
              :loading="filterOptionsLoading.owner_username"
              @visible-change="(visible) => handleFilterVisible('owner_username', visible)"
            >
              <el-option
                v-for="item in filterOptions.owner_usernames"
                :key="item"
                :label="item"
                :value="item"
              />
            </el-select>
          </el-form-item>
          <el-form-item label="Key 名称">
            <el-select
              v-model="draftLogFilters.api_key_id"
              clearable
              filterable
              remote
              :remote-method="(query) => loadFilterOptions('api_key_id', query)"
              :loading="filterOptionsLoading.api_key_id"
              @visible-change="(visible) => handleFilterVisible('api_key_id', visible)"
            >
              <el-option
                v-for="item in filterOptions.api_key_ids"
                :key="apiKeyOptionValue(item)"
                :label="apiKeyOptionLabel(item)"
                :value="apiKeyOptionValue(item)"
              />
            </el-select>
          </el-form-item>
        </div>
      </el-form>

      <template #footer>
        <div class="log-filter-advanced-actions">
          <span>已选 {{ draftAdvancedFilterCount }} 项</span>
          <el-button @click="clearAdvancedDraft">清空高级</el-button>
          <el-button type="primary" :loading="refreshLoading" @click="applyAdvancedFilters">
            应用筛选
          </el-button>
        </div>
      </template>
    </el-drawer>

    <div v-loading="initialStatsLoading" class="dashboard-summary-grid log-summary-grid">
      <div
        v-for="card in summaryCards"
        :key="card.key"
        class="dashboard-summary-card"
        :class="`dashboard-summary-card--${card.tone}`"
      >
        <div class="dashboard-summary-card__icon">
          <el-icon><component :is="card.icon" /></el-icon>
        </div>
        <div class="dashboard-summary-card__title">{{ card.title }}</div>
        <div class="dashboard-summary-card__value">{{ card.value }}</div>
        <div class="dashboard-summary-card__meta">{{ card.meta }}</div>
      </div>
    </div>

    <div class="table-area">
      <el-table
        v-if="!isMobile"
        class="log-table"
        v-loading="initialLogsLoading"
        :data="logs"
        style="width: 100%"
        empty-text="暂无日志"
      >
        <el-table-column
          v-for="column in visibleLogColumns"
          :key="column.key"
          :prop="column.prop"
          :label="column.label"
          :width="column.width"
          :min-width="column.minWidth"
          :show-overflow-tooltip="column.showOverflowTooltip"
        >
          <template #default="{ row }">
           <el-tag v-if="column.key === 'request_status'" :type="requestStatusTagType(resolveDisplayStatus(row))">
             {{ formatRequestStatus(resolveDisplayStatus(row)) }}
          </el-tag>
            <div v-else-if="column.key === 'created_at'" class="log-cell-stack">
              <div class="log-cell-stack__line">
                <span class="log-cell-stack__label">创建:</span>
                <span class="log-cell-stack__value">{{ formatTimeOrDash(row.created_at) }}</span>
              </div>
              <div class="log-cell-stack__line">
                <span class="log-cell-stack__label">开始:</span>
                <span class="log-cell-stack__value">{{ formatTimeOrDash(row.processing_started_at) }}</span>
              </div>
              <div class="log-cell-stack__line">
                <span class="log-cell-stack__label">完成:</span>
                <span class="log-cell-stack__value">{{ formatTimeOrDash(row.completed_at) }}</span>
              </div>
            </div>
            <div v-else-if="column.key === 'model'" class="log-cell-stack">
              <div class="log-cell-stack__line">
                <span class="log-cell-stack__label">入站:</span>
                <span class="log-cell-stack__value">{{ row.model || "-" }}</span>
              </div>
              <div class="log-cell-stack__line">
                <span class="log-cell-stack__label">上游:</span>
                <span class="log-cell-stack__value">{{ row.upstream_model || "-" }}</span>
              </div>
            </div>
            <div v-else-if="column.key === 'latency'" class="log-cell-stack">
              <div class="log-cell-stack__line">
                <span class="log-cell-stack__label">耗时:</span>
                <span class="log-cell-stack__value">{{ formatLatencyValue(row.duration_ms) }}</span>
              </div>
              <div class="log-cell-stack__line">
                <span class="log-cell-stack__label">TTFT:</span>
                <span class="log-cell-stack__value">{{ formatLatencyValue(row.ttft_ms) }}</span>
              </div>
            </div>
            <div v-else-if="column.key === 'tokens'" class="token-cell">
              <el-tag
                class="token-cell__pill"
                size="small"
                round
                :type="row.is_stream ? 'success' : 'info'"
              >
                {{ row.is_stream ? "流" : "非流" }}
              </el-tag>
              <span>{{ formatTokenSummary(row) }}</span>
            </div>
            <span v-else>{{ formatLogCell(row, column) }}</span>
          </template>
        </el-table-column>
        <el-table-column label="操作" width="90" fixed="right">
          <template #default="{ row }">
            <el-button size="small" :icon="View" @click="openLogDetail(row)">详情</el-button>
          </template>
        </el-table-column>
      </el-table>

      <div v-else v-loading="initialLogsLoading" class="log-mobile-list">
        <el-empty v-if="!initialLogsLoading && logs.length === 0" description="暂无日志" />
        <article
          v-for="row in logs"
          v-else
          :key="row.id || row.request_log_id || `${row.request_id}-${row.created_at}`"
          class="log-mobile-card"
        >
          <header class="log-mobile-card__header">
            <div class="log-mobile-card__tags">
              <el-tag :type="requestStatusTagType(resolveDisplayStatus(row))">
                {{ formatRequestStatus(resolveDisplayStatus(row)) }}
              </el-tag>
              <el-tag effect="plain" :type="requestTypeTagType(row.request_type)">
                {{ formatRequestType(row.request_type) || "未知类型" }}
              </el-tag>
            </div>
            <time>{{ formatTimeOrDash(row.created_at) }}</time>
          </header>

          <div class="log-mobile-card__model">
            <strong>{{ row.model || "-" }}</strong>
            <span v-if="row.upstream_model && row.upstream_model !== row.model">
              → {{ row.upstream_model }}
            </span>
          </div>

          <dl class="log-mobile-card__grid">
            <div>
              <dt>用户 / Key</dt>
              <dd>{{ row.owner_username || "-" }} / {{ formatApiKeyName(row) || "-" }}</dd>
            </div>
            <div>
              <dt>渠道</dt>
              <dd>{{ formatChannelName(row) || "-" }}</dd>
            </div>
            <div>
              <dt>状态码</dt>
              <dd>{{ row.status_code ?? "-" }}</dd>
            </div>
            <div>
              <dt>耗时 / TTFT</dt>
              <dd>{{ formatLatencyValue(row.duration_ms) }} / {{ formatLatencyValue(row.ttft_ms) }}</dd>
            </div>
            <div class="log-mobile-card__wide">
              <dt>Token</dt>
              <dd>{{ row.is_stream ? "流式 · " : "" }}{{ formatTokenSummary(row) }}</dd>
            </div>
            <div class="log-mobile-card__wide">
              <dt>成本</dt>
              <dd>{{ formatCost(row.cost) }}</dd>
            </div>
            <div class="log-mobile-card__wide">
              <dt>请求 ID</dt>
              <dd class="log-mobile-card__request-id">
                <span :title="row.request_id || ''">{{ row.request_id || "-" }}</span>
                <el-tooltip v-if="row.request_id" content="复制请求 ID">
                  <el-button
                    :icon="CopyDocument"
                    circle
                    text
                    aria-label="复制请求 ID"
                    @click="copyLogDetailContent('请求 ID', row.request_id)"
                  />
                </el-tooltip>
              </dd>
            </div>
          </dl>

          <el-button class="log-mobile-card__detail" :icon="View" @click="openLogDetail(row)">
            查看详情
          </el-button>
        </article>
      </div>

      <div class="pagination-bar">
        <el-pagination
          v-model:current-page="logPage"
          v-model:page-size="logPageSize"
          background
          :layout="isMobile ? 'prev, pager, next' : 'total, sizes, prev, pager, next'"
          :pager-count="isMobile ? 5 : 7"
          :size="isMobile ? 'small' : 'default'"
          :page-sizes="[20, 50, 100, 200]"
          :total="logTotal"
          @current-change="refreshLogPageData"
          @size-change="handleLogPageSizeChange"
        />
      </div>
    </div>

    <!-- 日志详情 Dialog -->
    <el-dialog
      v-model="logDetailVisible"
      title="日志详情"
      width="900px"
      :fullscreen="isMobile"
      class="log-detail-dialog"
      @closed="resetLogDetail"
    >
      <div v-loading="logDetailLoading">
        <el-alert v-if="logDetailError" :title="logDetailError" type="error" :closable="false" />
        <template v-else-if="selectedLog">
          <el-descriptions :column="isMobile ? 1 : 2" border>
            <el-descriptions-item label="请求 ID">{{ selectedLog.request_id }}</el-descriptions-item>
            <el-descriptions-item label="请求状态">
            <el-tag :type="requestStatusTagType(resolveDisplayStatus(selectedLog))">
               {{ formatRequestStatus(resolveDisplayStatus(selectedLog)) }}
            </el-tag>
            </el-descriptions-item>
            <el-descriptions-item label="请求类型">
              <el-tag :type="requestTypeTagType(selectedLog.request_type)">
                {{ formatRequestType(selectedLog.request_type) }}
              </el-tag>
            </el-descriptions-item>
            <el-descriptions-item label="父日志 ID">
              <template v-if="selectedLog.parent_request_log_id">
                <el-button link type="primary" @click="openLogDetailById(selectedLog.parent_request_log_id)">
                  #{{ selectedLog.parent_request_log_id }}
                </el-button>
              </template>
              <span v-else>-</span>
            </el-descriptions-item>
            <el-descriptions-item label="会话键">{{ selectedLog.conversation_key || "-" }}</el-descriptions-item>
            <el-descriptions-item label="Turn ID">{{ selectedLog.conversation_turn_id || "-" }}</el-descriptions-item>
            <el-descriptions-item label="窗口 ID">{{ selectedLog.conversation_window_id || "-" }}</el-descriptions-item>
            <el-descriptions-item label="上一响应">{{ selectedLog.previous_response_id || "-" }}</el-descriptions-item>
            <el-descriptions-item label="模型">{{ selectedLog.model }}</el-descriptions-item>
            <el-descriptions-item label="上游模型">{{ selectedLog.upstream_model }}</el-descriptions-item>
            <el-descriptions-item label="渠道">{{ formatChannelName(selectedLog) || "-" }}</el-descriptions-item>
            <el-descriptions-item label="状态码">{{ selectedLog.status_code }}</el-descriptions-item>
            <el-descriptions-item v-if="selectedLog.request_type === 'main' && (selectedLog.attempt_count || 0) > 0" label="渠道尝试">
              共 {{ selectedLog.attempt_count }} 次
              <span v-if="(selectedLog.failed_attempt_count || 0) > 0" class="log-retry-hint">
                （失败 {{ selectedLog.failed_attempt_count }} 次）
              </span>
            </el-descriptions-item>
            <el-descriptions-item label="成本">{{ formatCost(selectedLog.cost) }}</el-descriptions-item>
            <el-descriptions-item label="创建时间">{{ formatTimeOrDash(selectedLog.created_at) }}</el-descriptions-item>
            <el-descriptions-item label="开始处理">{{ formatTimeOrDash(selectedLog.processing_started_at) }}</el-descriptions-item>
            <el-descriptions-item label="完成时间">{{ formatTimeOrDash(selectedLog.completed_at) }}</el-descriptions-item>
            <el-descriptions-item label="流式写出">
              {{ selectedLog.is_stream ? "是" : "否" }}
            </el-descriptions-item>
          </el-descriptions>
          <div class="log-detail-actions">
            <el-button
              v-if="selectedLog.request_type === 'main' && selectedLog.request_id"
              size="small"
              type="primary"
             plain
             @click="openRelatedLogs(selectedLog.request_id, 'ocr')"
             :disabled="!selectedLog.id"
           >
             查看同请求 OCR 子日志
           </el-button>
           <el-button
             v-if="selectedLog.request_type === 'main' && selectedLog.request_id"
             size="small"
             plain
             @click="openRelatedLogs(selectedLog.request_id, 'attempt')"
             :disabled="!selectedLog.id"
           >
             查看渠道尝试记录
           </el-button>
           <el-button
             v-if="(selectedLog.request_type === 'ocr' || selectedLog.request_type === 'attempt') && selectedLog.request_id"
             size="small"
             plain
             @click="openRelatedLogs(selectedLog.request_id, 'main')"
             :disabled="!selectedLog.id"
           >
             查看主请求日志
           </el-button>
          </div>
          <el-alert v-if="selectedLog?.error" class="log-detail-error" title="错误" type="error" :closable="false">
            <pre class="json-view">{{ selectedLog.error }}</pre>
          </el-alert>
          <el-tabs style="margin-top: 16px">
            <el-tab-pane v-if="selectedStreamLines.length" label="SSE 流">
              <div class="stream-view-toolbar">
                <el-radio-group v-model="streamDetailMode" size="small">
                  <el-radio-button label="merged">合并事件</el-radio-button>
                  <el-radio-button label="raw">原始行</el-radio-button>
                </el-radio-group>
                <el-button
                  size="small"
                  :icon="CopyDocument"
                  @click="copyStreamDetailContent()"
                >
                  复制当前视图
                </el-button>
              </div>

              <div v-if="streamDetailMode === 'raw'" class="stream-record-list">
                <div
                  v-for="line in selectedStreamLines"
                  :key="line.sequence"
                  class="stream-record-card"
                >
                  <div class="stream-record-card__meta">
                    <span>#{{ line.sequence }}</span>
                    <span>{{ line.source || "upstream" }}</span>
                  </div>
                  <pre class="json-view stream-record-card__body">{{ formatStreamLine(line.raw_line) }}</pre>
                </div>
              </div>

              <div v-else class="stream-record-list">
                <div
                  v-for="event in selectedStreamEvents"
                  :key="event.key"
                  class="stream-record-card"
                >
                  <div class="stream-record-card__meta">
                    <span>事件 {{ event.index }}</span>
                    <span>{{ event.line_count }} 行</span>
                  </div>
                  <pre class="json-view stream-record-card__body">{{ event.text }}</pre>
                </div>
              </div>
            </el-tab-pane>
            <el-tab-pane v-for="section in logDetailSections" :key="section.key" :label="section.label">
              <div class="json-view-frame">
                <el-tooltip :content="`复制${section.label}`">
                  <el-button class="json-copy-button" :icon="CopyDocument" circle size="small" @click="copyLogDetailContent(section.label, selectedLog?.[section.key])" />
                </el-tooltip>
                <div class="json-view json-view--with-action json-view--pretty">
                  <VueJsonPretty :data="parseStoredJson(selectedLog?.[section.key])" :deep="1" show-icon show-length />
                </div>
              </div>
            </el-tab-pane>
          </el-tabs>
        </template>
      </div>
    </el-dialog>
  </div>
</template>

<script setup>
import { ref, reactive, computed, onBeforeUnmount, onMounted, watch } from "vue";
import { ElMessage } from "element-plus/es/components/message/index.mjs";
import { ElRadioButton, ElRadioGroup } from "element-plus/es/components/radio/index.mjs";
import { Box, Check, Coin, CopyDocument, DataLine, Delete, Filter, Lightning, Refresh, Search, Setting, Timer, View } from "@element-plus/icons-vue";
import VueJsonPretty from "vue-json-pretty";
import "vue-json-pretty/lib/styles.css";
import { createSseStream } from "./api/sseClient.js";

const props = defineProps({
  api: { type: Function, required: true },
  isSuperadmin: { type: Boolean, default: false },
  active: { type: Boolean, required: true }
});

const isMobile = ref(false);
const logsLoading = ref(false);
const hasLoadedLogs = ref(false);
const logs = ref([]);
const logPage = ref(1);
const logPageSize = ref(20);
const logTotal = ref(0);
const statsLoading = ref(false);
const hasLoadedStats = ref(false);
let mobileMediaQuery = null;
const statsData = reactive({
  currency_rate: 7.25,
  summary: defaultSummary()
});

const filterOptions = reactive({
  request_ids: [],
  conversation_keys: [],
  conversation_turn_ids: [],
  conversation_window_ids: [],
  previous_response_ids: [],
 models: [],
 channel_ids: [],
  owner_usernames: [],
  api_key_ids: [],
  paths: [],
  status_codes: [],
  request_statuses: ["queued", "processing", "success", "failed"],
  request_types: ["main", "ocr", "attempt"]
});

const filterOptionFieldMap = {
  request_id: "request_ids",
  conversation_key: "conversation_keys",
  conversation_turn_id: "conversation_turn_ids",
  conversation_window_id: "conversation_window_ids",
  previous_response_id: "previous_response_ids",
  model: "models",
  channel_id: "channel_ids",
  owner_username: "owner_usernames",
  api_key_id: "api_key_ids",
  path: "paths",
  status_code: "status_codes"
};
const filterOptionsLoading = reactive({
  request_id: false,
  conversation_key: false,
  conversation_turn_id: false,
  conversation_window_id: false,
  previous_response_id: false,
  model: false,
  channel_id: false,
  owner_username: false,
  api_key_id: false,
  path: false,
  status_code: false
});
const filterSuggestionTokens = Object.create(null);
const filterOptionRequestTokens = Object.create(null);

function buildDefaultLogTimeRange() {
  const now = Date.now();
  const threeHours = 3 * 60 * 60 * 1000;
  return {
    created_from: new Date(now - threeHours),
    created_to: new Date(now + threeHours)
  };
}

function buildRecentLogTimeRange(hours) {
  const now = Date.now();
  return [new Date(now - hours * 60 * 60 * 1000), new Date(now)];
}

function buildDefaultLogFilters() {
  return {
    ...buildDefaultLogTimeRange(),
    request_id: "",
    conversation_key: "",
    parent_request_log_id: "",
    conversation_turn_id: "",
    conversation_window_id: "",
    previous_response_id: "",
    model: "",
    channel_id: "",
    owner_username: "",
    api_key_id: "",
    status_code: "",
    path: "",
    request_status: "",
    request_type: ""
  };
}

function cloneLogFilters(source) {
  return Object.fromEntries(Object.entries(source).map(([key, value]) => [
    key,
    value instanceof Date ? new Date(value.getTime()) : value
  ]));
}

const logTimePresetOptions = [
  { value: "default", label: "前后 3 小时" },
  { value: "1h", label: "最近 1 小时", hours: 1 },
  { value: "6h", label: "最近 6 小时", hours: 6 },
  { value: "24h", label: "最近 24 小时", hours: 24 },
  { value: "7d", label: "最近 7 天", hours: 24 * 7 },
  { value: "custom", label: "自定义时间" }
];
const identifierFieldOptions = [
  { value: "request_id", label: "请求 ID" },
  { value: "conversation_key", label: "会话键" },
  { value: "conversation_turn_id", label: "Turn ID" },
  { value: "conversation_window_id", label: "窗口 ID" },
  { value: "previous_response_id", label: "上一响应" }
];
const identifierFilterKeys = identifierFieldOptions.map((item) => item.value);
const advancedFilterKeys = [
  ...identifierFilterKeys,
  "path",
  "request_type",
  "status_code",
  "owner_username",
  "api_key_id"
];
const filterChipLabels = {
  request_id: "请求 ID",
  parent_request_log_id: "父日志",
  conversation_key: "会话键",
  conversation_turn_id: "Turn ID",
  conversation_window_id: "窗口 ID",
  previous_response_id: "上一响应",
  model: "模型",
  channel_id: "渠道",
  owner_username: "用户",
  api_key_id: "Key",
  status_code: "状态码",
  path: "路径",
  request_status: "状态",
  request_type: "日志类型"
};

const logFilters = reactive(buildDefaultLogFilters());
const draftLogFilters = reactive(cloneLogFilters(logFilters));
const logFilterKeys = Object.keys(logFilters);
const appliedLogTimePreset = ref("default");
const draftLogTimePreset = ref("default");
const quickIdentifierField = ref("request_id");
const appliedQuickIdentifierField = ref("request_id");
const previousQuickIdentifierField = ref("request_id");
const advancedFiltersVisible = ref(false);
const identifierValue = computed({
  get: () => draftLogFilters[quickIdentifierField.value] || "",
  set: (value) => {
    draftLogFilters[quickIdentifierField.value] = value || "";
  }
});
const logFiltersDirty = computed(() =>
  draftLogTimePreset.value !== appliedLogTimePreset.value
  || logFilterKeys.some((key) =>
    normalizeLogFilterValue(key, draftLogFilters[key]) !== normalizeLogFilterValue(key, logFilters[key]))
);
const activeLogFilterChips = computed(() =>
  logFilterKeys
    .filter((key) => key !== "created_from" && key !== "created_to" && hasLogFilterValue(logFilters[key]))
    .map((key) => ({
      key,
      label: `${filterChipLabels[key] || key}: ${formatFilterChipValue(key, logFilters[key])}`
    }))
);
const appliedAdvancedFilterCount = computed(() =>
  countAdvancedFilters(logFilters, appliedQuickIdentifierField.value, appliedLogTimePreset.value));
const draftAdvancedFilterCount = computed(() =>
  countAdvancedFilters(draftLogFilters, quickIdentifierField.value, draftLogTimePreset.value));

const selectedLog = ref(null);
const logDetailVisible = ref(false);
const logDetailLoading = ref(false);
const logDetailError = ref("");
const streamDetailMode = ref("merged");
let logDetailRequestToken = 0;
const logDetailSections = [
  { key: "request_headers", label: "请求头" },
  { key: "request_body", label: "原始请求" },
  { key: "upstream_request_body", label: "转换后请求" },
  { key: "upstream_response_body", label: "转换前响应" },
  { key: "response_body", label: "转换后响应" },
  { key: "ocr_json", label: "OCR 元数据" },
  { key: "web_search_json", label: "Web Search" }
];

const logColumnDefinitions = [
  { key: "created_at", prop: "created_at", label: "时间", width: 220 },
  { key: "request_id", prop: "request_id", label: "请求", width: 130, showOverflowTooltip: true },
  { key: "conversation_key", prop: "conversation_key", label: "会话", width: 180, showOverflowTooltip: true },
  { key: "request_status", prop: "request_status", label: "状态", width: 100 },
  { key: "owner_username", prop: "owner_username", label: "用户", width: 120, showOverflowTooltip: true },
  { key: "api_key_id", prop: "api_key_id", label: "Key 名称", width: 140, showOverflowTooltip: true },
  { key: "model", prop: "model", label: "模型", minWidth: 190 },
  { key: "channel_id", prop: "channel_id", label: "渠道", minWidth: 130, showOverflowTooltip: true },
  { key: "status_code", prop: "status_code", label: "状态码", width: 90 },
  { key: "latency", label: "耗时 / TTFT", width: 150 },
  { key: "tokens", label: "Token", width: 210 },
  { key: "cost", prop: "cost", label: "成本", width: 110 }
];
const defaultLogColumnKeys = logColumnDefinitions
  .map((c) => c.key)
  .filter((key) => key !== "request_id");
const logColumnMap = Object.fromEntries(logColumnDefinitions.map((c) => [c.key, c]));
const logColumnOrder = ref(logColumnDefinitions.map((c) => c.key));
const visibleLogColumnKeys = ref(defaultLogColumnKeys.slice());

const orderedLogColumns = computed(() =>
  logColumnOrder.value.map((key) => logColumnMap[key]).filter(Boolean)
);
const visibleLogColumns = computed(() =>
  orderedLogColumns.value.filter((c) => visibleLogColumnKeys.value.includes(c.key))
);
// --- SSE: 日志实时更新 ---
const logSseEnabled = ref(true);
const logSseLabel = computed(() => logSseEnabled.value ? "实时更新" : "已暂停");
const logStream = createSseStream({
  path: "/logs/stream",
  events: {
    logs: () => {
      if (!props.active || logsLoading.value || statsLoading.value) return;
      refreshLogPageData();
    }
  }
});
const refreshLoading = computed(() => logsLoading.value || statsLoading.value);
const initialLogsLoading = computed(() => logsLoading.value && !hasLoadedLogs.value);
const initialStatsLoading = computed(() => statsLoading.value && !hasLoadedStats.value);
const clearingLogs = ref(false);

async function clearAllLogs() {
  clearingLogs.value = true;
  try {
    const result = await props.api("/logs", { method: "DELETE" });
    const deleted = result?.deleted_logs ?? 0;
    ElMessage.success(`已清除 ${deleted} 条日志`);
    await refreshLogPageData(1);
  } catch (error) {
    ElMessage.error(error.message || "清除日志失败");
  } finally {
    clearingLogs.value = false;
  }
}
const summaryCards = computed(() => {
  const summary = statsData.summary || defaultSummary();
  return [
    {
      key: "requests",
      title: "总请求数",
      value: formatInteger(summary.request_count),
      meta: `成功: ${formatInteger(summary.success_count)}  近 1 小时: ${formatInteger(summary.recent_1h_request_count)}`,
      icon: DataLine,
      tone: "blue"
    },
    {
      key: "tokens",
      title: "总 TOKEN 数",
      value: formatInteger(summary.total_tokens),
      meta: `输入: ${formatInteger(summary.input_tokens)}  缓存: ${formatInteger(summary.cached_tokens)}  输出: ${formatInteger(summary.output_tokens)}`,
      icon: Box,
      tone: "cyan"
    },
    {
      key: "cost",
      title: "总计费",
      value: formatDualCurrency(summary.cost),
      meta: `近 1 小时: ${formatDualCurrency(summary.recent_1h_cost)}`,
      icon: Coin,
      tone: "green"
    },
    {
      key: "rpm",
      title: "RPM",
      value: formatCompactNumber(summary.rpm),
      meta: "每分钟请求数",
      icon: Timer,
      tone: "green"
    },
    {
      key: "tpm",
      title: "TPM",
      value: formatCompactNumber(summary.tpm),
      meta: "每分钟 Token 数",
      icon: Lightning,
      tone: "red"
    }
  ];
});

const selectedStreamLines = computed(() => Array.isArray(selectedLog.value?.stream_lines) ? selectedLog.value.stream_lines : []);
const selectedStreamEvents = computed(() => mergeStreamLines(selectedStreamLines.value));

async function loadLogs(page = logPage.value) {
  logsLoading.value = true;
  logPage.value = typeof page === "number" ? page : logPage.value;
  try {
    const params = new URLSearchParams({ page: String(logPage.value), page_size: String(logPageSize.value) });
    for (const [key, value] of Object.entries(logFilters)) {
      const normalized = normalizeLogFilterValue(key, value);
      if (normalized !== null) params.set(key, normalized);
    }
    const data = await props.api(`/logs?${params.toString()}`);
    logs.value = data.events || [];
    logTotal.value = data.total || 0;
    hasLoadedLogs.value = true;
    return true;
  } catch (error) {
    ElMessage.error(error.message);
    return false;
  } finally {
    logsLoading.value = false;
  }
}

async function loadStats() {
  statsLoading.value = true;
  try {
    const params = new URLSearchParams({ range: "custom" });
    const start = normalizeLogFilterValue("created_from", logFilters.created_from);
    const end = normalizeLogFilterValue("created_to", logFilters.created_to);
    if (start !== null) params.set("start", start);
    if (end !== null) params.set("end", end);

    for (const [key, value] of Object.entries(logFilters)) {
      if (key === "created_from" || key === "created_to") continue;
      const normalized = normalizeLogFilterValue(key, value);
      if (normalized !== null) params.set(key, normalized);
    }

    const data = await props.api(`/stats?${params.toString()}`);
    statsData.currency_rate = data.currency_rate || 7.25;
    statsData.summary = { ...defaultSummary(), ...(data.summary || {}) };
    hasLoadedStats.value = true;
    return true;
  } catch (error) {
    ElMessage.error(error.message);
    return false;
  } finally {
    statsLoading.value = false;
  }
}

function startLogSseStream() {
  if (!logSseEnabled.value || !props.active) return;
  logStream.start();
}

function stopLogSseStream() {
  logStream.stop();
}

function setLogSseMode(enabled) {
  logSseEnabled.value = enabled === true;
  if (logSseEnabled.value) {
    startLogSseStream();
  } else {
    stopLogSseStream();
  }
}

function handleLogPageSizeChange() { logPage.value = 1; refreshLogPageData(1); }

function handleFilterVisible(field, visible) {
  if (visible) loadFilterOptions(field);
}

function resolveLogTimePresetRange(preset) {
  if (preset === "default") {
    const range = buildDefaultLogTimeRange();
    return [range.created_from, range.created_to];
  }
  const option = logTimePresetOptions.find((item) => item.value === preset);
  return option?.hours ? buildRecentLogTimeRange(option.hours) : null;
}

function applyLogTimePreset(target, preset) {
  const range = resolveLogTimePresetRange(preset);
  if (!range) return;
  target.created_from = range[0];
  target.created_to = range[1];
}

function handleLogTimePresetChange(preset) {
  if (preset === "custom") {
    advancedFiltersVisible.value = true;
    return;
  }
  applyLogTimePreset(draftLogFilters, preset);
}

function refreshAppliedLogTimeRange() {
  if (appliedLogTimePreset.value === "custom") return;
  applyLogTimePreset(logFilters, appliedLogTimePreset.value);
  if (draftLogTimePreset.value === appliedLogTimePreset.value) {
    draftLogFilters.created_from = new Date(logFilters.created_from.getTime());
    draftLogFilters.created_to = new Date(logFilters.created_to.getTime());
  }
}

function handleIdentifierFieldChange(nextField) {
  filterSuggestionTokens.quick_identifier = (filterSuggestionTokens.quick_identifier || 0) + 1;
  const previousField = previousQuickIdentifierField.value;
  if (previousField !== nextField) {
    draftLogFilters[previousField] = "";
  }
  previousQuickIdentifierField.value = nextField;
}

async function identifierSuggestions(query, callback) {
  await loadTextFilterSuggestions(quickIdentifierField.value, query, callback, "quick_identifier");
}

function captureLogViewState() {
  return {
    logs: logs.value,
    total: logTotal.value,
    page: logPage.value,
    hasLoadedLogs: hasLoadedLogs.value,
    summary: statsData.summary,
    currencyRate: statsData.currency_rate,
    hasLoadedStats: hasLoadedStats.value
  };
}

function restoreLogViewState(state) {
  logs.value = state.logs;
  logTotal.value = state.total;
  logPage.value = state.page;
  hasLoadedLogs.value = state.hasLoadedLogs;
  statsData.summary = state.summary;
  statsData.currency_rate = state.currencyRate;
  hasLoadedStats.value = state.hasLoadedStats;
}

async function submitLogFilters(page = 1) {
  if (draftLogTimePreset.value === "custom"
    && (!draftLogFilters.created_from || !draftLogFilters.created_to
      || new Date(draftLogFilters.created_from).getTime() >= new Date(draftLogFilters.created_to).getTime())) {
    ElMessage.warning("自定义时间范围无效");
    return false;
  }
  const previousFilters = cloneLogFilters(logFilters);
  const previousTimePreset = appliedLogTimePreset.value;
  const previousIdentifierField = appliedQuickIdentifierField.value;
  const previousView = captureLogViewState();
  if (draftLogTimePreset.value !== "custom") {
    applyLogTimePreset(draftLogFilters, draftLogTimePreset.value);
  }
  Object.assign(logFilters, cloneLogFilters(draftLogFilters));
  appliedLogTimePreset.value = draftLogTimePreset.value;
  appliedQuickIdentifierField.value = quickIdentifierField.value;
  const results = await refreshLogPageData(page);
  if (!results.every(Boolean)) {
    Object.assign(logFilters, previousFilters);
    appliedLogTimePreset.value = previousTimePreset;
    appliedQuickIdentifierField.value = previousIdentifierField;
    restoreLogViewState(previousView);
    return false;
  }
  return true;
}

function resetLogFilters() {
  const defaults = buildDefaultLogFilters();
  Object.assign(draftLogFilters, cloneLogFilters(defaults));
  draftLogTimePreset.value = "default";
  quickIdentifierField.value = "request_id";
  previousQuickIdentifierField.value = "request_id";
  return submitLogFilters(1);
}

function clearAdvancedDraft() {
  for (const key of advancedFilterKeys) {
    if (key !== quickIdentifierField.value) draftLogFilters[key] = "";
  }
  if (draftLogTimePreset.value === "custom") {
    draftLogTimePreset.value = "default";
    applyLogTimePreset(draftLogFilters, "default");
  }
}

async function applyAdvancedFilters() {
  if (await submitLogFilters(1)) advancedFiltersVisible.value = false;
}

async function clearAppliedFilterChip(key) {
  const previousFilters = cloneLogFilters(logFilters);
  const previousDraftValue = draftLogFilters[key];
  const previousView = captureLogViewState();
  logFilters[key] = "";
  draftLogFilters[key] = "";
  const results = await refreshLogPageData(1);
  if (!results.every(Boolean)) {
    Object.assign(logFilters, previousFilters);
    draftLogFilters[key] = previousDraftValue;
    restoreLogViewState(previousView);
    return false;
  }
  return true;
}

function countAdvancedFilters(source, identifierField, timePreset) {
  const count = advancedFilterKeys.filter((key) =>
    key !== identifierField && hasLogFilterValue(source[key]))
    .length;
  return count + (timePreset === "custom" ? 1 : 0);
}

function hasLogFilterValue(value) {
  return value !== "" && value !== null && value !== undefined;
}

function logFilterSignature(source) {
  return logFilterKeys
    .map((key) => `${key}=${normalizeLogFilterValue(key, source[key]) ?? ""}`)
    .join("&");
}

function moveLogColumn(index, direction) {
  const target = index + direction;
  if (target < 0 || target >= logColumnOrder.value.length) return;
  const next = logColumnOrder.value.slice();
  const [item] = next.splice(index, 1);
  next.splice(target, 0, item);
  logColumnOrder.value = next;
}

function resetLogColumns() {
  logColumnOrder.value = logColumnDefinitions.map((column) => column.key);
  visibleLogColumnKeys.value = defaultLogColumnKeys.slice();
}

async function openLogDetail(row) {
  const token = ++logDetailRequestToken;
  selectedLog.value = null;
  logDetailError.value = "";
  streamDetailMode.value = "merged";
  logDetailVisible.value = true;
  logDetailLoading.value = true;
  try {
    if (row?.id === null || row?.id === undefined) throw new Error("日志缺少详情 ID");
    const detail = await props.api(`/logs/${row.id}`);
    if (token === logDetailRequestToken) selectedLog.value = detail;
  } catch (error) {
    if (token === logDetailRequestToken) { logDetailError.value = error.message; ElMessage.error(error.message); }
  } finally {
    if (token === logDetailRequestToken) logDetailLoading.value = false;
  }
}

function openLogDetailById(logId) {
  openLogDetail({ id: logId });
}

function openRelatedLogs(requestId, requestType) {
  // When viewing child logs (attempt/ocr) from a main request, use the main
  // request's database id as parent_request_log_id filter so the backend can
  // match child logs via ParentRequestLogId. When navigating back to the main
  // request from a child, use the child's request_id to find the parent.
  const parentLogId = selectedLog.value?.id;
  const isChildQuery = requestType === "attempt" || requestType === "ocr";

  const nextFilters = buildDefaultLogFilters();
  nextFilters.created_from = logFilters.created_from;
  nextFilters.created_to = logFilters.created_to;
  if (isChildQuery && parentLogId) {
    nextFilters.parent_request_log_id = parentLogId;
    nextFilters.request_id = "";
  } else {
    nextFilters.request_id = requestId || "";
  }
  nextFilters.request_type = requestType || "";
  Object.assign(draftLogFilters, cloneLogFilters(nextFilters));
  draftLogTimePreset.value = appliedLogTimePreset.value;
  if (isChildQuery && parentLogId) {
    quickIdentifierField.value = "parent_request_log_id";
    previousQuickIdentifierField.value = "parent_request_log_id";
  } else {
    quickIdentifierField.value = "request_id";
    previousQuickIdentifierField.value = "request_id";
  }
  logDetailVisible.value = false;
  submitLogFilters(1);
}

function resetLogDetail() {
  logDetailRequestToken += 1;
  selectedLog.value = null;
  logDetailError.value = "";
  logDetailLoading.value = false;
  streamDetailMode.value = "merged";
}

async function copyLogDetailContent(label, value) {
  const text = formatStoredJson(value);
  if (!text) { ElMessage.warning(`${label}没有可复制内容`); return; }
  try {
    await copyLogDetailText(text);
    ElMessage.success(`${label}已复制`);
  } catch (error) {
    ElMessage.error(error.message || "复制失败");
  }
}

async function copyStreamDetailContent() {
  const text = streamDetailMode.value === "raw"
    ? buildRawStreamText(selectedStreamLines.value)
    : buildMergedStreamText(selectedStreamEvents.value);
  if (!text) {
    ElMessage.warning("当前没有可复制的 SSE 内容");
    return;
  }
  try {
    await copyLogDetailText(text);
    ElMessage.success(streamDetailMode.value === "raw" ? "原始 SSE 已复制" : "合并 SSE 已复制");
  } catch (error) {
    ElMessage.error(error.message || "复制失败");
  }
}

async function copyLogDetailText(text) {
  if (navigator.clipboard?.writeText) { await navigator.clipboard.writeText(text); return; }
  fallbackCopyLogDetailText(text);
}

function fallbackCopyLogDetailText(text) {
  const textarea = document.createElement("textarea");
  textarea.value = text;
  textarea.setAttribute("readonly", "");
  textarea.style.cssText = "position:fixed;top:0;left:0;opacity:0;pointer-events:none";
  document.body.appendChild(textarea);
  textarea.select();
  const copied = document.execCommand("copy");
  document.body.removeChild(textarea);
  if (!copied) throw new Error("浏览器拒绝了复制操作");
}

async function loadFilterOptions(field, query = "") {
  const optionKey = filterOptionFieldMap[field];
  if (!optionKey) return [];
  const requestToken = (filterOptionRequestTokens[field] || 0) + 1;
  filterOptionRequestTokens[field] = requestToken;
  const contextSignature = logFilterSignature(draftLogFilters);
  filterOptionsLoading[field] = true;
  try {
    const params = new URLSearchParams({ field });
    const queryText = String(query || "").trim();
    if (queryText) params.set("q", queryText);
    for (const [key, value] of Object.entries(draftLogFilters)) {
      const normalized = normalizeLogFilterValue(key, value);
      if (normalized !== null) params.set(key, normalized);
    }
    const data = await props.api(`/log-filter-options?${params.toString()}`);
    if (requestToken === filterOptionRequestTokens[field]
      && contextSignature === logFilterSignature(draftLogFilters)
      && Array.isArray(data[optionKey])) {
      filterOptions[optionKey] = data[optionKey];
    }
  } catch (error) {
    ElMessage.error(error.message);
  } finally {
    if (requestToken === filterOptionRequestTokens[field]) filterOptionsLoading[field] = false;
  }
  return filterOptions[optionKey] || [];
}

async function requestIdSuggestions(query, callback) {
  await loadTextFilterSuggestions("request_id", query, callback);
}

async function conversationKeySuggestions(query, callback) {
  await loadTextFilterSuggestions("conversation_key", query, callback);
}

async function conversationTurnIdSuggestions(query, callback) {
  await loadTextFilterSuggestions("conversation_turn_id", query, callback);
}

async function conversationWindowIdSuggestions(query, callback) {
  await loadTextFilterSuggestions("conversation_window_id", query, callback);
}

async function previousResponseIdSuggestions(query, callback) {
  await loadTextFilterSuggestions("previous_response_id", query, callback);
}

async function modelSuggestions(query, callback) {
  await loadTextFilterSuggestions("model", query, callback);
}

async function loadTextFilterSuggestions(field, query, callback, requestKey = field) {
  const token = (filterSuggestionTokens[requestKey] || 0) + 1;
  filterSuggestionTokens[requestKey] = token;
  const queryText = String(query || "").trim();
  if (queryText.length < 2) {
    callback([]);
    return;
  }
  const values = await loadFilterOptions(field, queryText);
  if (filterSuggestionTokens[requestKey] !== token) return;
  if (requestKey === "quick_identifier" && quickIdentifierField.value !== field) return;
  callback(buildSuggestions(values));
}

function buildSuggestions(values) {
  return (values || []).map((v) => ({ value: String(v) }));
}

function apiKeyOptionValue(item) {
  if (item && typeof item === "object" && item.id !== null && item.id !== undefined) return String(item.id);
  return String(item ?? "");
}

function apiKeyOptionLabel(item) {
  if (item && typeof item === "object") {
    const name = String(item.name || "").trim();
    return name || `#${apiKeyOptionValue(item)}`;
  }
  const value = apiKeyOptionValue(item);
  return value ? `#${value}` : "";
}

function channelOptionValue(item) {
  if (item && typeof item === "object" && item.id !== null && item.id !== undefined) return String(item.id);
  return String(item ?? "");
}

function channelOptionLabel(item) {
  if (item && typeof item === "object") {
    const name = String(item.name || "").trim();
    return name || channelOptionValue(item);
  }
  return String(item ?? "");
}

function formatFilterChipValue(key, value) {
  if (key === "request_status") return formatRequestStatus(value);
  if (key === "request_type") return formatRequestType(value);
  if (key === "channel_id") {
    const option = filterOptions.channel_ids.find((item) => channelOptionValue(item) === String(value));
    return option ? channelOptionLabel(option) : String(value);
  }
  if (key === "api_key_id") {
    const option = filterOptions.api_key_ids.find((item) => apiKeyOptionValue(item) === String(value));
    return option ? apiKeyOptionLabel(option) : String(value);
  }
  return String(value);
}

// --- Formatting helpers ---

function formatLogCell(row, column) {
  switch (column.key) {
    case "created_at": return formatTime(row.created_at);
    case "api_key_id": return formatApiKeyName(row);
    case "channel_id": return formatChannelName(row);
    case "cost": return formatCost(row.cost);
    default: return row[column.prop] ?? "";
  }
}

function formatRequestType(value) {
  if (value === "ocr") return "OCR";
  if (value === "attempt") return "渠道尝试";
  return value === "main" ? "主请求" : value || "";
}

function requestTypeTagType(value) {
  if (value === "ocr") return "warning";
  if (value === "attempt") return "danger";
  return "info";
}

function formatRequestStatus(value) {
  switch (value) {
    case "queued": return "排队中";
    case "processing": return "处理中";
    case "success": return "成功";
    case "failed": return "失败";
    case "success_with_retry": return "成功（重试）";
    default: return value || "-";
  }
}

function requestStatusTagType(value) {
  switch (value) {
    case "queued": return "info";
    case "processing": return "warning";
    case "success": return "success";
    case "failed": return "danger";
    case "success_with_retry": return "warning";
    default: return "info";
  }
}

// 状态列优先展示带重试语义的展示状态，无则回退到原始 request_status。
function resolveDisplayStatus(row) {
  return row.display_request_status || row.request_status;
}

function formatApiKeyName(row) {
  const name = String(row.api_key_name || "").trim();
  if (name) return name;
  return row.api_key_id === null || row.api_key_id === undefined ? "" : `#${row.api_key_id}`;
}

function formatChannelName(row) {
  const name = String(row.channel_name || "").trim();
  if (name) return name;
  return row.channel_id ? row.channel_id : "";
}

function formatStoredJson(value) {
  if (value === null || value === undefined || value === "") return "";
  if (typeof value === "string") { try { return formatJson(JSON.parse(value)); } catch { return value; } }
  return formatJson(value);
}

function parseStoredJson(value) {
  if (value === null || value === undefined || value === "") return "";
  if (typeof value === "string") { try { return JSON.parse(value); } catch { return value; } }
  return value;
}

function formatCost(value) {
  const number = Number(value || 0);
  if (!number) return "¥0.000000 / $0.000000";
  const usd = number / 7.3;
  return `¥${number.toFixed(6)} / $${usd.toFixed(6)}`;
}

function formatTime(timestamp) {
  if (!timestamp) return "";
  return new Date(Number(timestamp) * 1000).toLocaleString();
}

function formatTimeOrDash(timestamp) {
  return formatTime(timestamp) || "-";
}

function formatLatencyValue(value) {
  if (value === null || value === undefined) return "-";
  const number = Number(value);
  if (!Number.isFinite(number)) return "-";
  return number < 1000 ? `${Math.round(number)} ms` : `${(number / 1000).toFixed(number >= 10000 ? 1 : 2)} s`;
}

function formatTokenSummary(row) {
  return `入 ${row.input_tokens || 0} / 缓 ${row.cached_tokens || 0} / 出 ${row.output_tokens || 0}`;
}

function normalizeLogFilterValue(key, value) {
  if (value === "" || value === null || value === undefined) {
    return null;
  }

  if (key === "created_from" || key === "created_to") {
    const parsed = Number(value);
    return Number.isFinite(parsed) ? String(parsed / 1000) : null;
  }

  return String(value);
}

function formatJson(value) {
  return JSON.stringify(value, null, 2);
}

function formatStreamLine(value) {
  return value === "" ? "(空行)" : String(value ?? "");
}

function mergeStreamLines(lines) {
  const events = [];
  let bucket = [];
  let eventIndex = 1;

  const flush = () => {
    if (!bucket.length) return;
    const normalized = bucket[bucket.length - 1]?.raw_line === "" ? bucket.slice(0, -1) : bucket.slice();
    events.push({
      key: `event-${eventIndex}-${bucket[0]?.sequence ?? 0}`,
      index: eventIndex,
      line_count: normalized.length,
      text: normalized.map((item) => String(item.raw_line ?? "")).join("\n") || "(空事件)"
    });
    eventIndex += 1;
    bucket = [];
  };

  for (const line of lines || []) {
    bucket.push(line);
    if (line?.raw_line === "") flush();
  }

  flush();
  return events;
}

function buildRawStreamText(lines) {
  return (lines || []).map((line) => {
    const rawText = line?.raw_line === "" ? "(空行)" : String(line?.raw_line ?? "");
    return `#${line?.sequence ?? 0} ${line?.source || "upstream"}\n${rawText}`;
  }).join("\n\n");
}

function buildMergedStreamText(events) {
  return (events || []).map((event) => `事件 ${event.index}\n${event.text}`).join("\n\n");
}

function defaultSummary() {
  return {
    request_count: 0,
    success_count: 0,
    recent_1h_request_count: 0,
    input_tokens: 0,
    cached_tokens: 0,
    output_tokens: 0,
    total_tokens: 0,
    recent_1h_tokens: 0,
    cost: 0,
    recent_1h_cost: 0,
    rpm: 0,
    tpm: 0
  };
}

function formatInteger(value) {
  return Math.round(Number(value || 0)).toLocaleString();
}

function formatCompactNumber(value) {
  const number = Number(value || 0);
  if (Number.isInteger(number)) return formatInteger(number);
  return number.toLocaleString(undefined, { maximumFractionDigits: 2 });
}

function formatDualCurrency(value) {
  const cny = Number(value || 0);
  const usd = cny / (statsData.currency_rate || 7.25);
  return `¥${formatCurrencyNumber(cny)}/$${formatCurrencyNumber(usd)}`;
}

function formatCurrencyNumber(value) {
  return Number(value || 0).toLocaleString(undefined, {
    minimumFractionDigits: 2,
    maximumFractionDigits: 2
  });
}

function refreshLogPageData(page = logPage.value) {
  refreshAppliedLogTimeRange();
  return Promise.all([loadLogs(page), loadStats()]);
}

// --- Visibility / auto-refresh ---

const loaded = ref(false);

function syncMobileViewport(event) {
  const nextMobile = event.matches;
  const shouldCompactPage = nextMobile && logPageSize.value > 20;
  isMobile.value = nextMobile;

  if (shouldCompactPage) {
    logPageSize.value = 20;
    logPage.value = 1;
    if (props.active && loaded.value) {
      refreshLogPageData(1);
    }
  }
}

watch(() => props.active, (now) => {
  if (now) {
    if (!loaded.value) refreshLogPageData();
    loaded.value = true;
    startLogSseStream();
  } else {
    stopLogSseStream();
  }
}, { immediate: true });

onMounted(() => {
  mobileMediaQuery = window.matchMedia("(max-width: 600px)");
  isMobile.value = mobileMediaQuery.matches;
  mobileMediaQuery.addEventListener("change", syncMobileViewport);
});

onBeforeUnmount(() => {
  mobileMediaQuery?.removeEventListener("change", syncMobileViewport);
  stopLogSseStream();
});
</script>

<style scoped>
.log-cell-stack {
  display: flex;
  flex-direction: column;
  gap: 2px;
  line-height: 1.35;
}

.log-cell-stack__line {
  display: flex;
  align-items: center;
  gap: 4px;
  min-width: 0;
}

.log-cell-stack__label {
  color: var(--el-text-color-secondary);
  flex: 0 0 auto;
}

.log-cell-stack__value {
  min-width: 0;
  word-break: break-all;
}

.token-cell {
  display: flex;
  align-items: center;
  gap: 8px;
  min-width: 0;
}

.token-cell__pill {
  flex: 0 0 auto;
}

.stream-view-toolbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  margin-bottom: 12px;
}

.stream-record-list {
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.stream-record-card {
  border: 1px solid var(--el-border-color-light);
  border-radius: 8px;
  padding: 12px;
  background: var(--el-fill-color-blank);
}

.stream-record-card__meta {
  display: flex;
  flex-wrap: wrap;
  gap: 12px;
  margin-bottom: 8px;
  color: var(--el-text-color-secondary);
  font-size: 12px;
}

.stream-record-card__body {
  margin: 0;
}

.log-detail-actions {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  margin-top: 12px;
}

.log-mobile-list {
  display: grid;
  gap: 12px;
}

.log-mobile-card {
  min-width: 0;
  padding: 14px;
  border: 1px solid var(--el-border-color-light);
  border-radius: 8px;
  background: var(--el-bg-color);
}

.log-mobile-card__header,
.log-mobile-card__model {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
  min-width: 0;
}

.log-mobile-card__header time {
  color: var(--el-text-color-secondary);
  font-size: 12px;
  text-align: right;
}

.log-mobile-card__tags {
  display: flex;
  gap: 6px;
  flex-wrap: wrap;
}

.log-mobile-card__model {
  justify-content: flex-start;
  margin-top: 12px;
  padding-bottom: 12px;
  border-bottom: 1px solid var(--el-border-color-lighter);
  overflow-wrap: anywhere;
}

.log-mobile-card__model span {
  color: var(--el-text-color-secondary);
  font-size: 12px;
}

.log-mobile-card__grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 12px;
  margin: 12px 0 0;
}

.log-mobile-card__grid > div {
  min-width: 0;
}

.log-mobile-card__request-id {
  display: flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
}

.log-mobile-card__request-id > span {
  min-width: 0;
  overflow: hidden;
  font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.log-mobile-card__request-id :deep(.el-button) {
  flex: 0 0 44px;
  width: 44px;
  min-height: 44px;
}

.log-mobile-card__grid dt {
  margin-bottom: 4px;
  color: var(--el-text-color-secondary);
  font-size: 12px;
}

.log-mobile-card__grid dd {
  margin: 0;
  color: var(--el-text-color-primary);
  font-size: 13px;
  line-height: 1.45;
  overflow-wrap: anywhere;
}

.log-mobile-card__wide {
  grid-column: 1 / -1;
}

.log-mobile-card__detail {
  width: 100%;
  min-height: 44px;
  margin-top: 14px;
}

.log-summary-grid {
  display: grid;
  grid-template-columns: repeat(5, minmax(180px, 1fr));
  gap: 16px;
  margin: 4px 0 16px;
}

.dashboard-summary-card {
  position: relative;
  min-height: 124px;
  box-sizing: border-box;
  padding: 20px 18px 16px;
  border: 1px solid #d8dee8;
  border-radius: 8px;
  background: #fff;
  box-shadow: 0 2px 8px rgb(31 45 61 / 10%);
  overflow: hidden;
}

.dashboard-summary-card__icon {
  position: absolute;
  top: 16px;
  right: 16px;
  display: grid;
  place-items: center;
  width: 50px;
  height: 50px;
  border-radius: 8px;
  font-size: 24px;
}

.dashboard-summary-card__title {
  padding-right: 56px;
  color: var(--el-text-color-secondary);
  font-size: 15px;
  font-weight: 700;
}

.dashboard-summary-card__value {
  margin-top: 28px;
  color: #121826;
  font-size: 18px;
  font-weight: 700;
  line-height: 1.1;
  white-space: nowrap;
}

.dashboard-summary-card__meta {
  margin-top: 12px;
  color: var(--el-text-color-secondary);
  font-size: 10px;
  line-height: 1.4;
  white-space: normal;
  overflow-wrap: anywhere;
}

.dashboard-summary-card--blue .dashboard-summary-card__icon {
  background: #eef5ff;
  color: #356fc7;
}

.dashboard-summary-card--cyan .dashboard-summary-card__icon {
  background: #eef7fb;
  color: #337ea3;
}

.dashboard-summary-card--green .dashboard-summary-card__icon {
  background: #edf8f0;
  color: #32865c;
}

.dashboard-summary-card--red .dashboard-summary-card__icon {
  background: #fdf0f0;
  color: #e05b5b;
}

.dashboard-summary-card--green .dashboard-summary-card__value {
  color: #2f8a5a;
}

.dashboard-summary-card--red .dashboard-summary-card__value {
  color: #e05b5b;
}

@media (max-width: 1440px) {
  .log-summary-grid {
    grid-template-columns: repeat(3, minmax(180px, 1fr));
  }
}

@media (max-width: 960px) {
  .log-summary-grid {
    grid-template-columns: repeat(2, minmax(180px, 1fr));
  }
}

@media (max-width: 600px) {
  .log-summary-grid {
    grid-template-columns: 1fr;
  }

  .pagination-bar {
    display: flex;
    justify-content: center;
    min-width: 0;
    padding-top: 14px;
    overflow: hidden;
  }

  .pagination-bar .el-pagination {
    min-width: 0;
  }

  .log-column-settings__actions .el-button {
    min-height: 44px;
  }

  .log-detail-actions,
  .stream-view-toolbar {
    align-items: stretch;
    flex-direction: column;
  }

  .log-detail-actions .el-button,
  .stream-view-toolbar .el-button {
    width: 100%;
    min-height: 44px;
    margin-left: 0;
  }

  .stream-view-toolbar .el-radio-group {
    width: 100%;
  }

  .stream-view-toolbar :deep(.el-radio-button) {
    flex: 1 1 0;
  }

  .stream-view-toolbar :deep(.el-radio-button__inner) {
    width: 100%;
  }

  :global(.log-detail-dialog .el-dialog__header) {
    padding-right: max(48px, env(safe-area-inset-right));
  }

  :global(.log-detail-dialog .el-dialog__body) {
    padding: 12px;
  }

  :global(.log-detail-dialog .el-descriptions__content),
  :global(.log-detail-dialog .el-tabs__item) {
    overflow-wrap: anywhere;
  }
}
</style>
