<template>
  <div class="login-wrap">
    <el-card class="login-card" shadow="never">
      <template #header>
        <div>
          <strong>OpenCodex 管理台</strong>
          <div class="text-muted">请输入用户名和密码</div>
        </div>
      </template>
      <el-form label-position="top" @submit.prevent="handleLogin">
        <el-form-item label="用户名">
          <el-input
            v-model="username"
            autocomplete="username"
            @keyup.enter="handleLogin"
          />
        </el-form-item>
        <el-form-item label="密码">
          <el-input
            v-model="password"
            type="password"
            show-password
            autocomplete="current-password"
            @keyup.enter="handleLogin"
          />
        </el-form-item>
        <el-button type="primary" class="full-width" :loading="loading" @click="handleLogin">
          登录
        </el-button>
      </el-form>
    </el-card>
  </div>
</template>

<script setup>
import { ref } from "vue";
import { ElMessage } from "element-plus/es/components/message/index.mjs";

const props = defineProps({
  api: { type: Function, required: true }
});

const emit = defineEmits(["login"]);

const username = ref("");
const password = ref("");
const loading = ref(false);

async function handleLogin() {
  loading.value = true;
  try {
    const data = await props.api("/login", {
      method: "POST",
      headers: { "Content-Type": "application/x-www-form-urlencoded" },
      body: new URLSearchParams({ username: username.value, password: password.value })
    });
    password.value = "";
    emit("login", data);
  } catch (error) {
    ElMessage.error(error.message);
  } finally {
    loading.value = false;
  }
}
</script>
