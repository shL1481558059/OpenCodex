<template>
  <div>
    <div class="toolbar">
      <div>
        <h2>渠道配置</h2>
        <div class="text-muted">保存单个渠道后立即生效</div>
      </div>
      <div class="toolbar-actions">
        <el-button :icon="Refresh" @click="loadConfig">刷新</el-button>
        <el-button :icon="Connection" :disabled="selectedChannels.length === 0" @click="openBulkChannelTest">
          批量测试
        </el-button>
        <el-button :icon="Download" @click="exportChannels">导出</el-button>
        <el-button :icon="Upload" @click="triggerImportChannels">导入</el-button>
        <input
          ref="importChannelsInput"
          type="file"
          accept="application/json,.json"
          style="display:none"
          @change="handleImportChannelsFile"
        />
        <el-button type="primary" :icon="Plus" @click="openChannelDrawer()">新增渠道</el-button>
      </div>
    </div>

    <el-row :gutter="12">
      <el-col :span="12">
        <el-statistic title="渠道总数" :value="channels.length" />
      </el-col>
      <el-col :span="12">
        <el-statistic title="启用渠道" :value="enabledChannelCount" />
      </el-col>
    </el-row>

    <div class="table-area">
      <el-table
        ref="channelTableRef"
        v-loading="configLoading"
        :data="channels"
        row-key="id"
        style="width: 100%; margin-top: 16px"
        empty-text="暂无渠道"
        @selection-change="handleChannelSelectionChange"
      >
        <el-table-column type="selection" width="48" />
        <el-table-column
          v-if="props.isSuperadmin"
          prop="owner_username"
          label="所属用户"
          min-width="130"
          show-overflow-tooltip
        />
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
        <el-table-column label="健康状态" width="120">
          <template #default="{ row }">
            <el-tag :type="healthStatusTagType(row.health_status)">{{ formatHealthStatus(row.health_status) }}</el-tag>
          </template>
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
                    <el-dropdown-item
                      :disabled="!canResetChannelHealth(row) || resetChannelHealthLoadingId === row.id"
                      @click="confirmResetChannelHealth(row)"
                    >
                      <el-icon><Refresh /></el-icon>重置可用状态
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
            <el-form-item label="容量">
              <el-input-number
                v-model="channelDraft.capacity"
                :min="1"
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

        <el-divider content-position="left">兼容规则</el-divider>
        <el-row :gutter="12">
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

    <el-dialog v-model="discoverModelsVisible" title="发现模型" width="720px">
      <el-table
        ref="discoveredModelsTableRef"
        :data="discoveredModelRows"
        row-key="model"
        max-height="420"
        empty-text="未发现模型"
        @selection-change="handleDiscoveredModelSelectionChange"
      >
        <el-table-column type="selection" width="48" :selectable="isDiscoveredModelSelectable" />
        <el-table-column prop="model" label="模型" min-width="260" show-overflow-tooltip />
        <el-table-column label="映射状态" width="120">
          <template #default="{ row }">
            <el-tag :type="row.exists ? 'info' : 'success'">
              {{ row.exists ? "已存在" : "可添加" }}
            </el-tag>
          </template>
        </el-table-column>
      </el-table>

      <template #footer>
        <div class="drawer-footer">
          <el-button @click="discoverModelsVisible = false">取消</el-button>
          <el-button
            type="primary"
            :disabled="selectedDiscoveredModels.length === 0"
            @click="addSelectedModels"
          >
            添加到模型映射
          </el-button>
        </div>
      </template>
    </el-dialog>

    <!-- 批量测试 Dialog -->
    <el-dialog
      v-model="bulkTestVisible"
      title="批量测试渠道"
      width="960px"
      :close-on-click-modal="!bulkTestRunning"
      :before-close="handleBulkTestBeforeClose"
    >
      <el-form label-position="top" :model="bulkTestForm" class="channel-test-form">
        <el-row :gutter="12">
          <el-col :span="12">
            <el-form-item label="提示词">
              <el-input
                v-model="bulkTestForm.prompt"
                type="textarea"
                :rows="3"
                placeholder="请输入用于测试连接的提示词"
              />
            </el-form-item>
          </el-col>
          <el-col :span="6">
            <el-form-item label="最大输出 Tokens">
              <el-input-number
                v-model="bulkTestForm.max_output_tokens"
                :min="1"
                :step="1"
                step-strictly
                class="full-width"
              />
            </el-form-item>
          </el-col>
          <el-col :span="6">
            <el-form-item label="并发数">
              <el-input-number
                v-model="bulkTestForm.concurrency"
                :min="1"
                :max="10"
                :step="1"
                step-strictly
                class="full-width"
              />
            </el-form-item>
          </el-col>
        </el-row>
      </el-form>

      <div class="bulk-test-summary">
        <el-tag type="info">总数 {{ bulkTestSummary.total }}</el-tag>
        <el-tag type="success">成功 {{ bulkTestSummary.success }}</el-tag>
        <el-tag type="danger">失败 {{ bulkTestSummary.error }}</el-tag>
        <el-tag type="warning">测试中 {{ bulkTestSummary.running }}</el-tag>
        <el-tag type="info">等待 {{ bulkTestSummary.pending }}</el-tag>
        <el-tag v-if="bulkTestSummary.cancelled" type="info">取消 {{ bulkTestSummary.cancelled }}</el-tag>
      </div>

      <el-table
        :data="bulkTestRows"
        row-key="key"
        max-height="460"
        class="bulk-test-table"
        empty-text="暂无测试渠道"
      >
        <el-table-column prop="channel.name" label="渠道" min-width="150" show-overflow-tooltip>
          <template #default="{ row }">
            {{ row.channel.name || row.channel.id }}
          </template>
        </el-table-column>
        <el-table-column prop="channel.type" label="服务类型" width="110">
          <template #default="{ row }">
            <el-tag>{{ row.channel.type }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column prop="model" label="模型" min-width="170" show-overflow-tooltip>
          <template #default="{ row }">
            {{ row.model || "-" }}
          </template>
        </el-table-column>
        <el-table-column label="状态" width="100">
          <template #default="{ row }">
            <el-tag :type="bulkTestStatusTagType(row.status)">
              {{ formatBulkTestStatus(row.status) }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column label="耗时" width="100">
          <template #default="{ row }">
            {{ displayMs(row.result?.duration_ms) }}
          </template>
        </el-table-column>
        <el-table-column label="结果" min-width="260">
          <template #default="{ row }">
            <div class="bulk-test-output">{{ formatBulkTestResult(row) }}</div>
          </template>
        </el-table-column>
      </el-table>

      <template #footer>
        <div class="drawer-footer">
          <el-button @click="closeBulkTestDialog">关闭</el-button>
          <el-button v-if="bulkTestRunning" type="warning" @click="cancelBulkChannelTest">取消测试</el-button>
          <el-button
            type="primary"
            :loading="bulkTestRunning"
            :disabled="bulkTestRows.length === 0"
            @click="runBulkChannelTests"
          >
            {{ bulkTestRunButtonText }}
          </el-button>
        </div>
      </template>
    </el-dialog>

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

      <el-collapse
        v-if="hasChannelTestDetails(testResult)"
        class="channel-test-details"
      >
        <el-collapse-item title="响应详情" name="details">
          <div class="channel-test-detail-grid">
            <div v-if="testResult.details" class="channel-test-detail-section">
              <div class="channel-test-detail-title">测试信息</div>
              <pre class="channel-test-json">{{ formatChannelTestJson(channelTestSummaryDetails(testResult)) }}</pre>
            </div>
            <div v-if="testResult.details?.upstream_response" class="channel-test-detail-section">
              <div class="channel-test-detail-title">上游响应</div>
              <pre class="channel-test-json">{{ formatChannelTestJson(testResult.details.upstream_response) }}</pre>
            </div>
            <div v-if="testResult.details?.error_response" class="channel-test-detail-section">
              <div class="channel-test-detail-title">错误响应</div>
              <pre class="channel-test-json">{{ formatChannelTestJson(testResult.details.error_response) }}</pre>
            </div>
            <div v-if="testResult.details?.upstream_request" class="channel-test-detail-section">
              <div class="channel-test-detail-title">上游请求</div>
              <pre class="channel-test-json">{{ formatChannelTestJson(testResult.details.upstream_request) }}</pre>
            </div>
            <div v-if="testResult.raw_events?.length" class="channel-test-detail-section">
              <div class="channel-test-detail-title">原始事件</div>
              <pre class="channel-test-json">{{ formatChannelTestJson(testResult.raw_events) }}</pre>
            </div>
          </div>
        </el-collapse-item>
      </el-collapse>

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
import { ref, reactive, computed, nextTick, onMounted } from "vue";
import { ElMessage } from "element-plus/es/components/message/index.mjs";
import { ElMessageBox } from "element-plus/es/components/message-box/index.mjs";
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
  Download,
  DocumentCopy,
  Edit,
  MoreFilled,
  Plus,
  Refresh,
  Upload,
} from "@element-plus/icons-vue";
const props = defineProps({
  api: { type: Function, required: true },
  isSuperadmin: { type: Boolean, default: false },
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
const discoverModelsVisible = ref(false);
const discoveredModelsTableRef = ref(null);
const discoveredModels = ref([]);
const selectedDiscoveredModels = ref([]);
const config = reactive({ channels: [] });
const channelTableRef = ref(null);
const selectedChannels = ref([]);
const bulkTestVisible = ref(false);
const bulkTestRunning = ref(false);
const bulkTestCancelRequested = ref(false);
const bulkTestRows = ref([]);
const bulkTestForm = reactive({
  prompt: "你好",
  max_output_tokens: 256,
  concurrency: 3
});
const channelToggleSavingKeys = reactive(new Set());
const resetChannelHealthLoadingId = ref("");
const bulkTestAbortControllers = new Set();

const channels = computed(() => config.channels || []);
const enabledChannelCount = computed(() => channels.value.filter((c) => c.enabled !== false).length);
const channelTestModelOptions = computed(() => normalizeModels(testingChannel.value?.models).map((item) => item.model));
const existingDiscoveredModelNames = computed(() => {
  const names = new Set();
  for (const item of normalizeModels(channelDraft.models)) {
    if (item.model) names.add(item.model);
    if (item.upstream_model) names.add(item.upstream_model);
  }
  return names;
});
const discoveredModelRows = computed(() =>
  discoveredModels.value.map((model) => ({
    model,
    exists: existingDiscoveredModelNames.value.has(model)
  }))
);
const bulkTestSummary = computed(() => {
  const summary = {
    total: bulkTestRows.value.length,
    pending: 0,
    running: 0,
    success: 0,
    error: 0,
    cancelled: 0
  };

  for (const row of bulkTestRows.value) {
    if (Object.prototype.hasOwnProperty.call(summary, row.status)) {
      summary[row.status] += 1;
    }
  }

  return summary;
});
const bulkTestRunButtonText = computed(() => {
  if (bulkTestRunning.value) {
    return "测试中";
  }

  return bulkTestRows.value.some((row) => row.status !== "pending") ? "重新测试" : "开始测试";
});
const channelTestTitle = computed(() => {
  const name = testingChannel.value?.name || testingChannel.value?.id || "";
  return name ? `测试连接 - ${name}` : "测试连接";
});
async function loadConfig() {
  configLoading.value = true;
  try {
    const data = await props.api("/config");
    config.channels = Array.isArray(data.channels) ? data.channels : [];
    selectedChannels.value = [];
    await nextTick();
    channelTableRef.value?.clearSelection();
  } catch (error) {
    ElMessage.error(error.message);
  } finally {
    configLoading.value = false;
  }
}

const importChannelsInput = ref(null);

function exportChannels() {
  const payload = {
    exported_at: new Date().toISOString(),
    type: "channels",
    channels: config.channels
  };
  const blob = new Blob([JSON.stringify(payload, null, 2)], { type: "application/json" });
  const url = URL.createObjectURL(blob);
  const link = document.createElement("a");
  link.href = url;
  link.download = `channels-${Date.now()}.json`;
  document.body.appendChild(link);
  link.click();
  document.body.removeChild(link);
  URL.revokeObjectURL(url);
  ElMessage.success("渠道配置已导出");
}

function triggerImportChannels() {
  importChannelsInput.value?.click();
}

async function handleImportChannelsFile(event) {
  const file = event.target.files?.[0];
  if (!file) return;
  event.target.value = "";
  try {
    const text = await file.text();
    const parsed = JSON.parse(text);
    const channels = Array.isArray(parsed.channels) ? parsed.channels : Array.isArray(parsed) ? parsed : null;
    if (!channels) {
      ElMessage.error("导入文件格式不正确：缺少 channels 数组");
      return;
    }
    await props.api("/config/import", {
      method: "POST",
      body: JSON.stringify({ channels })
    });
    ElMessage.success("渠道配置导入成功");
    await loadConfig();
  } catch (error) {
    ElMessage.error(error.message || "导入失败");
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
  const data = await props.api("/config", {
    method: "POST",
    body: JSON.stringify({ channels: nextChannels })
  });
  config.channels = Array.isArray(data?.channels) ? data.channels : nextChannels;
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

function handleChannelSelectionChange(selection) {
  selectedChannels.value = Array.isArray(selection) ? selection : [];
}

function openBulkChannelTest() {
  if (selectedChannels.value.length === 0) {
    ElMessage.warning("请先选择渠道");
    return;
  }

  bulkTestRows.value = selectedChannels.value.map((channel, index) =>
    createBulkTestRow(channel, index)
  );
  bulkTestCancelRequested.value = false;
  bulkTestVisible.value = true;
}

async function runBulkChannelTests() {
  if (bulkTestRunning.value || bulkTestRows.value.length === 0) {
    return;
  }

  bulkTestCancelRequested.value = false;
  resetBulkTestRows();
  const runnableRows = bulkTestRows.value.filter((row) => row.status === "pending");
  if (runnableRows.length === 0) {
    return;
  }

  bulkTestRunning.value = true;
  let nextIndex = 0;
  const workerCount = Math.min(normalizeBulkConcurrency(), runnableRows.length);
  const runNext = async () => {
    while (!bulkTestCancelRequested.value) {
      const row = runnableRows[nextIndex];
      nextIndex += 1;
      if (!row) {
        return;
      }
      await runBulkChannelTestRow(row);
    }
  };

  try {
    await Promise.all(Array.from({ length: workerCount }, runNext));
  } finally {
    if (bulkTestCancelRequested.value) {
      markPendingBulkRowsCancelled();
    }
    bulkTestAbortControllers.clear();
    bulkTestRunning.value = false;
  }
}

function cancelBulkChannelTest() {
  bulkTestCancelRequested.value = true;
  for (const controller of bulkTestAbortControllers) {
    controller.abort();
  }
}

function handleBulkTestBeforeClose(done) {
  if (bulkTestRunning.value) {
    cancelBulkChannelTest();
  }
  done();
}

function closeBulkTestDialog() {
  if (bulkTestRunning.value) {
    cancelBulkChannelTest();
  }
  bulkTestVisible.value = false;
}

function createBulkTestRow(channel, index) {
  const copiedChannel = cloneChannelForTest(channel);
  const model = normalizeModels(copiedChannel.models)[0]?.model || "";
  return {
    key: `${copiedChannel.id || "channel"}:${index}`,
    channel: copiedChannel,
    model,
    status: "pending",
    result: createChannelTestState()
  };
}

function resetBulkTestRows() {
  for (const row of bulkTestRows.value) {
    row.result = createChannelTestState();
    if (!row.model) {
      row.status = "error";
      row.result.phase = "error";
      row.result.error = "缺少模型映射";
      row.result.body = { error: "缺少模型映射" };
      row.result.duration_ms = 0;
      continue;
    }

    row.status = "pending";
  }
}

async function runBulkChannelTestRow(row) {
  row.status = "running";
  row.result = createChannelTestState();
  const controller = new AbortController();
  const startedAt = performance.now();
  bulkTestAbortControllers.add(controller);
  try {
    const payload = buildChannelTestPayload(row.channel);
    payload.model = row.model;
    payload.input = bulkTestForm.prompt || "你好";
    payload.max_output_tokens = normalizeBulkMaxOutputTokens();
    await streamChannelTest(
      payload,
      (event) => {
        applyChannelTestStreamEvent(row.result, event);
      },
      { signal: controller.signal }
    );
    finalizeChannelTestResult(row.result);
    row.result.duration_ms = Math.round(performance.now() - startedAt);
    row.status = row.result.phase === "error" ? "error" : "success";
  } catch (error) {
    const aborted = isAbortError(error);
    row.status = aborted ? "cancelled" : "error";
    row.result = {
      phase: "error",
      error: aborted ? "已取消" : error.message,
      duration_ms: Math.round(performance.now() - startedAt),
      response: { output_text: "" },
      details: null,
      raw_events: row.result?.raw_events || [],
      hasReceivedEvent: row.result?.hasReceivedEvent === true,
      body: null
    };
  } finally {
    bulkTestAbortControllers.delete(controller);
  }
}

function markPendingBulkRowsCancelled() {
  for (const row of bulkTestRows.value) {
    if (row.status !== "pending") {
      continue;
    }

    row.status = "cancelled";
    row.result.phase = "error";
    row.result.error = "已取消";
    row.result.duration_ms = 0;
  }
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

function canResetChannelHealth(channel) {
  return channel?.health_status === "open" || channel?.health_status === "half_open";
}

async function confirmResetChannelHealth(channel) {
  if (!canResetChannelHealth(channel) || !channel?.id) {
    return;
  }

  try {
    await ElMessageBox.confirm(
      `确认重置渠道“${channel.name || channel.id}”的可用状态吗？`,
      "重置可用状态",
      {
        type: "warning",
        confirmButtonText: "重置",
        cancelButtonText: "取消"
      }
    );
  } catch {
    return;
  }

  resetChannelHealthLoadingId.value = channel.id;
  try {
    await props.api(`/channels/${channel.id}/reset-health`, {
      method: "POST",
      body: "{}"
    });
    ElMessage.success("已重置渠道可用状态");
    await loadConfig();
  } catch (error) {
    ElMessage.error(error.message);
  } finally {
    resetChannelHealthLoadingId.value = "";
  }
}

async function discoverModels() {
  discoverLoading.value = true;
  discoveredModels.value = [];
  selectedDiscoveredModels.value = [];
  try {
    const channel = buildChannelFromDraft();
    const payload = buildChannelTestPayload(channel);
    const data = await props.api("/discover-models", {
      method: "POST",
      body: JSON.stringify(payload)
    });
    const models = uniqueStringList(data?.models || []);
    discoveredModels.value = models;
    if (models.length === 0) {
      ElMessage.info("未发现模型");
      return;
    }

    discoverModelsVisible.value = true;
    await nextTick();
    selectDefaultDiscoveredModels();
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
      details: null,
      raw_events: [],
      hasReceivedEvent: false,
      body: null
    };
  } finally {
    testLoading.value = false;
  }
}

function addSelectedModels() {
  let addedCount = 0;
  for (const model of selectedDiscoveredModels.value) {
    if (!existingDiscoveredModelNames.value.has(model)) {
      channelDraft.models.push({ model, upstream_model: model, supports_image: false });
      addedCount += 1;
    }
  }
  if (addedCount > 0) {
    ElMessage.success(`已添加 ${addedCount} 个模型`);
  }
  discoverModelsVisible.value = false;
  selectedDiscoveredModels.value = [];
}

function handleDiscoveredModelSelectionChange(rows) {
  selectedDiscoveredModels.value = rows.map((row) => row.model);
}

function isDiscoveredModelSelectable(row) {
  return !row.exists;
}

function selectDefaultDiscoveredModels() {
  const table = discoveredModelsTableRef.value;
  if (!table) return;
  table.clearSelection();
  for (const row of discoveredModelRows.value) {
    if (!row.exists) {
      table.toggleRowSelection(row, true);
    }
  }
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

async function streamChannelTest(payload, onEvent, options = {}) {
  const response = await fetch(`${devApiPrefix}/test-channel/stream`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(payload),
    signal: options.signal
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
    capacity: 3,
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
  if (!Number.isInteger(capacity) || capacity <= 0) {
    throw new Error("容量必须是正整数");
  }
  const id = ensureChannelId(channelDraft.id, channelDraft.name);
  return {
    id,
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

function ensureChannelId(id, name) {
  const normalizedId = String(id || "").trim();
  if (normalizedId) {
    return normalizedId;
  }

  return `${slugifyChannelName(name) || "channel"}-${randomChannelSuffix()}`;
}

function slugifyChannelName(name) {
  return String(name || "")
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "")
    .slice(0, 24);
}

function randomChannelSuffix() {
  return Math.random().toString(36).slice(2, 8);
}

// --- Shared utils ---

function buildSuggestions(values, query) {
  const lowered = String(query || "").toLowerCase();
  return (values || [])
    .filter((value) => String(value).toLowerCase().includes(lowered))
    .map((value) => ({ value: String(value) }));
}

function uniqueStringList(values) {
  const seen = new Set();
  const result = [];
  for (const value of values || []) {
    const text = String(value || "").trim();
    if (text && !seen.has(text)) {
      seen.add(text);
      result.push(text);
    }
  }
  return result;
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

function formatBulkTestResult(row) {
  switch (row.status) {
    case "pending":
      return "等待测试";
    case "running":
      return formatChannelTestResult(row.result);
    case "cancelled":
      return "已取消";
    default:
      return formatChannelTestResult(row.result);
  }
}

function formatBulkTestStatus(status) {
  switch (status) {
    case "pending":
      return "等待";
    case "running":
      return "测试中";
    case "success":
      return "成功";
    case "error":
      return "失败";
    case "cancelled":
      return "取消";
    default:
      return status || "-";
  }
}

function bulkTestStatusTagType(status) {
  switch (status) {
    case "success":
      return "success";
    case "error":
      return "danger";
    case "running":
      return "warning";
    default:
      return "info";
  }
}

function hasChannelTestDetails(result) {
  return Boolean(result?.details || result?.raw_events?.length);
}

function channelTestSummaryDetails(result) {
  const details = result?.details || {};
  return {
    status_code: details.status_code,
    duration_ms: details.duration_ms ?? result?.duration_ms,
    request_model: details.request_model,
    upstream_model: details.upstream_model,
    channel_id: details.channel_id,
    channel_type: details.channel_type,
    error: details.error
  };
}

function formatChannelTestJson(value) {
  if (value === null || value === undefined) {
    return "";
  }

  if (typeof value === "string") {
    return value;
  }

  try {
    return JSON.stringify(value, null, 2);
  } catch {
    return String(value);
  }
}

function formatCapacityStatus(channel) {
  const activeRequests = Number(channel?.active_requests ?? 0);
  const capacity = normalizeCapacityValue(channel?.capacity);
  return `${activeRequests} / ${capacity ?? "-"}`;
}

function normalizeBulkMaxOutputTokens() {
  const value = Number(bulkTestForm.max_output_tokens);
  return Number.isInteger(value) && value > 0 ? value : 256;
}

function normalizeBulkConcurrency() {
  const value = Number(bulkTestForm.concurrency);
  if (!Number.isInteger(value) || value <= 0) {
    return 3;
  }

  return Math.min(value, 10);
}

function cloneChannelForTest(channel) {
  return JSON.parse(JSON.stringify(channel || {}));
}

function isAbortError(error) {
  return error?.name === "AbortError";
}

function formatHealthStatus(value) {
  switch (value) {
    case "disabled":
      return "停用";
    case "open":
      return "熔断开启";
    case "half_open":
      return "降级";
    default:
      return "健康";
  }
}

function healthStatusTagType(value) {
  switch (value) {
    case "disabled":
      return "info";
    case "open":
      return "danger";
    case "half_open":
      return "warning";
    default:
      return "success";
  }
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

function isPlainObject(value) {
  return value && typeof value === "object" && !Array.isArray(value);
}

// Expose loadConfig so App can call it on init
onMounted(() => loadConfig());
</script>
