<template>
  <div v-if="loadingSession" class="login-wrap">
    <el-empty description="正在加载管理台" />
  </div>

  <Login v-else-if="!authenticated" :api="api" @login="handleLogin" />

  <div v-else class="app-page">
    <el-container class="app-shell">
      <el-header class="app-header">
        <div class="header-brand">
          <button class="mobile-menu-button" type="button" aria-label="打开菜单" title="打开菜单" @click="mobileMenuVisible = true">
            <el-icon><Expand /></el-icon>
          </button>
          <strong>OpenCodex Proxy</strong>
        </div>
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
        <el-aside :width="menuCollapsed ? '72px' : '260px'" class="app-aside" :class="{ 'app-aside--collapsed': menuCollapsed }">
          <el-menu class="side-menu" :collapse="menuCollapsed" :default-active="activeTab" @select="handleMenuSelect">
            <el-menu-item v-for="item in visibleMenuItems" :key="item.index" :index="item.index">
              <el-icon><component :is="item.icon" /></el-icon>
              <span>{{ item.label }}</span>
            </el-menu-item>
          </el-menu>
          <button
            class="menu-collapse-button"
            type="button"
            :aria-label="menuCollapsed ? '展开菜单' : '收起菜单'"
            :title="menuCollapsed ? '展开菜单' : '收起菜单'"
            @click="menuCollapsed = !menuCollapsed"
          >
            <el-icon>
              <DArrowRight v-if="menuCollapsed" />
              <DArrowLeft v-else />
            </el-icon>
            <span v-if="!menuCollapsed">收起</span>
          </button>
        </el-aside>

        <el-main class="main-content">
          <div class="content-panel">
            <section v-if="activeTab === 'dashboard'">
              <div class="section-scroll">
                <Dashboard :api="api" :active="activeTab === 'dashboard'" />
              </div>
            </section>
            <section v-if="activeTab === 'channels'">
              <Channels :api="api"  />
            </section>
            <section v-if="activeTab === 'api-keys'">
              <AccessKeys :api="api" :is-superadmin="isSuperadmin" :users="usersData"  />
            </section>
            <section v-if="isSuperadmin && activeTab === 'users'">
              <Users :api="api" :current-user="currentUser"  @users-loaded="onUsersLoaded" />
            </section>
            <section v-if="isSuperadmin && activeTab === 'web-search'">
              <WebSearch :api="api"  />
            </section>
            <section v-if="activeTab === 'logs'">
              <Logs :api="api" :is-superadmin="isSuperadmin" :active="activeTab === 'logs'" />
            </section>
          </div>
        </el-main>
      </el-container>
    </el-container>

    <el-drawer
      v-model="mobileMenuVisible"
      title="菜单"
      direction="ltr"
      size="280px"
      custom-class="mobile-menu-drawer"
    >
      <el-menu class="mobile-drawer-menu" :default-active="activeTab" @select="handleMobileMenuSelect">
        <el-menu-item v-for="item in visibleMenuItems" :key="item.index" :index="item.index">
          <el-icon><component :is="item.icon" /></el-icon>
          <span>{{ item.label }}</span>
        </el-menu-item>
      </el-menu>
    </el-drawer>
  </div>
</template>

<script setup>
import { ref, computed, onMounted, defineAsyncComponent } from "vue";
import {
  Connection,
  DArrowLeft,
  DArrowRight,
  DataLine,
  Expand,
  Key,
  Search,
  SwitchButton,
  Tickets,
  User
} from "@element-plus/icons-vue";
const Dashboard = defineAsyncComponent(() => import("./Dashboard.vue"));
const Login = defineAsyncComponent(() => import("./Login.vue"));
const Channels = defineAsyncComponent(() => import("./Channels.vue"));
const AccessKeys = defineAsyncComponent(() => import("./AccessKeys.vue"));
const Users = defineAsyncComponent(() => import("./Users.vue"));
const WebSearch = defineAsyncComponent(() => import("./WebSearch.vue"));
const Logs = defineAsyncComponent(() => import("./Logs.vue"));

const activeTab = ref("dashboard");
const authenticated = ref(false);
const loadingSession = ref(true);
const currentUser = ref(null);
const menuCollapsed = ref(false);
const mobileMenuVisible = ref(false);

// Users list shared with AccessKeys for owner_username dropdown
const usersData = ref([]);

const isSuperadmin = computed(() => currentUser.value?.role === "superadmin");
const visibleMenuItems = computed(() =>
  menuItems.filter((item) => !item.superadminOnly || isSuperadmin.value)
);

const menuItems = [
  { index: "dashboard", label: "仪表盘", icon: DataLine },
  { index: "channels", label: "渠道配置", icon: Connection },
  { index: "api-keys", label: "API Key 管理", icon: Key },
  { index: "users", label: "用户管理", icon: User, superadminOnly: true },
  { index: "web-search", label: "Web Search 模拟", icon: Search, superadminOnly: true },
  { index: "logs", label: "请求日志", icon: Tickets }
];

function onUsersLoaded(users) {
  usersData.value = users;
}

// --- API helper ---

const devApiPrefix = import.meta.env.DEV ? import.meta.env.BASE_URL.replace(/\/$/, "") : "";

async function api(url, options = {}) {
  const response = await fetch(`${devApiPrefix}${url}`, {
    headers: { "Content-Type": "application/json", ...(options.headers || {}) },
    ...options
  });
  const contentType = response.headers.get("content-type") || "";
  const data = contentType.includes("application/json") ? await response.json() : await response.text();
  if (!response.ok) {
    const message = typeof data === "string"
      ? data
      : data.ErrorMsg || data.error?.message || data.error || response.statusText;
    throw new Error(message);
  }
  if (data && typeof data === "object" && typeof data.succeeded === "boolean" && "ErrorCode" in data && "ErrorMsg" in data) {
    return "Data" in data ? data.Data : data;
  }
  return data;
}

// --- Auth ---

async function checkSession() {
  loadingSession.value = true;
  try {
    const data = await api("/session");
    setAuthenticatedUser(data);
  } finally {
    loadingSession.value = false;
  }
}

function handleLogin(data) {
  setAuthenticatedUser(data);
  activeTab.value = "dashboard";
}

function handleMenuSelect(tab) {
  activeTab.value = tab;
}

function handleMobileMenuSelect(tab) {
  handleMenuSelect(tab);
  mobileMenuVisible.value = false;
}

async function logout() {
  await api("/logout", { method: "POST", body: "{}" });
  authenticated.value = false;
  currentUser.value = null;
  activeTab.value = "dashboard";
}

function setAuthenticatedUser(data) {
  authenticated.value = data.authenticated === true;
  currentUser.value = authenticated.value ? data.user || null : null;
  ensureAllowedActiveTab();
}

function ensureAllowedActiveTab() {
  if (!isSuperadmin.value && ["users", "web-search"].includes(activeTab.value)) {
    activeTab.value = "dashboard";
  }
}

onMounted(async () => {
  await checkSession();
});
</script>
