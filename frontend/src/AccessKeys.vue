<template>
  <div class="access-keys-page">
    <div class="toolbar">
      <div>
        <h2>API Key 管理</h2>
        <div class="text-muted">用于调用 /v1/* 代理接口；完整 Key 仅创建时显示一次，列表展示脱敏 Key</div>
      </div>
      <div class="toolbar-actions">
        <el-button :icon="Refresh" @click="loadAccessKeys">刷新</el-button>
        <el-button :icon="Download" @click="exportAccessKeys">导出</el-button>
        <el-button :icon="Upload" @click="triggerImportAccessKeys">导入</el-button>
        <input
          ref="importAccessKeysInput"
          type="file"
          accept="application/json,.json"
          style="display:none"
          @change="handleImportAccessKeysFile"
        />
        <el-button type="primary" :icon="Plus" @click="openAccessKeyDialog()">新增 API Key</el-button>
      </div>
    </div>

    <el-row class="access-key-stats" :gutter="12">
      <el-col :span="8" :xs="12">
        <el-statistic title="Key 总数" :value="accessKeys.length" />
      </el-col>
      <el-col :span="8" :xs="12">
        <el-statistic title="启用 Key" :value="enabledAccessKeyCount" />
      </el-col>
      <el-col class="access-key-stats__recent" :span="8" :xs="24">
        <el-statistic
          title="最近使用"
          :value="lastAccessKeyUsedTimestamp"
          :formatter="formatLastAccessKeyUsed"
        />
      </el-col>
    </el-row>

    <div class="table-area desktop-access-key-list">
      <el-table
        v-loading="accessKeysLoading"
        :data="accessKeys"
        row-key="id"
        style="width: 100%; margin-top: 16px"
        empty-text="暂无 API Key"
      >
        <el-table-column v-if="isSuperadmin" prop="owner_username" label="用户" min-width="130" show-overflow-tooltip />
        <el-table-column prop="name" label="名称" min-width="160" show-overflow-tooltip />
        <el-table-column prop="masked_key" label="Key" min-width="220" show-overflow-tooltip />
        <el-table-column label="最近使用" width="180">
          <template #default="{ row }">{{ formatTime(row.last_used_at) || "-" }}</template>
        </el-table-column>
        <el-table-column label="状态" width="100">
          <template #default="{ row }">
            <el-tag :type="row.enabled === false ? 'warning' : 'success'">
              {{ row.enabled === false ? "停用" : "启用" }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column label="操作" width="300" align="center">
          <template #default="{ row }">
            <div class="inline-actions channel-table-actions">
              <el-button
                size="small"
                :icon="CopyDocument"
                :disabled="!row.key"
                @click="copyText(row.key)"
              >
                复制
              </el-button>
              <el-button
                size="small"
                :type="row.enabled === false ? 'success' : 'warning'"
                plain
                @click="toggleAccessKey(row)"
              >
                {{ row.enabled === false ? "启用" : "停用" }}
              </el-button>
              <el-popconfirm title="删除这个 API Key？" @confirm="deleteAccessKey(row)">
                <template #reference>
                  <el-button size="small" type="danger" :icon="Delete">删除</el-button>
                </template>
              </el-popconfirm>
            </div>
          </template>
        </el-table-column>
      </el-table>
    </div>

    <div v-loading="accessKeysLoading" class="mobile-card-list mobile-access-key-list">
      <el-empty
        v-if="!accessKeysLoading && accessKeys.length === 0"
        description="暂无 API Key"
        :image-size="64"
      />
      <article v-for="row in accessKeys" :key="row.id" class="mobile-data-card">
        <header class="mobile-data-card__header">
          <strong class="mobile-data-card__title">{{ row.name || "未命名 Key" }}</strong>
          <el-tag :type="row.enabled === false ? 'warning' : 'success'">
            {{ row.enabled === false ? "停用" : "启用" }}
          </el-tag>
        </header>

        <dl class="mobile-data-card__details">
          <div v-if="isSuperadmin">
            <dt>用户</dt>
            <dd>{{ row.owner_username || "-" }}</dd>
          </div>
          <div>
            <dt>Key</dt>
            <dd><code class="mobile-key-value">{{ row.masked_key || "-" }}</code></dd>
          </div>
          <div>
            <dt>最近使用</dt>
            <dd>{{ formatTime(row.last_used_at) || "-" }}</dd>
          </div>
        </dl>

        <div class="mobile-card-actions">
          <el-button
            :icon="CopyDocument"
            :disabled="!row.key"
            @click="copyText(row.key)"
          >
            复制
          </el-button>
          <el-button
            :type="row.enabled === false ? 'success' : 'warning'"
            plain
            @click="toggleAccessKey(row)"
          >
            {{ row.enabled === false ? "启用" : "停用" }}
          </el-button>
          <el-popconfirm title="删除这个 API Key？" @confirm="deleteAccessKey(row)">
            <template #reference>
              <el-button type="danger" :icon="Delete">删除</el-button>
            </template>
          </el-popconfirm>
        </div>
      </article>
    </div>

    <!-- 新增 API Key Dialog -->
    <el-dialog
      v-model="accessKeyDialogVisible"
      class="access-key-dialog"
      title="新增 API Key"
      width="min(560px, calc(100vw - 24px))"
      @closed="createdAccessKey = null"
    >
      <el-form label-position="top" :model="accessKeyDraft">
        <el-form-item v-if="isSuperadmin" label="归属用户">
          <el-select v-model="accessKeyDraft.owner_username" class="full-width" filterable :loading="usersLoading">
            <el-option
              v-for="user in enabledUsers"
              :key="user.username"
              :label="`${user.username} (${user.role === 'superadmin' ? '超级管理员' : '普通用户'})`"
              :value="user.username"
            />
          </el-select>
        </el-form-item>
        <el-form-item label="名称">
          <el-input v-model="accessKeyDraft.name" placeholder="例如：本机 Codex" autocomplete="off" />
        </el-form-item>
        <el-alert
          v-if="createdAccessKey"
          class="created-key-alert"
          type="success"
          :closable="false"
          title="API Key 已创建"
        >
          <div class="created-key-box">
            <code>{{ createdAccessKey.key }}</code>
            <el-button size="small" @click="copyText(createdAccessKey.key)">复制</el-button>
          </div>
        </el-alert>
      </el-form>

      <template #footer>
        <div class="drawer-footer">
          <el-button @click="accessKeyDialogVisible = false">关闭</el-button>
          <el-button type="primary" :loading="accessKeySaving" @click="createAccessKey">创建</el-button>
        </div>
      </template>
    </el-dialog>
  </div>
</template>

<script setup>
import { ref, reactive, computed, onMounted } from "vue";
import { ElMessage } from "element-plus/es/components/message/index.mjs";
import { CopyDocument, Delete, Download, Plus, Refresh, Upload } from "@element-plus/icons-vue";
const props = defineProps({
  api: { type: Function, required: true },
  isSuperadmin: { type: Boolean, default: false },
});
const accessKeysLoading = ref(false);
const usersLoading = ref(false);
const accessKeyDialogVisible = ref(false);
const accessKeySaving = ref(false);
const createdAccessKey = ref(null);
const accessKeyDraft = reactive({ owner_username: "", name: "" });
const accessKeys = ref([]);
const users = ref([]);

const enabledAccessKeyCount = computed(() => accessKeys.value.filter((k) => k.enabled !== false).length);
const lastAccessKeyUsedTimestamp = computed(() => {
  const timestamps = accessKeys.value
    .map((k) => Number(k.last_used_at || 0))
    .filter((v) => v > 0)
    .sort((a, b) => b - a);
  return timestamps[0] || 0;
});

const enabledUsers = computed(() => users.value.filter((u) => u.enabled !== false));

async function loadAccessKeys() {
  accessKeysLoading.value = true;
  try {
    const data = await props.api("/api-keys");
    accessKeys.value = Array.isArray(data.keys) ? data.keys : [];
  } catch (error) {
    ElMessage.error(error.message);
  } finally {
    accessKeysLoading.value = false;
  }
}

const importAccessKeysInput = ref(null);

function exportAccessKeys() {
  const keys = accessKeys.value.map((k) => ({
    owner_username: k.owner_username || "",
    name: k.name || "",
    key: k.key || "",
    enabled: k.enabled !== false
  }));
  const payload = {
    exported_at: new Date().toISOString(),
    type: "api_keys",
    keys
  };
  const blob = new Blob([JSON.stringify(payload, null, 2)], { type: "application/json" });
  const url = URL.createObjectURL(blob);
  const link = document.createElement("a");
  link.href = url;
  link.download = `api-keys-${Date.now()}.json`;
  document.body.appendChild(link);
  link.click();
  document.body.removeChild(link);
  URL.revokeObjectURL(url);
  ElMessage.success("API Key 已导出（含明文，请妥善保管）");
}

function triggerImportAccessKeys() {
  importAccessKeysInput.value?.click();
}

async function handleImportAccessKeysFile(event) {
  const file = event.target.files?.[0];
  if (!file) return;
  event.target.value = "";
  try {
    const text = await file.text();
    const parsed = JSON.parse(text);
    const keys = Array.isArray(parsed.keys) ? parsed.keys : Array.isArray(parsed) ? parsed : null;
    if (!keys) {
      ElMessage.error("导入文件格式不正确：缺少 keys 数组");
      return;
    }
    await props.api("/api-keys/import", {
      method: "POST",
      body: JSON.stringify({ keys })
    });
    ElMessage.success("API Key 导入成功");
    await loadAccessKeys();
  } catch (error) {
    ElMessage.error(error.message || "导入失败");
  }
}

async function loadUsers() {
  if (!props.isSuperadmin) {
    users.value = [];
    return;
  }

  usersLoading.value = true;
  try {
    const data = await props.api("/users");
    users.value = Array.isArray(data.users) ? data.users : [];
  } catch (error) {
    ElMessage.error(error.message);
  } finally {
    usersLoading.value = false;
  }
}

function openAccessKeyDialog() {
  accessKeyDraft.owner_username = "";
  accessKeyDraft.name = "";
  createdAccessKey.value = null;
  accessKeyDialogVisible.value = true;
}

async function createAccessKey() {
  accessKeySaving.value = true;
  try {
    const payload = { name: accessKeyDraft.name };
    if (props.isSuperadmin && accessKeyDraft.owner_username) {
      payload.owner_username = accessKeyDraft.owner_username;
    }
    const data = await props.api("/api-keys", {
      method: "POST",
      body: JSON.stringify(payload)
    });
    createdAccessKey.value = data.key || data;
    await loadAccessKeys();
  } catch (error) {
    ElMessage.error(error.message);
  } finally {
    accessKeySaving.value = false;
  }
}

async function toggleAccessKey(row) {
  try {
    await props.api(`/api-keys/${row.id}`, {
      method: "PATCH",
      body: JSON.stringify({ enabled: row.enabled === false })
    });
    await loadAccessKeys();
  } catch (error) {
    ElMessage.error(error.message);
  }
}

async function deleteAccessKey(row) {
  try {
    await props.api(`/api-keys/${row.id}`, { method: "DELETE" });
    await loadAccessKeys();
  } catch (error) {
    ElMessage.error(error.message);
  }
}

async function copyText(text) {
  try {
    await navigator.clipboard.writeText(text);
    ElMessage.success("已复制");
  } catch {
    const textarea = document.createElement("textarea");
    textarea.value = text;
    textarea.setAttribute("readonly", "");
    textarea.style.cssText = "position:fixed;top:0;left:0;opacity:0;pointer-events:none";
    document.body.appendChild(textarea);
    textarea.select();
    document.execCommand("copy");
    document.body.removeChild(textarea);
    ElMessage.success("已复制");
  }
}

function formatTime(timestamp) {
  if (!timestamp) return "";
  return new Date(Number(timestamp) * 1000).toLocaleString();
}

function formatLastAccessKeyUsed(value) {
  return value ? formatTime(value) : "-";
}

onMounted(() => {
  loadAccessKeys();
  loadUsers();
});
</script>

<style scoped>
.mobile-access-key-list {
  display: none;
}

@media (max-width: 600px) {
  .desktop-access-key-list {
    display: none;
  }

  .mobile-access-key-list {
    display: grid;
  }

  .access-key-stats {
    row-gap: 12px;
  }

  .access-key-stats :deep(.el-col) {
    min-width: 0;
  }

  .access-key-stats :deep(.el-statistic) {
    min-height: 78px;
    padding: 12px;
    border: 1px solid var(--el-border-color-lighter);
    border-radius: 6px;
  }

  .access-key-stats__recent :deep(.el-statistic__content) {
    font-size: 18px;
    overflow-wrap: anywhere;
  }

  .mobile-card-list {
    position: relative;
    gap: 12px;
    min-height: 96px;
    margin-top: 16px;
  }

  .mobile-data-card {
    min-width: 0;
    padding: 14px;
    border: 1px solid var(--el-border-color-lighter);
    border-radius: 6px;
    background: var(--el-bg-color);
  }

  .mobile-data-card__header {
    display: flex;
    align-items: flex-start;
    justify-content: space-between;
    gap: 12px;
  }

  .mobile-data-card__title {
    min-width: 0;
    font-size: 15px;
    line-height: 1.45;
    overflow-wrap: anywhere;
  }

  .mobile-data-card__header :deep(.el-tag) {
    flex: 0 0 auto;
  }

  .mobile-data-card__details {
    display: grid;
    gap: 8px;
    margin: 14px 0;
  }

  .mobile-data-card__details > div {
    display: grid;
    grid-template-columns: 72px minmax(0, 1fr);
    gap: 10px;
    align-items: start;
  }

  .mobile-data-card__details dt {
    color: var(--el-text-color-secondary);
  }

  .mobile-data-card__details dd {
    min-width: 0;
    margin: 0;
    text-align: right;
    overflow-wrap: anywhere;
  }

  .mobile-key-value {
    display: block;
    font-size: 12px;
    word-break: break-all;
  }

  .mobile-card-actions {
    display: grid;
    grid-template-columns: repeat(3, minmax(0, 1fr));
    gap: 8px;
  }

  .mobile-card-actions :deep(.el-button) {
    width: 100%;
    min-width: 0;
    min-height: 44px;
    margin-left: 0;
    padding-inline: 8px;
  }

  .access-keys-page .toolbar-actions {
    display: grid;
    grid-template-columns: repeat(2, minmax(0, 1fr));
    gap: 8px;
  }

  .access-keys-page .toolbar-actions :deep(.el-button) {
    width: 100%;
    min-height: 44px;
    margin-left: 0;
  }

  .access-keys-page .toolbar-actions :deep(.el-button:last-child) {
    grid-column: 1 / -1;
  }

  :global(.access-key-dialog) {
    max-height: calc(100dvh - 24px);
    margin: 12px auto !important;
    display: flex;
    flex-direction: column;
    overflow: hidden;
  }

  :global(.access-key-dialog .el-dialog__body) {
    min-height: 0;
    padding: 12px 16px 16px;
    overflow-y: auto;
  }

  :global(.access-key-dialog .el-dialog__footer) {
    flex: 0 0 auto;
    padding: 12px 16px calc(12px + env(safe-area-inset-bottom));
  }

  :global(.access-key-dialog .el-input__inner),
  :global(.access-key-dialog .el-select__input) {
    font-size: 16px;
  }

  :global(.access-key-dialog .drawer-footer .el-button) {
    min-height: 44px;
  }

  :global(.access-key-dialog .created-key-box code) {
    overflow-wrap: anywhere;
    word-break: break-all;
  }
}
</style>
