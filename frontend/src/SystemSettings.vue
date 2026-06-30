<template>
  <div class="section-scroll">
    <div class="toolbar">
      <div>
        <h2>系统设置</h2>
        <div class="text-muted">后端监听配置保存后需要重启服务生效</div>
      </div>
      <div class="toolbar-actions">
        <el-button :icon="Refresh" :loading="loading" @click="loadSettings">刷新</el-button>
      </div>
    </div>

    <el-form v-loading="loading" class="settings-form" label-position="top">
      <el-form-item label="访问范围">
        <el-segmented v-model="draft.access_mode" :options="accessModeOptions" />
      </el-form-item>

      <el-alert
        v-if="draft.access_mode === 'lan'"
        class="settings-alert"
        type="warning"
        title="局域网访问会允许同一网络内的设备连接当前服务"
        show-icon
        :closable="false"
      />

      <el-form-item label="后端端口">
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
          <el-descriptions-item label="监听地址">{{ bindHostLabel }}</el-descriptions-item>
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
</template>

<script setup>
import { computed, onMounted, reactive, ref } from "vue";
import { ElMessage } from "element-plus/es/components/message/index.mjs";
import { Check, Refresh, RefreshRight } from "@element-plus/icons-vue";
import { isTauriRuntime, restartBackend } from "./tauriBackend";

const props = defineProps({
  api: { type: Function, required: true }
});

const accessModeOptions = [
  { label: "仅本地访问", value: "localhost" },
  { label: "局域网访问", value: "lan" }
];

const loading = ref(false);
const saving = ref(false);
const restarting = ref(false);
const restartRequired = ref(false);
const settings = ref(null);
const tauriRuntime = isTauriRuntime();
const draft = reactive({
  access_mode: "localhost",
  port: 18080
});

const bindHostLabel = computed(() => {
  const bindHost = settings.value?.bind_host || "127.0.0.1";
  return bindHost === "0.0.0.0" ? "0.0.0.0（局域网访问）" : "127.0.0.1（仅本地访问）";
});

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

onMounted(() => loadSettings());
</script>
