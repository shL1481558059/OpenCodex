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
                <el-checkbox :label="column.key">{{ column.label }}</el-checkbox>
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
        <el-button :icon="Refresh" @click="loadLogs()">刷新</el-button>
      </div>
    </div>

    <el-form class="log-filter-form" inline>
      <el-form-item label="请求 ID">
        <el-autocomplete v-model="logFilters.request_id" :fetch-suggestions="requestIdSuggestions" clearable @select="loadLogs(1)" @clear="loadLogs(1)" />
      </el-form-item>
      <el-form-item label="模型">
        <el-autocomplete v-model="logFilters.model" :fetch-suggestions="modelSuggestions" clearable @select="loadLogs(1)" @clear="loadLogs(1)" />
      </el-form-item>
      <el-form-item label="渠道">
        <el-select v-model="logFilters.channel_id" clearable filterable @change="loadLogs(1)">
          <el-option v-for="item in filterOptions.channel_ids" :key="item" :label="item" :value="item" />
        </el-select>
      </el-form-item>
      <el-form-item label="路径">
        <el-select v-model="logFilters.path" clearable filterable @change="loadLogs(1)">
          <el-option v-for="item in filterOptions.paths" :key="item" :label="item" :value="item" />
        </el-select>
      </el-form-item>
      <el-form-item label="状态">
        <el-select v-model="logFilters.request_status" clearable @change="loadLogs(1)">
          <el-option v-for="item in filterOptions.request_statuses" :key="item" :label="item === 'success' ? '成功' : '失败'" :value="item" />
        </el-select>
      </el-form-item>
      <el-form-item label="状态码">
        <el-select v-model="logFilters.status_code" clearable filterable @change="loadLogs(1)">
          <el-option v-for="item in filterOptions.status_codes" :key="item" :label="item" :value="String(item)" />
        </el-select>
      </el-form-item>
      <el-form-item v-if="isSuperadmin" label="用户">
        <el-select v-model="logFilters.owner_username" clearable filterable @change="loadLogs(1)">
          <el-option v-for="item in filterOptions.owner_usernames" :key="item" :label="item" :value="item" />
        </el-select>
      </el-form-item>
      <el-form-item label="Key ID">
        <el-select v-model="logFilters.api_key_id" clearable filterable @change="loadLogs(1)">
          <el-option v-for="item in filterOptions.api_key_ids" :key="item" :label="item" :value="String(item)" />
        </el-select>
      </el-form-item>
      <el-form-item class="log-filter-actions">
        <el-button type="primary" :icon="Search" @click="loadLogs(1)">查询</el-button>
      </el-form-item>
    </el-form>

    <div class="table-area">
      <el-table
        class="log-table"
        v-loading="logsLoading"
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
            <el-tag v-if="column.key === 'request_status'" :type="row.request_status === 'success' ? 'success' : 'danger'">
              {{ row.request_status === "success" ? "成功" : "失败" }}
            </el-tag>
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
          @current-change="loadLogs"
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
              {{ selectedLog.request_status === "success" ? "成功" : "失败" }}
            </el-descriptions-item>
            <el-descriptions-item label="模型">{{ selectedLog.model }}</el-descriptions-item>
            <el-descriptions-item label="上游模型">{{ selectedLog.upstream_model }}</el-descriptions-item>
            <el-descriptions-item label="状态码">{{ selectedLog.status_code }}</el-descriptions-item>
            <el-descriptions-item label="成本">{{ formatCost(selectedLog.cost) }}</el-descriptions-item>
          </el-descriptions>
          <el-tabs style="margin-top: 16px">
            <el-tab-pane label="请求头">
              <div class="json-view-frame">
                <el-tooltip content="复制请求头">
                  <el-button class="json-copy-button" :icon="CopyDocument" circle size="small" @click="copyLogDetailContent('请求头', selectedLog?.request_headers)" />
                </el-tooltip>
                <pre class="json-view json-view--with-action">{{ formatStoredJson(selectedLog?.request_headers) }}</pre>
              </div>
            </el-tab-pane>
            <el-tab-pane label="请求 Body">
              <div class="json-view-frame">
                <el-tooltip content="复制请求 Body">
                  <el-button class="json-copy-button" :icon="CopyDocument" circle size="small" @click="copyLogDetailContent('请求 Body', selectedLog?.request_body)" />
                </el-tooltip>
                <pre class="json-view json-view--with-action">{{ formatStoredJson(selectedLog?.request_body) }}</pre>
              </div>
            </el-tab-pane>
            <el-tab-pane label="响应">
              <div class="detail-grid">
                <el-alert v-if="selectedLog?.error" title="错误" type="error" :closable="false">
                  <pre class="json-view">{{ selectedLog.error }}</pre>
                </el-alert>
                <div class="json-view-frame">
                  <el-tooltip content="复制响应">
                    <el-button class="json-copy-button" :icon="CopyDocument" circle size="small" @click="copyLogDetailContent('响应', selectedLog?.response_body)" />
                  </el-tooltip>
                  <pre class="json-view json-view--with-action">{{ formatStoredJson(selectedLog?.response_body) }}</pre>
                </div>
              </div>
            </el-tab-pane>
            <el-tab-pane label="Web Search">
              <pre class="json-view">{{ formatStoredJson(selectedLog?.web_search_json) }}</pre>
            </el-tab-pane>
          </el-tabs>
        </template>
      </div>
    </el-dialog>
  </div>
</template>

<script setup>
import { ref, reactive, computed, onBeforeUnmount, watch } from "vue";
import { ElMessage } from "element-plus";
import { Check, CopyDocument, Refresh, Search, Setting, View } from "@element-plus/icons-vue";

const props = defineProps({
  api: { type: Function, required: true },
  isSuperadmin: { type: Boolean, default: false },
  active: { type: Boolean, required: true }
});

const logsLoading = ref(false);
const logs = ref([]);
const logPage = ref(1);
const logPageSize = ref(20);
const logTotal = ref(0);
const logAutoRefreshOptions = [5, 10, 30, 60];
const logAutoRefreshSeconds = ref(0);
let logAutoRefreshTimer = null;

const filterOptions = reactive({
  request_ids: [],
  models: [],
  upstream_models: [],
  channel_ids: [],
  owner_usernames: [],
  api_key_ids: [],
  paths: [],
  status_codes: [],
  request_statuses: ["success", "failed"]
});

const logFilters = reactive({
  request_id: "",
  model: "",
  channel_id: "",
  owner_username: "",
  api_key_id: "",
  status_code: "",
  path: "",
  request_status: ""
});

const selectedLog = ref(null);
const logDetailVisible = ref(false);
const logDetailLoading = ref(false);
const logDetailError = ref("");
let logDetailRequestToken = 0;

const logColumnDefinitions = [
  { key: "created_at", prop: "created_at", label: "时间", width: 180 },
  { key: "request_id", prop: "request_id", label: "请求", width: 130, showOverflowTooltip: true },
  { key: "request_status", prop: "request_status", label: "状态", width: 90 },
  { key: "owner_username", prop: "owner_username", label: "用户", width: 120, showOverflowTooltip: true },
  { key: "api_key_id", prop: "api_key_id", label: "Key ID", width: 90 },
  { key: "model", prop: "model", label: "模型", minWidth: 160, showOverflowTooltip: true },
  { key: "channel_id", prop: "channel_id", label: "渠道", minWidth: 130, showOverflowTooltip: true },
  { key: "status_code", prop: "status_code", label: "状态码", width: 90 },
  { key: "duration_ms", prop: "duration_ms", label: "耗时", width: 95 },
  { key: "ttft_ms", prop: "ttft_ms", label: "TTFT", width: 95 },
  { key: "tokens", label: "Token", width: 190 },
  { key: "cost", prop: "cost", label: "成本", width: 110 },
  { key: "request_body", prop: "request_body", label: "请求 Body", minWidth: 220, showOverflowTooltip: true },
  { key: "response_body", prop: "response_body", label: "响应", minWidth: 220, showOverflowTooltip: true }
];
const defaultLogColumnKeys = logColumnDefinitions.map((c) => c.key);
const logColumnMap = Object.fromEntries(logColumnDefinitions.map((c) => [c.key, c]));
const logColumnOrder = ref(defaultLogColumnKeys.slice());
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

async function loadLogs(page = logPage.value) {
  logsLoading.value = true;
  logPage.value = typeof page === "number" ? page : logPage.value;
  try {
    const params = new URLSearchParams({ page: String(logPage.value), page_size: String(logPageSize.value) });
    for (const [key, value] of Object.entries(logFilters)) {
      if (value !== "" && value !== null && value !== undefined) params.set(key, value);
    }
    const data = await props.api(`/admin/api/logs?${params.toString()}`);
    logs.value = data.events || [];
    logTotal.value = data.total || 0;
    Object.assign(filterOptions, data.filter_options || {});
  } catch (error) {
    ElMessage.error(error.message);
  } finally {
    logsLoading.value = false;
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
  if (!props.active || logsLoading.value) return;
  await loadLogs();
}

function handleLogPageSizeChange() { logPage.value = 1; loadLogs(1); }

function resetLogFilters() {
  Object.assign(logFilters, { request_id: "", model: "", channel_id: "", owner_username: "", api_key_id: "", status_code: "", path: "", request_status: "" });
  loadLogs(1);
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
  logDetailVisible.value = true;
  logDetailLoading.value = true;
  try {
    if (row?.id === null || row?.id === undefined) throw new Error("日志缺少详情 ID");
    const detail = await props.api(`/admin/api/logs/${row.id}`);
    if (token === logDetailRequestToken) selectedLog.value = detail;
  } catch (error) {
    if (token === logDetailRequestToken) { logDetailError.value = error.message; ElMessage.error(error.message); }
  } finally {
    if (token === logDetailRequestToken) logDetailLoading.value = false;
  }
}

function resetLogDetail() {
  logDetailRequestToken += 1;
  selectedLog.value = null;
  logDetailError.value = "";
  logDetailLoading.value = false;
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

function requestIdSuggestions(query, callback) {
  callback(buildSuggestions(filterOptions.request_ids, query));
}

function modelSuggestions(query, callback) {
  callback(buildSuggestions(filterOptions.models, query));
}

function buildSuggestions(values, query) {
  const lowered = String(query || "").toLowerCase();
  return (values || [])
    .filter((v) => String(v).toLowerCase().includes(lowered))
    .map((v) => ({ value: String(v) }));
}

// --- Formatting helpers ---

function formatLogCell(row, column) {
  switch (column.key) {
    case "created_at": return formatTime(row.created_at);
    case "duration_ms": return displayMs(row.duration_ms);
    case "ttft_ms": return displayMs(row.ttft_ms);
    case "tokens": return `${row.input_tokens || 0} / ${row.cached_tokens || 0} / ${row.output_tokens || 0}`;
    case "cost": return formatCost(row.cost);
    case "request_body": return previewJson(row.request_body);
    case "response_body": return previewJson(row.response_body || row.error);
    default: return row[column.prop] ?? "";
  }
}

function formatStoredJson(value) {
  if (value === null || value === undefined || value === "") return "";
  if (typeof value === "string") { try { return formatJson(JSON.parse(value)); } catch { return value; } }
  return formatJson(value);
}

function previewJson(value) {
  const text = formatStoredJson(value).replace(/\s+/g, " ").trim();
  return text.length > 120 ? `${text.slice(0, 120)}...` : text;
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

function displayMs(value) {
  return value === null || value === undefined ? "-" : `${value} ms`;
}

function formatJson(value) {
  return JSON.stringify(value, null, 2);
}

// --- Visibility / auto-refresh ---

const loaded = ref(false);

watch(() => props.active, (now) => {
  if (now) {
    if (!loaded.value) loadLogs();
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
