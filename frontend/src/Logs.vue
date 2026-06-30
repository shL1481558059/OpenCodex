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
        <el-dropdown trigger="click" @command="setLogAutoRefreshSeconds">
          <el-button :type="logAutoRefreshSeconds ? 'primary' : 'default'" :icon="Refresh">
            {{ logAutoRefreshLabel }}
          </el-button>
          <template #dropdown>
            <el-dropdown-menu class="log-auto-refresh-menu">
              <div class="log-auto-refresh-menu__title">启用自动刷新</div>
              <el-dropdown-item :command="0">
                <span class="log-auto-refresh-menu__item">
                  <span>关闭</span>
                  <el-icon v-if="logAutoRefreshSeconds === 0"><Check /></el-icon>
                </span>
              </el-dropdown-item>
              <el-dropdown-item v-for="seconds in logAutoRefreshOptions" :key="seconds" :command="seconds">
                <span class="log-auto-refresh-menu__item">
                  <span>{{ seconds }} 秒</span>
                  <el-icon v-if="logAutoRefreshSeconds === seconds"><Check /></el-icon>
                </span>
              </el-dropdown-item>
            </el-dropdown-menu>
          </template>
        </el-dropdown>
       <el-button :icon="Refresh" :loading="refreshLoading" @click="refreshLogPageData()">刷新</el-button>
        <el-popconfirm
          v-if="isSuperadmin"
          title="确定清除全部请求日志？此操作不可恢复，将删除所有日志、详情及 SSE 流行。"
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

    <el-form class="log-filter-form" inline>
      <el-form-item label="开始时间">
        <el-date-picker
          v-model="logFilters.created_from"
          type="datetime"
          clearable
          @change="refreshLogPageData(1)"
        />
      </el-form-item>
      <el-form-item label="结束时间">
        <el-date-picker
          v-model="logFilters.created_to"
          type="datetime"
          clearable
          @change="refreshLogPageData(1)"
        />
      </el-form-item>
      <el-form-item label="请求 ID">
        <el-autocomplete v-model="logFilters.request_id" :fetch-suggestions="requestIdSuggestions" clearable @focus="loadFilterOptions('request_id')" @select="refreshLogPageData(1)" @clear="refreshLogPageData(1)" />
      </el-form-item>
      <el-form-item label="模型">
        <el-autocomplete v-model="logFilters.model" :fetch-suggestions="modelSuggestions" clearable @focus="loadFilterOptions('model')" @select="refreshLogPageData(1)" @clear="refreshLogPageData(1)" />
      </el-form-item>
      <el-form-item label="渠道">
        <el-select v-model="logFilters.channel_id" clearable filterable remote :remote-method="(query) => loadFilterOptions('channel_id', query)" :loading="filterOptionsLoading.channel_id" @visible-change="(visible) => handleFilterVisible('channel_id', visible)" @change="refreshLogPageData(1)">
          <el-option v-for="item in filterOptions.channel_ids" :key="channelOptionValue(item)" :label="channelOptionLabel(item)" :value="channelOptionValue(item)" />
        </el-select>
      </el-form-item>
      <el-form-item label="路径">
        <el-select v-model="logFilters.path" clearable filterable remote :remote-method="(query) => loadFilterOptions('path', query)" :loading="filterOptionsLoading.path" @visible-change="(visible) => handleFilterVisible('path', visible)" @change="refreshLogPageData(1)">
          <el-option v-for="item in filterOptions.paths" :key="item" :label="item" :value="item" />
        </el-select>
      </el-form-item>
      <el-form-item label="状态">
        <el-select v-model="logFilters.request_status" clearable @change="refreshLogPageData(1)">
          <el-option v-for="item in filterOptions.request_statuses" :key="item" :label="formatRequestStatus(item)" :value="item" />
        </el-select>
      </el-form-item>
      <el-form-item label="请求类型">
        <el-select v-model="logFilters.request_type" clearable @change="refreshLogPageData(1)">
          <el-option v-for="item in filterOptions.request_types" :key="item" :label="formatRequestType(item)" :value="item" />
        </el-select>
      </el-form-item>
      <el-form-item label="状态码">
        <el-select v-model="logFilters.status_code" clearable filterable remote :remote-method="(query) => loadFilterOptions('status_code', query)" :loading="filterOptionsLoading.status_code" @visible-change="(visible) => handleFilterVisible('status_code', visible)" @change="refreshLogPageData(1)">
          <el-option v-for="item in filterOptions.status_codes" :key="item" :label="item" :value="String(item)" />
        </el-select>
      </el-form-item>
      <el-form-item v-if="isSuperadmin" label="用户">
        <el-select v-model="logFilters.owner_username" clearable filterable remote :remote-method="(query) => loadFilterOptions('owner_username', query)" :loading="filterOptionsLoading.owner_username" @visible-change="(visible) => handleFilterVisible('owner_username', visible)" @change="refreshLogPageData(1)">
          <el-option v-for="item in filterOptions.owner_usernames" :key="item" :label="item" :value="item" />
        </el-select>
      </el-form-item>
      <el-form-item label="Key 名称">
        <el-select v-model="logFilters.api_key_id" clearable filterable remote :remote-method="(query) => loadFilterOptions('api_key_id', query)" :loading="filterOptionsLoading.api_key_id" @visible-change="(visible) => handleFilterVisible('api_key_id', visible)" @change="refreshLogPageData(1)">
          <el-option v-for="item in filterOptions.api_key_ids" :key="apiKeyOptionValue(item)" :label="apiKeyOptionLabel(item)" :value="apiKeyOptionValue(item)" />
        </el-select>
      </el-form-item>
      <el-form-item class="log-filter-actions">
        <el-button type="primary" :icon="Search" @click="refreshLogPageData(1)">查询</el-button>
      </el-form-item>
    </el-form>

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
            <el-tag v-if="column.key === 'request_status'" :type="requestStatusTagType(row.request_status)">
              {{ formatRequestStatus(row.request_status) }}
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

      <div class="pagination-bar">
        <el-pagination
          v-model:current-page="logPage"
          v-model:page-size="logPageSize"
          background
          layout="total, sizes, prev, pager, next"
          :page-sizes="[20, 50, 100, 200]"
          :total="logTotal"
          @current-change="refreshLogPageData"
          @size-change="handleLogPageSizeChange"
        />
      </div>
    </div>

    <!-- 日志详情 Dialog -->
    <el-dialog v-model="logDetailVisible" title="日志详情" width="900px" @closed="resetLogDetail">
      <div v-loading="logDetailLoading">
        <el-alert v-if="logDetailError" :title="logDetailError" type="error" :closable="false" />
        <template v-else-if="selectedLog">
          <el-descriptions :column="2" border>
            <el-descriptions-item label="请求 ID">{{ selectedLog.request_id }}</el-descriptions-item>
            <el-descriptions-item label="请求状态">
              <el-tag :type="requestStatusTagType(selectedLog.request_status)">
                {{ formatRequestStatus(selectedLog.request_status) }}
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
            <el-descriptions-item label="模型">{{ selectedLog.model }}</el-descriptions-item>
            <el-descriptions-item label="上游模型">{{ selectedLog.upstream_model }}</el-descriptions-item>
            <el-descriptions-item label="渠道">{{ formatChannelName(selectedLog) || "-" }}</el-descriptions-item>
            <el-descriptions-item label="状态码">{{ selectedLog.status_code }}</el-descriptions-item>
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
            >
              查看同请求 OCR 子日志
            </el-button>
            <el-button
              v-if="selectedLog.request_type === 'main' && selectedLog.request_id"
              size="small"
              plain
              @click="openRelatedLogs(selectedLog.request_id, 'attempt')"
            >
              查看渠道尝试记录
            </el-button>
            <el-button
              v-if="(selectedLog.request_type === 'ocr' || selectedLog.request_type === 'attempt') && selectedLog.request_id"
              size="small"
              plain
              @click="openRelatedLogs(selectedLog.request_id, 'main')"
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
                    <span>{{ formatTimeOrDash(line.occurred_at) }}</span>
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
                    <span>{{ formatTimeOrDash(event.started_at) }}</span>
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
import { Box, Check, Coin, CopyDocument, DataLine, Delete, Lightning, Refresh, Search, Setting, Timer, View } from "@element-plus/icons-vue";
import VueJsonPretty from "vue-json-pretty";
import "vue-json-pretty/lib/styles.css";

const props = defineProps({
  api: { type: Function, required: true },
  isSuperadmin: { type: Boolean, default: false },
  active: { type: Boolean, required: true }
});

const logsLoading = ref(false);
const hasLoadedLogs = ref(false);
const logs = ref([]);
const logPage = ref(1);
const logPageSize = ref(20);
const logTotal = ref(0);
const statsLoading = ref(false);
const hasLoadedStats = ref(false);
const logAutoRefreshOptions = [5, 10, 30, 60];
const logAutoRefreshSeconds = ref(0);
let logAutoRefreshTimer = null;
const statsData = reactive({
  currency_rate: 7.25,
  summary: defaultSummary()
});

const filterOptions = reactive({
  request_ids: [],
  models: [],
  upstream_models: [],
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
  model: "models",
  channel_id: "channel_ids",
  owner_username: "owner_usernames",
  api_key_id: "api_key_ids",
  path: "paths",
  status_code: "status_codes"
};
const filterOptionsLoading = reactive({
  request_id: false,
  model: false,
  channel_id: false,
  owner_username: false,
  api_key_id: false,
  path: false,
  status_code: false
});

function buildDefaultLogTimeRange() {
  const now = Date.now();
  const threeHours = 3 * 60 * 60 * 1000;
  return {
    created_from: new Date(now - threeHours),
    created_to: new Date(now + threeHours)
  };
}

const defaultLogTimeRange = buildDefaultLogTimeRange();

const logFilters = reactive({
  created_from: defaultLogTimeRange.created_from,
  created_to: defaultLogTimeRange.created_to,
  request_id: "",
  model: "",
  channel_id: "",
  owner_username: "",
  api_key_id: "",
  status_code: "",
  path: "",
  request_status: "",
  request_type: ""
});

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
  { key: "stream_timings_json", label: "流式时序" },
  { key: "ocr_json", label: "OCR 元数据" },
  { key: "web_search_json", label: "Web Search" }
];

const logColumnDefinitions = [
  { key: "created_at", prop: "created_at", label: "时间", width: 220 },
  { key: "request_id", prop: "request_id", label: "请求", width: 130, showOverflowTooltip: true },
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
const logAutoRefreshLabel = computed(() =>
  logAutoRefreshSeconds.value ? `${logAutoRefreshSeconds.value} 秒刷新` : "自动刷新"
);
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
  } catch (error) {
    ElMessage.error(error.message);
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
  } catch (error) {
    ElMessage.error(error.message);
  } finally {
    statsLoading.value = false;
  }
}

function setLogAutoRefreshSeconds(seconds) {
  const next = Number(seconds || 0);
  logAutoRefreshSeconds.value = logAutoRefreshOptions.includes(next) ? next : 0;
  restartLogAutoRefreshTimer();
  if (logAutoRefreshSeconds.value > 0) refreshLogsFromAutoRefresh();
}

function restartLogAutoRefreshTimer() {
  stopLogAutoRefreshTimer();
  if (logAutoRefreshSeconds.value === 0) return;
  logAutoRefreshTimer = window.setInterval(refreshLogsFromAutoRefresh, logAutoRefreshSeconds.value * 1000);
}

function stopLogAutoRefreshTimer() {
  if (logAutoRefreshTimer !== null) { clearInterval(logAutoRefreshTimer); logAutoRefreshTimer = null; }
}

async function refreshLogsFromAutoRefresh() {
  if (!props.active || logsLoading.value || statsLoading.value) return;
  await Promise.all([loadLogs(), loadStats()]);
}

function handleLogPageSizeChange() { logPage.value = 1; refreshLogPageData(1); }

function handleFilterVisible(field, visible) {
  if (visible) loadFilterOptions(field);
}

function resetLogFilters() {
  Object.assign(logFilters, {
    ...buildDefaultLogTimeRange(),
    request_id: "",
    model: "",
    channel_id: "",
    owner_username: "",
    api_key_id: "",
    status_code: "",
    path: "",
    request_status: "",
    request_type: ""
  });
  refreshLogPageData(1);
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
  logColumnOrder.value = defaultLogColumnKeys.slice();
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
  logFilters.request_id = requestId || "";
  logFilters.request_type = requestType || "";
  logDetailVisible.value = false;
  refreshLogPageData(1);
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
  filterOptionsLoading[field] = true;
  try {
    const params = new URLSearchParams({ field });
    const queryText = String(query || "").trim();
    if (queryText) params.set("q", queryText);
    for (const [key, value] of Object.entries(logFilters)) {
      const normalized = normalizeLogFilterValue(key, value);
      if (normalized !== null) params.set(key, normalized);
    }
    const data = await props.api(`/log-filter-options?${params.toString()}`);
    if (Array.isArray(data[optionKey])) filterOptions[optionKey] = data[optionKey];
  } catch (error) {
    ElMessage.error(error.message);
  } finally {
    filterOptionsLoading[field] = false;
  }
  return filterOptions[optionKey] || [];
}

async function requestIdSuggestions(query, callback) {
  callback(buildSuggestions(await loadFilterOptions("request_id", query)));
}

async function modelSuggestions(query, callback) {
  callback(buildSuggestions(await loadFilterOptions("model", query)));
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
    default: return value || "-";
  }
}

function requestStatusTagType(value) {
  switch (value) {
    case "queued": return "info";
    case "processing": return "warning";
    case "success": return "success";
    case "failed": return "danger";
    default: return "info";
  }
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
    const endedAt = bucket[bucket.length - 1]?.occurred_at ?? null;
    const normalized = bucket[bucket.length - 1]?.raw_line === "" ? bucket.slice(0, -1) : bucket.slice();
    events.push({
      key: `event-${eventIndex}-${bucket[0]?.sequence ?? 0}`,
      index: eventIndex,
      started_at: bucket[0]?.occurred_at ?? null,
      completed_at: endedAt,
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
    return `#${line?.sequence ?? 0} ${formatTimeOrDash(line?.occurred_at)} ${line?.source || "upstream"}\n${rawText}`;
  }).join("\n\n");
}

function buildMergedStreamText(events) {
  return (events || []).map((event) => `事件 ${event.index} ${formatTimeOrDash(event.started_at)}\n${event.text}`).join("\n\n");
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
  return Promise.all([loadLogs(page), loadStats()]);
}

// --- Visibility / auto-refresh ---

const loaded = ref(false);

watch(() => props.active, (now) => {
  if (now) {
    if (!loaded.value) refreshLogPageData();
    loaded.value = true;
    restartLogAutoRefreshTimer();
  } else {
    stopLogAutoRefreshTimer();
  }
}, { immediate: true });

onBeforeUnmount(() => {
  stopLogAutoRefreshTimer();
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

@media (max-width: 640px) {
  .log-summary-grid {
    grid-template-columns: 1fr;
  }
}
</style>
