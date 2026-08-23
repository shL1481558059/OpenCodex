<template>
  <div class="web-search-page">
    <div class="toolbar">
      <div>
        <h2>Web Search</h2>
        <div class="text-muted">配置 web_search 工具的代理处理方式</div>
      </div>
      <div class="toolbar-actions">
        <el-button :icon="Refresh" :loading="webSearchLoading" :disabled="webSearchSaving" @click="loadWebSearch">刷新</el-button>
        <el-button :icon="Download" :loading="webSearchExporting" :disabled="webSearchSaving" @click="exportWebSearch">导出</el-button>
        <el-button :icon="Upload" :disabled="webSearchSaving" @click="triggerImportWebSearch">导入</el-button>
        <input
          ref="importWebSearchInput"
          type="file"
          accept="application/json,.json"
          style="display:none"
          @change="handleImportWebSearchFile"
        />
        <el-button :icon="Plus" :disabled="webSearchSaving" @click="openWebSearchKeyDrawer()">新增 Web Search Key</el-button>
      </div>
    </div>

    <el-row class="web-search-stats" :gutter="12">
      <el-col :span="8" :xs="8">
        <el-statistic
          title="当前模式"
          :value="0"
          :formatter="formatWebSearchModeStatistic"
        />
      </el-col>
      <el-col :span="8" :xs="8">
        <el-statistic title="可用 Key" :value="webSearchEnabledKeyCount" />
      </el-col>
      <el-col :span="8" :xs="8">
        <el-statistic title="累计调用" :value="webSearchTotalUsage" />
      </el-col>
    </el-row>

    <div class="web-search-control-row">
      <span>处理方式</span>
      <el-segmented
        v-model="webSearchConfig.mode"
        :options="webSearchModeOptions"
        :disabled="webSearchLoading || webSearchSaving"
        @change="handleWebSearchModeChange"
      />
    </div>

    <div class="section-scroll">
      <el-table
        v-loading="webSearchLoading"
        class="desktop-web-search-list"
        :data="webSearchConfig.keys"
        row-key="client_id"
        style="width: 100%; margin-top: 16px"
        empty-text="暂无 Web Search Key"
      >
        <el-table-column label="#" width="64">
          <template #default="{ $index }">{{ $index + 1 }}</template>
        </el-table-column>
        <el-table-column label="服务商" width="120">
          <template #default="{ row }">
            {{ formatWebSearchProvider(row.provider) }}
          </template>
        </el-table-column>
        <el-table-column label="API Key" min-width="240">
          <template #default="{ row }">
            <span class="masked-key">{{ maskWebSearchKey(row.key) }}</span>
          </template>
        </el-table-column>
        <el-table-column label="调用次数" width="160">
          <template #default="{ row }">
            {{ Number(row.usage_count || 0) }} / {{ webSearchKeyLimit(row) }}
          </template>
        </el-table-column>
        <el-table-column label="单 Key 上限" width="130">
          <template #default="{ row }">
            {{ webSearchKeyLimit(row) }}
          </template>
        </el-table-column>
        <el-table-column label="状态" width="100">
          <template #default="{ row }">
            <el-tag :type="row.enabled === false ? 'warning' : 'success'">
              {{ row.enabled === false ? "停用" : "启用" }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column label="操作" width="240" align="center">
          <template #default="{ row, $index }">
            <div class="inline-actions channel-table-actions">
              <el-button
                size="small"
                type="primary"
                plain
                :loading="row.id && webSearchTestingId === row.id"
                :disabled="webSearchSaving"
                @click="testWebSearchKey(row)"
              >
                测试
              </el-button>
              <el-button size="small" :icon="Edit" :disabled="webSearchSaving" @click="openWebSearchKeyDrawer(row, $index)">编辑</el-button>
              <el-popconfirm title="删除这个 Web Search Key？" @confirm="deleteWebSearchKey($index)">
                <template #reference>
                  <el-button size="small" type="danger" :icon="Delete" :disabled="webSearchSaving">删除</el-button>
                </template>
              </el-popconfirm>
            </div>
          </template>
        </el-table-column>
      </el-table>

      <div v-loading="webSearchLoading" class="mobile-card-list mobile-web-search-list">
        <el-empty
          v-if="!webSearchLoading && webSearchConfig.keys.length === 0"
          description="暂无 Web Search Key"
          :image-size="64"
        />
        <article
          v-for="(row, index) in webSearchConfig.keys"
          :key="row.client_id"
          class="mobile-data-card"
        >
          <header class="mobile-data-card__header">
            <div class="mobile-data-card__heading">
              <span class="mobile-data-card__index">#{{ index + 1 }}</span>
              <strong class="mobile-data-card__title">{{ formatWebSearchProvider(row.provider) }}</strong>
            </div>
            <el-tag :type="row.enabled === false ? 'warning' : 'success'">
              {{ row.enabled === false ? "停用" : "启用" }}
            </el-tag>
          </header>

          <dl class="mobile-data-card__details">
            <div>
              <dt>API Key</dt>
              <dd><code class="mobile-key-value">{{ maskWebSearchKey(row.key) }}</code></dd>
            </div>
            <div>
              <dt>调用次数</dt>
              <dd>{{ Number(row.usage_count || 0) }} / {{ webSearchKeyLimit(row) }}</dd>
            </div>
            <div>
              <dt>单 Key 上限</dt>
              <dd>{{ webSearchKeyLimit(row) }}</dd>
            </div>
          </dl>

          <div class="mobile-card-actions">
            <el-button
              type="primary"
              plain
              :loading="row.id && webSearchTestingId === row.id"
              :disabled="webSearchSaving"
              @click="testWebSearchKey(row)"
            >
              测试
            </el-button>
            <el-button
              :icon="Edit"
              :disabled="webSearchSaving"
              @click="openWebSearchKeyDrawer(row, index)"
            >
              编辑
            </el-button>
            <el-popconfirm title="删除这个 Web Search Key？" @confirm="deleteWebSearchKey(index)">
              <template #reference>
                <el-button type="danger" :icon="Delete" :disabled="webSearchSaving">删除</el-button>
              </template>
            </el-popconfirm>
          </div>
        </article>
      </div>

      <el-alert
        v-if="webSearchTestResult"
        class="channel-test-result"
        :title="webSearchTestResult.ok ? 'Web Search Key 测试成功' : 'Web Search Key 测试失败'"
        :type="webSearchTestResult.ok ? 'success' : 'error'"
        show-icon
        :closable="false"
      >
        <div class="channel-test-result__meta">
          <span>耗时 {{ displayMs(webSearchTestResult.duration_ms) }}</span>
          <span v-if="webSearchTestResult.key">调用 {{ webSearchTestResult.key.usage_count }} / {{ webSearchTestResult.key.usage_limit || webSearchTestResult.key.key_usage_limit || webSearchConfig.default_key_usage_limit }}</span>
        </div>
        <div class="channel-test-output">{{ formatWebSearchTestResult(webSearchTestResult) }}</div>
      </el-alert>
    </div>

    <!-- Web Search Key 编辑 Drawer -->
    <el-drawer
      v-model="webSearchKeyDrawerVisible"
      class="web-search-key-drawer"
      :title="webSearchKeyEditingIndex === -1 ? '新增 Web Search Key' : '编辑 Web Search Key'"
      size="520px"
    >
      <el-form label-position="top" :model="webSearchKeyDraft">
        <el-form-item label="服务商">
          <el-select v-model="webSearchKeyDraft.provider" class="full-width">
            <el-option
              v-for="provider in webSearchProviderOptions"
              :key="provider.value"
              :label="provider.label"
              :value="provider.value"
            />
          </el-select>
        </el-form-item>
        <el-form-item label="API Key">
          <el-input
            v-model="webSearchKeyDraft.key"
            type="password"
            show-password
            placeholder="请输入 Web Search API Key"
            autocomplete="off"
          />
        </el-form-item>
        <el-form-item label="启用">
          <el-switch v-model="webSearchKeyDraft.enabled" />
        </el-form-item>
        <el-form-item label="已用次数">
          <el-input-number
            v-model="webSearchKeyDraft.usage_count"
            class="full-width"
            :min="0"
            :step="1"
            :precision="0"
            controls-position="right"
          />
        </el-form-item>
        <el-form-item label="单 Key 上限">
          <el-input-number
            v-model="webSearchKeyDraft.usage_limit"
            class="full-width"
            :min="1"
            :step="100"
            :precision="0"
            controls-position="right"
          />
        </el-form-item>
        <el-alert type="info" :closable="false" title="点击“应用”后立即生效。" />
      </el-form>

      <template #footer>
        <div class="drawer-footer">
          <el-button @click="webSearchKeyDrawerVisible = false">取消</el-button>
          <el-button type="primary" :loading="webSearchSaving" @click="applyWebSearchKeyDraft">
            应用
          </el-button>
        </div>
      </template>
    </el-drawer>
  </div>
</template>

<script setup>
import { ref, reactive, computed, onMounted } from "vue";
import { ElMessage } from "element-plus/es/components/message/index.mjs";
import { Delete, Download, Edit, Plus, Refresh, Upload } from "@element-plus/icons-vue";

const WEB_SEARCH_PROVIDER_LABELS = { tavily: "Tavily" };
const WEB_SEARCH_MODES = new Set(["simulate", "convert", "disabled"]);
const WEB_SEARCH_MODE_LABELS = {
  simulate: "模拟",
  convert: "转换",
  disabled: "关闭"
};

const props = defineProps({
  api: { type: Function, required: true },
});
const webSearchLoading = ref(false);
const webSearchSaving = ref(false);
const webSearchExporting = ref(false);
const webSearchTestingId = ref(null);
const webSearchTestResult = ref(null);
const webSearchKeyDrawerVisible = ref(false);
const webSearchKeyEditingIndex = ref(-1);
const webSearchConfig = reactive(defaultWebSearchConfig());
const webSearchKeyDraft = reactive(defaultWebSearchKeyDraft());

const webSearchModeOptions = [
  { label: "模拟", value: "simulate" },
  { label: "转换", value: "convert" },
  { label: "关闭", value: "disabled" }
];
const webSearchProviderOptions = computed(() =>
  normalizeWebSearchProviders(webSearchConfig.providers).map((provider) => ({
    value: provider,
    label: formatWebSearchProvider(provider)
  }))
);
const webSearchEnabledKeyCount = computed(() =>
  webSearchConfig.keys.filter((k) => k.enabled !== false && Number(k.usage_count || 0) < webSearchKeyLimit(k)).length
);
const webSearchTotalUsage = computed(() =>
  webSearchConfig.keys.reduce((total, k) => total + Number(k.usage_count || 0), 0)
);

async function loadWebSearch() {
  webSearchLoading.value = true;
  try {
    const data = await props.api("/web-search");
    assignWebSearchConfig(data);
  } catch (error) {
    ElMessage.error(error.message);
  } finally {
    webSearchLoading.value = false;
  }
}

const importWebSearchInput = ref(null);

function exportWebSearch() {
  webSearchExporting.value = true;
  try {
  const payload = {
    exported_at: new Date().toISOString(),
    type: "web_search",
    mode: normalizeWebSearchMode(webSearchConfig.mode),
    key_usage_limit: webSearchConfig.default_key_usage_limit,
    keys: webSearchConfig.keys.map((k) => ({
      provider: k.provider,
      key: k.key,
      enabled: k.enabled,
      usage_count: k.usage_count,
      usage_limit: k.usage_limit
    }))
  };
  const blob = new Blob([JSON.stringify(payload, null, 2)], { type: "application/json" });
  const url = URL.createObjectURL(blob);
  const link = document.createElement("a");
  link.href = url;
  link.download = `web-search-${Date.now()}.json`;
  document.body.appendChild(link);
  link.click();
  document.body.removeChild(link);
  URL.revokeObjectURL(url);
  ElMessage.success("Web Search 配置已导出（含明文，请妥善保管）");
  } finally {
    webSearchExporting.value = false;
  }
}

function triggerImportWebSearch() {
  importWebSearchInput.value?.click();
}

async function handleImportWebSearchFile(event) {
  const file = event.target.files?.[0];
  if (!file) return;
  event.target.value = "";
  try {
    const text = await file.text();
    const parsed = JSON.parse(text);
    if (!Array.isArray(parsed.keys)) {
      ElMessage.error("导入文件格式不正确：缺少 keys 数组");
      return;
    }
    if (!parsed.mode) {
      ElMessage.error("导入文件格式不正确：缺少 mode");
      return;
    }
    const payload = {
      mode: normalizeWebSearchMode(parsed.mode),
      key_usage_limit: parsed.key_usage_limit ?? webSearchConfig.default_key_usage_limit,
      keys: parsed.keys
    };
    await props.api("/web-search/import", {
      method: "POST",
      body: JSON.stringify(payload)
    });
    ElMessage.success("Web Search 配置导入成功");
    await loadWebSearch();
  } catch (error) {
    ElMessage.error(error.message || "导入失败");
  }
}

async function persistWebSearchConfig(successMessage = "Web Search 配置已生效") {
  webSearchSaving.value = true;
  try {
    const payload = buildWebSearchPayload();
    await props.api("/web-search", {
      method: "POST",
      body: JSON.stringify(payload)
    });
    try {
      const data = await props.api("/web-search");
      assignWebSearchConfig(data);
    } catch (refreshError) {
      // Save succeeded but refresh failed; keep local state without misleading error
    }
    ElMessage.success(successMessage);
  } catch (error) {
    ElMessage.error(error.message);
  } finally {
    webSearchSaving.value = false;
  }
}

async function handleWebSearchModeChange() {
  await persistWebSearchConfig(`Web Search 已切换为${formatWebSearchMode(webSearchConfig.mode)}模式`);
}

function openWebSearchKeyDrawer(key = null, index = -1) {
  webSearchKeyEditingIndex.value = index;
  assignWebSearchKeyDraft(key);
  webSearchKeyDrawerVisible.value = true;
}

async function applyWebSearchKeyDraft() {
  try {
    const built = buildWebSearchKeyFromDraft();
    if (webSearchKeyEditingIndex.value === -1) {
      webSearchConfig.keys.push(built);
    } else {
      webSearchConfig.keys.splice(webSearchKeyEditingIndex.value, 1, built);
    }
    webSearchKeyDrawerVisible.value = false;
    await persistWebSearchConfig();
  } catch (error) {
    ElMessage.error(error.message);
  }
}

async function deleteWebSearchKey(index) {
  webSearchConfig.keys.splice(index, 1);
  await persistWebSearchConfig();
}

async function testWebSearchKey(row) {
  webSearchTestingId.value = row.id;
  webSearchTestResult.value = null;
  try {
    const result = await props.api("/web-search/test-key", {
      method: "POST",
      body: JSON.stringify({ id: row.id })
    });
    webSearchTestResult.value = result;
    if (result.key) {
      const idx = webSearchConfig.keys.findIndex((k) => k.id === result.key.id);
      if (idx !== -1) {
        const normalized = normalizeWebSearchKeys([result.key])[0];
        normalized.key = webSearchConfig.keys[idx].key;
        Object.assign(webSearchConfig.keys[idx], normalized);
      }
    }
  } catch (error) {
    webSearchTestResult.value = { ok: false, error: error.message };
  } finally {
    webSearchTestingId.value = null;
  }
}

// --- Web Search helpers ---

function defaultWebSearchConfig() {
  return { mode: "convert", providers: ["tavily"], default_key_usage_limit: 1000, keys: [] };
}

function defaultWebSearchKey() {
  const usageLimit = defaultWebSearchKeyUsageLimit();
  return {
    client_id: `new-${Date.now()}-${Math.random().toString(16).slice(2)}`,
    id: null,
    provider: firstWebSearchProvider(),
    key: "",
    enabled: true,
    usage_count: 0,
    usage_limit: usageLimit,
    key_usage_limit: usageLimit
  };
}

function defaultWebSearchKeyDraft() {
  const usageLimit = defaultWebSearchKeyUsageLimit();
  return {
    client_id: "",
    id: null,
    provider: firstWebSearchProvider(),
    key: "",
    enabled: true,
    usage_count: 0,
    usage_limit: usageLimit,
    key_usage_limit: usageLimit
  };
}

function assignWebSearchKeyDraft(key) {
  const usageLimit = webSearchKeyLimit(key);
  Object.assign(webSearchKeyDraft, defaultWebSearchKeyDraft(), key || {}, {
    provider: normalizeWebSearchProvider(key?.provider),
    key: String(key?.key || ""),
    enabled: key?.enabled !== false,
    usage_count: normalizeNonNegativeInteger(key?.usage_count, 0),
    usage_limit: usageLimit,
    key_usage_limit: usageLimit
  });
}

function buildWebSearchKeyFromDraft() {
  const provider = normalizeWebSearchProvider(webSearchKeyDraft.provider);
  const key = String(webSearchKeyDraft.key || "").trim();
  if (!key) throw new Error("Web Search Key 不能为空");
  const usageCount = normalizeNonNegativeInteger(webSearchKeyDraft.usage_count, -1);
  if (usageCount < 0) throw new Error("已用次数必须是大于等于 0 的整数");
  const usageLimit = normalizePositiveInteger(webSearchKeyDraft.usage_limit || webSearchKeyDraft.key_usage_limit, 0);
  if (!usageLimit) throw new Error("单 Key 调用上限必须大于 0");
  return {
    client_id: webSearchKeyDraft.client_id || `new-${Date.now()}-${Math.random().toString(16).slice(2)}`,
    id: webSearchKeyDraft.id || null,
    provider,
    key,
    enabled: webSearchKeyDraft.enabled !== false,
    usage_count: usageCount,
    usage_limit: usageLimit,
    key_usage_limit: usageLimit
  };
}

function assignWebSearchConfig(data) {
  Object.assign(webSearchConfig, defaultWebSearchConfig(), data || {}, {
    mode: normalizeWebSearchMode(data?.mode),
    providers: normalizeWebSearchProviders(data?.providers),
    default_key_usage_limit: normalizePositiveInteger(data?.default_key_usage_limit || data?.key_usage_limit, 1000),
    keys: normalizeWebSearchKeys(data?.keys || [])
  });
}

function normalizeWebSearchProviders(providers) {
  if (!Array.isArray(providers) || providers.length === 0) return ["tavily"];
  const normalized = providers.map((p) => String(p || "").trim().toLowerCase()).filter(Boolean);
  return normalized.length ? Array.from(new Set(normalized)) : ["tavily"];
}

function firstWebSearchProvider() {
  return normalizeWebSearchProviders(webSearchConfig.providers)[0] || "tavily";
}

function normalizeWebSearchProvider(provider) {
  const normalized = String(provider || firstWebSearchProvider()).trim().toLowerCase();
  const options = normalizeWebSearchProviders(webSearchConfig.providers);
  return options.includes(normalized) ? normalized : firstWebSearchProvider();
}

function formatWebSearchProvider(provider) {
  const normalized = String(provider || "tavily").trim().toLowerCase();
  return WEB_SEARCH_PROVIDER_LABELS[normalized] || provider || "tavily";
}

function normalizeWebSearchMode(mode) {
  const normalized = String(mode || "convert").trim().toLowerCase();
  return WEB_SEARCH_MODES.has(normalized) ? normalized : "convert";
}

function formatWebSearchMode(mode) {
  return WEB_SEARCH_MODE_LABELS[normalizeWebSearchMode(mode)] || "转换";
}

function formatWebSearchModeStatistic() {
  return formatWebSearchMode(webSearchConfig.mode);
}

function defaultWebSearchKeyUsageLimit() {
  return normalizePositiveInteger(webSearchConfig.default_key_usage_limit, 1000);
}

function webSearchKeyLimit(key) {
  return normalizePositiveInteger(key?.usage_limit || key?.key_usage_limit, defaultWebSearchKeyUsageLimit());
}

function normalizeWebSearchKeys(keys) {
  if (!Array.isArray(keys)) return [];
  return keys.map((item, index) => {
    const usageLimit = normalizePositiveInteger(item?.usage_limit || item?.key_usage_limit, defaultWebSearchKeyUsageLimit());
    return {
      client_id: item?.id ? `saved-${item.id}` : item?.client_id || `new-${index}-${Date.now()}`,
      id: item?.id || null,
      provider: normalizeWebSearchProvider(item?.provider),
      key: String(item?.key || item?.api_key || ""),
      enabled: item?.enabled !== false,
      usage_count: normalizeNonNegativeInteger(item?.usage_count, 0),
      usage_limit: usageLimit,
      key_usage_limit: usageLimit
    };
  });
}

function buildWebSearchPayload() {
  const keys = webSearchConfig.keys.map((item) => {
    const usageLimit = webSearchKeyLimit(item);
    return {
      id: item.id || undefined,
      provider: normalizeWebSearchProvider(item.provider),
      key: String(item.key || "").trim(),
      enabled: item.enabled !== false,
      usage_count: normalizeNonNegativeInteger(item.usage_count, 0),
      usage_limit: usageLimit
    };
  });
  const emptyIndex = keys.findIndex((item) => !item.key);
  if (emptyIndex !== -1) throw new Error(`第 ${emptyIndex + 1} 个 Web Search Key 不能为空`);
  return { mode: normalizeWebSearchMode(webSearchConfig.mode), keys };
}

function formatWebSearchTestResult(result) {
  if (!result) return "";
  if (result.ok === false) {
    const error = result.result?.error || result.result?.summary?.error || result.result?.raw?.error;
    return error ? String(error) : "Web Search 请求失败";
  }
  const summary = result.result?.summary || {};
  const answer = String(summary.answer || result.result?.raw?.answer || "").trim();
  const rows = Array.isArray(summary.results) ? summary.results : [];
  const links = rows.map((item, i) => {
    const title = String(item?.title || item?.url || `结果 ${i + 1}`).trim();
    const url = String(item?.url || "").trim();
    return url ? `${i + 1}. ${title}\n${url}` : `${i + 1}. ${title}`;
  }).join("\n");
  return [answer || "Web Search 已返回结果。", links].filter(Boolean).join("\n\n");
}

function maskWebSearchKey(value) {
  const key = String(value || "").trim();
  if (!key) return "-";
  if (key.length <= 8) return "*".repeat(key.length);
  return `${key.slice(0, 4)}${"*".repeat(Math.min(16, key.length - 8))}${key.slice(-4)}`;
}

function normalizePositiveInteger(value, fallback) {
  const parsed = Number(value);
  return (!Number.isFinite(parsed) || parsed <= 0) ? fallback : Math.floor(parsed);
}

function normalizeNonNegativeInteger(value, fallback) {
  const parsed = Number(value);
  return (!Number.isFinite(parsed) || parsed < 0) ? fallback : Math.floor(parsed);
}

function displayMs(value) {
  return value === null || value === undefined ? "-" : `${value} ms`;
}

onMounted(() => loadWebSearch());
</script>

<style scoped>
.mobile-web-search-list {
  display: none;
}

@media (max-width: 600px) {
  .desktop-web-search-list {
    display: none;
  }

  .mobile-web-search-list {
    display: grid;
  }

  .web-search-stats {
    row-gap: 8px;
  }

  .web-search-stats :deep(.el-col) {
    min-width: 0;
  }

  .web-search-stats :deep(.el-statistic) {
    min-height: 78px;
    padding: 10px 8px;
    border: 1px solid var(--el-border-color-lighter);
    border-radius: 6px;
  }

  .web-search-stats :deep(.el-statistic__head) {
    margin-bottom: 6px;
    font-size: 12px;
  }

  .web-search-stats :deep(.el-statistic__content) {
    font-size: 20px;
    overflow-wrap: anywhere;
  }

  .web-search-control-row {
    gap: 8px;
    margin-top: 16px;
  }

  .web-search-control-row :deep(.el-segmented) {
    width: 100%;
  }

  .web-search-control-row :deep(.el-segmented__item) {
    flex: 1;
    min-height: 44px;
  }

  .mobile-card-list {
    position: relative;
    gap: 12px;
    min-height: 96px;
    margin-top: 16px;
  }

  .mobile-data-card {
    min-width: 0;
    padding: 14px;
    border: 1px solid var(--el-border-color-lighter);
    border-radius: 6px;
    background: var(--el-bg-color);
  }

  .mobile-data-card__header,
  .mobile-data-card__heading {
    display: flex;
    align-items: flex-start;
    gap: 8px;
  }

  .mobile-data-card__header {
    justify-content: space-between;
  }

  .mobile-data-card__heading {
    min-width: 0;
  }

  .mobile-data-card__index {
    flex: 0 0 auto;
    color: var(--el-text-color-secondary);
    font-size: 12px;
    line-height: 1.8;
  }

  .mobile-data-card__title {
    min-width: 0;
    font-size: 15px;
    line-height: 1.45;
    overflow-wrap: anywhere;
  }

  .mobile-data-card__header :deep(.el-tag) {
    flex: 0 0 auto;
  }

  .mobile-data-card__details {
    display: grid;
    gap: 8px;
    margin: 14px 0;
  }

  .mobile-data-card__details > div {
    display: grid;
    grid-template-columns: 88px minmax(0, 1fr);
    gap: 10px;
    align-items: start;
  }

  .mobile-data-card__details dt {
    color: var(--el-text-color-secondary);
  }

  .mobile-data-card__details dd {
    min-width: 0;
    margin: 0;
    text-align: right;
    overflow-wrap: anywhere;
  }

  .mobile-key-value {
    display: block;
    font-size: 12px;
    word-break: break-all;
  }

  .mobile-card-actions {
    display: grid;
    grid-template-columns: repeat(3, minmax(0, 1fr));
    gap: 8px;
  }

  .mobile-card-actions :deep(.el-button) {
    width: 100%;
    min-width: 0;
    min-height: 44px;
    margin-left: 0;
    padding-inline: 8px;
  }

  .web-search-page .toolbar-actions {
    display: grid;
    grid-template-columns: repeat(2, minmax(0, 1fr));
    gap: 8px;
  }

  .web-search-page .toolbar-actions :deep(.el-button) {
    width: 100%;
    min-height: 44px;
    margin-left: 0;
  }

  .web-search-page .toolbar-actions :deep(.el-button:last-child) {
    grid-column: 1 / -1;
  }

  .channel-test-result {
    min-width: 0;
  }

  .channel-test-output {
    overflow-wrap: anywhere;
    word-break: break-word;
  }

  :global(.web-search-key-drawer) {
    width: 100% !important;
    max-width: 100vw;
  }

  :global(.web-search-key-drawer .el-drawer__header) {
    flex: 0 0 auto;
    margin-bottom: 0;
    padding: 16px;
  }

  :global(.web-search-key-drawer .el-drawer__body) {
    min-height: 0;
    padding: 16px;
    overflow-y: auto;
  }

  :global(.web-search-key-drawer .el-drawer__footer) {
    flex: 0 0 auto;
    padding: 12px 16px calc(12px + env(safe-area-inset-bottom));
  }

  :global(.web-search-key-drawer .el-input__inner),
  :global(.web-search-key-drawer .el-select__input) {
    font-size: 16px;
  }

  :global(.web-search-key-drawer .drawer-footer .el-button) {
    min-height: 44px;
  }
}
</style>
