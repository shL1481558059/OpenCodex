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
        <el-table-column prop="priority" label="优先级" width="90" />
        <el-table-column label="容量状态" width="140">
          <template #default="{ row }">{{ formatCapacityStatus(row) }}</template>
        </el-table-column>
        <el-table-column label="状态" width="100">
          <template #default="{ row, $index }">
            <el-switch
              :model-value="row.enabled !== false"
              :loading="isChannelToggleSaving(row, $index)"
              :disabled="configLoading"
              :width="56"
              inline-prompt
              active-text="启用"
              inactive-text="停用"
              @change="toggleChannelEnabled(row, $index, $event)"
            />
          </template>
        </el-table-column>
        <el-table-column label="操作" width="240" min-width="240" align="center">
          <template #default="{ row, $index }">
            <div class="channel-action-buttons">
              <el-button size="small" :icon="Edit" class="action-btn" @click="openChannelDrawer(row, $index)">
                编辑
              </el-button>
              <el-popconfirm title="删除这个渠道？" @confirm="deleteChannel($index)">
                <template #reference>
                  <el-button size="small" type="danger" :icon="Delete" class="action-btn">
                    删除
                  </el-button>
                </template>
              </el-popconfirm>
              <el-dropdown trigger="click">
                <el-button size="small" :icon="MoreFilled" class="action-btn">
                  更多
                </el-button>
                <template #dropdown>
                  <el-dropdown-menu>
                    <el-dropdown-item @click="openChannelTest(row)">
                      <el-icon><Connection /></el-icon>测试连接
                    </el-dropdown-item>
                    <el-dropdown-item @click="copyChannel(row)">
                      <el-icon><DocumentCopy /></el-icon>复制
                    </el-dropdown-item>
                  </el-dropdown-menu>
                </template>
              </el-dropdown>
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
            <el-form-item label="优先级">
              <el-input-number
                v-model="channelDraft.priority"
                :min="0"
                :step="1"
                step-strictly
                class="full-width"
              />
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item label="容量（留空不限）">
              <el-input-number
                v-model="channelDraft.capacity"
                :min="1"
                :step="1"
                :value-on-clear="null"
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
          <el-table-column label="支持图片" width="110" align="center">
            <template #default="{ row }">
              <el-switch v-model="row.supports_image" />
            </template>
          </el-table-column>
          <el-table-column width="90">
            <template #default="{ $index }">
              <el-button type="danger" :icon="Delete" circle @click="channelDraft.models.splice($index, 1)" />
            </template>
          </el-table-column>
        </el-table>
        <el-button style="margin-top: 8px" :icon="Plus" @click="channelDraft.models.push({ model: '', upstream_model: '', supports_image: false })">
          添加模型
        </el-button>
        <el-button style="margin-top: 8px; margin-left: 8px" :loading="discoverLoading" @click="discoverModels">
          发现模型
        </el-button>
        <el-alert v-if="discoveredModels.length" style="margin-top: 12px" type="info" :closable="false">
          <el-checkbox-group v-model="selectedDiscoveredModels">
            <el-checkbox v-for="model in discoveredModels" :key="model" :label="model" :value="model" />
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
            <el-form-item label="drop_tool_types">
              <el-input v-model="compatTexts.drop_tool_types" type="textarea" :rows="4" placeholder="image_generation&#10;image_generation_call" />
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
        :title="getChannelTestAlertTitle(testResult)"
        :type="getChannelTestAlertType(testResult)"
        show-icon
        :closable="false"
      >
        <div class="channel-test-result__meta">
          <span v-if="testResult.duration_ms !== undefined">耗时 {{ displayMs(testResult.duration_ms) }}</span>
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
import { ElMessage } from "element-plus/es/components/message/index.mjs";
import {
  applyChannelTestStreamEvent,
  createChannelTestState,
  finalizeChannelTestResult,
  formatChannelTestResult,
  getChannelTestAlertTitle,
  getChannelTestAlertType
} from "./channelTestState.js";
import {
  Connection,
  Delete,
  DocumentCopy,
  Download,
  Edit,
  MoreFilled,
  Plus,
  Refresh,
  Upload
} from "@element-plus/icons-vue";
const props = defineProps({
  api: { type: Function, required: true },
});
const devApiPrefix = import.meta.env.DEV ? import.meta.env.BASE_URL.replace(/\/$/, "") : "";
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
  drop_tool_types: "",
  force_params: "",
  default_params: "",
  unsupported_params: ""
});

const testResult = ref(null);
const channelTestVisible = ref(false);
const testingChannel = ref(null);
const channelTestForm = reactive({ model: "", prompt: "你好" });
const discoveredModels = ref([]);
const selectedDiscoveredModels = ref([]);
const config = reactive({ channels: [] });
const channelToggleSavingKeys = reactive(new Set());

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
async function loadConfig() {
  configLoading.value = true;
  try {
    const data = await props.api("/config");
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
    await persistChannels(nextChannels);
    ElMessage.success("渠道配置已保存并生效");
  } catch (error) {
    ElMessage.error(error.message);
  } finally {
    saveLoading.value = false;
  }
}

async function persistChannels(nextChannels) {
  await props.api("/config", {
    method: "POST",
    body: JSON.stringify({ channels: nextChannels })
  });
  config.channels = nextChannels;
}

function openChannelDrawer(channel = null, index = -1) {
  editingIndex.value = index;
  const draftSource = channel || defaultChannel(nextChannelPriority());
  assignChannelDraft(draftSource);
  headersText.value = formatJson(draftSource.headers || {});
  assignCompat(draftSource.compat || {});
  channelDrawerVisible.value = true;
}

function openChannelTest(channel) {
  testingChannel.value = channel;
  channelTestForm.model = normalizeModels(channel.models)[0]?.model || "";
  channelTestForm.prompt = "你好";
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

async function toggleChannelEnabled(channel, index, enabled) {
  const key = channelToggleKey(channel, index);
  if (channelToggleSavingKeys.has(key)) {
    return;
  }

  channelToggleSavingKeys.add(key);
  const nextEnabled = enabled === true;
  const previousChannels = channels.value;
  const nextChannels = previousChannels.map((item, itemIndex) =>
    itemIndex === index ? { ...item, enabled: nextEnabled } : item
  );

  config.channels = nextChannels;
  try {
    await persistChannels(nextChannels);
    ElMessage.success(nextEnabled ? "渠道已启用" : "渠道已停用");
  } catch (error) {
    config.channels = previousChannels;
    ElMessage.error(error.message);
  } finally {
    channelToggleSavingKeys.delete(key);
  }
}

function isChannelToggleSaving(channel, index) {
  return channelToggleSavingKeys.has(channelToggleKey(channel, index));
}

function channelToggleKey(channel, index) {
  return channel?.id || `index:${index}`;
}


function copyChannel(channel) {
  const newId = `${channel.id || 'channel'}-copy-${Date.now()}`;
  const cloned = JSON.parse(JSON.stringify(channel));
  cloned.id = newId;
  openChannelDrawer(cloned, -1);
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
  const text = JSON.stringify(
    { channels: channels.value.map(exportChannel) },
    null,
    2
  );
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
    const data = await props.api("/discover-models", {
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
  const startedAt = performance.now();
  testResult.value = createChannelTestState();
  try {
    const channel = testingChannel.value;
    const payload = buildChannelTestPayload(channel);
    payload.model = channelTestForm.model || normalizeModels(channel.models)[0]?.model || "";
    payload.input = channelTestForm.prompt || "你好";
    payload.max_output_tokens = 256;
    await streamChannelTest(payload, (event) => {
      applyChannelTestStreamEvent(testResult.value, event);
    });
    finalizeChannelTestResult(testResult.value);
    testResult.value.duration_ms = Math.round(performance.now() - startedAt);
  } catch (error) {
    testResult.value = {
      phase: "error",
      error: error.message,
      duration_ms: Math.round(performance.now() - startedAt),
      response: { output_text: "" },
      raw_events: [],
      hasReceivedEvent: false,
      body: null
    };
  } finally {
    testLoading.value = false;
  }
}

function addSelectedModels() {
  for (const model of selectedDiscoveredModels.value) {
    if (!channelDraft.models.some((m) => m.model === model)) {
      channelDraft.models.push({ model, upstream_model: model, supports_image: false });
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
    priority: Number(channel.priority ?? 0),
    capacity: normalizeCapacityValue(channel.capacity),
    compat: channel.compat || {},
    models: channel.models || [],
    enabled: channel.enabled !== false,
    model: "",
    input: "你好",
    max_output_tokens: 256
  };
}

function channelTestModelSuggestions(query, callback) {
  callback(buildSuggestions(channelTestModelOptions.value, query));
}

async function streamChannelTest(payload, onEvent) {
  const response = await fetch(`${devApiPrefix}/test-channel/stream`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(payload)
  });
  if (!response.ok) {
    throw new Error(await response.text() || response.statusText);
  }
  if (!response.body) {
    throw new Error("浏览器不支持流式响应读取");
  }

  const reader = response.body.getReader();
  const decoder = new TextDecoder();
  let buffer = "";
  while (true) {
    const { value, done } = await reader.read();
    if (done) break;
    buffer += decoder.decode(value, { stream: true });
    buffer = consumeSseBuffer(buffer, onEvent);
  }
  buffer += decoder.decode();
  consumeSseBuffer(`${buffer}\n\n`, onEvent);
}

function consumeSseBuffer(buffer, onEvent) {
  let remaining = buffer;
  while (true) {
    const separator = remaining.indexOf("\n\n");
    if (separator === -1) {
      return remaining;
    }
    const chunk = remaining.slice(0, separator);
    remaining = remaining.slice(separator + 2);
    const event = parseSseChunk(chunk);
    if (event) onEvent(event);
  }
}

function parseSseChunk(chunk) {
  const lines = chunk.split(/\r?\n/);
  let eventName = "message";
  const data = [];
  for (const line of lines) {
    if (line.startsWith("event:")) {
      eventName = line.slice("event:".length).trim();
    } else if (line.startsWith("data:")) {
      data.push(line.slice("data:".length).trimStart());
    }
  }
  if (data.length === 0) return null;
  const text = data.join("\n");
  if (text === "[DONE]") {
    return { event: eventName, data: text };
  }
  try {
    return { event: eventName, data: JSON.parse(text), raw: text };
  } catch {
    return { event: eventName, data: text, raw: text };
  }
}

// --- Channel helpers ---

function defaultChannel(priority = 0) {
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
    priority,
    capacity: null,
    compat: {},
    models: [],
    enabled: true
  };
}

function assignChannelDraft(channel) {
  Object.assign(channelDraft, defaultChannel(normalizePriorityValue(channel.priority)), channel, {
    headers: channel.headers || {},
    priority: normalizePriorityValue(channel.priority),
    capacity: normalizeCapacityValue(channel.capacity),
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
    drop_tool_types: formatStringList(compat.drop_tool_types || []),
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
  const priority = normalizePriorityValue(channelDraft.priority);
  const capacity = normalizeCapacityValue(channelDraft.capacity);
  if (!Number.isInteger(priority) || priority < 0) {
    throw new Error("优先级必须是大于等于 0 的整数");
  }
  if (capacity !== null && (!Number.isInteger(capacity) || capacity <= 0)) {
    throw new Error("容量必须是正整数，或留空表示不限");
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
    priority,
    capacity,
    enabled: channelDraft.enabled === true,
    models: normalizeModels(channelDraft.models).filter((item) => item.model),
    compat: buildCompat()
  };
}

function buildCompat() {
  const compat = {
    rename_params: parseAssignmentMap(compatTexts.rename_params, false),
    drop_params: parseStringList(compatTexts.drop_params),
    drop_tool_types: parseStringList(compatTexts.drop_tool_types),
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
      return {
        model,
        upstream_model: String(item?.upstream_model || model).trim() || model,
        supports_image: item?.supports_image === true
      };
    })
    .filter((item) => item.model);
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

function displayMs(value) {
  return value === null || value === undefined ? "-" : `${value} ms`;
}

function formatCapacityStatus(channel) {
  const activeRequests = Number(channel?.active_requests ?? 0);
  const capacity = normalizeCapacityValue(channel?.capacity);
  return `${activeRequests} / ${capacity === null ? "不限" : capacity}`;
}

function formatJson(value) {
  return JSON.stringify(value, null, 2);
}

function nextChannelPriority() {
  return channels.value.reduce((maxPriority, channel) => {
    const priority = normalizePriorityValue(channel?.priority);
    return Math.max(maxPriority, priority);
  }, -1) + 1;
}

function normalizePriorityValue(value) {
  const priority = Number(value ?? 0);
  return Number.isInteger(priority) && priority >= 0 ? priority : 0;
}

function normalizeCapacityValue(value) {
  if (value === null || value === undefined || value === "") {
    return null;
  }

  const capacity = Number(value);
  return Number.isInteger(capacity) && capacity > 0 ? capacity : null;
}

function exportChannel(channel) {
  return {
    owner_username: channel.owner_username,
    id: channel.id,
    name: channel.name,
    type: channel.type,
    baseurl: channel.baseurl,
    apikey: channel.apikey,
    auth_mode: channel.auth_mode,
    headers: channel.headers || {},
    timeout_seconds: Number(channel.timeout_seconds || 120),
    retry_count: Number(channel.retry_count ?? 3),
    priority: normalizePriorityValue(channel.priority),
    capacity: normalizeCapacityValue(channel.capacity),
    compat: channel.compat || {},
    models: normalizeModels(channel.models),
    enabled: channel.enabled !== false
  };
}

function isPlainObject(value) {
  return value && typeof value === "object" && !Array.isArray(value);
}

// Expose loadConfig so App can call it on init
onMounted(() => loadConfig());
</script>
