<template>
  <div>
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

    <div class="table-area">
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
        <el-table-column label="操作" width="260" align="center">
          <template #default="{ row, $index }">
            <div class="inline-actions channel-table-actions">
              <el-button size="small" type="primary" plain :icon="Connection" @click="openChannelTest(row)">
                测试连接
              </el-button>
              <el-button size="small" :icon="Edit" @click="openChannelDrawer(row, $index)">编辑</el-button>
              <el-popconfirm title="删除这个渠道？" @confirm="deleteChannel($index)">
                <template #reference>
                  <el-button size="small" type="danger" :icon="Delete">删除</el-button>
                </template>
              </el-popconfirm>
            </div>
          </template>
        </el-table-column>
      </el-table>
    </div>

    <!-- 渠道编辑 Drawer -->
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
                <el-option label="配置 Key" value="config" />
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
            <el-form-item label="重试次数">
              <el-input-number
                v-model="channelDraft.retry_count"
                :min="0"
                :step="1"
                step-strictly
                class="full-width"
              />
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
        <el-button style="margin-top: 8px; margin-left: 8px" :loading="discoverLoading" @click="discoverModels">
          发现模型
        </el-button>
        <el-alert v-if="discoveredModels.length" style="margin-top: 12px" type="info" :closable="false">
          <el-checkbox-group v-model="selectedDiscoveredModels">
            <el-checkbox v-for="model in discoveredModels" :key="model" :label="model" />
          </el-checkbox-group>
          <el-button size="small" style="margin-top: 8px" @click="addSelectedModels">加入映射</el-button>
        </el-alert>

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
      </el-form>

      <template #footer>
        <div class="drawer-footer">
          <el-button @click="channelDrawerVisible = false">取消</el-button>
          <el-button type="primary" :loading="saveLoading" @click="saveChannel">保存渠道</el-button>
        </div>
      </template>
    </el-drawer>

    <!-- 渠道测试 Dialog -->
    <el-dialog v-model="channelTestVisible" :title="channelTestTitle" width="640px">
      <el-descriptions v-if="testingChannel" :column="2" border class="channel-test-summary">
        <el-descriptions-item label="渠道">{{ testingChannel.name || testingChannel.id }}</el-descriptions-item>
        <el-descriptions-item label="服务类型">{{ testingChannel.type }}</el-descriptions-item>
        <el-descriptions-item label="ID">{{ testingChannel.id }}</el-descriptions-item>
        <el-descriptions-item label="状态">
          {{ testingChannel.enabled === false ? "停用" : "启用" }}
        </el-descriptions-item>
      </el-descriptions>

      <el-form label-position="top" :model="channelTestForm" class="channel-test-form">
        <el-form-item label="模型">
          <el-autocomplete
            v-model="channelTestForm.model"
            :fetch-suggestions="channelTestModelSuggestions"
            clearable
            class="full-width"
            placeholder="选择或输入模型"
          />
        </el-form-item>
        <el-form-item label="提示词">
          <el-input
            v-model="channelTestForm.prompt"
            type="textarea"
            :rows="4"
            placeholder="请输入用于测试连接的提示词"
          />
        </el-form-item>
      </el-form>

      <el-alert
        v-if="testResult"
        class="channel-test-result"
        :title="testResult.ok === false ? '连接测试失败' : '连接测试成功'"
        :type="testResult.ok === false ? 'error' : 'success'"
        show-icon
        :closable="false"
      >
        <div class="channel-test-result__meta">
          <span v-if="testResult.duration_ms !== undefined">耗时 {{ displayMs(testResult.duration_ms) }}</span>
          <span v-if="testResult.status_code">状态码 {{ testResult.status_code }}</span>
          <span v-if="testResult.upstream_model">上游模型 {{ testResult.upstream_model }}</span>
        </div>
        <div class="channel-test-output">{{ formatChannelTestResult(testResult) }}</div>
      </el-alert>

      <template #footer>
        <div class="drawer-footer">
          <el-button @click="channelTestVisible = false">关闭</el-button>
          <el-button type="primary" :loading="testLoading" @click="testChannel">测试连接</el-button>
        </div>
      </template>
    </el-dialog>
  </div>
</template>

<script setup>
import { ref, reactive, computed, onMounted } from "vue";
import { ElMessage } from "element-plus";
import {
  Connection,
  Delete,
  Download,
  Edit,
  Plus,
  Refresh,
  Upload
} from "@element-plus/icons-vue";


const props = defineProps({
  api: { type: Function, required: true },
});

onMounted(() => loadConfig());



const configLoading = ref(false);
const saveLoading = ref(false);
const testLoading = ref(false);
const discoverLoading = ref(false);
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

onMounted(() => loadConfig());
const testResult = ref(null);
const channelTestVisible = ref(false);
const testingChannel = ref(null);
const channelTestForm = reactive({ model: "", prompt: "ping" });
const discoveredModels = ref([]);
const selectedDiscoveredModels = ref([]);
const config = reactive({ channels: [] });

const channels = computed(() => config.channels || []);
const enabledChannelCount = computed(() => channels.value.filter((c) => c.enabled !== false).length);
const modelMappingCount = computed(() =>
  channels.value.reduce((total, c) => total + normalizeModels(c.models).length, 0)
);
const channelTestModelOptions = computed(() => normalizeModels(testingChannel.value?.models).map((item) => item.model));
const channelTestTitle = computed(() => {
  const name = testingChannel.value?.name || testingChannel.value?.id || "";
  return name ? `测试连接 - ${name}` : "测试连接";
});

onMounted(() => loadConfig());

async function loadConfig() {
  configLoading.value = true;
  try {
    const data = await props.api("/admin/api/config");
    config.channels = Array.isArray(data.channels) ? data.channels : [];
  } catch (error) {
    ElMessage.error(error.message);
  } finally {
    configLoading.value = false;
  }
}

async function saveConfig(nextChannels) {
  saveLoading.value = true;
  try {
    await props.api("/admin/api/config", {
      method: "PUT",
      body: JSON.stringify({ channels: nextChannels })
    });
    config.channels = nextChannels;
    ElMessage.success("渠道配置已保存并生效");
  } catch (error) {
    ElMessage.error(error.message);
  } finally {
    saveLoading.value = false;
  }
}

function openChannelDrawer(channel = null, index = -1) {
  editingIndex.value = index;
  assignChannelDraft(channel || defaultChannel());
  headersText.value = formatJson(channel?.headers || {});
  assignCompat(channel?.compat || {});
  channelDrawerVisible.value = true;
}

function openChannelTest(channel) {
  testingChannel.value = channel;
  channelTestForm.model = normalizeModels(channel.models)[0]?.model || "";
  channelTestForm.prompt = "ping";
  testResult.value = null;
  channelTestVisible.value = true;
}

async function saveChannel() {
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
  } catch (error) {
    ElMessage.error(error.message);
  }
}

async function deleteChannel(index) {
  const nextChannels = channels.value.slice();
  nextChannels.splice(index, 1);
  await saveConfig(nextChannels);
}

async function importConfig(file) {
  try {
    const text = await file.text();
    const data = JSON.parse(text);
    if (!Array.isArray(data.channels)) {
      throw new Error("配置文件必须包含 channels 数组");
    }
    await saveConfig(data.channels);
  } catch (error) {
    ElMessage.error(error.message);
  }
  return false;
}

function exportConfig() {
  const text = JSON.stringify({ channels: channels.value }, null, 2);
  const blob = new Blob([text], { type: "application/json" });
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url;
  a.download = "opencodex-config.json";
  a.click();
  URL.revokeObjectURL(url);
}

async function discoverModels() {
  if (!testingChannel.value && !channelDraft.id) {
    ElMessage.warning("请先填写渠道 ID");
    return;
  }
  discoverLoading.value = true;
  discoveredModels.value = [];
  selectedDiscoveredModels.value = [];
  try {
    const channel = testingChannel.value || buildChannelFromDraft();
    const payload = buildChannelTestPayload(channel);
    payload.max_output_tokens = 1;
    const data = await props.api("/admin/api/test-channel", {
      method: "POST",
      body: JSON.stringify(payload)
    });
    const models = data?.models || [];
    discoveredModels.value = models;
    selectedDiscoveredModels.value = models.slice();
  } catch (error) {
    ElMessage.error(error.message);
  } finally {
    discoverLoading.value = false;
  }
}

async function testChannel() {
  testLoading.value = true;
  testResult.value = null;
  try {
    const channel = testingChannel.value;
    const payload = buildChannelTestPayload(channel);
    payload.model = channelTestForm.model || normalizeModels(channel.models)[0]?.model || "";
    payload.input = channelTestForm.prompt || "ping";
    payload.max_output_tokens = 256;
    const result = await props.api("/admin/api/test-channel", {
      method: "POST",
      body: JSON.stringify(payload)
    });
    testResult.value = result;
  } catch (error) {
    testResult.value = { ok: false, error: error.message };
  } finally {
    testLoading.value = false;
  }
}

function addSelectedModels() {
  for (const model of selectedDiscoveredModels.value) {
    if (!channelDraft.models.some((m) => m.model === model)) {
      channelDraft.models.push({ model, upstream_model: model });
    }
  }
  discoveredModels.value = [];
  selectedDiscoveredModels.value = [];
}

function buildChannelTestPayload(channel) {
  return {
    id: channel.id,
    name: channel.name,
    type: channel.type,
    baseurl: channel.baseurl,
    apikey: channel.apikey,
    auth_mode: channel.auth_mode,
    headers: channel.headers || {},
    timeout_seconds: Number(channel.timeout_seconds || 120),
    retry_count: Number(channel.retry_count ?? 3),
    compat: channel.compat || {},
    model: "",
    input: "ping",
    max_output_tokens: 256
  };
}

function channelTestModelSuggestions(query, callback) {
  callback(buildSuggestions(channelTestModelOptions.value, query));
}

// --- Channel helpers ---

function defaultChannel() {
  return {
    id: "",
    name: "",
    type: "chat",
    baseurl: "",
    apikey: "",
    auth_mode: "config",
    headers: {},
    timeout_seconds: 120,
    retry_count: 3,
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
  return {
    id: channelDraft.id.trim(),
    name: channelDraft.name.trim(),
    type: channelDraft.type,
    baseurl: channelDraft.baseurl.trim(),
    apikey: channelDraft.apikey,
    auth_mode: channelDraft.auth_mode,
    headers,
    timeout_seconds: Number(channelDraft.timeout_seconds || 120),
    retry_count: Number(channelDraft.retry_count ?? 3),
    enabled: channelDraft.enabled === true,
    models: normalizeModels(channelDraft.models).filter((item) => item.model),
    compat: buildCompat()
  };
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
  if (!Array.isArray(models)) return [];
  return models
    .map((item) => {
      const model = String(item?.model || "").trim();
      return { model, upstream_model: String(item?.upstream_model || model).trim() || model };
    })
    .filter((item) => item.model);
}

function formatChannelTestResult(result) {
  if (!result) return "";
  if (result.ok === false) {
    const details = extractErrorMessage(result.body);
    return [result.error || "上游请求失败", details].filter(Boolean).join("\n");
  }
  const responseText = extractResponseText(result.response);
  if (responseText) return responseText;
  return "连接已打通，但响应中没有可展示的文本内容。";
}

// --- Shared utils ---

function buildSuggestions(values, query) {
  const lowered = String(query || "").toLowerCase();
  return (values || [])
    .filter((value) => String(value).toLowerCase().includes(lowered))
    .map((value) => ({ value: String(value) }));
}

function parseJsonText(text, label) {
  try { return JSON.parse(text || "{}"); } catch { throw new Error(`${label} 不是合法 JSON`); }
}

function parseStringList(text) {
  return String(text || "").split("\n").map((l) => l.trim()).filter(Boolean);
}

function parseAssignmentMap(text, parseValue) {
  const result = {};
  for (const [index, line] of String(text || "").split("\n").entries()) {
    const trimmed = line.trim();
    if (!trimmed) continue;
    const separator = trimmed.indexOf("=");
    if (separator === -1) throw new Error(`第 ${index + 1} 行缺少 =`);
    const key = trimmed.slice(0, separator).trim();
    const rawValue = trimmed.slice(separator + 1).trim();
    result[key] = parseValue ? parseLooseValue(rawValue) : rawValue;
  }
  return result;
}

function parseLooseValue(value) {
  try { return JSON.parse(value); } catch { return value; }
}

function formatAssignmentMap(value) {
  return Object.entries(value || {})
    .map(([key, item]) => `${key}=${typeof item === "string" ? item : JSON.stringify(item)}`)
    .join("\n");
}

function formatStringList(value) {
  return Array.isArray(value) ? value.join("\n") : "";
}

function extractErrorMessage(value) {
  if (!value) return "";
  if (typeof value === "string") return value;
  if (typeof value.error === "string") return value.error;
  if (value.error?.message) return String(value.error.message);
  if (value.message) return String(value.message);
  return "";
}

function extractResponseText(response) {
  if (!response || typeof response !== "object") return "";
  const outputText = String(response.output_text || "").trim();
  if (outputText) return outputText;
  const choiceContent = response.choices?.[0]?.message?.content;
  const choiceText = stringifyContent(choiceContent).trim();
  if (choiceText) return choiceText;
  const messageText = stringifyContent(response.content).trim();
  if (messageText) return messageText;
  const output = Array.isArray(response.output) ? response.output : [];
  const parts = [];
  for (const item of output) {
    const content = Array.isArray(item?.content) ? item.content : [];
    for (const block of content) {
      const text = block?.text || block?.output_text;
      if (text) parts.push(String(text));
    }
  }
  return parts.join("\n").trim();
}

function stringifyContent(content) {
  if (typeof content === "string") return content;
  if (!Array.isArray(content)) return "";
  return content.map((item) => (typeof item === "string" ? item : item?.text || "")).filter(Boolean).join("\n");
}

function displayMs(value) {
  return value === null || value === undefined ? "-" : `${value} ms`;
}

function formatJson(value) {
  return JSON.stringify(value, null, 2);
}

function isPlainObject(value) {
  return value && typeof value === "object" && !Array.isArray(value);
}

// Expose loadConfig so App can call it on init
</script>
