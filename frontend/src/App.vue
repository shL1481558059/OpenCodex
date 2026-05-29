<template>
  <div v-if="loadingSession" class="login-wrap">
    <el-empty description="正在加载管理台" />
  </div>

  <div v-else-if="!authenticated" class="login-wrap">
    <el-card class="login-card" shadow="never">
      <template #header>
        <div>
          <strong>OpenCodex 管理台</strong>
          <div class="text-muted">请输入管理员密码</div>
        </div>
      </template>
      <el-form label-position="top" @submit.prevent="login">
        <input autocomplete="username" hidden value="admin" />
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
        <el-button :icon="SwitchButton" @click="logout">退出</el-button>
      </el-header>

      <el-container class="app-body">
        <el-aside width="260px" class="app-aside">
          <el-menu class="side-menu" :default-active="activeTab" @select="activeTab = $event">
            <el-menu-item index="channels">
              <el-icon><Connection /></el-icon>
              <span>渠道配置</span>
            </el-menu-item>
            <el-menu-item index="web-search">
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

            <section v-show="activeTab === 'web-search'">
              <div class="toolbar">
                <div>
                  <h2>Web Search 模拟</h2>
                  <div class="text-muted">仅在 Responses 请求显式声明 web_search 工具且模型主动调用时启用</div>
                </div>
                <div class="toolbar-actions">
                  <el-button :icon="Refresh" @click="loadWebSearch">刷新</el-button>
                  <el-button type="primary" :loading="webSearchSaving" @click="saveWebSearch">保存配置</el-button>
                  <el-button :icon="Plus" @click="addWebSearchKey">新增 Tavily Key</el-button>
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
                <el-switch v-model="webSearchConfig.enabled" />
                <el-input
                  v-model="webSearchTestQuery"
                  class="web-search-test-query"
                  placeholder="测试查询"
                  clearable
                />
              </div>

              <el-table
                v-loading="webSearchLoading"
                :data="webSearchConfig.keys"
                row-key="client_id"
                style="width: 100%; margin-top: 16px"
                empty-text="暂无 Tavily Key"
              >
                <el-table-column label="#" width="64">
                  <template #default="{ $index }">{{ $index + 1 }}</template>
                </el-table-column>
                <el-table-column label="Tavily Key" min-width="260">
                  <template #default="{ row }">
                    <el-input v-model="row.key" type="password" show-password />
                  </template>
                </el-table-column>
                <el-table-column label="调用次数" width="160">
                  <template #default="{ row }">
                    {{ Number(row.usage_count || 0) }} / {{ row.key_usage_limit || webSearchConfig.key_usage_limit }}
                  </template>
                </el-table-column>
                <el-table-column label="状态" width="100">
                  <template #default="{ row }">
                    <el-switch v-model="row.enabled" />
                  </template>
                </el-table-column>
                <el-table-column label="操作" width="180" align="center">
                  <template #default="{ row, $index }">
                    <div class="inline-actions channel-table-actions">
                      <el-button
                        size="small"
                        type="primary"
                        plain
                        :loading="row.id && webSearchTestingId === row.id"
                        @click="testWebSearchKey(row)"
                      >
                        测试
                      </el-button>
                      <el-button size="small" type="danger" :icon="Delete" @click="deleteWebSearchKey($index)">
                        删除
                      </el-button>
                    </div>
                  </template>
                </el-table-column>
              </el-table>

              <el-alert
                v-if="webSearchTestResult"
                class="channel-test-result"
                :title="webSearchTestResult.ok ? 'Tavily 测试成功' : 'Tavily 测试失败'"
                :type="webSearchTestResult.ok ? 'success' : 'error'"
                show-icon
                :closable="false"
              >
                <div class="channel-test-result__meta">
                  <span>耗时 {{ displayMs(webSearchTestResult.duration_ms) }}</span>
                  <span v-if="webSearchTestResult.key">调用 {{ webSearchTestResult.key.usage_count }} / {{ webSearchTestResult.key.key_usage_limit }}</span>
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
              <el-option label="透传或配置" value="pass_through_or_config" />
              <el-option label="透传" value="pass_through" />
              <el-option label="配置" value="config" />
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
  Delete,
  Download,
  Edit,
  Plus,
  Refresh,
  Search,
  Setting,
  SwitchButton,
  Upload,
  View
} from "@element-plus/icons-vue";

const activeTab = ref("channels");
const authenticated = ref(false);
const loadingSession = ref(true);
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

const config = reactive({ channels: [] });
const webSearchConfig = reactive(defaultWebSearchConfig());
const webSearchTestQuery = ref("OpenAI");
const webSearchTestResult = ref(null);
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
  paths: [],
  status_codes: [],
  request_statuses: ["success", "failed"]
});
const logFilters = reactive({
  request_id: "",
  model: "",
  channel_id: "",
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
const webSearchEnabledKeyCount = computed(() =>
  webSearchConfig.keys.filter((key) => key.enabled !== false && Number(key.usage_count || 0) < Number(key.key_usage_limit || webSearchConfig.key_usage_limit)).length
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
    authenticated.value = data.authenticated;
    if (authenticated.value) {
      await Promise.all([loadConfig(), loadWebSearch(), loadLogs()]);
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
      body: JSON.stringify({ password: loginPassword.value })
    });
    authenticated.value = data.authenticated;
    loginPassword.value = "";
    await Promise.all([loadConfig(), loadWebSearch(), loadLogs()]);
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

async function loadWebSearch() {
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

async function saveWebSearch() {
  webSearchSaving.value = true;
  try {
    const payload = buildWebSearchPayload();
    const data = await api("/admin/api/web-search", {
      method: "POST",
      body: JSON.stringify(payload)
    });
    assignWebSearchConfig(data);
    ElMessage.success("Web Search 模拟配置已保存");
  } catch (error) {
    ElMessage.error(error.message);
  } finally {
    webSearchSaving.value = false;
  }
}

function addWebSearchKey() {
  webSearchConfig.keys.push(defaultWebSearchKey());
  webSearchTestResult.value = null;
}

function deleteWebSearchKey(index) {
  webSearchConfig.keys.splice(index, 1);
  webSearchTestResult.value = null;
}

async function testWebSearchKey(row) {
  if (!row?.id) {
    ElMessage.warning("请先保存配置后再测试这个 Tavily Key");
    return;
  }
  webSearchTestingId.value = row.id;
  webSearchTestResult.value = null;
  try {
    const data = await api("/admin/api/web-search/test-key", {
      method: "POST",
      body: JSON.stringify({
        id: row.id,
        query: String(webSearchTestQuery.value || "").trim() || "OpenAI"
      })
    });
    webSearchTestResult.value = data;
    if (data.config) {
      assignWebSearchConfig(data.config);
    }
    if (data.ok) {
      ElMessage.success("Tavily Key 测试成功");
    } else {
      ElMessage.warning("Tavily Key 测试失败");
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
    auth_mode: "pass_through_or_config",
    headers: {},
    timeout_seconds: 120,
    retry_count: 3,
    compat: {},
    models: [],
    enabled: true
  };
}

function defaultWebSearchConfig() {
  return {
    enabled: false,
    key_usage_limit: 1000,
    keys: []
  };
}

function defaultWebSearchKey() {
  return {
    client_id: `new-${Date.now()}-${Math.random().toString(16).slice(2)}`,
    id: null,
    key: "",
    enabled: true,
    usage_count: 0,
    key_usage_limit: webSearchConfig?.key_usage_limit || 1000
  };
}

function assignWebSearchConfig(data) {
  Object.assign(webSearchConfig, defaultWebSearchConfig(), data || {}, {
    enabled: data?.enabled === true,
    key_usage_limit: Number(data?.key_usage_limit || 1000),
    keys: normalizeWebSearchKeys(data?.keys || [])
  });
}

function normalizeWebSearchKeys(keys) {
  if (!Array.isArray(keys)) {
    return [];
  }
  return keys.map((item, index) => ({
    client_id: item?.id ? `saved-${item.id}` : item?.client_id || `new-${index}-${Date.now()}`,
    id: item?.id || null,
    key: String(item?.key || item?.api_key || ""),
    enabled: item?.enabled !== false,
    usage_count: Number(item?.usage_count || 0),
    key_usage_limit: Number(item?.key_usage_limit || webSearchConfig.key_usage_limit || 1000)
  }));
}

function buildWebSearchPayload() {
  const keys = webSearchConfig.keys.map((item) => ({
    id: item.id || undefined,
    key: String(item.key || "").trim(),
    enabled: item.enabled !== false
  }));
  const emptyIndex = keys.findIndex((item) => !item.key);
  if (emptyIndex !== -1) {
    throw new Error(`第 ${emptyIndex + 1} 个 Tavily Key 不能为空`);
  }
  return {
    enabled: webSearchConfig.enabled === true,
    keys
  };
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
      if (typeof item === "string") {
        const model = item.trim();
        return { model, upstream_model: model };
      }
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
    return error ? String(error) : "Tavily 请求失败";
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
  return [answer || "Tavily 已返回结果。", links].filter(Boolean).join("\n\n");
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
