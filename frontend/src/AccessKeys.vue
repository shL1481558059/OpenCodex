<template>
  <div>
    <div class="toolbar">
      <div>
        <h2>API Key 管理</h2>
        <div class="text-muted">用于调用 /v1/* 代理接口，可在列表复制完整 Key</div>
      </div>
      <div class="toolbar-actions">
        <el-button :icon="Refresh" @click="loadAccessKeys">刷新</el-button>
        <el-button type="primary" :icon="Plus" @click="openAccessKeyDialog()">新增 API Key</el-button>
      </div>
    </div>

    <el-row :gutter="12">
      <el-col :span="8">
        <el-statistic title="Key 总数" :value="accessKeys.length" />
      </el-col>
      <el-col :span="8">
        <el-statistic title="启用 Key" :value="enabledAccessKeyCount" />
      </el-col>
      <el-col :span="8">
        <el-statistic title="最近使用" :value="lastAccessKeyUsedLabel" />
      </el-col>
    </el-row>

    <div class="table-area">
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

    <!-- 新增 API Key Dialog -->
    <el-dialog v-model="accessKeyDialogVisible" title="新增 API Key" width="560px" @closed="createdAccessKey = null">
      <el-form label-position="top" :model="accessKeyDraft">
        <el-form-item v-if="isSuperadmin" label="归属用户">
          <el-select v-model="accessKeyDraft.owner_username" class="full-width" filterable>
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
import { ElMessage } from "element-plus";
import { CopyDocument, Delete, Plus, Refresh } from "@element-plus/icons-vue";


const props = defineProps({
  api: { type: Function, required: true },
  isSuperadmin: { type: Boolean, default: false },
  users: { type: Array, default: () => [] },
});

onMounted(() => loadAccessKeys());



const accessKeysLoading = ref(false);
const accessKeyDialogVisible = ref(false);
const accessKeySaving = ref(false);
const createdAccessKey = ref(null);
const accessKeyDraft = reactive({ owner_username: "", name: "" });
const accessKeys = ref([]);

const enabledAccessKeyCount = computed(() => accessKeys.value.filter((k) => k.enabled !== false).length);
const lastAccessKeyUsedLabel = computed(() => {
  const timestamps = accessKeys.value
    .map((k) => Number(k.last_used_at || 0))
    .filter((v) => v > 0)
    .sort((a, b) => b - a);
  return timestamps.length ? formatTime(timestamps[0]) : "-";
});

onMounted(() => loadAccessKeys());
const enabledUsers = computed(() => props.users.filter((u) => u.enabled !== false));

async function loadAccessKeys() {
  accessKeysLoading.value = true;
  try {
    const data = await props.api("/admin/api/access-keys");
    accessKeys.value = Array.isArray(data.keys) ? data.keys : [];
  } catch (error) {
    ElMessage.error(error.message);
  } finally {
    accessKeysLoading.value = false;
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
    const data = await props.api("/admin/api/access-keys", {
      method: "POST",
      body: JSON.stringify(payload)
    });
    createdAccessKey.value = data;
    await loadAccessKeys();
  } catch (error) {
    ElMessage.error(error.message);
  } finally {
    accessKeySaving.value = false;
  }
}

async function toggleAccessKey(row) {
  try {
    await props.api(`/admin/api/access-keys/${row.id}`, {
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
    await props.api(`/admin/api/access-keys/${row.id}`, { method: "DELETE" });
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

</script>
