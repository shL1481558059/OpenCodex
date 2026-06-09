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
        <el-button :icon="Refresh" @click="loadLogs()">刷新</el-button>
      </div>
    </div>

    <el-form class="log-filter-form" inline>
      <el-form-item label="请求 ID">
        <el-autocomplete v-model="logFilters.request_id" :fetch-suggestions="requestIdSuggestions" clearable @focus="loadFilterOptions('request_id')" @select="loadLogs(1)" @clear="loadLogs(1)" />
      </el-form-item>
      <el-form-item label="模型">
        <el-autocomplete v-model="logFilters.model" :fetch-suggestions="modelSuggestions" clearable @focus="loadFilterOptions('model')" @select="loadLogs(1)" @clear="loadLogs(1)" />
      </el-form-item>
      <el-form-item label="渠道">
        <el-select v-model="logFilters.channel_id" clearable filterable remote :remote-method="(query) => loadFilterOptions('channel_id', query)" :loading="filterOptionsLoading.channel_id" @visible-change="(visible) => handleFilterVisible('channel_id', visible)" @change="loadLogs(1)">
          <el-option v-for="item in filterOptions.channel_ids" :key="item" :label="item" :value="item" />
        </el-select>
      </el-form-item>
      <el-form-item label="路径">
        <el-select v-model="logFilters.path" clearable filterable remote :remote-method="(query) => loadFilterOptions('path', query)" :loading="filterOptionsLoading.path" @visible-change="(visible) => handleFilterVisible('path', visible)" @change="loadLogs(1)">
          <el-option v-for="item in filterOptions.paths" :key="item" :label="item" :value="item" />
        </el-select>
      </el-form-item>
      <el-form-item label="状态">
        <el-select v-model="logFilters.request_status" clearable @change="loadLogs(1)">
          <el-option v-for="item in filterOptions.request_statuses" :key="item" :label="item === 'success' ? '成功' : '失败'" :value="item" />
        </el-select>
      </el-form-item>
      <el-form-item label="请求类型">
        <el-select v-model="logFilters.request_type" clearable @change="loadLogs(1)">
          <el-option v-for="item in filterOptions.request_types" :key="item" :label="formatRequestType(item)" :value="item" />
        </el-select>
      </el-form-item>
      <el-form-item label="状态码">
        <el-select v-model="logFilters.status_code" clearable filterable remote :remote-method="(query) => loadFilterOptions('status_code', query)" :loading="filterOptionsLoading.status_code" @visible-change="(visible) => handleFilterVisible('status_code', visible)" @change="loadLogs(1)">
          <el-option v-for="item in filterOptions.status_codes" :key="item" :label="item" :value="String(item)" />
        </el-select>
      </el-form-item>
      <el-form-item v-if="isSuperadmin" label="用户">
        <el-select v-model="logFilters.owner_username" clearable filterable remote :remote-method="(query) => loadFilterOptions('owner_username', query)" :loading="filterOptionsLoading.owner_username" @visible-change="(visible) => handleFilterVisible('owner_username', visible)" @change="loadLogs(1)">
          <el-option v-for="item in filterOptions.owner_usernames" :key="item" :label="item" :value="item" />
        </el-select>
      </el-form-item>
      <el-form-item label="Key 名称">
        <el-select v-model="logFilters.api_key_id" clearable filterable remote :remote-method="(query) => loadFilterOptions('api_key_id', query)" :loading="filterOptionsLoading.api_key_id" @visible-change="(visible) => handleFilterVisible('api_key_id', visible)" @change="loadLogs(1)">
          <el-option v-for="item in filterOptions.api_key_ids" :key="apiKeyOptionValue(item)" :label="apiKeyOptionLabel(item)" :value="apiKeyOptionValue(item)" />
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
            <el-tag v-else-if="column.key === 'request_type'" :type="row.request_type === 'ocr' ? 'warning' : 'info'">
              {{ formatRequestType(row.request_type) }}
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
            <el-descriptions-item label="请求类型">
              <el-tag :type="selectedLog.request_type === 'ocr' ? 'warning' : 'info'">
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
            <el-descriptions-item label="状态码">{{ selectedLog.status_code }}</el-descriptions-item>
            <el-descriptions-item label="成本">{{ formatCost(selectedLog.cost) }}</el-descriptions-item>
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
              v-if="selectedLog.request_type === 'ocr' && selectedLog.request_id"
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
import { Check, CopyDocument, Refresh, Search, Setting, View } from "@element-plus/icons-vue";
import VueJsonPretty from "vue-json-pretty";
import "vue-json-pretty/lib/styles.css";

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
  request_statuses: ["success", "failed"],
  request_types: ["main", "ocr"]
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

const logFilters = reactive({
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
  { key: "created_at", prop: "created_at", label: "时间", width: 180 },
  { key: "request_id", prop: "request_id", label: "请求", width: 130, showOverflowTooltip: true },
  { key: "request_status", prop: "request_status", label: "状态", width: 90 },
  { key: "request_type", prop: "request_type", label: "类型", width: 100 },
  { key: "owner_username", prop: "owner_username", label: "用户", width: 120, showOverflowTooltip: true },
  { key: "api_key_id", prop: "api_key_id", label: "Key 名称", width: 140, showOverflowTooltip: true },
  { key: "model", prop: "model", label: "模型", minWidth: 160, showOverflowTooltip: true },
  { key: "channel_id", prop: "channel_id", label: "渠道", minWidth: 130, showOverflowTooltip: true },
  { key: "status_code", prop: "status_code", label: "状态码", width: 90 },
  { key: "duration_ms", prop: "duration_ms", label: "耗时", width: 95 },
  { key: "ttft_ms", prop: "ttft_ms", label: "TTFT", width: 95 },
  { key: "tokens", label: "Token", width: 190 },
  { key: "cost", prop: "cost", label: "成本", width: 110 }
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
    const data = await props.api(`/logs?${params.toString()}`);
    logs.value = data.events || [];
    logTotal.value = data.total || 0;
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

function handleFilterVisible(field, visible) {
  if (visible) loadFilterOptions(field);
}

function resetLogFilters() {
  Object.assign(logFilters, { request_id: "", model: "", channel_id: "", owner_username: "", api_key_id: "", status_code: "", path: "", request_status: "", request_type: "" });
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
  loadLogs(1);
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

async function loadFilterOptions(field, query = "") {
  const optionKey = filterOptionFieldMap[field];
  if (!optionKey) return [];
  filterOptionsLoading[field] = true;
  try {
    const params = new URLSearchParams({ field });
    const queryText = String(query || "").trim();
    if (queryText) params.set("q", queryText);
    for (const [key, value] of Object.entries(logFilters)) {
      if (value !== "" && value !== null && value !== undefined) params.set(key, value);
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

// --- Formatting helpers ---

function formatLogCell(row, column) {
  switch (column.key) {
    case "created_at": return formatTime(row.created_at);
    case "api_key_id": return formatApiKeyName(row);
    case "duration_ms": return displayMs(row.duration_ms);
    case "ttft_ms": return displayMs(row.ttft_ms);
    case "tokens": return `${row.input_tokens || 0} / ${row.cached_tokens || 0} / ${row.output_tokens || 0}`;
    case "cost": return formatCost(row.cost);
    default: return row[column.prop] ?? "";
  }
}

function formatRequestType(value) {
  return value === "ocr" ? "OCR" : value === "main" ? "主请求" : value || "";
}

function formatApiKeyName(row) {
  const name = String(row.api_key_name || "").trim();
  if (name) return name;
  return row.api_key_id === null || row.api_key_id === undefined ? "" : `#${row.api_key_id}`;
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

<style scoped>
.log-detail-actions {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  margin-top: 12px;
}
</style>
