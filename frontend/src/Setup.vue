<template>
  <div class="login-wrap">
    <el-card class="login-card setup-card" shadow="never">
      <template #header>
        <div>
          <strong>OpenCodex 初始化</strong>
          <div class="text-muted">创建超级管理员并设置本机服务</div>
        </div>
      </template>

      <el-form label-position="top" @submit.prevent="handleSetup">
        <el-form-item label="用户名">
          <el-input v-model="username" autocomplete="username" @keyup.enter="handleSetup" />
        </el-form-item>

        <el-form-item label="密码">
          <el-input
            v-model="password"
            type="password"
            show-password
            autocomplete="new-password"
            @keyup.enter="handleSetup"
          />
        </el-form-item>

        <el-form-item label="访问范围">
          <el-segmented v-model="accessMode" :options="accessModeOptions" class="full-width" />
        </el-form-item>

        <el-alert
          v-if="accessMode === 'lan'"
          class="setup-alert"
          type="warning"
          title="局域网访问会允许同一网络内的设备连接当前服务"
          show-icon
          :closable="false"
        />

        <el-form-item label="后端端口">
          <el-input-number
            v-model="port"
            class="full-width"
            :min="1024"
            :max="65535"
            :step="1"
            controls-position="right"
          />
        </el-form-item>

        <el-button type="primary" class="full-width" :loading="loading" @click="handleSetup">
          完成初始化
        </el-button>
      </el-form>
    </el-card>
  </div>
</template>

<script setup>
import { ref, watch } from "vue";
import { ElMessage } from "element-plus/es/components/message/index.mjs";
import { isTauriRuntime, restartBackend } from "./tauriBackend";

const props = defineProps({
  api: { type: Function, required: true },
  initialSettings: { type: Object, default: null }
});

const emit = defineEmits(["setup-complete"]);

const accessModeOptions = [
  { label: "仅本地访问", value: "localhost" },
  { label: "局域网访问", value: "lan" }
];

const username = ref("admin");
const password = ref("");
const accessMode = ref(props.initialSettings?.access_mode || "localhost");
const port = ref(Number(props.initialSettings?.port || 18080));
const loading = ref(false);

watch(
  () => props.initialSettings,
  (settings) => {
    accessMode.value = settings?.access_mode || "localhost";
    port.value = Number(settings?.port || 18080);
  }
);

async function handleSetup() {
  loading.value = true;
  try {
    const data = await props.api("/setup", {
      method: "POST",
      body: JSON.stringify({
        username: username.value,
        password: password.value,
        system_settings: {
          access_mode: accessMode.value,
          port: Number(port.value)
        }
      })
    });
    password.value = "";

    const settings = data.system_settings;
    if (settings?.restart_required && isTauriRuntime()) {
      const restartResult = await restartBackend();
      window.location.href = restartResult?.admin_url || settings.admin_url;
      return;
    }

    emit("setup-complete", data);
  } catch (error) {
    ElMessage.error(error.message);
  } finally {
    loading.value = false;
  }
}
</script>
