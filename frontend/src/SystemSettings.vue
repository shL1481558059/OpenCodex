<template>
  <div class="section-scroll system-settings-page">
    <div class="toolbar">
      <div>
        <h2>系统设置</h2>
        <div class="text-muted">按业务管理服务、代理策略与图片识别能力</div>
      </div>
    </div>

    <el-tabs v-model="activeSettingsTab" class="settings-tabs">
      <el-tab-pane v-if="isSuperadmin" label="服务配置" name="service">
        <div class="settings-tab-panel">
          <div class="settings-tab-header">
            <div>
              <h3>服务配置</h3>
              <div class="text-muted">配置本机服务的访问范围和监听端口，保存后可能需要重启服务生效</div>
            </div>
            <div class="settings-tab-actions">
              <el-button :icon="Refresh" :loading="loading" @click="loadSettings">刷新</el-button>
            </div>
          </div>

          <el-form v-loading="loading" class="settings-form" label-position="top">
            <el-form-item v-if="tauriRuntime" label="访问范围">
              <el-segmented v-model="draft.access_mode" :options="accessModeOptions" />
            </el-form-item>

            <el-alert
              v-if="tauriRuntime && draft.access_mode === 'lan'"
              class="settings-alert"
              type="warning"
              title="局域网访问会允许同一网络内的设备连接当前服务"
              show-icon
              :closable="false"
            />

            <el-form-item v-if="tauriRuntime" label="后端端口">
              <el-input-number
                v-model="draft.port"
                :min="1024"
                :max="65535"
                :step="1"
                controls-position="right"
              />
            </el-form-item>

            <div v-if="settings" class="settings-meta">
              <el-descriptions :column="1" border>
                <el-descriptions-item v-if="tauriRuntime" label="监听地址">{{ bindHostLabel }}</el-descriptions-item>
                <el-descriptions-item label="管理台地址">
                  <code>{{ settings.admin_url }}</code>
                </el-descriptions-item>
                <el-descriptions-item label="桌面托管">
                  <el-tag :type="settings.managed_by_desktop ? 'success' : 'info'">
                    {{ settings.managed_by_desktop ? "是" : "否" }}
                  </el-tag>
                </el-descriptions-item>
              </el-descriptions>
            </div>

            <div class="settings-actions">
              <el-button type="primary" :icon="Check" :loading="saving" @click="saveSettings">保存</el-button>
              <el-button
                v-if="restartRequired && tauriRuntime"
                type="warning"
                :icon="RefreshRight"
                :loading="restarting"
                @click="restartService"
              >
                立即重启服务
              </el-button>
            </div>

            <el-alert
              v-if="restartRequired"
              class="settings-alert"
              type="info"
              title="配置已保存，重启后端服务后生效"
              show-icon
              :closable="false"
            />
          </el-form>
        </div>
      </el-tab-pane>

      <el-tab-pane v-if="isSuperadmin" label="代理策略" name="proxy">
        <div class="settings-tab-panel">
          <div class="settings-tab-header">
            <div>
              <h3>代理策略</h3>
              <div class="text-muted">控制代理是否拦截探测请求，配置保存后立即生效</div>
            </div>
            <div class="settings-tab-actions">
              <el-button :icon="Refresh" :loading="proxyLoading" @click="loadProxySettings">刷新</el-button>
            </div>
          </div>

          <el-form v-loading="proxyLoading" class="settings-form" label-position="top">
            <el-form-item label="拦截探测请求">
              <div class="settings-switch-row">
                <el-switch
                  v-model="proxyDraft.intercept_probe_requests"
                  active-text="开启"
                  inactive-text="关闭"
                />
                <el-button
                  type="primary"
                  :icon="Check"
                  :loading="proxySaving"
                  @click="saveProxySettings"
                >
                  保存
                </el-button>
              </div>
              <div class="text-muted">开启后，代理会对符合探测特征的请求进行拦截处理</div>
            </el-form-item>
          </el-form>
        </div>
      </el-tab-pane>

      <el-tab-pane label="图片识别" name="vision">
        <div class="settings-tab-panel">
          <div class="settings-tab-header">
            <div>
              <h3>{{ isSuperadmin ? "图片识别转移模型" : "我的图片识别转移模型" }}</h3>
              <div class="text-muted">
                纯文本模型收到图片时，先用这里配置的视觉模型把图片转成文字，再把纯文本请求发给原模型
              </div>
            </div>
            <div class="settings-tab-actions">
              <el-button :icon="Refresh" :loading="visionLoading" @click="loadVisionTransfer">刷新</el-button>
            </div>
          </div>

          <el-form v-loading="visionLoading" class="settings-form" label-position="top">
            <el-form-item v-if="isSuperadmin" label="所属用户">
              <el-select v-model="visionOwner" filterable class="settings-select" @change="loadVisionTransfer">
                <el-option v-for="item in ownerOptions" :key="item.value" :label="item.label" :value="item.value" />
              </el-select>
              <div class="text-muted">这里选的是访问密钥的所属用户，不是当前登录账号</div>
            </el-form-item>

            <el-alert
              v-if="!visionHasCandidates"
              class="settings-alert"
              type="warning"
              :title="candidateEmptyHint"
              show-icon
              :closable="false"
            />

            <el-form-item label="主视觉渠道">
              <el-select
                v-model="visionDraft.primary.channelId"
                class="settings-select"
                clearable
                placeholder="选择渠道"
                @change="(value) => onChannelChange('primary', value)"
              >
                <el-option
                  v-for="item in visionChannelOptions"
                  :key="item.value"
                  :label="`${item.label}（${item.channelType}）`"
                  :value="item.value"
                />
              </el-select>
            </el-form-item>

            <el-form-item label="主视觉模型">
              <el-select v-model="visionDraft.primary.model" class="settings-select" clearable placeholder="选择模型">
                <el-option
                  v-for="item in primaryModelOptions"
                  :key="item.value"
                  :label="item.label"
                  :value="item.value"
                />
              </el-select>
            </el-form-item>

            <el-form-item label="兜底视觉渠道">
              <el-select
                v-model="visionDraft.fallback.channelId"
                class="settings-select"
                clearable
                placeholder="可留空"
                @change="(value) => onChannelChange('fallback', value)"
              >
                <el-option
                  v-for="item in visionChannelOptions"
                  :key="item.value"
                  :label="`${item.label}（${item.channelType}）`"
                  :value="item.value"
                />
              </el-select>
            </el-form-item>

            <el-form-item label="兜底视觉模型">
              <el-select v-model="visionDraft.fallback.model" class="settings-select" clearable placeholder="可留空">
                <el-option
                  v-for="item in fallbackModelOptions"
                  :key="item.value"
                  :label="item.label"
                  :value="item.value"
                />
              </el-select>
            </el-form-item>

            <el-alert
              v-if="!fallbackEmpty && !hasModelCapability(visionState, visionDraft.fallback)"
              class="settings-alert"
              type="info"
              :title="fallbackHint"
              show-icon
              :closable="false"
            />

            <div v-if="visionState.configured" class="settings-meta">
              <el-descriptions :column="1" border>
                <el-descriptions-item label="当前生效的主">
                  <el-tag :type="primaryStatus.type">{{ primaryStatus.text }}</el-tag>
                </el-descriptions-item>
                <el-descriptions-item label="当前生效的兜底">
                  <el-tag :type="fallbackStatus.type">{{ fallbackStatus.text }}</el-tag>
                </el-descriptions-item>
              </el-descriptions>
            </div>

            <el-alert
              v-if="visionValidation"
              class="settings-alert"
              type="warning"
              :title="visionValidation"
              show-icon
              :closable="false"
            />

            <div class="settings-actions">
              <el-button
                type="primary"
                :icon="Check"
                :loading="visionSaving"
                :disabled="!visionCanSave"
                @click="saveVisionTransfer"
              >
                保存
              </el-button>
              <el-button
                v-if="visionState.configured"
                :icon="Delete"
                :loading="visionDeleting"
                @click="deleteVisionTransfer"
              >
                清除配置
              </el-button>
            </div>
          </el-form>
        </div>
      </el-tab-pane>
    </el-tabs>
  </div>
</template>

<script setup>
import { computed, onMounted, reactive, ref } from "vue";
import { ElMessage } from "element-plus/es/components/message/index.mjs";
import { Check, Delete, Refresh, RefreshRight } from "@element-plus/icons-vue";
import { isTauriRuntime, restartBackend } from "./tauriBackend";
import {
  applyCandidates,
  applyServerSettings,
  canSave,
  channelOptions,
  createVisionTransferState,
  hasCandidates,
  hasModelCapability,
  modelOptions,
  selectChannel,
  statusSummary,
  toSaveRequest,
  validationMessage
} from "./visionTransferState";

const props = defineProps({
  api: { type: Function, required: true },
  isSuperadmin: { type: Boolean, default: false }
});

const accessModeOptions = [
  { label: "仅本地访问", value: "localhost" },
  { label: "局域网访问", value: "lan" }
];

const activeSettingsTab = ref(props.isSuperadmin ? "service" : "vision");
const loading = ref(false);
const saving = ref(false);
const restarting = ref(false);
const restartRequired = ref(false);
const settings = ref(null);
const tauriRuntime = isTauriRuntime();
const proxyLoading = ref(false);
const proxySaving = ref(false);
const proxyDraft = reactive({
  intercept_probe_requests: false
});
const draft = reactive({
  access_mode: "localhost",
  port: 18080
});

const bindHostLabel = computed(() => {
  const bindHost = settings.value?.bind_host || "127.0.0.1";
  return bindHost === "0.0.0.0" ? "0.0.0.0（局域网访问）" : "127.0.0.1（仅本地访问）";
});

const visionLoading = ref(false);
const visionSaving = ref(false);
const visionDeleting = ref(false);
const visionOwner = ref("");
const ownerOptions = ref([]);
const visionState = reactive(createVisionTransferState());
const visionDraft = visionState.draft;

const visionChannelOptions = computed(() => channelOptions(visionState));
const primaryModelOptions = computed(() => modelOptions(visionState, visionDraft.primary.channelId));
const fallbackModelOptions = computed(() => modelOptions(visionState, visionDraft.fallback.channelId));
const visionHasCandidates = computed(() => hasCandidates(visionState));
const visionValidation = computed(() => validationMessage(visionState));
const visionCanSave = computed(() => canSave(visionState));
const primaryStatus = computed(() => statusSummary(visionState.server.primary));
const fallbackStatus = computed(() => statusSummary(visionState.server.fallback));
const fallbackEmpty = computed(
  () => !visionDraft.fallback.channelId && !visionDraft.fallback.model
);
const fallbackHint = computed(() =>
  hasModelCapability(visionState, visionDraft.fallback)
    ? "兜底命中不支持图片的模型：主视觉渠道失败时，带图片的请求会直接失败"
    : "未配置兜底：主视觉渠道失败时，带图片的请求会直接失败"
);
const candidateEmptyHint = computed(() =>
  props.isSuperadmin
    ? "该用户名下没有已启用且标注支持图片的模型，请先到「渠道配置」添加视觉渠道，并在「模型信息」里标注 supports_image"
    : "你名下没有已启用且标注支持图片的模型，请先在「渠道配置」添加视觉渠道，或联系管理员标注模型的图片能力"
);

function onChannelChange(group, value) {
  selectChannel(visionState, group, value);
}

async function loadSettings() {
  loading.value = true;
  try {
    const data = await props.api("/system-settings");
    assignSettings(data);
  } catch (error) {
    ElMessage.error(error.message);
  } finally {
    loading.value = false;
  }
}

async function loadProxySettings() {
  proxyLoading.value = true;
  try {
    const data = await props.api("/system-settings/proxy-settings");
    applyProxySettings(data);
  } catch (error) {
    ElMessage.error(error.message);
  } finally {
    proxyLoading.value = false;
  }
}

async function saveSettings() {
  saving.value = true;
  try {
    const data = await props.api("/system-settings", {
      method: "PUT",
      body: JSON.stringify({
        access_mode: draft.access_mode,
        port: Number(draft.port)
      })
    });
    assignSettings(data);
    restartRequired.value = data.restart_required === true;
    ElMessage.success(restartRequired.value ? "配置已保存，重启后生效" : "配置已保存");
  } catch (error) {
    ElMessage.error(error.message);
  } finally {
    saving.value = false;
  }
}

async function saveProxySettings() {
  proxySaving.value = true;
  try {
    const data = await props.api("/system-settings/proxy-settings", {
      method: "PUT",
      body: JSON.stringify({
        key: "intercept_probe_requests",
        value: String(proxyDraft.intercept_probe_requests === true)
      })
    });
    applyProxySettings(data);
    ElMessage.success("探测设置已保存");
  } catch (error) {
    ElMessage.error(error.message);
  } finally {
    proxySaving.value = false;
  }
}

function applyProxySettings(data) {
  const settingsMap = data?.settings || {};
  proxyDraft.intercept_probe_requests = settingsMap.intercept_probe_requests === "true";
}

async function restartService() {
  restarting.value = true;
  try {
    const result = await restartBackend();
    window.location.href = result?.admin_url || settings.value?.admin_url || "/admin/";
  } catch (error) {
    ElMessage.error(error.message);
  } finally {
    restarting.value = false;
  }
}

function assignSettings(data) {
  settings.value = data;
  draft.access_mode = data?.access_mode || "localhost";
  draft.port = Number(data?.port || 18080);
  restartRequired.value = data?.restart_required === true;
}

function ownerQuery() {
  return visionOwner.value ? `?owner_username=${encodeURIComponent(visionOwner.value)}` : "";
}

async function loadOwnerOptions() {
  if (!props.isSuperadmin) {
    return;
  }

  try {
    const data = await props.api("/users/options");
    ownerOptions.value = (Array.isArray(data) ? data : []).map((item) => ({
      value: item.id,
      label: item.name ?? String(item.id)
    }));
  } catch (error) {
    ElMessage.error(error.message);
  }
}

async function loadVisionTransfer() {
  visionLoading.value = true;
  try {
    const [settingsData, candidatesData] = await Promise.all([
      props.api(`/system-settings/vision-transfer${ownerQuery()}`),
      props.api(`/system-settings/vision-transfer/candidates${ownerQuery()}`)
    ]);
    applyCandidates(visionState, candidatesData?.candidates);
    applyServerSettings(visionState, settingsData);
    visionOwner.value = settingsData?.owner_username || visionOwner.value;
  } catch (error) {
    ElMessage.error(error.message);
  } finally {
    visionLoading.value = false;
  }
}

async function saveVisionTransfer() {
  visionSaving.value = true;
  try {
    const data = await props.api("/system-settings/vision-transfer", {
      method: "PUT",
      body: JSON.stringify(toSaveRequest(visionState, visionOwner.value))
    });
    applyServerSettings(visionState, data);
    ElMessage.success("配置已保存，立即生效");
  } catch (error) {
    ElMessage.error(error.message);
  } finally {
    visionSaving.value = false;
  }
}

async function deleteVisionTransfer() {
  visionDeleting.value = true;
  try {
    await props.api(`/system-settings/vision-transfer${ownerQuery()}`, { method: "DELETE" });
    await loadVisionTransfer();
    ElMessage.success("配置已清除");
  } catch (error) {
    ElMessage.error(error.message);
  } finally {
    visionDeleting.value = false;
  }
}

onMounted(async () => {
  // 全局项仍然只有 superadmin 能读,普通 user 不发这个请求,避免一进页面就弹 403。
  if (props.isSuperadmin) {
    await Promise.all([loadSettings(), loadProxySettings(), loadOwnerOptions()]);
  }

  await loadVisionTransfer();
});
</script>
