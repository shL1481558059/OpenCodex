<template>
  <div>
    <div class="toolbar">
      <div>
        <h2>用户管理</h2>
        <div class="text-muted">普通用户由超级管理员创建、停用和删除</div>
      </div>
      <div class="toolbar-actions">
        <el-button :icon="Refresh" @click="loadUsers">刷新</el-button>
        <el-button type="primary" :icon="Plus" @click="openUserDialog()">新增用户</el-button>
      </div>
    </div>

    <div class="table-area">
      <el-table
        v-loading="usersLoading"
        :data="users"
        row-key="username"
        style="width: 100%"
        empty-text="暂无用户"
      >
        <el-table-column prop="username" label="用户名" min-width="160" show-overflow-tooltip />
        <el-table-column label="角色" width="130">
          <template #default="{ row }">
            <el-tag :type="row.role === 'superadmin' ? 'success' : 'info'">
              {{ row.role === "superadmin" ? "超级管理员" : "普通用户" }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column label="状态" width="100">
          <template #default="{ row }">
            <el-tag :type="row.enabled === false ? 'warning' : 'success'">
              {{ row.enabled === false ? "停用" : "启用" }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column label="创建时间" width="180">
          <template #default="{ row }">{{ formatTime(row.created_at) || "-" }}</template>
        </el-table-column>
        <el-table-column label="操作" width="340" align="center">
          <template #default="{ row }">
            <div class="inline-actions channel-table-actions">
              <el-button
                size="small"
                :disabled="isProtectedSuperadmin(row)"
                :type="row.enabled === false ? 'success' : 'warning'"
                plain
                @click="toggleUser(row)"
              >
                {{ row.enabled === false ? "启用" : "停用" }}
              </el-button>
              <el-button
                size="small"
                :disabled="row.role === 'superadmin'"
                :icon="Edit"
                @click="openResetPasswordDialog(row)"
              >
                重置密码
              </el-button>
              <el-popconfirm
                :title="`删除用户 ${row.username}？`"
                @confirm="deleteUser(row)"
              >
                <template #reference>
                  <el-button
                    size="small"
                    type="danger"
                    :icon="Delete"
                    :disabled="isCurrentUser(row)"
                  >
                    删除
                  </el-button>
                </template>
              </el-popconfirm>
            </div>
          </template>
        </el-table-column>
      </el-table>
    </div>

    <!-- 新增用户 Dialog -->
    <el-dialog v-model="userDialogVisible" title="新增用户" width="520px">
      <el-form label-position="top" :model="userDraft">
        <el-form-item label="用户名">
          <el-input v-model="userDraft.username" autocomplete="off" />
        </el-form-item>
        <el-form-item label="密码">
          <el-input v-model="userDraft.password" type="password" show-password autocomplete="new-password" />
        </el-form-item>
        <el-form-item label="启用">
          <el-switch v-model="userDraft.enabled" />
        </el-form-item>
      </el-form>

      <template #footer>
        <div class="drawer-footer">
          <el-button @click="userDialogVisible = false">取消</el-button>
          <el-button type="primary" :loading="userSaving" @click="createUser">创建用户</el-button>
        </div>
      </template>
    </el-dialog>

    <!-- 重置密码 Dialog -->
    <el-dialog v-model="resetPasswordDialogVisible" title="重置密码" width="520px">
      <el-form label-position="top" :model="resetPasswordDraft">
        <el-form-item label="用户名">
          <el-input v-model="resetPasswordDraft.username" disabled />
        </el-form-item>
        <el-form-item label="新密码">
          <el-input v-model="resetPasswordDraft.password" type="password" show-password autocomplete="new-password" />
        </el-form-item>
      </el-form>

      <template #footer>
        <div class="drawer-footer">
          <el-button @click="resetPasswordDialogVisible = false">取消</el-button>
          <el-button type="primary" :loading="resetPasswordSaving" @click="resetUserPassword">保存</el-button>
        </div>
      </template>
    </el-dialog>
  </div>
</template>

<script setup>
import { ref, reactive } from "vue";
import { ElMessage } from "element-plus";
import { Delete, Edit, Plus, Refresh } from "@element-plus/icons-vue";

const props = defineProps({
  api: { type: Function, required: true },
  currentUser: { type: Object, default: null }
});

const usersLoading = ref(false);
const userDialogVisible = ref(false);
const userSaving = ref(false);
const userDraft = reactive({ username: "", password: "", enabled: true });
const resetPasswordDialogVisible = ref(false);
const resetPasswordSaving = ref(false);
const resetPasswordDraft = reactive({ username: "", password: "" });
const users = ref([]);

async function loadUsers() {
  usersLoading.value = true;
  try {
    const data = await props.api("/admin/api/users");
    users.value = Array.isArray(data.users) ? data.users : [];
  } catch (error) {
    ElMessage.error(error.message);
  } finally {
    usersLoading.value = false;
  }
}

function openUserDialog() {
  userDraft.username = "";
  userDraft.password = "";
  userDraft.enabled = true;
  userDialogVisible.value = true;
}

async function createUser() {
  userSaving.value = true;
  try {
    await props.api("/admin/api/users", {
      method: "POST",
      body: JSON.stringify(userDraft)
    });
    userDialogVisible.value = false;
    await loadUsers();
  } catch (error) {
    ElMessage.error(error.message);
  } finally {
    userSaving.value = false;
  }
}

async function toggleUser(row) {
  try {
    await props.api(`/admin/api/users/${row.username}`, {
      method: "PATCH",
      body: JSON.stringify({ enabled: row.enabled === false })
    });
    await loadUsers();
  } catch (error) {
    ElMessage.error(error.message);
  }
}

function openResetPasswordDialog(row) {
  resetPasswordDraft.username = row.username;
  resetPasswordDraft.password = "";
  resetPasswordDialogVisible.value = true;
}

async function resetUserPassword() {
  resetPasswordSaving.value = true;
  try {
    await props.api(`/admin/api/users/${resetPasswordDraft.username}/password`, {
      method: "PUT",
      body: JSON.stringify({ password: resetPasswordDraft.password })
    });
    resetPasswordDialogVisible.value = false;
    ElMessage.success("密码已重置");
  } catch (error) {
    ElMessage.error(error.message);
  } finally {
    resetPasswordSaving.value = false;
  }
}

function isProtectedSuperadmin(row) {
  return row.role === "superadmin";
}

function isCurrentUser(row) {
  return props.currentUser?.username === row.username;
}

async function deleteUser(row) {
  try {
    await props.api(`/admin/api/users/${row.username}`, { method: "DELETE" });
    await loadUsers();
  } catch (error) {
    ElMessage.error(error.message);
  }
}

function formatTime(timestamp) {
  if (!timestamp) return "";
  return new Date(Number(timestamp) * 1000).toLocaleString();
}

defineExpose({ loadUsers, users });
</script>

