<template>
  <div v-if="loadingSession" class="login-wrap">
    <el-empty description="正在加载管理台" />
  </div>

  <div v-else-if="!authenticated" class="login-wrap">
    <el-card class="login-card" shadow="never">
      <template #header>
        <div>
          <strong>OpenCodex 管理台</strong>
          <div class="text-muted">请输入用户名和密码</div>
        </div>
      </template>
      <el-form label-position="top" @submit.prevent="login">
        <el-form-item label="用户名">
          <el-input
            v-model="loginUsername"
            autocomplete="username"
            @keyup.enter="login"
          />
        </el-form-item>
        <el-form-item label="密码">
          <el-input
            v-model="loginPassword"
            type="password"
            show-password
            autocomplete="current-password"
            @keyup.enter="login"
          />
        </el-form-item>
        <el-button type="primary" class="full-width" :loading="loginLoading" @click="login">
          登录
        </el-button>
      </el-form>
    </el-card>
  </div>

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
            <section v-show="activeTab === 'channels'">
              <div class="toolbar">
                <div>
                  <h2>渠道配置</h2>
                  <div class="text-muted">保存单个渠道后立即生效</div>
                </div>
                <div class="toolbar-actions">
                  <el-upload :show-file-list="false" accept="application/json" :before-upload="importConfig">
                    <template #trigger>
                      <el-button :icon="Upload">导入配置</el-button>
                    </template>
                  </el-upload>
                  <el-button :icon="Download" @click="exportConfig">导出配置</el-button>
                  <el-button :icon="Refresh" @click="loadConfig">刷新</el-button>
                  <el-button type="primary" :icon="Plus" @click="openChannelDrawer()">新增渠道</el-button>
                </div>
              </div>

              <el-row :gutter="12">
                <el-col :span="8">
                  <el-statistic title="渠道总数" :value="channels.length" />
                </el-col>
                <el-col :span="8">
                  <el-statistic title="启用渠道" :value="enabledChannelCount" />
                </el-col>
                <el-col :span="8">
                  <el-statistic title="模型映射" :value="modelMappingCount" />
                </el-col>
              </el-row>

              <el-table
                v-loading="configLoading"
                :data="channels"
                row-key="id"
                style="width: 100%; margin-top: 16px"
                empty-text="暂无渠道"
              >
                <el-table-column prop="id" label="ID" min-width="160" show-overflow-tooltip />
                <el-table-column prop="name" label="名称" min-width="140" show-overflow-tooltip />
                <el-table-column prop="type" label="服务类型" width="110">
                  <template #default="{ row }">
                    <el-tag>{{ row.type }}</el-tag>
                  </template>
                </el-table-column>
                <el-table-column prop="baseurl" label="Base URL" min-width="220" show-overflow-tooltip />
                <el-table-column label="模型映射" width="110">
                  <template #default="{ row }">{{ normalizeModels(row.models).length }}</template>
                </el-table-column>
                <el-table-column label="状态" width="100">
                  <template #default="{ row }">
                    <el-tag :type="row.enabled === false ? 'warning' : 'success'">
                      {{ row.enabled === false ? "停用" : "启用" }}
                    </el-tag>
                  </template>
                </el-table-column>
                <el-table-column label="操作" width="260" align="center">
                  <template #default="{ row, $index }">
                    <div class="inline-actions channel-table-actions">
                      <el-button size="small" type="primary" plain :icon="Connection" @click="openChannelTest(row)">
                        测试连接
                      </el-button>
                      <el-button size="small" :icon="Edit" @click="openChannelDrawer(row, $index)">编辑</el-button>
                      <el-popconfirm title="删除这个渠道？" @confirm="deleteChannel($index)">
                        <template #reference>
                          <el-button size="small" type="danger" :icon="Delete">删除</el-button>
                        </template>
                      </el-popconfirm>
                    </div>
                  </template>
                </el-table-column>
              </el-table>
            </section>

            <section v-show="activeTab === 'api-keys'">
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
            </section>

            <section v-if="isSuperadmin" v-show="activeTab === 'users'">
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
            </section>

            <section v-if="isSuperadmin" v-show="activeTab === 'web-search'">
              <div class="toolbar">
                <div>
                  <h2>Web Search 模拟</h2>
                  <div class="text-muted">仅在 Responses 请求显式声明 web_search 工具且模型主动调用时启用</div>
                </div>
                <div class="toolbar-actions">
                  <el-button :icon="Refresh" :loading="webSearchLoading" :disabled="webSearchSaving" @click="loadWebSearch">刷新</el-button>
                  <el-button :icon="Plus" :disabled="webSearchSaving" @click="openWebSearchKeyDrawer()">新增 Web Search Key</el-button>
                </div>
              </div>

              <el-row :gutter="12">
                <el-col :span="8">
                  <el-statistic title="全局开关" :value="webSearchConfig.enabled ? '启用' : '停用'" />
                </el-col>
                <el-col :span="8">
                  <el-statistic title="可用 Key" :value="webSearchEnabledKeyCount" />
                </el-col>
                <el-col :span="8">
                  <el-statistic title="累计调用" :value="webSearchTotalUsage" />
                </el-col>
              </el-row>

              <div class="web-search-control-row">
                <span>启用 Web Search 模拟</span>
                <el-switch
                  v-model="webSearchConfig.enabled"
                  :loading="webSearchSaving"
                  :disabled="webSearchLoading"
                  @change="handleWebSearchEnabledChange"
                />
              </div>

              <el-table
                v-loading="webSearchLoading"
                :data="webSearchConfig.keys"
                row-key="client_id"
                style="width: 100%; margin-top: 16px"
                empty-text="暂无 Web Search Key"
              >
                <el-table-column label="#" width="64">
                  <template #default="{ $index }">{{ $index + 1 }}</template>
                </el-table-column>
                <el-table-column label="服务商" width="120">
                  <template #default="{ row }">
                    {{ formatWebSearchProvider(row.provider) }}
                  </template>
                </el-table-column>
                <el-table-column label="API Key" min-width="240">
                  <template #default="{ row }">
                    <span class="masked-key">{{ maskWebSearchKey(row.key) }}</span>
                  </template>
                </el-table-column>
                <el-table-column label="调用次数" width="160">
                  <template #default="{ row }">
                    {{ Number(row.usage_count || 0) }} / {{ webSearchKeyLimit(row) }}
                  </template>
                </el-table-column>
                <el-table-column label="单 Key 上限" width="130">
                  <template #default="{ row }">
                    {{ webSearchKeyLimit(row) }}
                  </template>
                </el-table-column>
                <el-table-column label="状态" width="100">
                  <template #default="{ row }">
                    <el-tag :type="row.enabled === false ? 'warning' : 'success'">
                      {{ row.enabled === false ? "停用" : "启用" }}
                    </el-tag>
                  </template>
                </el-table-column>
                <el-table-column label="操作" width="240" align="center">
                  <template #default="{ row, $index }">
                    <div class="inline-actions channel-table-actions">
                      <el-button
                        size="small"
                        type="primary"
                        plain
                        :loading="row.id && webSearchTestingId === row.id"
                        :disabled="webSearchSaving"
                        @click="testWebSearchKey(row)"
                      >
                        测试
                      </el-button>
                      <el-button size="small" :icon="Edit" :disabled="webSearchSaving" @click="openWebSearchKeyDrawer(row, $index)">编辑</el-button>
                      <el-popconfirm title="删除这个 Web Search Key？" @confirm="deleteWebSearchKey($index)">
                        <template #reference>
                          <el-button size="small" type="danger" :icon="Delete" :disabled="webSearchSaving">删除</el-button>
                        </template>
                      </el-popconfirm>
                    </div>
                  </template>
                </el-table-column>
              </el-table>

              <el-alert
                v-if="webSearchTestResult"
                class="channel-test-result"
                :title="webSearchTestResult.ok ? 'Web Search Key 测试成功' : 'Web Search Key 测试失败'"
                :type="webSearchTestResult.ok ? 'success' : 'error'"
                show-icon
                :closable="false"
              >
                <div class="channel-test-result__meta">
                  <span>耗时 {{ displayMs(webSearchTestResult.duration_ms) }}</span>
                  <span v-if="webSearchTestResult.key">调用 {{ webSearchTestResult.key.usage_count }} / {{ webSearchTestResult.key.usage_limit || webSearchTestResult.key.key_usage_limit || webSearchConfig.default_key_usage_limit }}</span>
                </div>
                <div class="channel-test-output">{{ formatWebSearchTestResult(webSearchTestResult) }}</div>
              </el-alert>
            </section>

            <section v-show="activeTab === 'logs'">
              <div class="toolbar">
                <div>
                  <h2>请求日志</h2>
                  <div class="text-muted">表格分页展示，详情中查看完整请求与响应</div>
                </div>
                <div class="toolbar-actions">
                  <el-popover placement="bottom-end" width="320" trigger="click">
                    <template #reference>
                      <el-button :icon="Setting">列设置</el-button>
                    </template>
                    <div class="log-column-settings">
                      <div class="log-column-settings__header">
                        <span>显示列</span>
                        <el-button link type="primary" @click="resetLogColumns">恢复默认</el-button>
                      </div>
                      <el-checkbox-group v-model="visibleLogColumnKeys" class="log-column-settings__list">
                        <div v-for="(column, index) in orderedLogColumns" :key="column.key" class="log-column-settings__item">
                          <el-checkbox :label="column.key">{{ column.label }}</el-checkbox>
                          <div class="log-column-settings__actions">
                            <el-button
                              size="small"
                              text
                              :disabled="index === 0"
                              @click="moveLogColumn(index, -1)"
                            >
                              上移
                            </el-button>
                            <el-button
                              size="small"
                              text
                              :disabled="index === orderedLogColumns.length - 1"
                              @click="moveLogColumn(index, 1)"
                            >
                              下移
                            </el-button>
                          </div>
                        </div>
                      </el-checkbox-group>
                    </div>
                  </el-popover>
                  <el-button :icon="Refresh" @click="loadLogs">刷新</el-button>
                  <el-dropdown trigger="click" @command="setLogAutoRefreshSeconds">
                    <el-button :type="logAutoRefreshSeconds ? 'primary' : 'default'" :icon="Refresh">
                      {{ logAutoRefreshLabel }}
                    </el-button>
                    <template #dropdown>
                      <el-dropdown-menu class="log-auto-refresh-menu">
                        <div class="log-auto-refresh-menu__title">启用自动刷新</div>
                        <el-dropdown-item :command="0">
                          <span class="log-auto-refresh-menu__item">
                            <span>关闭</span>
                            <el-icon v-if="logAutoRefreshSeconds === 0"><Check /></el-icon>
                          </span>
                        </el-dropdown-item>
                        <el-dropdown-item v-for="seconds in logAutoRefreshOptions" :key="seconds" :command="seconds">
                          <span class="log-auto-refresh-menu__item">
                            <span>{{ seconds }} 秒</span>
                            <el-icon v-if="logAutoRefreshSeconds === seconds"><Check /></el-icon>
                          </span>
                        </el-dropdown-item>
                      </el-dropdown-menu>
                    </template>
                  </el-dropdown>
                  <el-button @click="resetLogFilters">重置</el-button>
                </div>
              </div>

              <el-form class="log-filter-form" :model="logFilters">
                <el-form-item label="请求 ID">
                  <el-autocomplete
                    v-model="logFilters.request_id"
                    :fetch-suggestions="requestIdSuggestions"
                    clearable
                    @select="loadLogs(1)"
                  />
                </el-form-item>
                <el-form-item label="模型">
                  <el-autocomplete
                    v-model="logFilters.model"
                    :fetch-suggestions="modelSuggestions"
                    clearable
                    @select="loadLogs(1)"
                  />
                </el-form-item>
                <el-form-item label="渠道">
                  <el-select v-model="logFilters.channel_id" clearable filterable @change="loadLogs(1)">
                    <el-option v-for="item in filterOptions.channel_ids" :key="item" :label="item" :value="item" />
                  </el-select>
                </el-form-item>
                <el-form-item label="状态码">
                  <el-select v-model="logFilters.status_code" clearable filterable @change="loadLogs(1)">
                    <el-option v-for="item in filterOptions.status_codes" :key="item" :label="item" :value="item" />
                  </el-select>
                </el-form-item>
                <el-form-item label="请求状态">
                  <el-select v-model="logFilters.request_status" clearable @change="loadLogs(1)">
                    <el-option label="成功" value="success" />
                    <el-option label="失败" value="failed" />
                  </el-select>
                </el-form-item>
                <el-form-item label="路径">
                  <el-select v-model="logFilters.path" clearable filterable @change="loadLogs(1)">
                    <el-option v-for="item in filterOptions.paths" :key="item" :label="item" :value="item" />
                  </el-select>
                </el-form-item>
                <el-form-item v-if="isSuperadmin" label="用户">
                  <el-select v-model="logFilters.owner_username" clearable filterable @change="loadLogs(1)">
                    <el-option v-for="item in filterOptions.owner_usernames" :key="item" :label="item" :value="item" />
                  </el-select>
                </el-form-item>
                <el-form-item label="Key ID">
                  <el-select v-model="logFilters.api_key_id" clearable filterable @change="loadLogs(1)">
                    <el-option v-for="item in filterOptions.api_key_ids" :key="item" :label="item" :value="String(item)" />
                  </el-select>
                </el-form-item>
                <el-form-item class="log-filter-actions">
                  <el-button type="primary" :icon="Search" @click="loadLogs(1)">查询</el-button>
                </el-form-item>
              </el-form>

              <el-table
                class="log-table"
                v-loading="logsLoading"
                :data="logs"
                style="width: 100%"
                empty-text="暂无日志"
              >
                <el-table-column
                  v-for="column in visibleLogColumns"
                  :key="column.key"
                  :prop="column.prop"
                  :label="column.label"
                  :width="column.width"
                  :min-width="column.minWidth"
                  :show-overflow-tooltip="column.showOverflowTooltip"
                >
                  <template #default="{ row }">
                    <el-tag v-if="column.key === 'request_status'" :type="row.request_status === 'success' ? 'success' : 'danger'">
                      {{ row.request_status === "success" ? "成功" : "失败" }}
                    </el-tag>
                    <span v-else>{{ formatLogCell(row, column) }}</span>
                  </template>
                </el-table-column>
                <el-table-column label="操作" width="90" fixed="right">
                  <template #default="{ row }">
                    <el-button size="small" :icon="View" @click="openLogDetail(row)">详情</el-button>
                  </template>
                </el-table-column>
              </el-table>

              <div class="pagination-bar">
                <el-pagination
                  v-model:current-page="logPage"
                  v-model:page-size="logPageSize"
                  background
                  layout="total, sizes, prev, pager, next"
                  :page-sizes="[20, 50, 100, 200]"
                  :total="logTotal"
                  @current-change="loadLogs"
                  @size-change="handleLogPageSizeChange"
                />
              </div>
            </section>
          </div>
        </el-main>
      </el-container>
    </el-container>
  </div>

  <el-drawer v-model="channelDrawerVisible" :title="editingIndex === -1 ? '新增渠道' : '编辑渠道'" size="720px">
    <el-form label-position="top" :model="channelDraft">
      <el-row :gutter="12">
        <el-col :span="12">
          <el-form-item label="ID">
            <el-input v-model="channelDraft.id" :disabled="editingIndex !== -1" />
          </el-form-item>
        </el-col>
        <el-col :span="12">
          <el-form-item label="名称">
            <el-input v-model="channelDraft.name" />
          </el-form-item>
        </el-col>
        <el-col :span="12">
          <el-form-item label="服务类型">
            <el-select v-model="channelDraft.type" class="full-width">
              <el-option label="responses" value="responses" />
              <el-option label="chat" value="chat" />
              <el-option label="messages" value="messages" />
            </el-select>
          </el-form-item>
        </el-col>
        <el-col :span="12">
          <el-form-item label="认证方式">
            <el-select v-model="channelDraft.auth_mode" class="full-width">
              <el-option label="配置 Key" value="config" />
              <el-option label="无" value="none" />
            </el-select>
          </el-form-item>
        </el-col>
        <el-col :span="24">
          <el-form-item label="Base URL">
            <el-input v-model="channelDraft.baseurl" placeholder="https://example.com/v1" />
          </el-form-item>
        </el-col>
        <el-col :span="12">
          <el-form-item label="API Key">
            <el-input v-model="channelDraft.apikey" type="password" show-password />
          </el-form-item>
        </el-col>
        <el-col :span="12">
          <el-form-item label="超时秒数">
            <el-input-number v-model="channelDraft.timeout_seconds" :min="1" class="full-width" />
          </el-form-item>
        </el-col>
        <el-col :span="12">
          <el-form-item label="重试次数">
            <el-input-number
              v-model="channelDraft.retry_count"
              :min="0"
              :step="1"
              step-strictly
              class="full-width"
            />
          </el-form-item>
        </el-col>
        <el-col :span="12">
          <el-form-item label="启用">
            <el-switch v-model="channelDraft.enabled" />
          </el-form-item>
        </el-col>
      </el-row>

      <el-divider content-position="left">请求头</el-divider>
      <el-input v-model="headersText" type="textarea" :rows="4" placeholder='{"X-Test":"yes"}' />

      <el-divider content-position="left">模型映射</el-divider>
      <el-table :data="channelDraft.models" empty-text="暂无模型映射">
        <el-table-column label="请求模型">
          <template #default="{ row }">
            <el-input v-model="row.model" />
          </template>
        </el-table-column>
        <el-table-column label="上游模型">
          <template #default="{ row }">
            <el-input v-model="row.upstream_model" />
          </template>
        </el-table-column>
        <el-table-column width="90">
          <template #default="{ $index }">
            <el-button type="danger" :icon="Delete" circle @click="channelDraft.models.splice($index, 1)" />
          </template>
        </el-table-column>
      </el-table>
      <el-button style="margin-top: 8px" :icon="Plus" @click="channelDraft.models.push({ model: '', upstream_model: '' })">
        添加模型
      </el-button>
      <el-button style="margin-top: 8px; margin-left: 8px" :loading="discoverLoading" @click="discoverModels">
        发现模型
      </el-button>
      <el-alert v-if="discoveredModels.length" style="margin-top: 12px" type="info" :closable="false">
        <el-checkbox-group v-model="selectedDiscoveredModels">
          <el-checkbox v-for="model in discoveredModels" :key="model" :label="model" />
        </el-checkbox-group>
        <el-button size="small" style="margin-top: 8px" @click="addSelectedModels">加入映射</el-button>
      </el-alert>

      <el-divider content-position="left">兼容规则</el-divider>
      <el-row :gutter="12">
        <el-col :span="24">
          <el-form-item label="fallback_thinking_on_tool_use">
            <el-switch v-model="compatDraft.fallback_thinking_on_tool_use" />
          </el-form-item>
        </el-col>
        <el-col :span="12">
          <el-form-item label="rename_params">
            <el-input v-model="compatTexts.rename_params" type="textarea" :rows="4" placeholder="from=to" />
          </el-form-item>
        </el-col>
        <el-col :span="12">
          <el-form-item label="drop_params">
            <el-input v-model="compatTexts.drop_params" type="textarea" :rows="4" placeholder="每行一个参数" />
          </el-form-item>
        </el-col>
        <el-col :span="12">
          <el-form-item label="force_params">
            <el-input v-model="compatTexts.force_params" type="textarea" :rows="4" placeholder='name={"type":"text"}' />
          </el-form-item>
        </el-col>
        <el-col :span="12">
          <el-form-item label="default_params">
            <el-input v-model="compatTexts.default_params" type="textarea" :rows="4" placeholder="temperature=0.2" />
          </el-form-item>
        </el-col>
        <el-col :span="24">
          <el-form-item label="unsupported_params">
            <el-input v-model="compatTexts.unsupported_params" type="textarea" :rows="3" placeholder="每行一个参数" />
          </el-form-item>
        </el-col>
      </el-row>
    </el-form>

    <template #footer>
      <div class="drawer-footer">
        <el-button @click="channelDrawerVisible = false">取消</el-button>
        <el-button type="primary" :loading="saveLoading" @click="saveChannel">保存渠道</el-button>
      </div>
    </template>
  </el-drawer>

  <el-drawer
    v-model="webSearchKeyDrawerVisible"
    :title="webSearchKeyEditingIndex === -1 ? '新增 Web Search Key' : '编辑 Web Search Key'"
    size="520px"
  >
    <el-form label-position="top" :model="webSearchKeyDraft">
      <el-form-item label="服务商">
        <el-select v-model="webSearchKeyDraft.provider" class="full-width">
          <el-option
            v-for="provider in webSearchProviderOptions"
            :key="provider.value"
            :label="provider.label"
            :value="provider.value"
          />
        </el-select>
      </el-form-item>
      <el-form-item label="API Key">
        <el-input
          v-model="webSearchKeyDraft.key"
          type="password"
          show-password
          placeholder="请输入 Web Search API Key"
          autocomplete="off"
        />
      </el-form-item>
      <el-form-item label="启用">
        <el-switch v-model="webSearchKeyDraft.enabled" />
      </el-form-item>
      <el-form-item label="已用次数">
        <el-input-number
          v-model="webSearchKeyDraft.usage_count"
          class="full-width"
          :min="0"
          :step="1"
          :precision="0"
          controls-position="right"
        />
      </el-form-item>
      <el-form-item label="单 Key 上限">
        <el-input-number
          v-model="webSearchKeyDraft.usage_limit"
          class="full-width"
          :min="1"
          :step="100"
          :precision="0"
          controls-position="right"
        />
      </el-form-item>
      <el-alert
        type="info"
        :closable="false"
        title="点击“应用”后立即生效。"
      />
    </el-form>

    <template #footer>
      <div class="drawer-footer">
        <el-button @click="webSearchKeyDrawerVisible = false">取消</el-button>
        <el-button type="primary" :loading="webSearchSaving" @click="applyWebSearchKeyDraft">
          应用
        </el-button>
      </div>
    </template>
  </el-drawer>

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

  <el-dialog v-model="channelTestVisible" :title="channelTestTitle" width="640px">
    <el-descriptions v-if="testingChannel" :column="2" border class="channel-test-summary">
      <el-descriptions-item label="渠道">{{ testingChannel.name || testingChannel.id }}</el-descriptions-item>
      <el-descriptions-item label="服务类型">{{ testingChannel.type }}</el-descriptions-item>
      <el-descriptions-item label="ID">{{ testingChannel.id }}</el-descriptions-item>
      <el-descriptions-item label="状态">
        {{ testingChannel.enabled === false ? "停用" : "启用" }}
      </el-descriptions-item>
    </el-descriptions>

    <el-form label-position="top" :model="channelTestForm" class="channel-test-form">
      <el-form-item label="模型">
        <el-autocomplete
          v-model="channelTestForm.model"
          :fetch-suggestions="channelTestModelSuggestions"
          clearable
          class="full-width"
          placeholder="选择或输入模型"
        />
      </el-form-item>
      <el-form-item label="提示词">
        <el-input
          v-model="channelTestForm.prompt"
          type="textarea"
          :rows="4"
          placeholder="请输入用于测试连接的提示词"
        />
      </el-form-item>
    </el-form>

    <el-alert
      v-if="testResult"
      class="channel-test-result"
      :title="testResult.ok === false ? '连接测试失败' : '连接测试成功'"
      :type="testResult.ok === false ? 'error' : 'success'"
      show-icon
      :closable="false"
    >
      <div class="channel-test-result__meta">
        <span v-if="testResult.duration_ms !== undefined">耗时 {{ displayMs(testResult.duration_ms) }}</span>
        <span v-if="testResult.status_code">状态码 {{ testResult.status_code }}</span>
        <span v-if="testResult.upstream_model">上游模型 {{ testResult.upstream_model }}</span>
      </div>
      <div class="channel-test-output">{{ formatChannelTestResult(testResult) }}</div>
    </el-alert>

    <template #footer>
      <div class="drawer-footer">
        <el-button @click="channelTestVisible = false">关闭</el-button>
        <el-button type="primary" :loading="testLoading" @click="testChannel">测试连接</el-button>
      </div>
    </template>
  </el-dialog>

  <el-dialog v-model="logDetailVisible" title="日志详情" width="900px">
    <el-descriptions v-if="selectedLog" :column="2" border>
      <el-descriptions-item label="请求 ID">{{ selectedLog.request_id }}</el-descriptions-item>
      <el-descriptions-item label="请求状态">
        {{ selectedLog.request_status === "success" ? "成功" : "失败" }}
      </el-descriptions-item>
      <el-descriptions-item label="模型">{{ selectedLog.model }}</el-descriptions-item>
      <el-descriptions-item label="上游模型">{{ selectedLog.upstream_model }}</el-descriptions-item>
      <el-descriptions-item label="状态码">{{ selectedLog.status_code }}</el-descriptions-item>
      <el-descriptions-item label="成本">{{ formatCost(selectedLog.cost) }}</el-descriptions-item>
    </el-descriptions>
    <el-tabs style="margin-top: 16px">
      <el-tab-pane label="请求头">
        <pre class="json-view">{{ formatStoredJson(selectedLog?.request_headers) }}</pre>
      </el-tab-pane>
      <el-tab-pane label="请求 Body">
        <pre class="json-view">{{ formatStoredJson(selectedLog?.request_body) }}</pre>
      </el-tab-pane>
      <el-tab-pane label="响应">
        <div class="detail-grid">
          <el-alert v-if="selectedLog?.error" title="错误" type="error" :closable="false">
            <pre class="json-view">{{ selectedLog.error }}</pre>
          </el-alert>
          <pre class="json-view">{{ formatStoredJson(selectedLog?.response_body) }}</pre>
        </div>
      </el-tab-pane>
      <el-tab-pane label="Web Search">
        <pre class="json-view">{{ formatStoredJson(selectedLog?.web_search_json) }}</pre>
      </el-tab-pane>
    </el-tabs>
  </el-dialog>
</template>

<script setup>
import { computed, onBeforeUnmount, onMounted, reactive, ref } from "vue";
import { ElMessage } from "element-plus";
import {
  Check,
  Connection,
  CopyDocument,
  Delete,
  Download,
  Edit,
  Key,
  Plus,
  Refresh,
  Search,
  Setting,
  SwitchButton,
  Tickets,
  Upload,
  User,
  View
} from "@element-plus/icons-vue";

const WEB_SEARCH_PROVIDER_LABELS = {
  tavily: "Tavily"
};

const activeTab = ref("channels");
const authenticated = ref(false);
const loadingSession = ref(true);
const currentUser = ref(null);
const loginUsername = ref("admin");
const loginPassword = ref("");
const loginLoading = ref(false);
const configLoading = ref(false);
const saveLoading = ref(false);
const logsLoading = ref(false);
const testLoading = ref(false);
const discoverLoading = ref(false);
const webSearchLoading = ref(false);
const webSearchSaving = ref(false);
const webSearchTestingId = ref(null);
const accessKeysLoading = ref(false);
const accessKeyDialogVisible = ref(false);
const accessKeySaving = ref(false);
const createdAccessKey = ref(null);
const accessKeyDraft = reactive(defaultAccessKeyDraft());
const usersLoading = ref(false);
const userDialogVisible = ref(false);
const userSaving = ref(false);
const userDraft = reactive(defaultUserDraft());
const resetPasswordDialogVisible = ref(false);
const resetPasswordSaving = ref(false);
const resetPasswordDraft = reactive(defaultResetPasswordDraft());

const config = reactive({ channels: [] });
const webSearchConfig = reactive(defaultWebSearchConfig());
const webSearchTestResult = ref(null);
const webSearchKeyDrawerVisible = ref(false);
const webSearchKeyEditingIndex = ref(-1);
const webSearchKeyDraft = reactive(defaultWebSearchKeyDraft());
const channelDrawerVisible = ref(false);
const editingIndex = ref(-1);
const channelDraft = reactive(defaultChannel());
const headersText = ref("{}");
const compatDraft = reactive({ fallback_thinking_on_tool_use: false });
const compatTexts = reactive({
  rename_params: "",
  drop_params: "",
  force_params: "",
  default_params: "",
  unsupported_params: ""
});
const testResult = ref(null);
const channelTestVisible = ref(false);
const testingChannel = ref(null);
const channelTestForm = reactive({
  model: "",
  prompt: "ping"
});
const discoveredModels = ref([]);
const selectedDiscoveredModels = ref([]);
const accessKeys = ref([]);
const users = ref([]);

const logs = ref([]);
const logPage = ref(1);
const logPageSize = ref(20);
const logTotal = ref(0);
const logAutoRefreshOptions = [5, 10, 30, 60];
const logAutoRefreshSeconds = ref(0);
let logAutoRefreshTimer = null;
const filterOptions = reactive({
  request_ids: [],
  models: [],
  upstream_models: [],
  channel_ids: [],
  owner_usernames: [],
  api_key_ids: [],
  paths: [],
  status_codes: [],
  request_statuses: ["success", "failed"]
});
const logFilters = reactive({
  request_id: "",
  model: "",
  channel_id: "",
  owner_username: "",
  api_key_id: "",
  status_code: "",
  path: "",
  request_status: ""
});
const selectedLog = ref(null);
const logDetailVisible = ref(false);

const logColumnDefinitions = [
  { key: "created_at", prop: "created_at", label: "时间", width: 180 },
  { key: "request_id", prop: "request_id", label: "请求", width: 130, showOverflowTooltip: true },
  { key: "request_status", prop: "request_status", label: "状态", width: 90 },
  { key: "owner_username", prop: "owner_username", label: "用户", width: 120, showOverflowTooltip: true },
  { key: "api_key_id", prop: "api_key_id", label: "Key ID", width: 90 },
  { key: "model", prop: "model", label: "模型", minWidth: 160, showOverflowTooltip: true },
  { key: "channel_id", prop: "channel_id", label: "渠道", minWidth: 130, showOverflowTooltip: true },
  { key: "status_code", prop: "status_code", label: "状态码", width: 90 },
  { key: "duration_ms", prop: "duration_ms", label: "耗时", width: 95 },
  { key: "ttft_ms", prop: "ttft_ms", label: "TTFT", width: 95 },
  { key: "tokens", label: "Token", width: 190 },
  { key: "cost", prop: "cost", label: "成本", width: 110 },
  { key: "request_body", prop: "request_body", label: "请求 Body", minWidth: 220, showOverflowTooltip: true },
  { key: "response_body", prop: "response_body", label: "响应", minWidth: 220, showOverflowTooltip: true }
];
const defaultLogColumnKeys = logColumnDefinitions.map((column) => column.key);
const logColumnMap = Object.fromEntries(logColumnDefinitions.map((column) => [column.key, column]));
const logColumnOrder = ref(defaultLogColumnKeys.slice());
const visibleLogColumnKeys = ref(defaultLogColumnKeys.slice());

const channels = computed(() => config.channels || []);
const enabledChannelCount = computed(() => channels.value.filter((channel) => channel.enabled !== false).length);
const modelMappingCount = computed(() =>
  channels.value.reduce((total, channel) => total + normalizeModels(channel.models).length, 0)
);
const webSearchProviderOptions = computed(() =>
  normalizeWebSearchProviders(webSearchConfig.providers).map((provider) => ({
    value: provider,
    label: formatWebSearchProvider(provider)
  }))
);
const webSearchEnabledKeyCount = computed(() =>
  webSearchConfig.keys.filter((key) => key.enabled !== false && Number(key.usage_count || 0) < webSearchKeyLimit(key)).length
);
const webSearchTotalUsage = computed(() =>
  webSearchConfig.keys.reduce((total, key) => total + Number(key.usage_count || 0), 0)
);
const channelTestModelOptions = computed(() => normalizeModels(testingChannel.value?.models).map((item) => item.model));
const channelTestTitle = computed(() => {
  const channelName = testingChannel.value?.name || testingChannel.value?.id || "";
  return channelName ? `测试连接 - ${channelName}` : "测试连接";
});
const orderedLogColumns = computed(() =>
  logColumnOrder.value.map((key) => logColumnMap[key]).filter(Boolean)
);
const visibleLogColumns = computed(() =>
  orderedLogColumns.value.filter((column) => visibleLogColumnKeys.value.includes(column.key))
);
const logAutoRefreshLabel = computed(() =>
  logAutoRefreshSeconds.value ? `${logAutoRefreshSeconds.value} 秒刷新` : "自动刷新"
);
const isSuperadmin = computed(() => currentUser.value?.role === "superadmin");
const enabledUsers = computed(() => users.value.filter((user) => user.enabled !== false));
const enabledAccessKeyCount = computed(() => accessKeys.value.filter((key) => key.enabled !== false).length);
const lastAccessKeyUsedLabel = computed(() => {
  const timestamps = accessKeys.value
    .map((key) => Number(key.last_used_at || 0))
    .filter((value) => value > 0)
    .sort((a, b) => b - a);
  return timestamps.length ? formatTime(timestamps[0]) : "-";
});

onMounted(async () => {
  await checkSession();
});

onBeforeUnmount(() => {
  stopLogAutoRefreshTimer();
});

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

async function login() {
  loginLoading.value = true;
  try {
    const data = await api("/admin/api/login", {
      method: "POST",
      body: JSON.stringify({
        username: loginUsername.value,
        password: loginPassword.value
      })
    });
    setAuthenticatedUser(data);
    loginPassword.value = "";
    await loadInitialData();
  } catch (error) {
    ElMessage.error(error.message);
  } finally {
    loginLoading.value = false;
  }
}

async function logout() {
  await api("/admin/api/logout", { method: "POST", body: "{}" });
  setLogAutoRefreshSeconds(0);
  authenticated.value = false;
  currentUser.value = null;
  accessKeys.value = [];
  users.value = [];
  assignWebSearchConfig(defaultWebSearchConfig());
}

function setAuthenticatedUser(data) {
  authenticated.value = data.authenticated === true;
  currentUser.value = authenticated.value ? data.user || null : null;
  ensureAllowedActiveTab();
}

async function loadInitialData() {
  ensureAllowedActiveTab();
  const tasks = [loadConfig(), loadAccessKeys(), loadLogs()];
  if (isSuperadmin.value) {
    tasks.push(loadUsers(), loadWebSearch());
  }
  await Promise.all(tasks);
}

function ensureAllowedActiveTab() {
  if (!isSuperadmin.value && ["users", "web-search"].includes(activeTab.value)) {
    activeTab.value = "channels";
  }
}

async function loadConfig() {
  configLoading.value = true;
  try {
    const data = await api("/admin/api/config");
    config.channels = Array.isArray(data.channels) ? data.channels : [];
  } catch (error) {
    ElMessage.error(error.message);
  } finally {
    configLoading.value = false;
  }
}

async function saveConfig(nextChannels) {
  const data = await api("/admin/api/config", {
    method: "POST",
    body: JSON.stringify({ channels: nextChannels })
  });
  config.channels = Array.isArray(data.channels) ? data.channels : [];
}

function openChannelDrawer(channel = null, index = -1) {
  editingIndex.value = index;
  assignChannelDraft(channel ? clone(channel) : defaultChannel());
  headersText.value = formatJson(channelDraft.headers || {});
  assignCompat(channelDraft.compat || {});
  discoveredModels.value = [];
  selectedDiscoveredModels.value = [];
  channelDrawerVisible.value = true;
}

function openChannelTest(channel) {
  testingChannel.value = clone(channel);
  const models = normalizeModels(testingChannel.value.models).map((item) => item.model);
  channelTestForm.model = models[0] || "";
  channelTestForm.prompt = "ping";
  testResult.value = null;
  channelTestVisible.value = true;
}

async function saveChannel() {
  saveLoading.value = true;
  try {
    const channel = buildChannelFromDraft();
    const nextChannels = channels.value.slice();
    if (editingIndex.value === -1) {
      nextChannels.push(channel);
    } else {
      nextChannels.splice(editingIndex.value, 1, channel);
    }
    await saveConfig(nextChannels);
    channelDrawerVisible.value = false;
    ElMessage.success("渠道已保存并生效");
  } catch (error) {
    ElMessage.error(error.message);
  } finally {
    saveLoading.value = false;
  }
}

async function deleteChannel(index) {
  const nextChannels = channels.value.slice();
  nextChannels.splice(index, 1);
  try {
    await saveConfig(nextChannels);
    ElMessage.success("渠道已删除");
  } catch (error) {
    ElMessage.error(error.message);
  }
}

async function importConfig(file) {
  try {
    const text = await file.text();
    const payload = JSON.parse(text);
    const data = await api("/admin/api/config/import", {
      method: "POST",
      body: JSON.stringify(payload)
    });
    config.channels = data.config?.channels || [];
    ElMessage.success(`导入 ${data.imported} 个渠道，跳过 ${data.skipped} 个`);
  } catch (error) {
    ElMessage.error(error.message);
  }
  return false;
}

function exportConfig() {
  window.location.href = "/admin/api/config/export";
}

async function loadAccessKeys() {
  accessKeysLoading.value = true;
  try {
    const data = await api("/admin/api/api-keys");
    accessKeys.value = Array.isArray(data.keys) ? data.keys : [];
  } catch (error) {
    ElMessage.error(error.message);
  } finally {
    accessKeysLoading.value = false;
  }
}

function openAccessKeyDialog() {
  Object.assign(accessKeyDraft, defaultAccessKeyDraft(), {
    owner_username: currentUser.value?.username || ""
  });
  createdAccessKey.value = null;
  accessKeyDialogVisible.value = true;
}

async function createAccessKey() {
  accessKeySaving.value = true;
  try {
    const payload = {
      name: String(accessKeyDraft.name || "").trim()
    };
    if (isSuperadmin.value) {
      const owner = String(accessKeyDraft.owner_username || "").trim();
      if (!owner) {
        throw new Error("请选择归属用户");
      }
      payload.owner_username = owner;
    }
    const data = await api("/admin/api/api-keys", {
      method: "POST",
      body: JSON.stringify(payload)
    });
    createdAccessKey.value = data.key || null;
    await loadAccessKeys();
    ElMessage.success("API Key 已创建");
  } catch (error) {
    ElMessage.error(error.message);
  } finally {
    accessKeySaving.value = false;
  }
}

async function toggleAccessKey(row) {
  try {
    await api(`/admin/api/api-keys/${row.id}`, {
      method: "PATCH",
      body: JSON.stringify({ enabled: row.enabled === false })
    });
    await loadAccessKeys();
    ElMessage.success(row.enabled === false ? "API Key 已启用" : "API Key 已停用");
  } catch (error) {
    ElMessage.error(error.message);
  }
}

async function deleteAccessKey(row) {
  try {
    await api(`/admin/api/api-keys/${row.id}`, {
      method: "DELETE",
      body: "{}"
    });
    await loadAccessKeys();
    ElMessage.success("API Key 已删除");
  } catch (error) {
    ElMessage.error(error.message);
  }
}

async function copyText(text) {
  const value = String(text || "");
  if (!value) {
    return;
  }
  try {
    if (navigator.clipboard?.writeText) {
      await navigator.clipboard.writeText(value);
    } else {
      const textarea = document.createElement("textarea");
      textarea.value = value;
      textarea.setAttribute("readonly", "");
      textarea.style.position = "fixed";
      textarea.style.opacity = "0";
      document.body.appendChild(textarea);
      textarea.select();
      document.execCommand("copy");
      document.body.removeChild(textarea);
    }
    ElMessage.success("已复制");
  } catch {
    ElMessage.error("复制失败，请手动复制");
  }
}

async function loadUsers() {
  if (!isSuperadmin.value) {
    users.value = [];
    return;
  }
  usersLoading.value = true;
  try {
    const data = await api("/admin/api/users");
    users.value = Array.isArray(data.users) ? data.users : [];
  } catch (error) {
    ElMessage.error(error.message);
  } finally {
    usersLoading.value = false;
  }
}

function openUserDialog() {
  Object.assign(userDraft, defaultUserDraft());
  userDialogVisible.value = true;
}

async function createUser() {
  userSaving.value = true;
  try {
    const username = String(userDraft.username || "").trim();
    const password = String(userDraft.password || "");
    if (!username) {
      throw new Error("用户名不能为空");
    }
    if (!password) {
      throw new Error("密码不能为空");
    }
    await api("/admin/api/users", {
      method: "POST",
      body: JSON.stringify({
        username,
        password,
        enabled: userDraft.enabled !== false
      })
    });
    userDialogVisible.value = false;
    await Promise.all([loadUsers(), loadAccessKeys()]);
    ElMessage.success("用户已创建");
  } catch (error) {
    ElMessage.error(error.message);
  } finally {
    userSaving.value = false;
  }
}

async function toggleUser(row) {
  try {
    await api(`/admin/api/users/${encodeURIComponent(row.username)}`, {
      method: "PATCH",
      body: JSON.stringify({ enabled: row.enabled === false })
    });
    await Promise.all([loadUsers(), loadAccessKeys()]);
    ElMessage.success(row.enabled === false ? "用户已启用" : "用户已停用");
  } catch (error) {
    ElMessage.error(error.message);
  }
}

function openResetPasswordDialog(row) {
  Object.assign(resetPasswordDraft, defaultResetPasswordDraft(), {
    username: row.username
  });
  resetPasswordDialogVisible.value = true;
}

async function resetUserPassword() {
  resetPasswordSaving.value = true;
  try {
    const username = String(resetPasswordDraft.username || "").trim();
    const password = String(resetPasswordDraft.password || "");
    if (!password) {
      throw new Error("新密码不能为空");
    }
    await api(`/admin/api/users/${encodeURIComponent(username)}`, {
      method: "PATCH",
      body: JSON.stringify({ password })
    });
    resetPasswordDialogVisible.value = false;
    await loadUsers();
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
  return row.username === currentUser.value?.username;
}

async function deleteUser(row) {
  try {
    await api(`/admin/api/users/${encodeURIComponent(row.username)}`, {
      method: "DELETE",
      body: "{}"
    });
    await Promise.all([loadUsers(), loadAccessKeys(), loadConfig(), loadLogs()]);
    ElMessage.success("用户已删除");
  } catch (error) {
    ElMessage.error(error.message);
  }
}

async function loadWebSearch() {
  if (!isSuperadmin.value) {
    assignWebSearchConfig(defaultWebSearchConfig());
    return;
  }
  webSearchLoading.value = true;
  try {
    const data = await api("/admin/api/web-search");
    assignWebSearchConfig(data);
  } catch (error) {
    ElMessage.error(error.message);
  } finally {
    webSearchLoading.value = false;
  }
}

async function persistWebSearchConfig(successMessage = "Web Search 模拟配置已生效") {
  webSearchSaving.value = true;
  try {
    const payload = buildWebSearchPayload();
    const data = await api("/admin/api/web-search", {
      method: "POST",
      body: JSON.stringify(payload)
    });
    assignWebSearchConfig(data);
    if (successMessage) {
      ElMessage.success(successMessage);
    }
    return true;
  } catch (error) {
    ElMessage.error(error.message);
    await loadWebSearch();
    return false;
  } finally {
    webSearchSaving.value = false;
  }
}

async function handleWebSearchEnabledChange() {
  await persistWebSearchConfig("Web Search 模拟开关已生效");
}

function openWebSearchKeyDrawer(key = null, index = -1) {
  webSearchKeyEditingIndex.value = index;
  assignWebSearchKeyDraft(key ? clone(key) : defaultWebSearchKey());
  webSearchTestResult.value = null;
  webSearchKeyDrawerVisible.value = true;
}

async function applyWebSearchKeyDraft() {
  try {
    const key = buildWebSearchKeyFromDraft();
    if (webSearchKeyEditingIndex.value === -1) {
      webSearchConfig.keys.push(key);
    } else {
      webSearchConfig.keys.splice(webSearchKeyEditingIndex.value, 1, key);
    }
    webSearchTestResult.value = null;
    const saved = await persistWebSearchConfig("Web Search Key 已生效");
    if (saved) {
      webSearchKeyDrawerVisible.value = false;
    }
  } catch (error) {
    ElMessage.error(error.message);
  }
}

async function deleteWebSearchKey(index) {
  webSearchConfig.keys.splice(index, 1);
  webSearchTestResult.value = null;
  await persistWebSearchConfig("Web Search Key 已删除");
}

async function testWebSearchKey(row) {
  if (!row?.id) {
    ElMessage.warning("这个 Web Search Key 尚未生效，点击“应用”后再测试");
    return;
  }
  webSearchTestingId.value = row.id;
  webSearchTestResult.value = null;
  try {
    const data = await api("/admin/api/web-search/test-key", {
      method: "POST",
      body: JSON.stringify({ id: row.id })
    });
    webSearchTestResult.value = data;
    if (data.config) {
      assignWebSearchConfig(data.config);
    }
    if (data.ok) {
      ElMessage.success("Web Search Key 测试成功");
    } else {
      ElMessage.warning("Web Search Key 测试失败");
    }
  } catch (error) {
    ElMessage.error(error.message);
  } finally {
    webSearchTestingId.value = null;
  }
}

async function discoverModels() {
  discoverLoading.value = true;
  try {
    const channel = buildChannelFromDraft();
    const data = await api("/admin/api/channels/discover-models", {
      method: "POST",
      body: JSON.stringify({ channel })
    });
    discoveredModels.value = data.models || [];
    selectedDiscoveredModels.value = discoveredModels.value.slice();
    ElMessage.success(`发现 ${discoveredModels.value.length} 个模型`);
  } catch (error) {
    ElMessage.error(error.message);
  } finally {
    discoverLoading.value = false;
  }
}

async function testChannel() {
  testLoading.value = true;
  try {
    if (!testingChannel.value) {
      throw new Error("请选择要测试的渠道");
    }
    const payload = buildChannelTestPayload(testingChannel.value);
    testResult.value = await api("/admin/api/channels/test", {
      method: "POST",
      body: JSON.stringify({ channel: testingChannel.value, payload })
    });
  } catch (error) {
    ElMessage.error(error.message);
  } finally {
    testLoading.value = false;
  }
}

function addSelectedModels() {
  const existing = new Set(normalizeModels(channelDraft.models).map((item) => item.model));
  for (const model of selectedDiscoveredModels.value) {
    if (!existing.has(model)) {
      channelDraft.models.push({ model, upstream_model: model });
      existing.add(model);
    }
  }
}

function buildChannelTestPayload(channel) {
  const model = String(channelTestForm.model || "").trim();
  const prompt = String(channelTestForm.prompt || "").trim();
  if (!model) {
    throw new Error("请输入要测试的模型");
  }
  if (!prompt) {
    throw new Error("请输入测试提示词");
  }
  if (channel.type === "messages") {
    return {
      model,
      messages: [{ role: "user", content: [{ type: "text", text: prompt }] }],
      max_tokens: 256
    };
  }
  if (channel.type === "chat") {
    return {
      model,
      messages: [{ role: "user", content: prompt }],
      max_tokens: 256
    };
  }
  return {
    model,
    input: prompt,
    max_output_tokens: 256
  };
}

async function loadLogs(page = logPage.value) {
  logsLoading.value = true;
  logPage.value = typeof page === "number" ? page : logPage.value;
  try {
    const params = new URLSearchParams({
      page: String(logPage.value),
      page_size: String(logPageSize.value)
    });
    for (const [key, value] of Object.entries(logFilters)) {
      if (value !== "" && value !== null && value !== undefined) {
        params.set(key, value);
      }
    }
    const data = await api(`/admin/api/logs?${params.toString()}`);
    logs.value = data.events || [];
    logTotal.value = data.total || 0;
    Object.assign(filterOptions, data.filter_options || {});
  } catch (error) {
    ElMessage.error(error.message);
  } finally {
    logsLoading.value = false;
  }
}

function setLogAutoRefreshSeconds(seconds) {
  const nextSeconds = Number(seconds || 0);
  logAutoRefreshSeconds.value = logAutoRefreshOptions.includes(nextSeconds) ? nextSeconds : 0;
  restartLogAutoRefreshTimer();
  if (logAutoRefreshSeconds.value > 0) {
    refreshLogsFromAutoRefresh();
  }
}

function restartLogAutoRefreshTimer() {
  stopLogAutoRefreshTimer();
  if (logAutoRefreshSeconds.value === 0) {
    return;
  }
  logAutoRefreshTimer = window.setInterval(
    refreshLogsFromAutoRefresh,
    logAutoRefreshSeconds.value * 1000
  );
}

function stopLogAutoRefreshTimer() {
  if (logAutoRefreshTimer === null) {
    return;
  }
  window.clearInterval(logAutoRefreshTimer);
  logAutoRefreshTimer = null;
}

async function refreshLogsFromAutoRefresh() {
  if (!authenticated.value || activeTab.value !== "logs" || logsLoading.value) {
    return;
  }
  await loadLogs();
}

function handleLogPageSizeChange() {
  logPage.value = 1;
  loadLogs(1);
}

function resetLogFilters() {
  Object.assign(logFilters, {
    request_id: "",
    model: "",
    channel_id: "",
    owner_username: "",
    api_key_id: "",
    status_code: "",
    path: "",
    request_status: ""
  });
  loadLogs(1);
}

function moveLogColumn(index, direction) {
  const target = index + direction;
  if (target < 0 || target >= logColumnOrder.value.length) {
    return;
  }
  const nextOrder = logColumnOrder.value.slice();
  const [item] = nextOrder.splice(index, 1);
  nextOrder.splice(target, 0, item);
  logColumnOrder.value = nextOrder;
}

function resetLogColumns() {
  logColumnOrder.value = defaultLogColumnKeys.slice();
  visibleLogColumnKeys.value = defaultLogColumnKeys.slice();
}

function openLogDetail(row) {
  selectedLog.value = row;
  logDetailVisible.value = true;
}

function requestIdSuggestions(query, callback) {
  callback(buildSuggestions(filterOptions.request_ids, query));
}

function modelSuggestions(query, callback) {
  callback(buildSuggestions(filterOptions.models, query));
}

function channelTestModelSuggestions(query, callback) {
  callback(buildSuggestions(channelTestModelOptions.value, query));
}

function buildSuggestions(values, query) {
  const lowered = String(query || "").toLowerCase();
  return (values || [])
    .filter((value) => String(value).toLowerCase().includes(lowered))
    .map((value) => ({ value: String(value) }));
}

function defaultChannel() {
  return {
    id: "",
    name: "",
    type: "chat",
    baseurl: "",
    apikey: "",
    auth_mode: "config",
    headers: {},
    timeout_seconds: 120,
    retry_count: 3,
    compat: {},
    models: [],
    enabled: true
  };
}

function defaultAccessKeyDraft() {
  return {
    owner_username: "",
    name: ""
  };
}

function defaultUserDraft() {
  return {
    username: "",
    password: "",
    enabled: true
  };
}

function defaultResetPasswordDraft() {
  return {
    username: "",
    password: ""
  };
}

function defaultWebSearchConfig() {
  return {
    enabled: false,
    providers: ["tavily"],
    default_key_usage_limit: 1000,
    keys: []
  };
}

function defaultWebSearchKey() {
  const usageLimit = defaultWebSearchKeyUsageLimit();
  return {
    client_id: `new-${Date.now()}-${Math.random().toString(16).slice(2)}`,
    id: null,
    provider: firstWebSearchProvider(),
    key: "",
    enabled: true,
    usage_count: 0,
    usage_limit: usageLimit,
    key_usage_limit: usageLimit
  };
}

function defaultWebSearchKeyDraft() {
  const usageLimit = defaultWebSearchKeyUsageLimit();
  return {
    client_id: "",
    id: null,
    provider: firstWebSearchProvider(),
    key: "",
    enabled: true,
    usage_count: 0,
    usage_limit: usageLimit,
    key_usage_limit: usageLimit
  };
}

function assignWebSearchKeyDraft(key) {
  const usageLimit = webSearchKeyLimit(key);
  Object.assign(webSearchKeyDraft, defaultWebSearchKeyDraft(), key || {}, {
    provider: normalizeWebSearchProvider(key?.provider),
    key: String(key?.key || ""),
    enabled: key?.enabled !== false,
    usage_count: normalizeNonNegativeInteger(key?.usage_count, 0),
    usage_limit: usageLimit,
    key_usage_limit: usageLimit
  });
}

function buildWebSearchKeyFromDraft() {
  const provider = normalizeWebSearchProvider(webSearchKeyDraft.provider);
  const key = String(webSearchKeyDraft.key || "").trim();
  if (!key) {
    throw new Error("Web Search Key 不能为空");
  }
  const usageCount = normalizeNonNegativeInteger(webSearchKeyDraft.usage_count, -1);
  if (usageCount < 0) {
    throw new Error("已用次数必须是大于等于 0 的整数");
  }
  const usageLimit = normalizePositiveInteger(webSearchKeyDraft.usage_limit || webSearchKeyDraft.key_usage_limit, 0);
  if (!usageLimit) {
    throw new Error("单 Key 调用上限必须大于 0");
  }
  return {
    client_id: webSearchKeyDraft.client_id || `new-${Date.now()}-${Math.random().toString(16).slice(2)}`,
    id: webSearchKeyDraft.id || null,
    provider,
    key,
    enabled: webSearchKeyDraft.enabled !== false,
    usage_count: usageCount,
    usage_limit: usageLimit,
    key_usage_limit: usageLimit
  };
}

function assignWebSearchConfig(data) {
  Object.assign(webSearchConfig, defaultWebSearchConfig(), data || {}, {
    enabled: data?.enabled === true,
    providers: normalizeWebSearchProviders(data?.providers),
    default_key_usage_limit: normalizePositiveInteger(
      data?.default_key_usage_limit || data?.key_usage_limit,
      1000
    ),
    keys: normalizeWebSearchKeys(data?.keys || [])
  });
}

function normalizeWebSearchProviders(providers) {
  if (!Array.isArray(providers) || providers.length === 0) {
    return ["tavily"];
  }
  const normalized = providers
    .map((provider) => String(provider || "").trim().toLowerCase())
    .filter(Boolean);
  return normalized.length ? Array.from(new Set(normalized)) : ["tavily"];
}

function firstWebSearchProvider() {
  return normalizeWebSearchProviders(webSearchConfig.providers)[0] || "tavily";
}

function normalizeWebSearchProvider(provider) {
  const normalized = String(provider || firstWebSearchProvider()).trim().toLowerCase();
  const options = normalizeWebSearchProviders(webSearchConfig.providers);
  return options.includes(normalized) ? normalized : firstWebSearchProvider();
}

function formatWebSearchProvider(provider) {
  const normalized = String(provider || "tavily").trim().toLowerCase();
  return WEB_SEARCH_PROVIDER_LABELS[normalized] || provider || "tavily";
}

function defaultWebSearchKeyUsageLimit() {
  return normalizePositiveInteger(webSearchConfig.default_key_usage_limit, 1000);
}

function webSearchKeyLimit(key) {
  return normalizePositiveInteger(
    key?.usage_limit || key?.key_usage_limit,
    defaultWebSearchKeyUsageLimit()
  );
}

function normalizeWebSearchKeys(keys) {
  if (!Array.isArray(keys)) {
    return [];
  }
  return keys.map((item, index) => {
    const usageLimit = normalizePositiveInteger(
      item?.usage_limit || item?.key_usage_limit,
      defaultWebSearchKeyUsageLimit()
    );
    return {
      client_id: item?.id ? `saved-${item.id}` : item?.client_id || `new-${index}-${Date.now()}`,
      id: item?.id || null,
      provider: normalizeWebSearchProvider(item?.provider),
      key: String(item?.key || item?.api_key || ""),
      enabled: item?.enabled !== false,
      usage_count: normalizeNonNegativeInteger(item?.usage_count, 0),
      usage_limit: usageLimit,
      key_usage_limit: usageLimit
    };
  });
}

function buildWebSearchPayload() {
  const keys = webSearchConfig.keys.map((item) => {
    const usageLimit = webSearchKeyLimit(item);
    return {
      id: item.id || undefined,
      provider: normalizeWebSearchProvider(item.provider),
      key: String(item.key || "").trim(),
      enabled: item.enabled !== false,
      usage_count: normalizeNonNegativeInteger(item.usage_count, 0),
      usage_limit: usageLimit
    };
  });
  const emptyIndex = keys.findIndex((item) => !item.key);
  if (emptyIndex !== -1) {
    throw new Error(`第 ${emptyIndex + 1} 个 Web Search Key 不能为空`);
  }
  return {
    enabled: webSearchConfig.enabled === true,
    keys
  };
}

function normalizePositiveInteger(value, fallback) {
  const parsed = Number(value);
  if (!Number.isFinite(parsed) || parsed <= 0) {
    return fallback;
  }
  return Math.floor(parsed);
}

function normalizeNonNegativeInteger(value, fallback) {
  const parsed = Number(value);
  if (!Number.isFinite(parsed) || parsed < 0) {
    return fallback;
  }
  return Math.floor(parsed);
}

function assignChannelDraft(channel) {
  Object.assign(channelDraft, defaultChannel(), channel, {
    headers: channel.headers || {},
    compat: channel.compat || {},
    models: normalizeModels(channel.models)
  });
}

function assignCompat(compat) {
  Object.assign(compatDraft, {
    fallback_thinking_on_tool_use: compat.fallback_thinking_on_tool_use === true
  });
  Object.assign(compatTexts, {
    rename_params: formatAssignmentMap(compat.rename_params || {}),
    drop_params: formatStringList(compat.drop_params || []),
    force_params: formatAssignmentMap(compat.force_params || {}),
    default_params: formatAssignmentMap(compat.default_params || {}),
    unsupported_params: formatStringList(compat.unsupported_params || [])
  });
}

function buildChannelFromDraft() {
  const headers = parseJsonText(headersText.value || "{}", "请求头");
  if (!headers || typeof headers !== "object" || Array.isArray(headers)) {
    throw new Error("请求头必须是 JSON 对象");
  }
  const channel = {
    id: channelDraft.id.trim(),
    name: channelDraft.name.trim(),
    type: channelDraft.type,
    baseurl: channelDraft.baseurl.trim(),
    apikey: channelDraft.apikey,
    auth_mode: channelDraft.auth_mode,
    headers,
    timeout_seconds: Number(channelDraft.timeout_seconds || 120),
    retry_count: Number(channelDraft.retry_count ?? 3),
    enabled: channelDraft.enabled === true,
    models: normalizeModels(channelDraft.models).filter((item) => item.model),
    compat: buildCompat()
  };
  return channel;
}

function buildCompat() {
  const compat = {
    rename_params: parseAssignmentMap(compatTexts.rename_params, false),
    drop_params: parseStringList(compatTexts.drop_params),
    force_params: parseAssignmentMap(compatTexts.force_params, true),
    default_params: parseAssignmentMap(compatTexts.default_params, true),
    unsupported_params: parseStringList(compatTexts.unsupported_params)
  };
  if (compatDraft.fallback_thinking_on_tool_use) {
    compat.fallback_thinking_on_tool_use = true;
  }
  for (const key of Object.keys(compat)) {
    const value = compat[key];
    if ((Array.isArray(value) && value.length === 0) || (isPlainObject(value) && Object.keys(value).length === 0)) {
      delete compat[key];
    }
  }
  return compat;
}

function normalizeModels(models) {
  if (!Array.isArray(models)) {
    return [];
  }
  return models
    .map((item) => {
      const model = String(item?.model || "").trim();
      return { model, upstream_model: String(item?.upstream_model || model).trim() || model };
    })
    .filter((item) => item.model);
}

function parseJsonText(text, label) {
  try {
    return JSON.parse(text || "{}");
  } catch (error) {
    throw new Error(`${label} 不是合法 JSON`);
  }
}

function parseStringList(text) {
  return String(text || "")
    .split("\n")
    .map((line) => line.trim())
    .filter(Boolean);
}

function parseAssignmentMap(text, parseValue) {
  const result = {};
  for (const [index, line] of String(text || "").split("\n").entries()) {
    const trimmed = line.trim();
    if (!trimmed) {
      continue;
    }
    const separator = trimmed.indexOf("=");
    if (separator === -1) {
      throw new Error(`第 ${index + 1} 行缺少 =`);
    }
    const key = trimmed.slice(0, separator).trim();
    const rawValue = trimmed.slice(separator + 1).trim();
    result[key] = parseValue ? parseLooseValue(rawValue) : rawValue;
  }
  return result;
}

function parseLooseValue(value) {
  try {
    return JSON.parse(value);
  } catch {
    return value;
  }
}

function formatAssignmentMap(value) {
  return Object.entries(value || {})
    .map(([key, item]) => `${key}=${typeof item === "string" ? item : JSON.stringify(item)}`)
    .join("\n");
}

function formatStringList(value) {
  return Array.isArray(value) ? value.join("\n") : "";
}

function formatStoredJson(value) {
  if (value === null || value === undefined || value === "") {
    return "";
  }
  if (typeof value === "string") {
    try {
      return formatJson(JSON.parse(value));
    } catch {
      return value;
    }
  }
  return formatJson(value);
}

function previewJson(value) {
  const text = formatStoredJson(value).replace(/\s+/g, " ").trim();
  return text.length > 120 ? `${text.slice(0, 120)}...` : text;
}

function formatLogCell(row, column) {
  switch (column.key) {
    case "created_at":
      return formatTime(row.created_at);
    case "duration_ms":
      return displayMs(row.duration_ms);
    case "ttft_ms":
      return displayMs(row.ttft_ms);
    case "tokens":
      return `${row.input_tokens || 0} / ${row.cached_tokens || 0} / ${row.output_tokens || 0}`;
    case "cost":
      return formatCost(row.cost);
    case "request_body":
      return previewJson(row.request_body);
    case "response_body":
      return previewJson(row.response_body || row.error);
    default:
      return row[column.prop] ?? "";
  }
}

function formatChannelTestResult(result) {
  if (!result) {
    return "";
  }
  if (result.ok === false) {
    const details = extractErrorMessage(result.body);
    return [result.error || "上游请求失败", details].filter(Boolean).join("\n");
  }
  const responseText = extractResponseText(result.response);
  if (responseText) {
    return responseText;
  }
  return "连接已打通，但响应中没有可展示的文本内容。";
}

function formatWebSearchTestResult(result) {
  if (!result) {
    return "";
  }
  if (result.ok === false) {
    const error = result.result?.error || result.result?.summary?.error || result.result?.raw?.error;
    return error ? String(error) : "Web Search 请求失败";
  }
  const summary = result.result?.summary || {};
  const answer = String(summary.answer || result.result?.raw?.answer || "").trim();
  const rows = Array.isArray(summary.results) ? summary.results : [];
  const links = rows
    .map((item, index) => {
      const title = String(item?.title || item?.url || `结果 ${index + 1}`).trim();
      const url = String(item?.url || "").trim();
      return url ? `${index + 1}. ${title}\n${url}` : `${index + 1}. ${title}`;
    })
    .join("\n");
  return [answer || "Web Search 已返回结果。", links].filter(Boolean).join("\n\n");
}

function maskWebSearchKey(value) {
  const key = String(value || "").trim();
  if (!key) {
    return "-";
  }
  if (key.length <= 8) {
    return "*".repeat(key.length);
  }
  return `${key.slice(0, 4)}${"*".repeat(Math.min(16, key.length - 8))}${key.slice(-4)}`;
}

function extractErrorMessage(value) {
  if (!value) {
    return "";
  }
  if (typeof value === "string") {
    return value;
  }
  if (typeof value.error === "string") {
    return value.error;
  }
  if (value.error?.message) {
    return String(value.error.message);
  }
  if (value.message) {
    return String(value.message);
  }
  return "";
}

function extractResponseText(response) {
  if (!response || typeof response !== "object") {
    return "";
  }
  const outputText = String(response.output_text || "").trim();
  if (outputText) {
    return outputText;
  }
  const choiceContent = response.choices?.[0]?.message?.content;
  const choiceText = stringifyContent(choiceContent).trim();
  if (choiceText) {
    return choiceText;
  }
  const messageText = stringifyContent(response.content).trim();
  if (messageText) {
    return messageText;
  }
  const output = Array.isArray(response.output) ? response.output : [];
  const outputParts = [];
  for (const item of output) {
    const content = Array.isArray(item?.content) ? item.content : [];
    for (const block of content) {
      const text = block?.text || block?.output_text;
      if (text) {
        outputParts.push(String(text));
      }
    }
  }
  return outputParts.join("\n").trim();
}

function stringifyContent(content) {
  if (typeof content === "string") {
    return content;
  }
  if (!Array.isArray(content)) {
    return "";
  }
  return content
    .map((item) => {
      if (typeof item === "string") {
        return item;
      }
      return item?.text || "";
    })
    .filter(Boolean)
    .join("\n");
}

function formatJson(value) {
  return JSON.stringify(value, null, 2);
}

function formatTime(timestamp) {
  if (!timestamp) {
    return "";
  }
  return new Date(Number(timestamp) * 1000).toLocaleString();
}

function displayMs(value) {
  return value === null || value === undefined ? "-" : `${value} ms`;
}

function formatCost(value) {
  const number = Number(value || 0);
  return number ? `$${number.toFixed(6)}` : "$0.000000";
}

function clone(value) {
  return JSON.parse(JSON.stringify(value || {}));
}

function isPlainObject(value) {
  return value && typeof value === "object" && !Array.isArray(value);
}
</script>
