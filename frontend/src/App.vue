<template>
  <div v-if="loadingSession" class="login-wrap">
    <el-empty description="正在加载管理台" />
  </div>

  <Login v-else-if="!authenticated" :api="api" @login="handleLogin" />

  <div v-else class="app-page">
    <el-container class="app-shell">
      <el-header class="app-header">
        <strong>OpenCodex Proxy</strong>
        <div class="header-actions">
          <div v-if="currentUser" class="current-user">
            <span>{{ currentUser.username }}</span>
            <el-tag size="small" :type="isSuperadmin ? 'success' : 'info'">
              {{ isSuperadmin ? "超级管理员" : "普通用户" }}
            </el-tag>
          </div>
          <el-button :icon="SwitchButton" @click="logout">退出</el-button>
        </div>
      </el-header>

      <el-container class="app-body">
        <el-aside width="260px" class="app-aside">
          <el-menu class="side-menu" :default-active="activeTab" @select="activeTab = $event">
            <el-menu-item index="dashboard">
              <el-icon><DataLine /></el-icon>
              <span>仪表盘</span>
            </el-menu-item>
            <el-menu-item index="channels">
              <el-icon><Connection /></el-icon>
              <span>渠道配置</span>
            </el-menu-item>
            <el-menu-item index="api-keys">
              <el-icon><Key /></el-icon>
              <span>API Key 管理</span>
            </el-menu-item>
            <el-menu-item v-if="isSuperadmin" index="users">
              <el-icon><User /></el-icon>
              <span>用户管理</span>
            </el-menu-item>
            <el-menu-item v-if="isSuperadmin" index="web-search">
              <el-icon><Search /></el-icon>
              <span>Web Search 模拟</span>
            </el-menu-item>
            <el-menu-item index="logs">
              <el-icon><Tickets /></el-icon>
              <span>请求日志</span>
            </el-menu-item>
          </el-menu>
        </el-aside>

        <el-main class="main-content">
          <div class="content-panel">
            <section v-show="activeTab === 'dashboard'">
              <div class="section-scroll">
                <Dashboard :api="api" :active="activeTab === 'dashboard'" />
              </div>
            </section>
            <section v-show="activeTab === 'channels'">
              <Channels ref="channelsRef" :api="api" />
            </section>
            <section v-show="activeTab === 'api-keys'">
              <AccessKeys ref="accessKeysRef" :api="api" :is-superadmin="isSuperadmin" :users="usersRefData" />
            </section>
            <section v-if="isSuperadmin" v-show="activeTab === 'users'">
              <Users ref="usersRef" :api="api" :current-user="currentUser" />
            </section>
            <section v-if="isSuperadmin" v-show="activeTab === 'web-search'">
              <WebSearch ref="webSearchRef" :api="api" />
            </section>
            <section v-show="activeTab === 'logs'">
              <Logs ref="logsRef" :api="api" :is-superadmin="isSuperadmin" :active="activeTab === 'logs'" />
            </section>
          </div>
        </el-main>
      </el-container>
    </el-container>
  </div>
</template>

<script setup>
import { ref, computed, nextTick, onMounted, onBeforeUnmount, defineAsyncComponent } from "vue";
import { ElMessage } from "element-plus";
import {
  Connection,
  DataLine,
  Key,
  Search,
  SwitchButton,
  Tickets,
  User
} from "@element-plus/icons-vue";
import Dashboard from "./Dashboard.vue";
import Login from "./Login.vue";

const Channels = defineAsyncComponent(() => import("./Channels.vue"));
const AccessKeys = defineAsyncComponent(() => import("./AccessKeys.vue"));
const Users = defineAsyncComponent(() => import("./Users.vue"));
const WebSearch = defineAsyncComponent(() => import("./WebSearch.vue"));
const Logs = defineAsyncComponent(() => import("./Logs.vue"));

const activeTab = ref("dashboard");
const authenticated = ref(false);
const loadingSession = ref(true);
const currentUser = ref(null);

// Refs to child components for calling exposed methods
const channelsRef = ref(null);
const accessKeysRef = ref(null);
const usersRef = ref(null);
const webSearchRef = ref(null);
const logsRef = ref(null);

// Shared data passed as props
const usersRefData = computed(() => usersRef.value?.users || []);

const isSuperadmin = computed(() => currentUser.value?.role === "superadmin");

// --- API helper ---

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

// --- Auth ---

async function checkSession() {
  loadingSession.value = true;
  try {
    const data = await api("/admin/api/session");
    setAuthenticatedUser(data);
    if (authenticated.value) {
      await loadInitialData();
    }
  } finally {
    loadingSession.value = false;
  }
}

function handleLogin(data) {
  setAuthenticatedUser(data);
  activeTab.value = "dashboard";
  // nextTick so async components have mounted before we call their exposed methods
  nextTick(() => loadInitialData());
}

async function logout() {
  await api("/admin/api/logout", { method: "POST", body: "{}" });
  authenticated.value = false;
  currentUser.value = null;
  activeTab.value = "dashboard";
}

function setAuthenticatedUser(data) {
  authenticated.value = data.authenticated === true;
  currentUser.value = authenticated.value ? data.user || null : null;
  ensureAllowedActiveTab();
}

// --- Init ---

let initialDataLoaded = false;

async function loadInitialData() {
  if (initialDataLoaded) return;
  initialDataLoaded = true;
  ensureAllowedActiveTab();
  // nextTick ensures async components are mounted
  await nextTick();
  const tasks = [
    channelsRef.value?.loadConfig(),
    accessKeysRef.value?.loadAccessKeys(),
    logsRef.value?.loadLogs()
  ];
  if (isSuperadmin.value) {
    tasks.push(
      usersRef.value?.loadUsers(),
      webSearchRef.value?.loadWebSearch()
    );
  }
  await Promise.all(tasks.filter(Boolean));
}

function ensureAllowedActiveTab() {
  if (!isSuperadmin.value && ["users", "web-search"].includes(activeTab.value)) {
    activeTab.value = "dashboard";
  }
}

onMounted(async () => {
  await checkSession();
});

onBeforeUnmount(() => {
  // stop log auto-refresh if logs component is mounted
  if (logsRef.value) {
    logsRef.value.setLogAutoRefreshSeconds?.(0);
  }
});
</script>

