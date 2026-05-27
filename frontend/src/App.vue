<template>
  <div v-if="loadingSession" class="login-wrap">
    <el-empty description="正在加载管理台" />
  </div>

  <div v-else-if="!authenticated" class="login-wrap">
    <el-card class="login-card" shadow="never">
      <template #header>
        <div>
          <strong>OpenCodex 管理台</strong>
          <div class="text-muted">请输入管理员密码</div>
        </div>
      </template>
      <el-form label-position="top" @submit.prevent="login">
        <input autocomplete="username" hidden value="admin" />
        <el-form-item label="密码">
          <el-input
            v-model="loginPassword"
            type="password"
            show-password
            autocomplete="current-password"
            @keyup.enter="login"
          />
        </el-form-item>
        <el-button type="primary" class="full-width" :loading="loginLoading" @click="login">
          登录
        </el-button>
      </el-form>
    </el-card>
  </div>

  <el-container v-else class="app-shell">
    <el-aside width="220px">
      <el-menu :default-active="activeTab" @select="activeTab = $event">
        <el-menu-item index="channels">
          <el-icon><Connection /></el-icon>
          <span>渠道配置</span>
        </el-menu-item>
        <el-menu-item index="logs">
          <el-icon><Tickets /></el-icon>
          <span>请求日志</span>
        </el-menu-item>
      </el-menu>
    </el-aside>

    <el-container>
      <el-header>
        <div class="toolbar">
          <div>
            <strong>OpenCodex Proxy</strong>
            <div class="text-muted">渠道配置和请求日志</div>
          </div>
          <el-button :icon="SwitchButton" @click="logout">退出</el-button>
        </div>
      </el-header>

      <el-main class="main-content">
        <section v-show="activeTab === 'channels'">
          <div class="toolbar">
            <div>
              <h2>渠道配置</h2>
              <div class="text-muted">保存单个渠道后立即生效</div>
            </div>
            <div class="toolbar-actions">
              <el-upload :show-file-list="false" accept="application/json" :before-upload="importConfig">
                <template #trigger>
                  <el-button :icon="Upload">导入配置</el-button>
                </template>
              </el-upload>
              <el-button :icon="Download" @click="exportConfig">导出配置</el-button>
              <el-button :icon="Refresh" @click="loadConfig">刷新</el-button>
              <el-button type="primary" :icon="Plus" @click="openChannelDrawer()">新增渠道</el-button>
            </div>
          </div>

          <el-row :gutter="12">
            <el-col :span="8">
              <el-statistic title="渠道总数" :value="channels.length" />
            </el-col>
            <el-col :span="8">
              <el-statistic title="启用渠道" :value="enabledChannelCount" />
            </el-col>
            <el-col :span="8">
              <el-statistic title="模型映射" :value="modelMappingCount" />
            </el-col>
          </el-row>

          <el-table
            v-loading="configLoading"
            :data="channels"
            row-key="id"
            style="width: 100%; margin-top: 16px"
            empty-text="暂无渠道"
          >
            <el-table-column prop="id" label="ID" min-width="160" show-overflow-tooltip />
            <el-table-column prop="name" label="名称" min-width="140" show-overflow-tooltip />
            <el-table-column prop="type" label="服务类型" width="110">
              <template #default="{ row }">
                <el-tag>{{ row.type }}</el-tag>
              </template>
            </el-table-column>
            <el-table-column prop="baseurl" label="Base URL" min-width="220" show-overflow-tooltip />
            <el-table-column label="模型映射" width="110">
              <template #default="{ row }">{{ normalizeModels(row.models).length }}</template>
            </el-table-column>
            <el-table-column label="状态" width="100">
              <template #default="{ row }">
                <el-tag :type="row.enabled === false ? 'warning' : 'success'">
                  {{ row.enabled === false ? "停用" : "启用" }}
                </el-tag>
              </template>
            </el-table-column>
            <el-table-column label="操作" width="250" fixed="right">
              <template #default="{ row, $index }">
                <div class="inline-actions">
                  <el-button size="small" :icon="Edit" @click="openChannelDrawer(row, $index)">编辑</el-button>
                  <el-button size="small" :disabled="$index === 0" @click="moveChannel($index, -1)">上移</el-button>
                  <el-button size="small" :disabled="$index === channels.length - 1" @click="moveChannel($index, 1)">
                    下移
                  </el-button>
                  <el-popconfirm title="删除这个渠道？" @confirm="deleteChannel($index)">
                    <template #reference>
                      <el-button size="small" type="danger" :icon="Delete">删除</el-button>
                    </template>
                  </el-popconfirm>
                </div>
              </template>
            </el-table-column>
          </el-table>
        </section>

        <section v-show="activeTab === 'logs'">
          <div class="toolbar">
            <div>
              <h2>请求日志</h2>
              <div class="text-muted">表格分页展示，详情中查看完整请求与响应</div>
            </div>
            <div class="toolbar-actions">
              <el-button :icon="Refresh" @click="loadLogs">刷新</el-button>
              <el-button @click="resetLogFilters">重置</el-button>
            </div>
          </div>

          <el-form :inline="true" :model="logFilters">
            <el-form-item label="请求 ID">
              <el-autocomplete
                v-model="logFilters.request_id"
                :fetch-suggestions="requestIdSuggestions"
                clearable
                @select="loadLogs(1)"
              />
            </el-form-item>
            <el-form-item label="模型">
              <el-autocomplete
                v-model="logFilters.model"
                :fetch-suggestions="modelSuggestions"
                clearable
                @select="loadLogs(1)"
              />
            </el-form-item>
            <el-form-item label="渠道">
              <el-select v-model="logFilters.channel_id" clearable filterable style="width: 180px" @change="loadLogs(1)">
                <el-option v-for="item in filterOptions.channel_ids" :key="item" :label="item" :value="item" />
              </el-select>
            </el-form-item>
            <el-form-item label="状态码">
              <el-select v-model="logFilters.status_code" clearable filterable style="width: 140px" @change="loadLogs(1)">
                <el-option v-for="item in filterOptions.status_codes" :key="item" :label="item" :value="item" />
              </el-select>
            </el-form-item>
            <el-form-item label="请求状态">
              <el-select v-model="logFilters.request_status" clearable style="width: 140px" @change="loadLogs(1)">
                <el-option label="成功" value="success" />
                <el-option label="失败" value="failed" />
              </el-select>
            </el-form-item>
            <el-form-item label="路径">
              <el-select v-model="logFilters.path" clearable filterable style="width: 190px" @change="loadLogs(1)">
                <el-option v-for="item in filterOptions.paths" :key="item" :label="item" :value="item" />
              </el-select>
            </el-form-item>
            <el-form-item>
              <el-button type="primary" :icon="Search" @click="loadLogs(1)">查询</el-button>
            </el-form-item>
          </el-form>

          <el-table
            v-loading="logsLoading"
            :data="logs"
            style="width: 100%"
            empty-text="暂无日志"
          >
            <el-table-column prop="created_at" label="时间" width="180">
              <template #default="{ row }">{{ formatTime(row.created_at) }}</template>
            </el-table-column>
            <el-table-column prop="request_id" label="请求" width="130" show-overflow-tooltip />
            <el-table-column prop="request_status" label="状态" width="90">
              <template #default="{ row }">
                <el-tag :type="row.request_status === 'success' ? 'success' : 'danger'">
                  {{ row.request_status === "success" ? "成功" : "失败" }}
                </el-tag>
              </template>
            </el-table-column>
            <el-table-column prop="model" label="模型" min-width="160" show-overflow-tooltip />
            <el-table-column prop="channel_id" label="渠道" min-width="130" show-overflow-tooltip />
            <el-table-column prop="status_code" label="状态码" width="90" />
            <el-table-column prop="duration_ms" label="耗时" width="95">
              <template #default="{ row }">{{ displayMs(row.duration_ms) }}</template>
            </el-table-column>
            <el-table-column prop="ttft_ms" label="TTFT" width="95">
              <template #default="{ row }">{{ displayMs(row.ttft_ms) }}</template>
            </el-table-column>
            <el-table-column label="Token" width="190">
              <template #default="{ row }">
                {{ row.input_tokens || 0 }} / {{ row.cached_tokens || 0 }} / {{ row.output_tokens || 0 }}
              </template>
            </el-table-column>
            <el-table-column prop="cost" label="成本" width="110">
              <template #default="{ row }">{{ formatCost(row.cost) }}</template>
            </el-table-column>
            <el-table-column label="请求 Body" min-width="220">
              <template #default="{ row }">{{ previewJson(row.request_body) }}</template>
            </el-table-column>
            <el-table-column label="响应" min-width="220">
              <template #default="{ row }">{{ previewJson(row.response_body || row.error) }}</template>
            </el-table-column>
            <el-table-column label="操作" width="90" fixed="right">
              <template #default="{ row }">
                <el-button size="small" :icon="View" @click="openLogDetail(row)">详情</el-button>
              </template>
            </el-table-column>
          </el-table>

          <div style="margin-top: 16px; display: flex; justify-content: flex-end">
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
        </section>
      </el-main>
    </el-container>
  </el-container>

  <el-drawer v-model="channelDrawerVisible" :title="editingIndex === -1 ? '新增渠道' : '编辑渠道'" size="720px">
    <el-form label-position="top" :model="channelDraft">
      <el-row :gutter="12">
        <el-col :span="12">
          <el-form-item label="ID">
            <el-input v-model="channelDraft.id" :disabled="editingIndex !== -1" />
          </el-form-item>
        </el-col>
        <el-col :span="12">
          <el-form-item label="名称">
            <el-input v-model="channelDraft.name" />
          </el-form-item>
        </el-col>
        <el-col :span="12">
          <el-form-item label="服务类型">
            <el-select v-model="channelDraft.type" class="full-width">
              <el-option label="responses" value="responses" />
              <el-option label="chat" value="chat" />
              <el-option label="messages" value="messages" />
            </el-select>
          </el-form-item>
        </el-col>
        <el-col :span="12">
          <el-form-item label="认证方式">
            <el-select v-model="channelDraft.auth_mode" class="full-width">
              <el-option label="透传或配置" value="pass_through_or_config" />
              <el-option label="透传" value="pass_through" />
              <el-option label="配置" value="config" />
              <el-option label="无" value="none" />
            </el-select>
          </el-form-item>
        </el-col>
        <el-col :span="24">
          <el-form-item label="Base URL">
            <el-input v-model="channelDraft.baseurl" placeholder="https://example.com/v1" />
          </el-form-item>
        </el-col>
        <el-col :span="12">
          <el-form-item label="API Key">
            <el-input v-model="channelDraft.apikey" type="password" show-password />
          </el-form-item>
        </el-col>
        <el-col :span="12">
          <el-form-item label="超时秒数">
            <el-input-number v-model="channelDraft.timeout_seconds" :min="1" class="full-width" />
          </el-form-item>
        </el-col>
        <el-col :span="12">
          <el-form-item label="启用">
            <el-switch v-model="channelDraft.enabled" />
          </el-form-item>
        </el-col>
      </el-row>

      <el-divider content-position="left">请求头</el-divider>
      <el-input v-model="headersText" type="textarea" :rows="4" placeholder='{"X-Test":"yes"}' />

      <el-divider content-position="left">模型映射</el-divider>
      <el-table :data="channelDraft.models" empty-text="暂无模型映射">
        <el-table-column label="请求模型">
          <template #default="{ row }">
            <el-input v-model="row.model" />
          </template>
        </el-table-column>
        <el-table-column label="上游模型">
          <template #default="{ row }">
            <el-input v-model="row.upstream_model" />
          </template>
        </el-table-column>
        <el-table-column width="90">
          <template #default="{ $index }">
            <el-button type="danger" :icon="Delete" circle @click="channelDraft.models.splice($index, 1)" />
          </template>
        </el-table-column>
      </el-table>
      <el-button style="margin-top: 8px" :icon="Plus" @click="channelDraft.models.push({ model: '', upstream_model: '' })">
        添加模型
      </el-button>

      <el-divider content-position="left">兼容规则</el-divider>
      <el-row :gutter="12">
        <el-col :span="24">
          <el-form-item label="fallback_thinking_on_tool_use">
            <el-switch v-model="compatDraft.fallback_thinking_on_tool_use" />
          </el-form-item>
        </el-col>
        <el-col :span="12">
          <el-form-item label="rename_params">
            <el-input v-model="compatTexts.rename_params" type="textarea" :rows="4" placeholder="from=to" />
          </el-form-item>
        </el-col>
        <el-col :span="12">
          <el-form-item label="drop_params">
            <el-input v-model="compatTexts.drop_params" type="textarea" :rows="4" placeholder="每行一个参数" />
          </el-form-item>
        </el-col>
        <el-col :span="12">
          <el-form-item label="force_params">
            <el-input v-model="compatTexts.force_params" type="textarea" :rows="4" placeholder='name={"type":"text"}' />
          </el-form-item>
        </el-col>
        <el-col :span="12">
          <el-form-item label="default_params">
            <el-input v-model="compatTexts.default_params" type="textarea" :rows="4" placeholder="temperature=0.2" />
          </el-form-item>
        </el-col>
        <el-col :span="24">
          <el-form-item label="unsupported_params">
            <el-input v-model="compatTexts.unsupported_params" type="textarea" :rows="3" placeholder="每行一个参数" />
          </el-form-item>
        </el-col>
      </el-row>

      <el-divider content-position="left">渠道测试</el-divider>
      <el-input v-model="testPayloadText" type="textarea" :rows="6" />
      <div class="inline-actions" style="margin-top: 8px">
        <el-button :icon="Refresh" @click="resetTestPayload">重置测试请求</el-button>
        <el-button :loading="discoverLoading" @click="discoverModels">发现模型</el-button>
        <el-button type="primary" :loading="testLoading" @click="testChannel">测试渠道</el-button>
      </div>
      <el-alert v-if="testResult" style="margin-top: 12px" :type="testResult.ok === false ? 'error' : 'success'" :closable="false">
        <pre class="json-view">{{ formatJson(testResult) }}</pre>
      </el-alert>
      <el-alert v-if="discoveredModels.length" style="margin-top: 12px" type="info" :closable="false">
        <el-checkbox-group v-model="selectedDiscoveredModels">
          <el-checkbox v-for="model in discoveredModels" :key="model" :label="model" />
        </el-checkbox-group>
        <el-button size="small" style="margin-top: 8px" @click="addSelectedModels">加入映射</el-button>
      </el-alert>
    </el-form>

    <template #footer>
      <div class="drawer-footer">
        <el-button @click="channelDrawerVisible = false">取消</el-button>
        <el-button type="primary" :loading="saveLoading" @click="saveChannel">保存渠道</el-button>
      </div>
    </template>
  </el-drawer>

  <el-dialog v-model="logDetailVisible" title="日志详情" width="900px">
    <el-descriptions v-if="selectedLog" :column="2" border>
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
        <pre class="json-view">{{ formatStoredJson(selectedLog?.request_headers) }}</pre>
      </el-tab-pane>
      <el-tab-pane label="请求 Body">
        <pre class="json-view">{{ formatStoredJson(selectedLog?.request_body) }}</pre>
      </el-tab-pane>
      <el-tab-pane label="响应">
        <div class="detail-grid">
          <el-alert v-if="selectedLog?.error" title="错误" type="error" :closable="false">
            <pre class="json-view">{{ selectedLog.error }}</pre>
          </el-alert>
          <pre class="json-view">{{ formatStoredJson(selectedLog?.response_body) }}</pre>
        </div>
      </el-tab-pane>
    </el-tabs>
  </el-dialog>
</template>

<script setup>
import { computed, onMounted, reactive, ref } from "vue";
import { ElMessage } from "element-plus";
import {
  Delete,
  Download,
  Edit,
  Plus,
  Refresh,
  Search,
  SwitchButton,
  Upload,
  View
} from "@element-plus/icons-vue";

const activeTab = ref("channels");
const authenticated = ref(false);
const loadingSession = ref(true);
const loginPassword = ref("");
const loginLoading = ref(false);
const configLoading = ref(false);
const saveLoading = ref(false);
const logsLoading = ref(false);
const testLoading = ref(false);
const discoverLoading = ref(false);

const config = reactive({ channels: [] });
const channelDrawerVisible = ref(false);
const editingIndex = ref(-1);
const channelDraft = reactive(defaultChannel());
const headersText = ref("{}");
const compatDraft = reactive({ fallback_thinking_on_tool_use: false });
const compatTexts = reactive({
  rename_params: "",
  drop_params: "",
  force_params: "",
  default_params: "",
  unsupported_params: ""
});
const testPayloadText = ref("");
const testResult = ref(null);
const discoveredModels = ref([]);
const selectedDiscoveredModels = ref([]);

const logs = ref([]);
const logPage = ref(1);
const logPageSize = ref(50);
const logTotal = ref(0);
const filterOptions = reactive({
  request_ids: [],
  models: [],
  upstream_models: [],
  channel_ids: [],
  paths: [],
  status_codes: [],
  request_statuses: ["success", "failed"]
});
const logFilters = reactive({
  request_id: "",
  model: "",
  channel_id: "",
  status_code: "",
  path: "",
  request_status: ""
});
const selectedLog = ref(null);
const logDetailVisible = ref(false);

const channels = computed(() => config.channels || []);
const enabledChannelCount = computed(() => channels.value.filter((channel) => channel.enabled !== false).length);
const modelMappingCount = computed(() =>
  channels.value.reduce((total, channel) => total + normalizeModels(channel.models).length, 0)
);

onMounted(async () => {
  await checkSession();
});

async function api(url, options = {}) {
  const response = await fetch(url, {
    headers: { "Content-Type": "application/json", ...(options.headers || {}) },
    ...options
  });
  const contentType = response.headers.get("content-type") || "";
  const data = contentType.includes("application/json") ? await response.json() : await response.text();
  if (!response.ok) {
    const message = typeof data === "string" ? data : data.error?.message || data.error || response.statusText;
    throw new Error(message);
  }
  return data;
}

async function checkSession() {
  loadingSession.value = true;
  try {
    const data = await api("/admin/api/session");
    authenticated.value = data.authenticated;
    if (authenticated.value) {
      await Promise.all([loadConfig(), loadLogs()]);
    }
  } finally {
    loadingSession.value = false;
  }
}

async function login() {
  loginLoading.value = true;
  try {
    const data = await api("/admin/api/login", {
      method: "POST",
      body: JSON.stringify({ password: loginPassword.value })
    });
    authenticated.value = data.authenticated;
    loginPassword.value = "";
    await Promise.all([loadConfig(), loadLogs()]);
  } catch (error) {
    ElMessage.error(error.message);
  } finally {
    loginLoading.value = false;
  }
}

async function logout() {
  await api("/admin/api/logout", { method: "POST", body: "{}" });
  authenticated.value = false;
}

async function loadConfig() {
  configLoading.value = true;
  try {
    const data = await api("/admin/api/config");
    config.channels = Array.isArray(data.channels) ? data.channels : [];
  } catch (error) {
    ElMessage.error(error.message);
  } finally {
    configLoading.value = false;
  }
}

async function saveConfig(nextChannels) {
  const data = await api("/admin/api/config", {
    method: "POST",
    body: JSON.stringify({ channels: nextChannels })
  });
  config.channels = Array.isArray(data.channels) ? data.channels : [];
}

function openChannelDrawer(channel = null, index = -1) {
  editingIndex.value = index;
  assignChannelDraft(channel ? clone(channel) : defaultChannel());
  headersText.value = formatJson(channelDraft.headers || {});
  assignCompat(channelDraft.compat || {});
  resetTestPayload();
  testResult.value = null;
  discoveredModels.value = [];
  selectedDiscoveredModels.value = [];
  channelDrawerVisible.value = true;
}

async function saveChannel() {
  saveLoading.value = true;
  try {
    const channel = buildChannelFromDraft();
    const nextChannels = channels.value.slice();
    if (editingIndex.value === -1) {
      nextChannels.push(channel);
    } else {
      nextChannels.splice(editingIndex.value, 1, channel);
    }
    await saveConfig(nextChannels);
    channelDrawerVisible.value = false;
    ElMessage.success("渠道已保存并生效");
  } catch (error) {
    ElMessage.error(error.message);
  } finally {
    saveLoading.value = false;
  }
}

async function deleteChannel(index) {
  const nextChannels = channels.value.slice();
  nextChannels.splice(index, 1);
  try {
    await saveConfig(nextChannels);
    ElMessage.success("渠道已删除");
  } catch (error) {
    ElMessage.error(error.message);
  }
}

async function moveChannel(index, direction) {
  const target = index + direction;
  if (target < 0 || target >= channels.value.length) {
    return;
  }
  const nextChannels = channels.value.slice();
  const [item] = nextChannels.splice(index, 1);
  nextChannels.splice(target, 0, item);
  try {
    await saveConfig(nextChannels);
  } catch (error) {
    ElMessage.error(error.message);
  }
}

async function importConfig(file) {
  try {
    const text = await file.text();
    const payload = JSON.parse(text);
    const data = await api("/admin/api/config/import", {
      method: "POST",
      body: JSON.stringify(payload)
    });
    config.channels = data.config?.channels || [];
    ElMessage.success(`导入 ${data.imported} 个渠道，跳过 ${data.skipped} 个`);
  } catch (error) {
    ElMessage.error(error.message);
  }
  return false;
}

function exportConfig() {
  window.location.href = "/admin/api/config/export";
}

async function discoverModels() {
  discoverLoading.value = true;
  try {
    const channel = buildChannelFromDraft();
    const data = await api("/admin/api/channels/discover-models", {
      method: "POST",
      body: JSON.stringify({ channel })
    });
    discoveredModels.value = data.models || [];
    selectedDiscoveredModels.value = discoveredModels.value.slice();
    ElMessage.success(`发现 ${discoveredModels.value.length} 个模型`);
  } catch (error) {
    ElMessage.error(error.message);
  } finally {
    discoverLoading.value = false;
  }
}

async function testChannel() {
  testLoading.value = true;
  try {
    const channel = buildChannelFromDraft();
    const payload = parseJsonText(testPayloadText.value, "测试请求");
    testResult.value = await api("/admin/api/channels/test", {
      method: "POST",
      body: JSON.stringify({ channel, payload })
    });
  } catch (error) {
    ElMessage.error(error.message);
  } finally {
    testLoading.value = false;
  }
}

function addSelectedModels() {
  const existing = new Set(normalizeModels(channelDraft.models).map((item) => item.model));
  for (const model of selectedDiscoveredModels.value) {
    if (!existing.has(model)) {
      channelDraft.models.push({ model, upstream_model: model });
      existing.add(model);
    }
  }
}

function resetTestPayload() {
  const model = normalizeModels(channelDraft.models)[0]?.model || "test-model";
  const content =
    channelDraft.type === "messages"
      ? { model, messages: [{ role: "user", content: [{ type: "text", text: "ping" }] }], max_tokens: 32 }
      : channelDraft.type === "chat"
        ? { model, messages: [{ role: "user", content: "ping" }], max_tokens: 32 }
        : { model, input: "ping", max_output_tokens: 32 };
  testPayloadText.value = formatJson(content);
}

async function loadLogs(page = logPage.value) {
  logsLoading.value = true;
  logPage.value = typeof page === "number" ? page : logPage.value;
  try {
    const params = new URLSearchParams({
      page: String(logPage.value),
      page_size: String(logPageSize.value)
    });
    for (const [key, value] of Object.entries(logFilters)) {
      if (value !== "" && value !== null && value !== undefined) {
        params.set(key, value);
      }
    }
    const data = await api(`/admin/api/logs?${params.toString()}`);
    logs.value = data.events || [];
    logTotal.value = data.total || 0;
    Object.assign(filterOptions, data.filter_options || {});
  } catch (error) {
    ElMessage.error(error.message);
  } finally {
    logsLoading.value = false;
  }
}

function handleLogPageSizeChange() {
  logPage.value = 1;
  loadLogs(1);
}

function resetLogFilters() {
  Object.assign(logFilters, {
    request_id: "",
    model: "",
    channel_id: "",
    status_code: "",
    path: "",
    request_status: ""
  });
  loadLogs(1);
}

function openLogDetail(row) {
  selectedLog.value = row;
  logDetailVisible.value = true;
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
    .filter((value) => String(value).toLowerCase().includes(lowered))
    .map((value) => ({ value: String(value) }));
}

function defaultChannel() {
  return {
    id: "",
    name: "",
    type: "chat",
    baseurl: "",
    apikey: "",
    auth_mode: "pass_through_or_config",
    headers: {},
    timeout_seconds: 120,
    compat: {},
    models: [],
    enabled: true
  };
}

function assignChannelDraft(channel) {
  Object.assign(channelDraft, defaultChannel(), channel, {
    headers: channel.headers || {},
    compat: channel.compat || {},
    models: normalizeModels(channel.models)
  });
}

function assignCompat(compat) {
  Object.assign(compatDraft, {
    fallback_thinking_on_tool_use: compat.fallback_thinking_on_tool_use === true
  });
  Object.assign(compatTexts, {
    rename_params: formatAssignmentMap(compat.rename_params || {}),
    drop_params: formatStringList(compat.drop_params || []),
    force_params: formatAssignmentMap(compat.force_params || {}),
    default_params: formatAssignmentMap(compat.default_params || {}),
    unsupported_params: formatStringList(compat.unsupported_params || [])
  });
}

function buildChannelFromDraft() {
  const headers = parseJsonText(headersText.value || "{}", "请求头");
  if (!headers || typeof headers !== "object" || Array.isArray(headers)) {
    throw new Error("请求头必须是 JSON 对象");
  }
  const channel = {
    id: channelDraft.id.trim(),
    name: channelDraft.name.trim(),
    type: channelDraft.type,
    baseurl: channelDraft.baseurl.trim(),
    apikey: channelDraft.apikey,
    auth_mode: channelDraft.auth_mode,
    headers,
    timeout_seconds: Number(channelDraft.timeout_seconds || 120),
    enabled: channelDraft.enabled === true,
    models: normalizeModels(channelDraft.models).filter((item) => item.model),
    compat: buildCompat()
  };
  return channel;
}

function buildCompat() {
  const compat = {
    rename_params: parseAssignmentMap(compatTexts.rename_params, false),
    drop_params: parseStringList(compatTexts.drop_params),
    force_params: parseAssignmentMap(compatTexts.force_params, true),
    default_params: parseAssignmentMap(compatTexts.default_params, true),
    unsupported_params: parseStringList(compatTexts.unsupported_params)
  };
  if (compatDraft.fallback_thinking_on_tool_use) {
    compat.fallback_thinking_on_tool_use = true;
  }
  for (const key of Object.keys(compat)) {
    const value = compat[key];
    if ((Array.isArray(value) && value.length === 0) || (isPlainObject(value) && Object.keys(value).length === 0)) {
      delete compat[key];
    }
  }
  return compat;
}

function normalizeModels(models) {
  if (!Array.isArray(models)) {
    return [];
  }
  return models
    .map((item) => {
      if (typeof item === "string") {
        const model = item.trim();
        return { model, upstream_model: model };
      }
      const model = String(item?.model || "").trim();
      return { model, upstream_model: String(item?.upstream_model || model).trim() || model };
    })
    .filter((item) => item.model);
}

function parseJsonText(text, label) {
  try {
    return JSON.parse(text || "{}");
  } catch (error) {
    throw new Error(`${label} 不是合法 JSON`);
  }
}

function parseStringList(text) {
  return String(text || "")
    .split("\n")
    .map((line) => line.trim())
    .filter(Boolean);
}

function parseAssignmentMap(text, parseValue) {
  const result = {};
  for (const [index, line] of String(text || "").split("\n").entries()) {
    const trimmed = line.trim();
    if (!trimmed) {
      continue;
    }
    const separator = trimmed.indexOf("=");
    if (separator === -1) {
      throw new Error(`第 ${index + 1} 行缺少 =`);
    }
    const key = trimmed.slice(0, separator).trim();
    const rawValue = trimmed.slice(separator + 1).trim();
    result[key] = parseValue ? parseLooseValue(rawValue) : rawValue;
  }
  return result;
}

function parseLooseValue(value) {
  try {
    return JSON.parse(value);
  } catch {
    return value;
  }
}

function formatAssignmentMap(value) {
  return Object.entries(value || {})
    .map(([key, item]) => `${key}=${typeof item === "string" ? item : JSON.stringify(item)}`)
    .join("\n");
}

function formatStringList(value) {
  return Array.isArray(value) ? value.join("\n") : "";
}

function formatStoredJson(value) {
  if (value === null || value === undefined || value === "") {
    return "";
  }
  if (typeof value === "string") {
    try {
      return formatJson(JSON.parse(value));
    } catch {
      return value;
    }
  }
  return formatJson(value);
}

function previewJson(value) {
  const text = formatStoredJson(value).replace(/\s+/g, " ").trim();
  return text.length > 120 ? `${text.slice(0, 120)}...` : text;
}

function formatJson(value) {
  return JSON.stringify(value, null, 2);
}

function formatTime(timestamp) {
  if (!timestamp) {
    return "";
  }
  return new Date(Number(timestamp) * 1000).toLocaleString();
}

function displayMs(value) {
  return value === null || value === undefined ? "-" : `${value} ms`;
}

function formatCost(value) {
  const number = Number(value || 0);
  return number ? `$${number.toFixed(6)}` : "$0.000000";
}

function clone(value) {
  return JSON.parse(JSON.stringify(value || {}));
}

function isPlainObject(value) {
  return value && typeof value === "object" && !Array.isArray(value);
}
</script>
