<template>
  <el-config-provider :locale="zhCn">
    <div v-if="loadingSession" class="login-wrap">
      <el-empty description="正在加载管理台" />
    </div>

    <div v-else-if="initError" class="login-wrap">
      <el-card class="login-card" shadow="never">
        <template #header>
          <strong>初始化失败</strong>
        </template>
        <el-alert type="error" :title="initError" show-icon :closable="false" />
        <el-button type="primary" class="full-width" style="margin-top: 16px" @click="initApp">重试</el-button>
      </el-card>
    </div>

    <Setup
      v-else-if="needsSetup"
      :api="api"
      :initial-settings="setupSettings"
      @setup-complete="handleSetupComplete"
    />

    <Login v-else-if="!authenticated" :api="api" @login="handleLogin" />

    <div v-else class="app-page">
      <el-container class="app-shell">
      <el-header class="app-header">
        <div class="header-brand">
          <button class="mobile-menu-button" type="button" aria-label="打开菜单" title="打开菜单" @click="mobileMenuVisible = true">
            <el-icon><Expand /></el-icon>
          </button>
          <div class="header-brand-logo">O</div>
          <span class="header-brand-title">OpenCodex Proxy</span>
        </div>
        <div class="header-actions">
          <div v-if="currentUser" class="current-user">
            <span>{{ currentUser.username }}</span>
            <el-tag size="small" :type="isSuperadmin ? 'success' : 'info'">
              {{ isSuperadmin ? "超级管理员" : "普通用户" }}
            </el-tag>
          </div>
          <el-dropdown trigger="click" @command="handleThemeCommand">
            <el-button class="theme-toggle-btn" size="default">
              <el-icon>
                <Sunny v-if="themeSetting === 'light'" />
                <Moon v-else-if="themeSetting === 'dark'" />
                <Monitor v-else />
              </el-icon>
              <span class="theme-label-text">{{ themeLabel }}</span>
            </el-button>
            <template #dropdown>
              <el-dropdown-menu>
                <el-dropdown-item command="light">
                  <span style="display: flex; align-items: center; justify-content: space-between; width: 100px;">
                    <span>浅色模式</span>
                    <el-icon v-if="themeSetting === 'light'"><Check /></el-icon>
                  </span>
                </el-dropdown-item>
                <el-dropdown-item command="dark">
                  <span style="display: flex; align-items: center; justify-content: space-between; width: 100px;">
                    <span>深色模式</span>
                    <el-icon v-if="themeSetting === 'dark'"><Check /></el-icon>
                  </span>
                </el-dropdown-item>
                <el-dropdown-item command="system">
                  <span style="display: flex; align-items: center; justify-content: space-between; width: 100px;">
                    <span>跟随系统</span>
                    <el-icon v-if="themeSetting === 'system'"><Check /></el-icon>
                  </span>
                </el-dropdown-item>
              </el-dropdown-menu>
            </template>
          </el-dropdown>
          <el-popconfirm
            title="确认退出当前账号登录？"
            confirm-button-text="退出登录"
            cancel-button-text="取消"
            confirm-button-type="danger"
            @confirm="logout"
          >
            <template #reference>
              <el-button class="logout-btn" :icon="SwitchButton" size="default">
                <span class="logout-text">退出</span>
              </el-button>
            </template>
          </el-popconfirm>
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
              <Channels :api="api" :is-superadmin="isSuperadmin" />
            </section>
            <section v-if="activeTab === 'api-keys'">
              <AccessKeys :api="api" :is-superadmin="isSuperadmin" />
            </section>
            <section v-if="isSuperadmin && activeTab === 'users'">
              <Users :api="api" :current-user="currentUser" />
            </section>
            <section v-if="isSuperadmin && activeTab === 'web-search'">
              <WebSearch :api="api"  />
            </section>
            <section v-if="isSuperadmin && activeTab === 'model-catalog'">
              <ModelCatalog :api="api" />
            </section>
            <section v-if="activeTab === 'system-settings'">
              <SystemSettings :api="api" :is-superadmin="isSuperadmin" />
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
     >
        <div v-if="currentUser" class="current-user" style="margin: 0 12px 12px; display: flex;">
          <span>{{ currentUser.username }}</span>
          <el-tag size="small" :type="isSuperadmin ? 'success' : 'info'">
            {{ isSuperadmin ? "超级管理员" : "普通用户" }}
          </el-tag>
        </div>
        <el-menu class="mobile-drawer-menu" :default-active="activeTab" @select="handleMobileMenuSelect">
          <el-menu-item v-for="item in visibleMenuItems" :key="item.index" :index="item.index">
            <el-icon><component :is="item.icon" /></el-icon>
            <span>{{ item.label }}</span>
          </el-menu-item>
        </el-menu>
      </el-drawer>
    </div>
  </el-config-provider>
</template>

<script setup>
import { ref, computed, onMounted, onUnmounted, defineAsyncComponent } from "vue";
import {
  Check,
  Connection,
  DArrowLeft,
  DArrowRight,
  DataLine,
  Expand,
  Key,
  Money,
  Monitor,
  Moon,
  Search,
  Setting,
  Sunny,
  SwitchButton,
  Tickets,
  User
} from "@element-plus/icons-vue";
import zhCn from "element-plus/es/locale/lang/zh-cn";
import { themeSetting, setTheme } from "./theme";

const Dashboard = defineAsyncComponent(() => import("./Dashboard.vue"));
const Setup = defineAsyncComponent(() => import("./Setup.vue"));
const Login = defineAsyncComponent(() => import("./Login.vue"));
const Channels = defineAsyncComponent(() => import("./Channels.vue"));
const AccessKeys = defineAsyncComponent(() => import("./AccessKeys.vue"));
const Users = defineAsyncComponent(() => import("./Users.vue"));
const WebSearch = defineAsyncComponent(() => import("./WebSearch.vue"));
const ModelCatalog = defineAsyncComponent(() => import("./ModelCatalog.vue"));
const SystemSettings = defineAsyncComponent(() => import("./SystemSettings.vue"));
const Logs = defineAsyncComponent(() => import("./Logs.vue"));

const activeTab = ref("dashboard");
const authenticated = ref(false);
const loadingSession = ref(true);
const needsSetup = ref(false);
const setupSettings = ref(null);
const initError = ref(null);
const currentUser = ref(null);
const menuCollapsed = ref(false);
const mobileMenuVisible = ref(false);

const isSuperadmin = computed(() => currentUser.value?.role === "superadmin");
const visibleMenuItems = computed(() =>
  menuItems.filter((item) => !item.superadminOnly || isSuperadmin.value)
);
const themeLabel = computed(() => {
  if (themeSetting.value === "light") return "浅色";
  if (themeSetting.value === "dark") return "深色";
  return "系统";
});

function handleThemeCommand(command) {
  setTheme(command);
}

const menuItems = [
  { index: "dashboard", label: "仪表盘", icon: DataLine },
  { index: "channels", label: "渠道配置", icon: Connection },
  { index: "api-keys", label: "API Key 管理", icon: Key },
  { index: "users", label: "用户管理", icon: User, superadminOnly: true },
  { index: "web-search", label: "Web Search", icon: Search, superadminOnly: true },
  { index: "model-catalog", label: "模型信息", icon: Money, superadminOnly: true },
  { index: "system-settings", label: "系统设置", icon: Setting },
  { index: "logs", label: "请求日志", icon: Tickets }
];

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
  const data = await api("/session");
  setAuthenticatedUser(data);
}

async function loadSetupStatus() {
  const data = await api("/setup/status");
  needsSetup.value = data.setup_required === true;
  setupSettings.value = data.system_settings || null;
}

function handleLogin(data) {
  setAuthenticatedUser(data);
  activeTab.value = "dashboard";
}

function handleSetupComplete(data) {
  needsSetup.value = false;
  setAuthenticatedUser(data.session);
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
  if (!isSuperadmin.value && ["users", "web-search", "model-catalog", "system-settings"].includes(activeTab.value)) {
    activeTab.value = "dashboard";
  }
}

onMounted(async () => {
  initApp();
});

// Close mobile drawer automatically when viewport grows past mobile breakpoint
let mobileBreakpoint = null;
function handleViewportChange(event) {
  if (!event.matches && mobileMenuVisible.value) {
    mobileMenuVisible.value = false;
  }
}
mobileBreakpoint = window.matchMedia("(max-width: 900px)");
mobileBreakpoint.addEventListener("change", handleViewportChange);
onUnmounted(() => {
  mobileBreakpoint?.removeEventListener("change", handleViewportChange);
});

async function initApp() {
  loadingSession.value = true;
  initError.value = null;
  try {
    await loadSetupStatus();
    if (!needsSetup.value) {
      await checkSession();
    }
  } catch (error) {
    initError.value = error?.message || String(error);
  } finally {
    loadingSession.value = false;
  }
  }
</script>
