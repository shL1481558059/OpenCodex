<template>
  <div class="channels-page">
    <div class="toolbar">
      <div>
        <h2>渠道配置</h2>
        <div class="text-muted">保存单个渠道后立即生效</div>
      </div>
      <div class="toolbar-actions">
        <el-button :icon="Refresh" @click="loadConfig">刷新</el-button>
        <el-tooltip :content="bulkChannelTestDisabledReason" :disabled="!bulkChannelTestDisabledReason">
          <span>
            <el-button :icon="Connection" :disabled="Boolean(bulkChannelTestDisabledReason)" @click="openBulkChannelTest">
              批量测试
            </el-button>
          </span>
        </el-tooltip>
        <el-button :icon="Edit" :disabled="selectedChannels.length === 0" @click="openBulkChannelEdit">
          批量编辑
        </el-button>
        <el-button :icon="Download" @click="exportChannels">导出</el-button>
        <el-button :icon="Upload" @click="triggerImportChannels">导入</el-button>
        <input
          ref="importChannelsInput"
          type="file"
          accept="application/json,.json"
          style="display:none"
          @change="handleImportChannelsFile"
        />
        <el-button type="primary" :icon="Plus" @click="openChannelDrawer()">新增渠道</el-button>
      </div>
    </div>

    <el-row :gutter="12" class="channel-stats">
      <el-col :span="12" :xs="24">
        <el-statistic title="渠道总数" :value="channels.length" />
      </el-col>
      <el-col :span="12" :xs="24">
        <el-statistic title="启用渠道" :value="enabledChannelCount" />
      </el-col>
    </el-row>

    <div class="table-area">
      <el-tabs v-model="channelView" class="channel-view-tabs" @tab-change="handleChannelViewChange">
        <el-tab-pane label="原始列表" name="raw">
          <el-table
            v-if="!isMobile"
            ref="channelTableRef"
            v-loading="configLoading"
            :data="channels"
            row-key="id"
            class="channel-raw-table"
            empty-text="暂无渠道"
            @selection-change="handleChannelSelectionChange"
          >
            <el-table-column type="selection" width="48" />
            <el-table-column
              v-if="props.isSuperadmin"
              prop="owner_username"
              label="所属用户"
              min-width="130"
              show-overflow-tooltip
            />
            <el-table-column prop="group_name" label="分组" width="120" show-overflow-tooltip>
              <template #default="{ row }">
                <el-tag v-if="normalizeGroupNameText(row.group_name)" effect="plain" type="info">
                  {{ normalizeGroupNameText(row.group_name) }}
                </el-tag>
                <span v-else class="text-muted">未分组</span>
              </template>
            </el-table-column>
            <el-table-column prop="name" label="名称" min-width="140" show-overflow-tooltip />
            <el-table-column prop="type" label="服务类型" width="110">
              <template #default="{ row }">
                <el-tag>{{ row.type }}</el-tag>
              </template>
            </el-table-column>
            <el-table-column prop="baseurl" label="Base URL" min-width="220" show-overflow-tooltip />
            <el-table-column prop="priority" label="优先级" width="90" />
            <el-table-column label="容量状态" width="140">
              <template #default="{ row }">{{ formatCapacityStatus(row) }}</template>
            </el-table-column>
            <el-table-column label="健康状态" width="120">
              <template #default="{ row }">
                <el-tag :type="healthStatusTagType(row.health_status)">{{ formatHealthStatus(row.health_status) }}</el-tag>
              </template>
            </el-table-column>
            <el-table-column label="状态" width="100">
              <template #default="{ row, $index }">
                <el-switch
                  :model-value="row.enabled !== false"
                  :loading="isChannelToggleSaving(row, $index)"
                  :disabled="configLoading"
                  :width="56"
                  inline-prompt
                  active-text="启用"
                  inactive-text="停用"
                  @change="toggleChannelEnabled(row, $index, $event)"
                />
              </template>
            </el-table-column>
            <el-table-column label="操作" width="240" min-width="240" align="center">
              <template #default="{ row, $index }">
                <div class="channel-action-buttons">
                  <el-button size="small" :icon="Edit" class="action-btn" @click="openChannelDrawer(row, $index)">
                    编辑
                  </el-button>
                  <el-popconfirm title="删除这个渠道？" @confirm="deleteChannel($index)">
                    <template #reference>
                      <el-button size="small" type="danger" :icon="Delete" class="action-btn">
                        删除
                      </el-button>
                    </template>
                  </el-popconfirm>
                  <el-dropdown trigger="click">
                    <el-button size="small" :icon="MoreFilled" class="action-btn">
                      更多
                    </el-button>
                    <template #dropdown>
                      <el-dropdown-menu>
                        <el-dropdown-item :disabled="!canUseChatStreamTest(row)" @click="openChannelTest(row)">
                          <el-icon><Connection /></el-icon>测试连接
                          <span v-if="isImagesChannel(row)">（图片渠道不支持聊天流测试）</span>
                        </el-dropdown-item>
                        <el-dropdown-item @click="openChannelPricing(row)">
                          <el-icon><Edit /></el-icon>定价管理
                        </el-dropdown-item>
                        <el-dropdown-item @click="copyChannel(row)">
                          <el-icon><DocumentCopy /></el-icon>复制
                        </el-dropdown-item>
                        <el-dropdown-item
                          :disabled="!canResetChannelHealth(row) || resetChannelHealthLoadingId === row.id"
                          @click="confirmResetChannelHealth(row)"
                        >
                          <el-icon><Refresh /></el-icon>重置可用状态
                        </el-dropdown-item>
                      </el-dropdown-menu>
                    </template>
                  </el-dropdown>
                </div>
              </template>
            </el-table-column>
          </el-table>
          <div v-else v-loading="configLoading" class="mobile-channel-list">
            <div v-if="channels.length" class="mobile-selection-bar">
              <el-checkbox
                :model-value="allMobileChannelsSelected"
                :indeterminate="someMobileChannelsSelected"
                @change="toggleAllMobileChannels"
              >
                全选
              </el-checkbox>
              <span>已选 {{ selectedChannels.length }} 个</span>
            </div>
            <el-empty v-if="channels.length === 0" description="暂无渠道" />
            <article v-for="channel in channels" :key="channel.id" class="mobile-channel-card">
              <div class="mobile-channel-card__header">
                <el-checkbox
                  :model-value="isChannelSelected(channel)"
                  :aria-label="`选择渠道 ${channel.name || channel.id}`"
                  @change="(checked) => setMobileChannelSelection(channel, checked)"
                />
                <div class="mobile-channel-card__identity">
                  <strong>{{ channel.name || channel.id }}</strong>
                  <span>{{ channel.id }}</span>
                </div>
                <el-switch
                  :model-value="channel.enabled !== false"
                  :loading="isChannelToggleSaving(channel, channelIndexById(channel.id))"
                  :disabled="configLoading"
                  :width="56"
                  inline-prompt
                  active-text="启用"
                  inactive-text="停用"
                  @change="toggleChannelEnabled(channel, channelIndexById(channel.id), $event)"
                />
              </div>

              <div class="mobile-channel-card__tags">
                <el-tag size="small">{{ channel.type }}</el-tag>
                <el-tag v-if="normalizeGroupNameText(channel.group_name)" size="small" effect="plain" type="info">
                  {{ normalizeGroupNameText(channel.group_name) }}
                </el-tag>
                <el-tag size="small" :type="healthStatusTagType(channel.health_status)">
                  {{ formatHealthStatus(channel.health_status) }}
                </el-tag>
              </div>

              <dl class="mobile-channel-card__details">
                <div v-if="props.isSuperadmin">
                  <dt>所属用户</dt>
                  <dd>{{ channel.owner_username || "-" }}</dd>
                </div>
                <div class="mobile-channel-card__url">
                  <dt>Base URL</dt>
                  <dd>{{ channel.baseurl || "未设置" }}</dd>
                </div>
                <div>
                  <dt>优先级</dt>
                  <dd>{{ channel.priority ?? 0 }}</dd>
                </div>
                <div>
                  <dt>容量</dt>
                  <dd>{{ formatCapacityStatus(channel) }}</dd>
                </div>
                <div>
                  <dt>模型</dt>
                  <dd>{{ normalizeModels(channel.models).length }}</dd>
                </div>
              </dl>

              <div class="mobile-channel-card__actions">
                <el-button :icon="Edit" @click="openChannelDrawer(channel, channelIndexById(channel.id))">编辑</el-button>
                <el-button
                  :icon="Connection"
                  :disabled="!canUseChatStreamTest(channel)"
                  @click="openChannelTest(channel)"
                >
                  测试
                </el-button>
                <el-dropdown trigger="click">
                  <el-button :icon="MoreFilled">更多</el-button>
                  <template #dropdown>
                    <el-dropdown-menu>
                      <el-dropdown-item @click="openChannelPricing(channel)">
                        <el-icon><Edit /></el-icon>定价管理
                      </el-dropdown-item>
                      <el-dropdown-item @click="copyChannel(channel)">
                        <el-icon><DocumentCopy /></el-icon>复制
                      </el-dropdown-item>
                      <el-dropdown-item
                        :disabled="!canResetChannelHealth(channel) || resetChannelHealthLoadingId === channel.id"
                        @click="confirmResetChannelHealth(channel)"
                      >
                        <el-icon><Refresh /></el-icon>重置可用状态
                      </el-dropdown-item>
                      <el-dropdown-item divided @click="confirmDeleteChannel(channel)">
                        <el-icon><Delete /></el-icon>删除
                      </el-dropdown-item>
                    </el-dropdown-menu>
                  </template>
                </el-dropdown>
              </div>
            </article>
          </div>
        </el-tab-pane>

        <el-tab-pane label="归并视图" name="grouped">
          <div v-loading="configLoading" class="channel-grouped-view">
            <div v-if="isMobile && channels.length" class="mobile-selection-bar">
              <el-checkbox
                :model-value="allMobileChannelsSelected"
                :indeterminate="someMobileChannelsSelected"
                @change="toggleAllMobileChannels"
              >
                全选
              </el-checkbox>
              <span>已选 {{ selectedChannels.length }} 个</span>
            </div>
            <el-empty v-if="groupedChannelSections.length === 0" description="暂无渠道" />
            <template v-else>
              <section
                v-for="section in groupedChannelSections"
                :key="section.key"
                class="channel-group-section"
              >
                <div class="channel-group-header">
                  <div class="channel-group-title">
                    <span>{{ section.label }}</span>
                    <el-tag size="small" effect="plain">{{ section.channelCount }} 个渠道</el-tag>
                    <el-tag size="small" type="success" effect="plain">启用 {{ section.enabledCount }}</el-tag>
                  </div>
                  <div class="channel-group-meta">{{ section.baseUrlGroups.length }} 个 Base URL</div>
                </div>

                <div
                  v-for="baseGroup in section.baseUrlGroups"
                  :key="baseGroup.key"
                  class="base-url-section"
                >
                  <div class="base-url-header">
                    <div class="base-url-main">
                      <strong :title="baseGroup.baseurl">{{ baseGroup.baseurl || "未设置 Base URL" }}</strong>
                      <div class="base-url-types">
                        <el-tag
                          v-for="type in baseGroup.types"
                          :key="type"
                          size="small"
                          effect="plain"
                        >
                          {{ type }}
                        </el-tag>
                      </div>
                    </div>
                    <div class="base-url-stats">
                      <span>{{ baseGroup.channels.length }} 渠道</span>
                      <span>{{ baseGroup.keyVariants }} Key</span>
                      <span>{{ baseGroup.modelCount }} 模型</span>
                      <span>启用 {{ baseGroup.enabledCount }}</span>
                    </div>
                  </div>

                  <el-table
                    v-if="!isMobile"
                    :data="baseGroup.channels"
                    row-key="id"
                    size="small"
                    class="channel-group-table"
                    empty-text="暂无渠道"
                    @selection-change="(selection) => handleGroupedSelectionChange(baseGroup.channels, selection)"
                  >
                    <el-table-column type="selection" width="44" />
                    <el-table-column prop="name" label="名称" min-width="150" show-overflow-tooltip />
                    <el-table-column prop="type" label="服务类型" width="110">
                      <template #default="{ row }">
                        <el-tag size="small">{{ row.type }}</el-tag>
                      </template>
                    </el-table-column>
                    <el-table-column label="模型" width="80">
                      <template #default="{ row }">{{ normalizeModels(row.models).length }}</template>
                    </el-table-column>
                    <el-table-column prop="priority" label="优先级" width="80" />
                    <el-table-column label="容量" width="90">
                      <template #default="{ row }">{{ formatCapacityStatus(row) }}</template>
                    </el-table-column>
                    <el-table-column label="健康" width="110">
                      <template #default="{ row }">
                        <el-tag size="small" :type="healthStatusTagType(row.health_status)">
                          {{ formatHealthStatus(row.health_status) }}
                        </el-tag>
                      </template>
                    </el-table-column>
                    <el-table-column label="状态" width="100">
                      <template #default="{ row }">
                        <el-switch
                          :model-value="row.enabled !== false"
                          :loading="isChannelToggleSaving(row, channelIndexById(row.id))"
                          :disabled="configLoading"
                          :width="56"
                          inline-prompt
                          active-text="启用"
                          inactive-text="停用"
                          @change="toggleChannelEnabled(row, channelIndexById(row.id), $event)"
                        />
                      </template>
                    </el-table-column>
                    <el-table-column label="操作" width="220" min-width="220" align="center">
                      <template #default="{ row }">
                        <div class="channel-action-buttons">
                          <el-button size="small" :icon="Edit" class="action-btn" @click="openChannelDrawer(row, channelIndexById(row.id))">
                            编辑
                          </el-button>
                          <el-popconfirm title="删除这个渠道？" @confirm="deleteChannelById(row.id)">
                            <template #reference>
                              <el-button size="small" type="danger" :icon="Delete" class="action-btn">
                                删除
                              </el-button>
                            </template>
                          </el-popconfirm>
                          <el-dropdown trigger="click">
                            <el-button size="small" :icon="MoreFilled" class="action-btn">
                              更多
                            </el-button>
                            <template #dropdown>
                              <el-dropdown-menu>
                                <el-dropdown-item :disabled="!canUseChatStreamTest(row)" @click="openChannelTest(row)">
                                  <el-icon><Connection /></el-icon>测试连接
                                  <span v-if="isImagesChannel(row)">（图片渠道不支持聊天流测试）</span>
                                </el-dropdown-item>
                                <el-dropdown-item @click="openChannelPricing(row)">
                                  <el-icon><Edit /></el-icon>定价管理
                                </el-dropdown-item>
                                <el-dropdown-item @click="copyChannel(row)">
                                  <el-icon><DocumentCopy /></el-icon>复制
                                </el-dropdown-item>
                                <el-dropdown-item
                                  :disabled="!canResetChannelHealth(row) || resetChannelHealthLoadingId === row.id"
                                  @click="confirmResetChannelHealth(row)"
                                >
                                  <el-icon><Refresh /></el-icon>重置可用状态
                                </el-dropdown-item>
                              </el-dropdown-menu>
                            </template>
                          </el-dropdown>
                        </div>
                      </template>
                    </el-table-column>
                  </el-table>
                  <div v-else class="mobile-channel-list mobile-channel-list--grouped">
                    <article v-for="channel in baseGroup.channels" :key="channel.id" class="mobile-channel-card">
                      <div class="mobile-channel-card__header">
                        <el-checkbox
                          :model-value="isChannelSelected(channel)"
                          :aria-label="`选择渠道 ${channel.name || channel.id}`"
                          @change="(checked) => setMobileChannelSelection(channel, checked)"
                        />
                        <div class="mobile-channel-card__identity">
                          <strong>{{ channel.name || channel.id }}</strong>
                          <span>{{ channel.id }}</span>
                        </div>
                        <el-switch
                          :model-value="channel.enabled !== false"
                          :loading="isChannelToggleSaving(channel, channelIndexById(channel.id))"
                          :disabled="configLoading"
                          :width="56"
                          inline-prompt
                          active-text="启用"
                          inactive-text="停用"
                          @change="toggleChannelEnabled(channel, channelIndexById(channel.id), $event)"
                        />
                      </div>
                      <div class="mobile-channel-card__tags">
                        <el-tag size="small">{{ channel.type }}</el-tag>
                        <el-tag size="small" :type="healthStatusTagType(channel.health_status)">
                          {{ formatHealthStatus(channel.health_status) }}
                        </el-tag>
                      </div>
                      <dl class="mobile-channel-card__details">
                        <div class="mobile-channel-card__url">
                          <dt>Base URL</dt>
                          <dd>{{ channel.baseurl || "未设置" }}</dd>
                        </div>
                        <div>
                          <dt>模型</dt>
                          <dd>{{ normalizeModels(channel.models).length }}</dd>
                        </div>
                        <div>
                          <dt>优先级</dt>
                          <dd>{{ channel.priority ?? 0 }}</dd>
                        </div>
                        <div>
                          <dt>容量</dt>
                          <dd>{{ formatCapacityStatus(channel) }}</dd>
                        </div>
                      </dl>
                      <div class="mobile-channel-card__actions">
                        <el-button :icon="Edit" @click="openChannelDrawer(channel, channelIndexById(channel.id))">编辑</el-button>
                        <el-button
                          :icon="Connection"
                          :disabled="!canUseChatStreamTest(channel)"
                          @click="openChannelTest(channel)"
                        >
                          测试
                        </el-button>
                        <el-dropdown trigger="click">
                          <el-button :icon="MoreFilled">更多</el-button>
                          <template #dropdown>
                            <el-dropdown-menu>
                              <el-dropdown-item @click="openChannelPricing(channel)">
                                <el-icon><Edit /></el-icon>定价管理
                              </el-dropdown-item>
                              <el-dropdown-item @click="copyChannel(channel)">
                                <el-icon><DocumentCopy /></el-icon>复制
                              </el-dropdown-item>
                              <el-dropdown-item
                                :disabled="!canResetChannelHealth(channel) || resetChannelHealthLoadingId === channel.id"
                                @click="confirmResetChannelHealth(channel)"
                              >
                                <el-icon><Refresh /></el-icon>重置可用状态
                              </el-dropdown-item>
                              <el-dropdown-item divided @click="confirmDeleteChannel(channel)">
                                <el-icon><Delete /></el-icon>删除
                              </el-dropdown-item>
                            </el-dropdown-menu>
                          </template>
                        </el-dropdown>
                      </div>
                    </article>
                  </div>
                </div>
              </section>
            </template>
          </div>
        </el-tab-pane>
      </el-tabs>
    </div>

    <!-- 渠道编辑 Drawer -->
    <el-drawer
      v-model="channelDrawerVisible"
      :title="editingIndex === -1 ? '新增渠道' : '编辑渠道'"
      :size="isMobile ? '100%' : '720px'"
      class="channel-editor-drawer"
    >
      <el-form label-position="top" :model="channelDraft">
        <el-row :gutter="12">
          <el-col :span="12">
            <el-form-item>
              <template #label>
                <span>启用</span>
              </template>
              <el-switch v-model="channelDraft.enabled" />
            </el-form-item>
          </el-col>
          <el-col v-if="supportsApplyPatchPromptCompat(channelDraft.type)" :span="24">
            <el-form-item>
              <template #label>
                <span class="form-label-with-tip">
                  <span>兼容 apply_patch 提示词</span>
                  <el-tooltip content="将补丁类提示词改写为上游更容易接受的格式，降低 apply_patch 指令被拒绝的概率。" placement="top">
                    <el-icon class="form-label-tip"><Warning /></el-icon>
                  </el-tooltip>
                </span>
              </template>
              <el-switch
                v-model="compatTexts.enable_apply_patch_prompt_compat"
                active-text="开启"
                inactive-text="关闭"
              />
            </el-form-item>
          </el-col>
          <el-col v-if="channelDraft.type === 'messages'" :span="24">
            <el-form-item>
              <template #label>
                <span class="form-label-with-tip">
                  <span>保留思考历史 (preserve_thinking_history)</span>
                  <el-tooltip content="透传并恢复思考相关内容，尽量保持多轮请求中的推理上下文连续。" placement="top">
                    <el-icon class="form-label-tip"><Warning /></el-icon>
                  </el-tooltip>
                </span>
              </template>
             <el-switch
               v-model="compatTexts.preserve_thinking_history"
               active-text="开启"
               inactive-text="关闭"
             />
             <div class="text-muted" style="margin-top: 4px; font-size: 12px">
               开启后会将对端 Anthropic thinking blocks（含签名）编码到 encrypted_content 字段，并在回传时恢复，确保交错思考上下文不丢失
             </div>
           </el-form-item>
         </el-col>
          <el-col :span="12">
            <el-form-item label="名称">
              <el-input v-model="channelDraft.name" />
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item label="分组">
              <el-input v-model="channelDraft.group_name" placeholder="未分组" />
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item label="服务类型">
              <el-select v-model="channelDraft.type" class="full-width" @change="handleChannelTypeChange">
                <el-option label="responses" value="responses" />
                <el-option label="chat" value="chat" />
                <el-option label="messages" value="messages" />
                <el-option label="images" value="images" />
              </el-select>
            </el-form-item>
          </el-col>
          <el-col v-if="isImagesChannel(channelDraft)" :span="12">
            <el-form-item label="图片 API 方言">
              <el-select v-model="compatTexts.images_api_dialect" class="full-width">
                <el-option label="OpenAI Images API" value="openai" />
                <el-option label="xAI Images API" value="xai" />
              </el-select>
              <div class="text-muted" style="margin-top: 4px; font-size: 12px">
                首版 generation / edit 均使用非流式请求
              </div>
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
            <el-form-item label="熔断时间（秒）">
              <el-input-number
                v-model="channelDraft.circuit_break_duration_seconds"
                :min="0"
                :step="1"
                step-strictly
                class="full-width"
              />
              <div class="text-muted" style="margin-top: 4px; font-size: 12px">
                0 表示不标记熔断状态
              </div>
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item label="重试次数">
              <el-input-number
                v-model="channelDraft.retry_count"
                :min="0"
                :step="1"
                step-strictly
                :disabled="isImagesChannel(channelDraft)"
                class="full-width"
              />
              <div v-if="isImagesChannel(channelDraft)" class="text-muted" style="margin-top: 4px; font-size: 12px">
                图片 generation / edit 不自动重试，固定为 0
              </div>
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item label="优先级">
              <el-input-number
                v-model="channelDraft.priority"
                :min="0"
                :step="1"
                step-strictly
                class="full-width"
              />
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item label="容量">
              <el-input-number
                v-model="channelDraft.capacity"
                :min="1"
                :step="1"
                step-strictly
                class="full-width"
              />
            </el-form-item>
          </el-col>
        </el-row>

        <el-divider content-position="left">请求头</el-divider>
        <el-input v-model="headersText" type="textarea" :rows="4" placeholder='{"X-Test":"yes"}' />

        <el-divider content-position="left">模型映射</el-divider>
        <el-table v-if="!isMobile" :data="channelDraft.models" empty-text="暂无模型映射">
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
        <div v-else class="model-mapping-list">
          <div v-if="channelDraft.models.length === 0" class="model-mapping-empty">暂无模型映射</div>
          <div v-for="(mapping, mappingIndex) in channelDraft.models" :key="mappingIndex" class="model-mapping-card">
            <div class="model-mapping-card__field">
              <span>请求模型</span>
              <el-input v-model="mapping.model" autocomplete="off" />
            </div>
            <div class="model-mapping-card__field">
              <span>上游模型</span>
              <el-input v-model="mapping.upstream_model" autocomplete="off" />
            </div>
            <el-button
              type="danger"
              plain
              :icon="Delete"
              @click="channelDraft.models.splice(mappingIndex, 1)"
            >
              删除映射
            </el-button>
          </div>
        </div>
        <el-button style="margin-top: 8px" :icon="Plus" @click="channelDraft.models.push(defaultModelMapping())">
          添加模型
        </el-button>
        <el-button style="margin-top: 8px; margin-left: 8px" :loading="discoverLoading" @click="discoverModels">
          发现模型
        </el-button>
        <el-button style="margin-top: 8px; margin-left: 8px" @click="openBatchEdit">
          批量编辑
        </el-button>

        <el-divider content-position="left">兼容规则</el-divider>
        <el-row :gutter="12">
          <el-col :span="12">
            <el-form-item>
              <template #label>
                <span class="form-label-with-tip">
                  <span>rename_params</span>
                  <el-tooltip content="将请求参数名重命名后再发给上游。每行一个 from=to 映射。" placement="top">
                    <el-icon class="form-label-tip"><Warning /></el-icon>
                  </el-tooltip>
                </span>
              </template>
              <el-input v-model="compatTexts.rename_params" type="textarea" :rows="4" placeholder="from=to" />
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item>
              <template #label>
                <span class="form-label-with-tip">
                  <span>drop_params</span>
                  <el-tooltip content="丢弃指定请求参数，避免把上游不支持或不需要的参数发出去。每行一个参数名。" placement="top">
                    <el-icon class="form-label-tip"><Warning /></el-icon>
                  </el-tooltip>
                </span>
              </template>
              <el-input v-model="compatTexts.drop_params" type="textarea" :rows="4" placeholder="每行一个参数" />
            </el-form-item>
          </el-col>
          <el-col v-if="!isImagesChannel(channelDraft)" :span="12">
            <el-form-item>
              <template #label>
                <span class="form-label-with-tip">
                  <span>drop_tool_types</span>
                  <el-tooltip content="丢弃指定工具类型，防止向不兼容的上游传递对应工具定义。每行一个工具类型。" placement="top">
                    <el-icon class="form-label-tip"><Warning /></el-icon>
                  </el-tooltip>
                </span>
              </template>
              <el-input v-model="compatTexts.drop_tool_types" type="textarea" :rows="4" placeholder="image_generation&#10;image_generation_call" />
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item>
              <template #label>
                <span class="form-label-with-tip">
                  <span>force_params</span>
                  <el-tooltip content="强制覆盖请求参数，即使调用方已传值也会被这里的配置替换。每行一个 name=value。" placement="top">
                    <el-icon class="form-label-tip"><Warning /></el-icon>
                  </el-tooltip>
                </span>
              </template>
              <el-input v-model="compatTexts.force_params" type="textarea" :rows="4" placeholder='name={"type":"text"}' />
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item>
              <template #label>
                <span class="form-label-with-tip">
                  <span>default_params</span>
                  <el-tooltip content="为缺失参数补默认值；只有调用方未传该参数时才会生效。每行一个 name=value。" placement="top">
                    <el-icon class="form-label-tip"><Warning /></el-icon>
                  </el-tooltip>
                </span>
              </template>
              <el-input v-model="compatTexts.default_params" type="textarea" :rows="4" placeholder="temperature=0.2" />
            </el-form-item>
          </el-col>
          <el-col :span="24">
            <el-form-item>
              <template #label>
                <span class="form-label-with-tip">
                  <span>unsupported_params</span>
                  <el-tooltip content="声明上游不支持的参数，命中后可提前过滤或提示，避免请求直接失败。每行一个参数名。" placement="top">
                    <el-icon class="form-label-tip"><Warning /></el-icon>
                  </el-tooltip>
                </span>
              </template>
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

    <el-dialog
      v-model="discoverModelsVisible"
      title="发现模型"
      width="720px"
      :fullscreen="isMobile"
      class="channel-mobile-dialog discover-models-dialog"
    >
      <el-table
        v-if="!isMobile"
        ref="discoveredModelsTableRef"
        :data="discoveredModelRows"
        row-key="model"
        max-height="420"
        empty-text="未发现模型"
        @selection-change="handleDiscoveredModelSelectionChange"
      >
        <el-table-column type="selection" width="48" :selectable="isDiscoveredModelSelectable" />
        <el-table-column prop="model" label="模型" min-width="260" show-overflow-tooltip />
        <el-table-column label="映射状态" width="120">
          <template #default="{ row }">
            <el-tag :type="row.exists ? 'info' : 'success'">
              {{ row.exists ? "已存在" : "可添加" }}
            </el-tag>
          </template>
        </el-table-column>
      </el-table>
      <div v-else class="discover-model-list">
        <div v-if="discoveredModelRows.length === 0" class="model-mapping-empty">未发现模型</div>
        <div v-for="row in discoveredModelRows" :key="row.model" class="discover-model-card">
          <el-checkbox
            :model-value="isDiscoveredModelSelected(row.model)"
            :disabled="row.exists"
            @change="(checked) => setDiscoveredModelSelection(row, checked)"
          />
          <span class="discover-model-card__name">{{ row.model }}</span>
          <el-tag size="small" :type="row.exists ? 'info' : 'success'">
            {{ row.exists ? "已存在" : "可添加" }}
          </el-tag>
        </div>
      </div>

      <template #footer>
        <div class="drawer-footer">
          <el-button @click="discoverModelsVisible = false">取消</el-button>
          <el-button
            type="primary"
            :disabled="selectedDiscoveredModels.length === 0"
            @click="addSelectedModels"
          >
            添加到模型映射
          </el-button>
        </div>
      </template>
    </el-dialog>

    <el-dialog
      v-model="batchEditVisible"
      title="批量编辑模型映射"
      width="640px"
      :fullscreen="isMobile"
      class="channel-mobile-dialog"
    >
      <el-input
        v-model="batchEditText"
        type="textarea"
        :rows="16"
        placeholder="每行一个映射，格式：请求模型,上游模型&#10;例如：&#10;gpt-4o,gpt-4o-2024-08-06&#10;claude-3-5-sonnet,claude-3-5-sonnet-20241022"
      />
      <template #footer>
        <div class="drawer-footer">
          <el-button @click="batchEditVisible = false">取消</el-button>
          <el-button type="primary" @click="applyBatchEdit">确定</el-button>
        </div>
      </template>
    </el-dialog>

    <el-dialog
      v-model="channelPricingVisible"
      :title="channelPricingTitle"
      width="1080px"
      :fullscreen="isMobile"
      class="channel-mobile-dialog channel-pricing-dialog"
    >
      <el-table
        v-if="!isMobile"
        v-loading="channelPricingLoading"
        :data="channelPricingRows"
        row-key="upstream_model"
        max-height="520"
        empty-text="暂无上游模型"
      >
        <el-table-column prop="upstream_model" label="上游模型" min-width="190" show-overflow-tooltip />
        <el-table-column label="状态" width="110">
          <template #default="{ row }">
            <el-tag :type="row.overridden ? 'warning' : 'info'">
              {{ row.overridden ? "覆盖全局" : "继承全局" }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column label="模型信息" min-width="260" show-overflow-tooltip>
          <template #default="{ row }">
            {{ formatChannelPricingModel(row) }}
          </template>
        </el-table-column>
        <el-table-column label="输入" width="115" align="right">
          <template #default="{ row }">{{ pricingRuleSummary(effectiveChannelPricingModel(row), "input") }}</template>
        </el-table-column>
        <el-table-column label="输出" width="115" align="right">
          <template #default="{ row }">{{ pricingRuleSummary(effectiveChannelPricingModel(row), "output") }}</template>
        </el-table-column>
        <el-table-column label="缓存写" width="115" align="right">
          <template #default="{ row }">{{ pricingRuleSummary(effectiveChannelPricingModel(row), "cache_write") }}</template>
        </el-table-column>
        <el-table-column label="缓存读" width="115" align="right">
          <template #default="{ row }">{{ pricingRuleSummary(effectiveChannelPricingModel(row), "cache_read") }}</template>
        </el-table-column>
        <el-table-column label="操作" width="210" align="center">
          <template #default="{ row }">
            <div class="inline-actions channel-table-actions">
              <el-button size="small" :icon="Edit" @click="openChannelPricingEditor(row)">编辑</el-button>
              <el-popconfirm
                v-if="row.overridden && row.override_model?.id"
                title="恢复为全局配置？"
                @confirm="restoreChannelPricing(row)"
              >
                <template #reference>
                  <el-button
                    size="small"
                    :icon="Refresh"
                    :loading="channelPricingRestoringId === row.override_model.id"
                  >
                    恢复默认
                  </el-button>
                </template>
              </el-popconfirm>
            </div>
          </template>
        </el-table-column>
      </el-table>
      <div v-else v-loading="channelPricingLoading" class="pricing-model-list">
        <div v-if="channelPricingRows.length === 0" class="model-mapping-empty">暂无上游模型</div>
        <article v-for="row in channelPricingRows" :key="row.upstream_model" class="pricing-model-card">
          <div class="pricing-model-card__header">
            <strong>{{ row.upstream_model }}</strong>
            <el-tag size="small" :type="row.overridden ? 'warning' : 'info'">
              {{ row.overridden ? "覆盖全局" : "继承全局" }}
            </el-tag>
          </div>
          <div class="pricing-model-card__info">{{ formatChannelPricingModel(row) }}</div>
          <dl class="pricing-model-card__rules">
            <div><dt>输入</dt><dd>{{ pricingRuleSummary(effectiveChannelPricingModel(row), "input") }}</dd></div>
            <div><dt>输出</dt><dd>{{ pricingRuleSummary(effectiveChannelPricingModel(row), "output") }}</dd></div>
            <div><dt>缓存写</dt><dd>{{ pricingRuleSummary(effectiveChannelPricingModel(row), "cache_write") }}</dd></div>
            <div><dt>缓存读</dt><dd>{{ pricingRuleSummary(effectiveChannelPricingModel(row), "cache_read") }}</dd></div>
          </dl>
          <div class="mobile-channel-card__actions">
            <el-button size="small" :icon="Edit" @click="openChannelPricingEditor(row)">编辑</el-button>
            <el-popconfirm
              v-if="row.overridden && row.override_model?.id"
              title="恢复为全局配置？"
              @confirm="restoreChannelPricing(row)"
            >
              <template #reference>
                <el-button
                  size="small"
                  :icon="Refresh"
                  :loading="channelPricingRestoringId === row.override_model.id"
                >
                  恢复默认
                </el-button>
              </template>
            </el-popconfirm>
          </div>
        </article>
      </div>

      <template #footer>
        <div class="drawer-footer">
          <el-button @click="channelPricingVisible = false">关闭</el-button>
          <el-button :icon="Refresh" :loading="channelPricingLoading" @click="loadChannelPricingRows">刷新</el-button>
        </div>
      </template>
    </el-dialog>

    <el-dialog
      v-model="channelPricingEditorVisible"
      :title="channelPricingEditorTitle"
      width="880px"
      :fullscreen="isMobile"
      class="channel-mobile-dialog channel-pricing-editor-dialog"
      append-to-body
    >
      <el-form label-position="top" :model="channelPricingDraft">
        <el-row :gutter="16">
          <el-col :span="16">
            <el-form-item label="供应商">
              <el-select v-model="channelPricingDraft.provider_code" class="full-width">
                <el-option
                  v-for="provider in modelProviders"
                  :key="provider.code"
                  :label="provider.name"
                  :value="provider.code"
                />
              </el-select>
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="状态">
              <el-switch v-model="channelPricingDraft.enabled" />
            </el-form-item>
          </el-col>
        </el-row>

        <el-row :gutter="16">
          <el-col :span="12">
            <el-form-item label="上游模型">
              <el-input v-model="channelPricingDraft.upstream_model" disabled />
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item label="模型标识">
              <el-input v-model="channelPricingDraft.model_key" autocomplete="off" />
            </el-form-item>
          </el-col>
        </el-row>

        <el-row :gutter="16">
          <el-col :span="12">
            <el-form-item label="显示名称">
              <el-input v-model="channelPricingDraft.display_name" autocomplete="off" />
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item label="匹配类型">
              <el-select v-model="channelPricingDraft.match_type" class="full-width">
                <el-option label="精确" value="exact" />
                <el-option label="前缀" value="prefix" />
                <el-option label="后缀" value="suffix" />
                <el-option label="包含" value="contains" />
              </el-select>
            </el-form-item>
          </el-col>
        </el-row>

        <el-form-item label="匹配键">
          <el-input v-model="channelPricingDraft.match_pattern" autocomplete="off" />
        </el-form-item>

        <el-form-item label="描述">
          <el-input v-model="channelPricingDraft.description" type="textarea" :rows="2" />
        </el-form-item>

        <el-row :gutter="16">
          <el-col :span="8">
            <el-form-item label="支持图片">
              <el-switch v-model="channelPricingDraft.capabilities.supports_image" />
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="上下文窗口">
              <el-input-number
                v-model="channelPricingDraft.capabilities.context_window"
                :min="0"
                :step="8192"
                class="full-width"
              />
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="币种">
              <el-input v-model="channelPricingDraft.pricing.currency" autocomplete="off" />
            </el-form-item>
          </el-col>
        </el-row>

        <el-divider content-position="left">计费规则</el-divider>
        <el-table
          v-if="!isMobile"
          :data="channelPricingDraft.pricing.rules"
          border
          size="small"
          class="pricing-rule-table"
        >
          <el-table-column label="计费项" width="110">
            <template #default="{ row }">{{ formatBillingItem(row.billing_item) }}</template>
          </el-table-column>
          <el-table-column label="模式" width="170">
            <template #default="{ row }">
              <el-select v-model="row.billing_mode" class="full-width">
                <el-option label="按次" value="per_request" />
                <el-option label="每百万 token" value="per_million_tokens" />
                <el-option label="阶梯 token" value="tiered_tokens" />
              </el-select>
            </template>
          </el-table-column>
          <el-table-column label="单价" width="160">
            <template #default="{ row }">
              <el-input-number v-model="row.unit_price" :min="0" :precision="8" :step="0.01" class="full-width" />
            </template>
          </el-table-column>
          <el-table-column label="阶梯">
            <template #default="{ row }">
              <el-input
                v-model="row.tiers_text"
                type="textarea"
                :rows="2"
                :disabled="row.billing_mode !== 'tiered_tokens'"
              />
            </template>
          </el-table-column>
          <el-table-column label="启用" width="80" align="center">
            <template #default="{ row }">
              <el-switch v-model="row.enabled" />
            </template>
          </el-table-column>
        </el-table>
        <div v-else class="pricing-rule-list">
          <div v-for="rule in channelPricingDraft.pricing.rules" :key="rule.billing_item" class="pricing-rule-card">
            <div class="pricing-rule-card__header">
              <strong>{{ formatBillingItem(rule.billing_item) }}</strong>
              <el-switch v-model="rule.enabled" inline-prompt active-text="启用" inactive-text="停用" />
            </div>
            <el-form-item label="模式">
              <el-select v-model="rule.billing_mode" class="full-width">
                <el-option label="按次" value="per_request" />
                <el-option label="每百万 token" value="per_million_tokens" />
                <el-option label="阶梯 token" value="tiered_tokens" />
              </el-select>
            </el-form-item>
            <el-form-item label="单价">
              <el-input-number v-model="rule.unit_price" :min="0" :precision="8" :step="0.01" class="full-width" />
            </el-form-item>
            <el-form-item label="阶梯">
              <el-input
                v-model="rule.tiers_text"
                type="textarea"
                :rows="2"
                :disabled="rule.billing_mode !== 'tiered_tokens'"
              />
            </el-form-item>
          </div>
        </div>

        <el-collapse class="advanced-collapse">
          <el-collapse-item title="Catalog JSON" name="catalog">
            <el-input v-model="channelPricingCatalogText" type="textarea" :rows="8" />
          </el-collapse-item>
        </el-collapse>
      </el-form>

      <template #footer>
        <div class="drawer-footer">
          <el-button @click="channelPricingEditorVisible = false">取消</el-button>
          <el-button type="primary" :loading="channelPricingSaving" @click="saveChannelPricing">保存</el-button>
        </div>
      </template>
    </el-dialog>

    <el-dialog
      v-model="bulkEditVisible"
      title="批量编辑渠道"
      width="640px"
      :fullscreen="isMobile"
      class="channel-mobile-dialog"
    >
      <el-alert
        class="bulk-edit-alert"
        type="info"
        :title="`将修改 ${selectedChannels.length} 个渠道`"
        show-icon
        :closable="false"
      />
      <div class="bulk-edit-grid">
        <div class="bulk-edit-row">
          <el-checkbox v-model="bulkEditFields.group_name">分组</el-checkbox>
          <el-input
            v-model="bulkEditDraft.group_name"
            :disabled="!bulkEditFields.group_name"
            placeholder="未分组"
          />
        </div>
        <div class="bulk-edit-row">
          <el-checkbox v-model="bulkEditFields.enabled">启用状态</el-checkbox>
          <el-switch
            v-model="bulkEditDraft.enabled"
            :disabled="!bulkEditFields.enabled"
            inline-prompt
            active-text="启用"
            inactive-text="停用"
            :width="56"
          />
        </div>
        <div class="bulk-edit-row">
          <el-checkbox v-model="bulkEditFields.priority">优先级</el-checkbox>
          <el-input-number
            v-model="bulkEditDraft.priority"
            :disabled="!bulkEditFields.priority"
            :min="0"
            :step="1"
            step-strictly
            class="full-width"
          />
        </div>
        <div class="bulk-edit-row">
          <el-checkbox v-model="bulkEditFields.capacity">容量</el-checkbox>
          <el-input-number
            v-model="bulkEditDraft.capacity"
            :disabled="!bulkEditFields.capacity"
            :min="1"
            :step="1"
            step-strictly
            class="full-width"
          />
        </div>
        <div class="bulk-edit-row">
          <el-checkbox v-model="bulkEditFields.timeout_seconds">超时秒数</el-checkbox>
          <el-input-number
            v-model="bulkEditDraft.timeout_seconds"
            :disabled="!bulkEditFields.timeout_seconds"
            :min="1"
            :step="1"
            step-strictly
            class="full-width"
          />
        </div>
        <div class="bulk-edit-row">
          <el-checkbox v-model="bulkEditFields.retry_count" :disabled="selectedChannelsContainImages">重试次数</el-checkbox>
          <el-input-number
            v-model="bulkEditDraft.retry_count"
            :disabled="selectedChannelsContainImages || !bulkEditFields.retry_count"
            :min="0"
            :step="1"
            step-strictly
            class="full-width"
          />
        </div>
        <div class="bulk-edit-row">
          <el-checkbox v-model="bulkEditFields.circuit_break_duration_seconds">熔断时间</el-checkbox>
          <el-input-number
            v-model="bulkEditDraft.circuit_break_duration_seconds"
            :disabled="!bulkEditFields.circuit_break_duration_seconds"
            :min="0"
            :step="1"
            step-strictly
            class="full-width"
          />
        </div>
      </div>

      <template #footer>
        <div class="drawer-footer">
          <el-button @click="bulkEditVisible = false">取消</el-button>
          <el-button type="primary" :loading="bulkEditSaving" @click="applyBulkChannelEdit">应用修改</el-button>
        </div>
      </template>
    </el-dialog>

    <!-- 批量测试 Dialog -->
    <el-dialog
      v-model="bulkTestVisible"
      title="批量测试渠道"
      width="960px"
      :fullscreen="isMobile"
      class="channel-mobile-dialog bulk-test-dialog"
      :close-on-click-modal="!bulkTestRunning"
      :before-close="handleBulkTestBeforeClose"
    >
      <el-form label-position="top" :model="bulkTestForm" class="channel-test-form">
        <el-row :gutter="12">
          <el-col :span="12" :xs="24">
            <el-form-item label="提示词">
              <el-input
                v-model="bulkTestForm.prompt"
                type="textarea"
                :rows="3"
                placeholder="请输入用于测试连接的提示词"
              />
            </el-form-item>
          </el-col>
          <el-col :span="6" :xs="24">
            <el-form-item label="最大输出 Tokens">
              <el-input-number
                v-model="bulkTestForm.max_output_tokens"
                :min="1"
                :step="1"
                step-strictly
                class="full-width"
              />
            </el-form-item>
          </el-col>
          <el-col :span="6" :xs="24">
            <el-form-item label="并发数">
              <el-input-number
                v-model="bulkTestForm.concurrency"
                :min="1"
                :max="10"
                :step="1"
                step-strictly
                class="full-width"
              />
            </el-form-item>
          </el-col>
        </el-row>
      </el-form>

      <div class="bulk-test-summary">
        <el-tag type="info">总数 {{ bulkTestSummary.total }}</el-tag>
        <el-tag type="success">成功 {{ bulkTestSummary.success }}</el-tag>
        <el-tag type="danger">失败 {{ bulkTestSummary.error }}</el-tag>
        <el-tag type="warning">测试中 {{ bulkTestSummary.running }}</el-tag>
        <el-tag type="info">等待 {{ bulkTestSummary.pending }}</el-tag>
        <el-tag v-if="bulkTestSummary.cancelled" type="info">取消 {{ bulkTestSummary.cancelled }}</el-tag>
      </div>

      <el-table
        v-if="!isMobile"
        :data="bulkTestRows"
        row-key="key"
        max-height="460"
        class="bulk-test-table"
        empty-text="暂无测试渠道"
      >
        <el-table-column prop="channel.name" label="渠道" min-width="150" show-overflow-tooltip>
          <template #default="{ row }">
            {{ row.channel.name || row.channel.id }}
          </template>
        </el-table-column>
        <el-table-column prop="channel.type" label="服务类型" width="110">
          <template #default="{ row }">
            <el-tag>{{ row.channel.type }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column prop="model" label="模型" min-width="170" show-overflow-tooltip>
          <template #default="{ row }">
            <el-select
              v-model="row.model"
              size="small"
              filterable
              allow-create
              default-first-option
              placeholder="选择模型"
              class="bulk-test-model-select"
            >
              <el-option
                v-for="model in row.modelOptions"
                :key="model"
                :label="model"
                :value="model"
              />
            </el-select>
          </template>
        </el-table-column>
        <el-table-column label="状态" width="100">
          <template #default="{ row }">
            <el-tag :type="bulkTestStatusTagType(row.status)">
              {{ formatBulkTestStatus(row.status) }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column label="耗时" width="100">
          <template #default="{ row }">
            {{ displayMs(row.result?.duration_ms) }}
          </template>
        </el-table-column>
        <el-table-column label="结果" min-width="260">
          <template #default="{ row }">
            <div class="bulk-test-output">{{ formatBulkTestResult(row) }}</div>
          </template>
        </el-table-column>
      </el-table>
      <div v-else class="bulk-test-card-list">
        <div v-if="bulkTestRows.length === 0" class="model-mapping-empty">暂无测试渠道</div>
        <article v-for="row in bulkTestRows" :key="row.key" class="bulk-test-card">
          <div class="bulk-test-card__header">
            <strong>{{ row.channel.name || row.channel.id }}</strong>
            <el-tag size="small" :type="bulkTestStatusTagType(row.status)">
              {{ formatBulkTestStatus(row.status) }}
            </el-tag>
          </div>
          <dl class="bulk-test-card__details">
           <div><dt>服务类型</dt><dd>{{ row.channel.type }}</dd></div>
            <div><dt>模型</dt><dd>
              <el-select
                v-model="row.model"
                size="small"
                filterable
                allow-create
                default-first-option
                placeholder="选择模型"
                class="bulk-test-model-select"
              >
                <el-option
                  v-for="model in row.modelOptions"
                  :key="model"
                  :label="model"
                  :value="model"
                />
              </el-select>
            </dd></div>
           <div><dt>耗时</dt><dd>{{ displayMs(row.result?.duration_ms) }}</dd></div>
          </dl>
          <div class="bulk-test-output">{{ formatBulkTestResult(row) }}</div>
        </article>
      </div>

      <template #footer>
        <div class="drawer-footer">
          <el-button @click="closeBulkTestDialog">关闭</el-button>
          <el-button v-if="bulkTestRunning" type="warning" @click="cancelBulkChannelTest">取消测试</el-button>
          <el-button
            type="primary"
            :loading="bulkTestRunning"
            :disabled="bulkTestRows.length === 0"
            @click="runBulkChannelTests"
          >
            {{ bulkTestRunButtonText }}
          </el-button>
        </div>
      </template>
    </el-dialog>

    <!-- 渠道测试 Dialog -->
    <el-dialog
      v-model="channelTestVisible"
      :title="channelTestTitle"
      width="640px"
      :fullscreen="isMobile"
      class="channel-mobile-dialog channel-test-dialog"
    >
      <el-descriptions v-if="testingChannel" :column="isMobile ? 1 : 2" border class="channel-test-summary">
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
        :title="getChannelTestAlertTitle(testResult)"
        :type="getChannelTestAlertType(testResult)"
        show-icon
        :closable="false"
      >
        <div class="channel-test-result__meta">
          <span v-if="testResult.duration_ms !== undefined">耗时 {{ displayMs(testResult.duration_ms) }}</span>
        </div>
        <div class="channel-test-output">{{ formatChannelTestResult(testResult) }}</div>
      </el-alert>

      <el-collapse
        v-if="hasChannelTestDetails(testResult)"
        class="channel-test-details"
      >
        <el-collapse-item title="响应详情" name="details">
          <div class="channel-test-detail-grid">
            <div v-if="testResult.details" class="channel-test-detail-section">
              <div class="channel-test-detail-title">测试信息</div>
              <pre class="channel-test-json">{{ formatChannelTestJson(channelTestSummaryDetails(testResult)) }}</pre>
            </div>
            <div v-if="testResult.details?.upstream_response" class="channel-test-detail-section">
              <div class="channel-test-detail-title">上游响应</div>
              <pre class="channel-test-json">{{ formatChannelTestJson(testResult.details.upstream_response) }}</pre>
            </div>
            <div v-if="testResult.details?.error_response" class="channel-test-detail-section">
              <div class="channel-test-detail-title">错误响应</div>
              <pre class="channel-test-json">{{ formatChannelTestJson(testResult.details.error_response) }}</pre>
            </div>
            <div v-if="testResult.details?.upstream_request" class="channel-test-detail-section">
              <div class="channel-test-detail-title">上游请求</div>
              <pre class="channel-test-json">{{ formatChannelTestJson(testResult.details.upstream_request) }}</pre>
            </div>
            <div v-if="testResult.raw_events?.length" class="channel-test-detail-section">
              <div class="channel-test-detail-title">原始事件</div>
              <pre class="channel-test-json">{{ formatChannelTestJson(testResult.raw_events) }}</pre>
            </div>
          </div>
        </el-collapse-item>
      </el-collapse>

      <template #footer>
        <div class="drawer-footer">
          <el-button @click="channelTestVisible = false">关闭</el-button>
          <el-button type="primary" :loading="testLoading" @click="testChannel">测试连接</el-button>
        </div>
      </template>
    </el-dialog>
  </div>
</template>

<script setup>
import { ref, reactive, computed, nextTick, onMounted, onBeforeUnmount } from "vue";
import { ElMessage } from "element-plus/es/components/message/index.mjs";
import { ElMessageBox } from "element-plus/es/components/message-box/index.mjs";
import {
  applyChannelTestStreamEvent,
  createChannelTestState,
  finalizeChannelTestResult,
  formatChannelTestResult,
  getChannelTestAlertTitle,
  getChannelTestAlertType
} from "./channelTestState.js";
import {
  applyChannelTypeContract,
  buildImagesCompat,
  canUseChatStreamTest,
  isImagesChannel
} from "./channelImagesState.js";
import { streamChannelTest } from "./channelTestStream.js";
import { createSseStream } from "./api/sseClient.js";
import {
  Connection,
  Delete,
  Download,
  DocumentCopy,
  Edit,
  MoreFilled,
  Plus,
  Refresh,
  Upload,
  Warning,
} from "@element-plus/icons-vue";
const props = defineProps({
  api: { type: Function, required: true },
  isSuperadmin: { type: Boolean, default: false },
});
const configLoading = ref(false);

// --- SSE: 渠道运行时实时更新 ---
const runtimeStream = createSseStream({
  path: "/channels/runtime/stream",
  events: { runtime: applyRuntimePayload }
});

const saveLoading = ref(false);
const testLoading = ref(false);
const discoverLoading = ref(false);
const channelDrawerVisible = ref(false);
const editingIndex = ref(-1);
const channelDraft = reactive(defaultChannel());
const headersText = ref("{}");
const compatTexts = reactive({
  enable_apply_patch_prompt_compat: false,
  preserve_thinking_history: false,
  rename_params: "",
  drop_params: "",
  drop_tool_types: "",
  force_params: "",
  default_params: "",
  unsupported_params: "",
  images_api_dialect: "openai"
});

const testResult = ref(null);
const channelTestVisible = ref(false);
const testingChannel = ref(null);
const channelTestForm = reactive({ model: "", prompt: "你好" });
const discoverModelsVisible = ref(false);
const discoveredModelsTableRef = ref(null);
const discoveredModels = ref([]);
const selectedDiscoveredModels = ref([]);

const batchEditVisible = ref(false);
const batchEditText = ref("");

const modelProviders = ref([]);
const billingItems = [
  { value: "input", label: "输入" },
  { value: "output", label: "输出" },
  { value: "cache_write", label: "缓存写" },
  { value: "cache_read", label: "缓存读" }
];
const channelPricingVisible = ref(false);
const channelPricingEditorVisible = ref(false);
const channelPricingLoading = ref(false);
const channelPricingSaving = ref(false);
const channelPricingRestoringId = ref("");
const channelPricingRows = ref([]);
const channelPricingChannel = ref(null);
const channelPricingCatalogText = ref("{}");
const channelPricingDraft = reactive(emptyChannelPricingDraft());
const config = reactive({ channels: [] });
const channelTableRef = ref(null);
const selectedChannels = ref([]);
const channelView = ref("raw");
const bulkEditVisible = ref(false);
const bulkEditSaving = ref(false);
const bulkEditFields = reactive(defaultBulkEditFields());
const bulkEditDraft = reactive(defaultBulkEditDraft());
const bulkTestVisible = ref(false);
const bulkTestRunning = ref(false);
const bulkTestCancelRequested = ref(false);
const bulkTestRows = ref([]);
const bulkTestForm = reactive({
  prompt: "你好",
  max_output_tokens: 256,
  concurrency: 3
});
const channelToggleSavingKeys = reactive(new Set());
const resetChannelHealthLoadingId = ref("");
const bulkTestAbortControllers = new Set();

const isMobile = ref(false);
let mobileMediaQuery;

function updateMobileViewport(event) {
  const nextMobile = Boolean(event?.matches ?? mobileMediaQuery?.matches);
  const changed = isMobile.value !== nextMobile;
  isMobile.value = nextMobile;

  if (changed && !nextMobile) {
    restoreDesktopSelections();
  }
}

const channels = computed(() => config.channels || []);
const enabledChannelCount = computed(() => channels.value.filter((c) => c.enabled !== false).length);
const selectedChannelIds = computed(() => new Set(selectedChannels.value.map((channel) => channel.id)));
const allMobileChannelsSelected = computed(() =>
  channels.value.length > 0 && channels.value.every((channel) => selectedChannelIds.value.has(channel.id))
);
const someMobileChannelsSelected = computed(() =>
  selectedChannels.value.length > 0 && !allMobileChannelsSelected.value
);
const selectedChannelsContainImages = computed(() => selectedChannels.value.some(isImagesChannel));
const bulkChannelTestDisabledReason = computed(() => {
  if (selectedChannels.value.length === 0) return "请先选择渠道";
  return selectedChannels.value.some(isImagesChannel)
    ? "图片渠道使用 generation / edit 非流式接口，不能使用聊天流批量测试"
    : "";
});
const groupedChannelSections = computed(() => buildGroupedChannelSections(channels.value));
const channelTestModelOptions = computed(() => normalizeModels(testingChannel.value?.models).map((item) => item.model));
const existingDiscoveredModelNames = computed(() => {
  const names = new Set();
  for (const item of normalizeModels(channelDraft.models)) {
    if (item.model) names.add(item.model);
    if (item.upstream_model) names.add(item.upstream_model);
  }
  return names;
});
const discoveredModelRows = computed(() =>
  discoveredModels.value.map((model) => ({
    model,
    exists: existingDiscoveredModelNames.value.has(model)
  }))
);
const bulkTestSummary = computed(() => {
  const summary = {
    total: bulkTestRows.value.length,
    pending: 0,
    running: 0,
    success: 0,
    error: 0,
    cancelled: 0
  };

  for (const row of bulkTestRows.value) {
    if (Object.prototype.hasOwnProperty.call(summary, row.status)) {
      summary[row.status] += 1;
    }
  }

  return summary;
});
const bulkTestRunButtonText = computed(() => {
  if (bulkTestRunning.value) {
    return "测试中";
  }

  return bulkTestRows.value.some((row) => row.status !== "pending") ? "重新测试" : "开始测试";
});
const channelTestTitle = computed(() => {
  const name = testingChannel.value?.name || testingChannel.value?.id || "";
  return name ? `测试连接 - ${name}` : "测试连接";
});
const channelPricingTitle = computed(() => {
  const channel = channelPricingChannel.value;
  return channel ? `定价管理 - ${channel.name || channel.id}` : "定价管理";
});
const channelPricingEditorTitle = computed(() => {
  const model = channelPricingDraft.upstream_model || "";
  return model ? `编辑定价 - ${model}` : "编辑定价";
});
async function loadConfig() {
  configLoading.value = true;
  try {
    const data = await props.api("/config");
    config.channels = Array.isArray(data.channels) ? data.channels : [];
    selectedChannels.value = [];
    await nextTick();
    channelTableRef.value?.clearSelection();
  } catch (error) {
    ElMessage.error(error.message);
  } finally {
    configLoading.value = false;
  }
}

async function loadModelProviders() {
  if (modelProviders.value.length > 0) {
    return;
  }

  try {
    const data = await props.api("/model-providers");
    modelProviders.value = Array.isArray(data.providers) ? data.providers : [];
  } catch {
    modelProviders.value = [];
  }
}

const importChannelsInput = ref(null);

function exportChannels() {
  const payload = {
    exported_at: new Date().toISOString(),
    type: "channels",
    channels: config.channels
  };
  const blob = new Blob([JSON.stringify(payload, null, 2)], { type: "application/json" });
  const url = URL.createObjectURL(blob);
  const link = document.createElement("a");
  link.href = url;
  link.download = `channels-${Date.now()}.json`;
  document.body.appendChild(link);
  link.click();
  document.body.removeChild(link);
  URL.revokeObjectURL(url);
  ElMessage.success("渠道配置已导出");
}

function triggerImportChannels() {
  importChannelsInput.value?.click();
}

async function handleImportChannelsFile(event) {
  const file = event.target.files?.[0];
  if (!file) return;
  event.target.value = "";
  try {
    const text = await file.text();
    const parsed = JSON.parse(text);
    const channels = Array.isArray(parsed.channels) ? parsed.channels : Array.isArray(parsed) ? parsed : null;
    if (!channels) {
      ElMessage.error("导入文件格式不正确：缺少 channels 数组");
      return;
    }
    await props.api("/config/import", {
      method: "POST",
      body: JSON.stringify({ channels })
    });
    ElMessage.success("渠道配置导入成功");
    await loadConfig();
  } catch (error) {
    ElMessage.error(error.message || "导入失败");
  }
}

async function persistChannel(channel, method = "POST") {
  const channelId = channel?.id ? `/${channel.id}` : "";
  if (method !== "POST" && !channelId) {
    throw new Error("渠道 id 不存在");
  }
  const data = await props.api(`/channels${method === "POST" ? "" : channelId}`, {
    method,
    body: JSON.stringify(channel)
  });
  config.channels = Array.isArray(data?.channels) ? data.channels : config.channels;
  reconcileSelectedChannels();
}

async function openChannelDrawer(channel = null, index = -1) {
  editingIndex.value = index;
  const draftSource = channel || defaultChannel(nextChannelPriority());
  assignChannelDraft(draftSource);
  headersText.value = formatJson(draftSource.headers || {});
  assignCompat(draftSource.compat || {});
  handleChannelTypeChange();
  channelDrawerVisible.value = true;
}

function openChannelTest(channel) {
  if (!canUseChatStreamTest(channel)) {
    ElMessage.warning("图片渠道使用 generation / edit 非流式接口，不能使用聊天流测试");
    return;
  }
  if (!channel?.id) {
    ElMessage.warning("请先保存渠道后再测试连接");
    return;
  }
  testingChannel.value = channel;
  channelTestForm.model = normalizeModels(channel.models)[0]?.model || "";
  channelTestForm.prompt = "你好";
  testResult.value = null;
  channelTestVisible.value = true;
}

function handleChannelSelectionChange(selection) {
  selectedChannels.value = Array.isArray(selection) ? selection : [];
}

function reconcileSelectedChannels() {
  const selectedIds = new Set(selectedChannels.value.map((channel) => channel.id));
  selectedChannels.value = channels.value.filter((channel) => selectedIds.has(channel.id));
}

async function restoreDesktopSelections() {
  const selectedIds = new Set(selectedChannels.value.map((channel) => channel.id));
  const selectedModels = new Set(selectedDiscoveredModels.value);

  if (channelView.value !== "raw") {
    selectedChannels.value = [];
  }

  await nextTick();

  if (channelView.value === "raw" && channelTableRef.value) {
    channelTableRef.value.clearSelection();
    for (const channel of channels.value) {
      if (selectedIds.has(channel.id)) {
        channelTableRef.value.toggleRowSelection(channel, true);
      }
    }
  }

  if (discoverModelsVisible.value && discoveredModelsTableRef.value) {
    discoveredModelsTableRef.value.clearSelection();
    for (const row of discoveredModelRows.value) {
      if (!row.exists && selectedModels.has(row.model)) {
        discoveredModelsTableRef.value.toggleRowSelection(row, true);
      }
    }
  }
}

function isChannelSelected(channel) {
  return selectedChannelIds.value.has(channel?.id);
}

function setMobileChannelSelection(channel, checked) {
  const channelId = channel?.id;
  if (!channelId) {
    return;
  }

  if (checked) {
    if (!selectedChannelIds.value.has(channelId)) {
      selectedChannels.value = [...selectedChannels.value, channel];
    }
    return;
  }

  selectedChannels.value = selectedChannels.value.filter((item) => item.id !== channelId);
}

function toggleAllMobileChannels(checked) {
  selectedChannels.value = checked ? [...channels.value] : [];
}

async function handleChannelViewChange() {
  selectedChannels.value = [];
  await nextTick();
  channelTableRef.value?.clearSelection();
}

function handleGroupedSelectionChange(groupRows, selection) {
  const groupIds = new Set((groupRows || []).map((row) => row.id));
  const selectedInGroup = Array.isArray(selection) ? selection : [];
  selectedChannels.value = [
    ...selectedChannels.value.filter((row) => !groupIds.has(row.id)),
    ...selectedInGroup
  ];
}

function openBulkChannelEdit() {
  if (selectedChannels.value.length === 0) {
    ElMessage.warning("请先选择渠道");
    return;
  }

  resetBulkEditState();
  bulkEditVisible.value = true;
}

async function applyBulkChannelEdit() {
  bulkEditSaving.value = true;
  try {
    const patch = buildBulkChannelPatch();
    const channelIds = uniqueStringList(selectedChannels.value.map((channel) => channel.id))
      .filter(Boolean);
    if (channelIds.length === 0) {
      throw new Error("请选择有效渠道");
    }

    const data = await props.api("/channels/batch", {
      method: "PATCH",
      body: JSON.stringify({
        channel_ids: channelIds,
        patch
      })
    });
    config.channels = Array.isArray(data?.channels) ? data.channels : config.channels;
    selectedChannels.value = [];
    await nextTick();
    channelTableRef.value?.clearSelection();
    bulkEditVisible.value = false;
    await loadConfig();
    ElMessage.success(`已更新 ${channelIds.length} 个渠道`);
  } catch (error) {
    ElMessage.error(error.message);
  } finally {
    bulkEditSaving.value = false;
  }
}

function openBulkChannelTest() {
  if (selectedChannels.value.some(isImagesChannel)) {
    ElMessage.warning("所选渠道包含图片渠道，不能使用聊天流批量测试");
    return;
  }
  if (selectedChannels.value.length === 0) {
    ElMessage.warning("请先选择渠道");
    return;
  }

  bulkTestRows.value = selectedChannels.value.map((channel, index) =>
    createBulkTestRow(channel, index)
  );
  bulkTestCancelRequested.value = false;
  bulkTestVisible.value = true;
}

async function runBulkChannelTests() {
  if (bulkTestRunning.value || bulkTestRows.value.length === 0) {
    return;
  }

  bulkTestCancelRequested.value = false;
  resetBulkTestRows();
  const runnableRows = bulkTestRows.value.filter((row) => row.status === "pending");
  if (runnableRows.length === 0) {
    return;
  }

  bulkTestRunning.value = true;
  let nextIndex = 0;
  const workerCount = Math.min(normalizeBulkConcurrency(), runnableRows.length);
  const runNext = async () => {
    while (!bulkTestCancelRequested.value) {
      const row = runnableRows[nextIndex];
      nextIndex += 1;
      if (!row) {
        return;
      }
      await runBulkChannelTestRow(row);
    }
  };

  try {
    await Promise.all(Array.from({ length: workerCount }, runNext));
  } finally {
    if (bulkTestCancelRequested.value) {
      markPendingBulkRowsCancelled();
    }
    bulkTestAbortControllers.clear();
    bulkTestRunning.value = false;
  }
}

function cancelBulkChannelTest() {
  bulkTestCancelRequested.value = true;
  for (const controller of bulkTestAbortControllers) {
    controller.abort();
  }
}

function handleBulkTestBeforeClose(done) {
  if (bulkTestRunning.value) {
    cancelBulkChannelTest();
  }
  done();
}

function closeBulkTestDialog() {
  if (bulkTestRunning.value) {
    cancelBulkChannelTest();
  }
  bulkTestVisible.value = false;
}

function createBulkTestRow(channel, index) {
 const testChannel = {
   id: channel?.id,
   name: channel?.name,
   type: channel?.type,
   enabled: channel?.enabled !== false,
   models: normalizeModels(channel?.models)
 };
  const modelOptions = testChannel.models.map((item) => item.model);
  const model = modelOptions[0] || "";
  return {
    key: `${testChannel.id || "channel"}:${index}`,
    channel: testChannel,
    model,
    modelOptions,
    status: "pending",
    result: createChannelTestState()
  };
}

function resetBulkTestRows() {
  for (const row of bulkTestRows.value) {
    row.result = createChannelTestState();
    if (!row.model && row.modelOptions?.length) {
      row.model = row.modelOptions[0];
    }
    if (!row.model) {
      row.status = "error";
      row.result.phase = "error";
      row.result.error = "缺少模型映射";
      row.result.body = { error: "缺少模型映射" };
      row.result.duration_ms = 0;
      continue;
    }

    row.status = "pending";
  }
}

async function runBulkChannelTestRow(row) {
  row.status = "running";
  row.result = createChannelTestState();
  const controller = new AbortController();
  const startedAt = performance.now();
  bulkTestAbortControllers.add(controller);
  try {
    if (!row.channel?.id) {
      throw new Error("渠道 id 不存在，请先保存渠道");
    }
    const payload = buildChannelTestRequest(
      row.channel.id,
      row.model,
      bulkTestForm.prompt || "你好",
      normalizeBulkMaxOutputTokens()
    );
    await streamChannelTest(
      payload,
      (event) => {
        applyChannelTestStreamEvent(row.result, event);
      },
      { signal: controller.signal }
    );
    finalizeChannelTestResult(row.result);
    row.result.duration_ms = Math.round(performance.now() - startedAt);
    row.status = row.result.phase === "error" ? "error" : "success";
  } catch (error) {
    const aborted = isAbortError(error);
    row.status = aborted ? "cancelled" : "error";
    row.result = {
      phase: "error",
      error: aborted ? "已取消" : error.message,
      duration_ms: Math.round(performance.now() - startedAt),
      response: { output_text: "" },
      details: null,
      raw_events: row.result?.raw_events || [],
      hasReceivedEvent: row.result?.hasReceivedEvent === true,
      body: null
    };
  } finally {
    bulkTestAbortControllers.delete(controller);
  }
}

function markPendingBulkRowsCancelled() {
  for (const row of bulkTestRows.value) {
    if (row.status !== "pending") {
      continue;
    }

    row.status = "cancelled";
    row.result.phase = "error";
    row.result.error = "已取消";
    row.result.duration_ms = 0;
  }
}

async function saveChannel() {
  saveLoading.value = true;
  try {
    const channel = buildChannelFromDraft();
    if (editingIndex.value === -1) {
      await persistChannel(channel, "POST");
    } else {
      await persistChannel(channel, "PUT");
    }
    channelDrawerVisible.value = false;
    ElMessage.success("渠道配置已保存并生效");
    await loadConfig();
  } catch (error) {
    ElMessage.error(error.message);
  } finally {
    saveLoading.value = false;
  }
}

async function deleteChannel(index) {
  const channel = channels.value[index];
  if (!channel?.id) {
    throw new Error("渠道 id 不存在");
  }
  const data = await props.api(`/channels/${channel.id}`, {
    method: "DELETE"
  });
  selectedChannels.value = selectedChannels.value.filter((item) => item.id !== channel.id);
  await loadConfig();
  ElMessage.success("渠道已删除");
}

async function deleteChannelById(channelId) {
  const index = channelIndexById(channelId);
  if (index < 0) {
    throw new Error("渠道 id 不存在");
  }

  await deleteChannel(index);
}

async function confirmDeleteChannel(channel) {
  try {
    await ElMessageBox.confirm(
      `确定删除渠道“${channel?.name || channel?.id}”？`,
      "删除渠道",
      {
        type: "warning",
        confirmButtonText: "删除",
        cancelButtonText: "取消"
      }
    );
  } catch {
    return;
  }
  await deleteChannelById(channel.id);
}

async function toggleChannelEnabled(channel, index, enabled) {
  const key = channelToggleKey(channel, index);
  if (channelToggleSavingKeys.has(key)) {
    return;
  }

  channelToggleSavingKeys.add(key);
  const nextEnabled = enabled === true;
  const previousChannels = channels.value;
  const nextChannels = previousChannels.map((item, itemIndex) =>
    itemIndex === index || (channel?.id && item.id === channel.id) ? { ...item, enabled: nextEnabled } : item
  );

  config.channels = nextChannels;
  reconcileSelectedChannels();
  try {
    await persistChannel({ ...channel, enabled: nextEnabled }, "PUT");
    ElMessage.success(nextEnabled ? "渠道已启用" : "渠道已停用");
  } catch (error) {
    config.channels = previousChannels;
    reconcileSelectedChannels();
    ElMessage.error(error.message);
  } finally {
    channelToggleSavingKeys.delete(key);
  }
}

function isChannelToggleSaving(channel, index) {
  return channelToggleSavingKeys.has(channelToggleKey(channel, index));
}

function channelToggleKey(channel, index) {
  return channel?.id || `index:${index}`;
}

function channelIndexById(channelId) {
  return channels.value.findIndex((channel) => channel.id === channelId);
}

function copyChannel(channel) {
  const newId = `${channel.id || 'channel'}-copy-${Date.now()}`;
  const cloned = JSON.parse(JSON.stringify(channel));
  cloned.id = newId;
  openChannelDrawer(cloned, -1);
}

function canResetChannelHealth(channel) {
  return channel?.health_status === "open" || channel?.health_status === "half_open";
}

async function confirmResetChannelHealth(channel) {
  if (!canResetChannelHealth(channel) || !channel?.id) {
    return;
  }

  try {
    await ElMessageBox.confirm(
      `确认重置渠道“${channel.name || channel.id}”的可用状态吗？`,
      "重置可用状态",
      {
        type: "warning",
        confirmButtonText: "重置",
        cancelButtonText: "取消"
      }
    );
  } catch {
    return;
  }

  resetChannelHealthLoadingId.value = channel.id;
  try {
    await props.api(`/channels/${channel.id}/reset-health`, {
      method: "POST",
      body: "{}"
    });
    ElMessage.success("已重置渠道可用状态");
    await loadConfig();
  } catch (error) {
    ElMessage.error(error.message);
  } finally {
    resetChannelHealthLoadingId.value = "";
  }
}

async function openChannelPricing(channel) {
  if (!channel?.id) {
    ElMessage.error("渠道 ID 不能为空");
    return;
  }

  channelPricingChannel.value = channel;
  channelPricingRows.value = [];
  channelPricingVisible.value = true;
  await loadModelProviders();
  await loadChannelPricingRows();
}

async function loadChannelPricingRows() {
  const channelId = channelPricingChannel.value?.id;
  if (!channelId) {
    return;
  }

  channelPricingLoading.value = true;
  try {
    const data = await props.api(`/channels/${channelId}/model-infos`);
    channelPricingRows.value = Array.isArray(data.models) ? data.models : [];
  } catch (error) {
    ElMessage.error(error.message);
    channelPricingRows.value = [];
  } finally {
    channelPricingLoading.value = false;
  }
}

function openChannelPricingEditor(row) {
  const model = effectiveChannelPricingModel(row);
  Object.assign(channelPricingDraft, emptyChannelPricingDraft());
  channelPricingDraft.upstream_model = row.upstream_model || "";
  channelPricingDraft.provider_code = model?.provider_code || modelProviders.value[0]?.code || "";
  channelPricingDraft.model_key = model?.model_key || row.upstream_model || "";
  channelPricingDraft.display_name = model?.display_name || row.upstream_model || "";
  channelPricingDraft.description = model?.description || "";
  channelPricingDraft.match_type = model?.match_type || "exact";
  channelPricingDraft.match_pattern = model?.match_pattern || row.upstream_model || "";
  channelPricingDraft.enabled = model?.enabled !== false;
  channelPricingDraft.capabilities = {
    supports_image: model?.capabilities?.supports_image === true,
    context_window: Number(model?.capabilities?.context_window || 0)
  };
  channelPricingDraft.pricing = normalizeChannelPricing(model?.pricing || null);
  channelPricingCatalogText.value = JSON.stringify(model?.catalog || defaultChannelPricingCatalog(row.upstream_model), null, 2);
  channelPricingEditorVisible.value = true;
}

async function saveChannelPricing() {
  const channelId = channelPricingChannel.value?.id;
  if (!channelId) {
    ElMessage.error("渠道 ID 不能为空");
    return;
  }

  channelPricingSaving.value = true;
  try {
    await props.api(`/channels/${channelId}/model-infos`, {
      method: "PUT",
      body: JSON.stringify(buildChannelPricingPayload())
    });
    channelPricingEditorVisible.value = false;
    await loadChannelPricingRows();
    ElMessage.success("渠道模型定价已保存");
  } catch (error) {
    ElMessage.error(error.message);
  } finally {
    channelPricingSaving.value = false;
  }
}

async function restoreChannelPricing(row) {
  const channelId = channelPricingChannel.value?.id;
  const overrideId = row?.override_model?.id;
  if (!channelId || !overrideId) {
    return;
  }

  channelPricingRestoringId.value = overrideId;
  try {
    await props.api(`/channels/${channelId}/model-infos/${overrideId}`, { method: "DELETE" });
    await loadChannelPricingRows();
    ElMessage.success("已恢复全局配置");
  } catch (error) {
    ElMessage.error(error.message);
  } finally {
    channelPricingRestoringId.value = "";
  }
}

function buildChannelPricingPayload() {
  const catalog = parseJsonText(channelPricingCatalogText.value || "{}", "Catalog JSON");
  if (!isPlainObject(catalog)) {
    throw new Error("Catalog JSON 必须是 JSON 对象");
  }

  return {
    upstream_model: channelPricingDraft.upstream_model,
    provider_code: channelPricingDraft.provider_code,
    model_key: channelPricingDraft.model_key,
    display_name: channelPricingDraft.display_name,
    description: channelPricingDraft.description,
    match_type: channelPricingDraft.match_type,
    match_pattern: channelPricingDraft.match_pattern,
    catalog,
    capabilities: {
      ...channelPricingDraft.capabilities,
      context_window: Number(channelPricingDraft.capabilities.context_window || 0)
    },
    pricing: {
      currency: channelPricingDraft.pricing.currency || "USD",
      enabled: channelPricingDraft.pricing.enabled !== false,
      rules: channelPricingDraft.pricing.rules.map((rule) => ({
        billing_item: rule.billing_item,
        billing_mode: rule.billing_mode,
        unit_price: Number(rule.unit_price || 0),
        tiers: rule.billing_mode === "tiered_tokens" ? parsePricingTiers(rule.tiers_text) : [],
        enabled: rule.enabled !== false
      }))
    },
    enabled: channelPricingDraft.enabled !== false
  };
}

function effectiveChannelPricingModel(row) {
  return row?.override_model || row?.global_model || null;
}

function formatChannelPricingModel(row) {
  const model = effectiveChannelPricingModel(row);
  if (!model) {
    return "-";
  }

  const provider = model.provider_name || model.provider_code || "";
  const name = model.display_name && model.display_name !== model.model_key
    ? `${model.model_key} / ${model.display_name}`
    : model.model_key;
  return provider ? `${provider} / ${name}` : name;
}

function pricingRuleSummary(model, item) {
  const rule = (model?.pricing?.rules || []).find((entry) => entry.billing_item === item && entry.enabled !== false);
  if (!rule) return "-";
  if (rule.billing_mode === "tiered_tokens") return "阶梯";
  if (rule.billing_mode === "per_request") return `${formatPrice(rule.unit_price)} / 次`;
  return formatPrice(rule.unit_price);
}

function normalizeChannelPricing(pricing) {
  const rulesByItem = new Map();
  for (const rule of pricing?.rules || []) {
    rulesByItem.set(rule.billing_item, normalizePricingRule(rule));
  }

  return {
    currency: pricing?.currency || "USD",
    enabled: pricing?.enabled !== false,
    rules: billingItems.map((item) => rulesByItem.get(item.value) || defaultPricingRule(item.value))
  };
}

function normalizePricingRule(rule) {
  return {
    billing_item: rule.billing_item,
    billing_mode: rule.billing_mode || "per_million_tokens",
    unit_price: Number(rule.unit_price || 0),
    tiers_text: JSON.stringify(rule.tiers || [], null, 2),
    enabled: rule.enabled !== false
  };
}

function defaultPricingRule(item) {
  return {
    billing_item: item,
    billing_mode: "per_million_tokens",
    unit_price: 0,
    tiers_text: "[]",
    enabled: true
  };
}

function parsePricingTiers(text) {
  const value = parseJsonText(text || "[]", "阶梯");
  if (!Array.isArray(value)) {
    throw new Error("阶梯必须是 JSON 数组");
  }

  return value.map((tier) => ({
    up_to: tier.up_to === null || tier.up_to === undefined || tier.up_to === "" ? null : Number(tier.up_to),
    unit_price: Number(tier.unit_price || 0)
  }));
}

function defaultChannelPricingCatalog(upstreamModel) {
  const model = String(upstreamModel || "").trim();
  return {
    slug: model,
    display_name: model,
    visibility: "list",
    supported_in_api: true
  };
}

function emptyChannelPricingDraft() {
  return {
    upstream_model: "",
    provider_code: "",
    model_key: "",
    display_name: "",
    description: "",
    match_type: "exact",
    match_pattern: "",
    catalog: {},
    capabilities: {
      supports_image: false,
      context_window: 128000
    },
    pricing: normalizeChannelPricing(null),
    enabled: true
  };
}

function formatBillingItem(value) {
  return billingItems.find((item) => item.value === value)?.label || value || "-";
}

function formatPrice(value) {
  const number = Number(value || 0);
  return Number.isInteger(number) ? String(number) : String(Number(number.toFixed(8)));
}

async function discoverModels() {
  discoverLoading.value = true;
  discoveredModels.value = [];
  selectedDiscoveredModels.value = [];
  try {
    const channel = buildChannelFromDraft();
    const payload = buildChannelTestPayload(channel);
    const data = await props.api("/discover-models", {
      method: "POST",
      body: JSON.stringify(payload)
    });
    const models = uniqueStringList(data?.models || []);
    discoveredModels.value = models;
    if (models.length === 0) {
      ElMessage.info("未发现模型");
      return;
    }

    discoverModelsVisible.value = true;
    await nextTick();
    selectDefaultDiscoveredModels();
  } catch (error) {
    ElMessage.error(error.message);
  } finally {
    discoverLoading.value = false;
  }
}

async function testChannel() {
  const channel = testingChannel.value;
  if (!channel?.id) {
    ElMessage.warning("请先保存渠道后再测试连接");
    return;
  }
  testLoading.value = true;
  const startedAt = performance.now();
  testResult.value = createChannelTestState();
  try {
    const payload = buildChannelTestRequest(
      channel.id,
      channelTestForm.model || normalizeModels(channel.models)[0]?.model || "",
      channelTestForm.prompt || "你好",
      256
    );
    await streamChannelTest(payload, (event) => {
      applyChannelTestStreamEvent(testResult.value, event);
    });
    finalizeChannelTestResult(testResult.value);
    testResult.value.duration_ms = Math.round(performance.now() - startedAt);
  } catch (error) {
    testResult.value = {
      phase: "error",
      error: error.message,
      duration_ms: Math.round(performance.now() - startedAt),
      response: { output_text: "" },
      details: null,
      raw_events: [],
      hasReceivedEvent: false,
      body: null
    };
  } finally {
    testLoading.value = false;
  }
}

function addSelectedModels() {
  let addedCount = 0;
  for (const model of selectedDiscoveredModels.value) {
    if (!existingDiscoveredModelNames.value.has(model)) {
      channelDraft.models.push(defaultModelMapping(model));
      addedCount += 1;
    }
  }
  if (addedCount > 0) {
    ElMessage.success(`已添加 ${addedCount} 个模型`);
  }
  discoverModelsVisible.value = false;
  selectedDiscoveredModels.value = [];
}

function openBatchEdit() {
  batchEditText.value = channelDraft.models
    .filter((m) => m.model)
    .map((m) => `${m.model},${m.upstream_model || m.model}`)
    .join("\n");
  batchEditVisible.value = true;
}

function applyBatchEdit() {
  const lines = batchEditText.value.split("\n");
  const newModels = [];
  for (const line of lines) {
    const trimmed = line.trim();
    if (!trimmed) continue;

    const commaIdx = trimmed.indexOf(",");
    if (commaIdx === -1) {
      ElMessage.warning(`格式错误（缺少逗号），已跳过：${trimmed}`);
      continue;
    }

    const model = trimmed.slice(0, commaIdx).trim();
    const upstream = trimmed.slice(commaIdx + 1).trim();

    if (!model) continue;

    newModels.push({
      model,
      upstream_model: upstream || model
    });
  }

  channelDraft.models = newModels;
  batchEditVisible.value = false;
  ElMessage.success(`已更新 ${newModels.length} 条模型映射`);
}


function handleDiscoveredModelSelectionChange(rows) {
  selectedDiscoveredModels.value = rows.map((row) => row.model);
}

function isDiscoveredModelSelected(model) {
  return selectedDiscoveredModels.value.includes(model);
}

function setDiscoveredModelSelection(row, checked) {
  if (row.exists) {
    return;
  }

  if (checked) {
    selectedDiscoveredModels.value = uniqueStringList([...selectedDiscoveredModels.value, row.model]);
    return;
  }

  selectedDiscoveredModels.value = selectedDiscoveredModels.value.filter((model) => model !== row.model);
}

function isDiscoveredModelSelectable(row) {
  return !row.exists;
}

function selectDefaultDiscoveredModels() {
  const table = discoveredModelsTableRef.value;
  if (!table) {
    selectedDiscoveredModels.value = discoveredModelRows.value
      .filter((row) => !row.exists)
      .map((row) => row.model);
    return;
  }
  table.clearSelection();
  for (const row of discoveredModelRows.value) {
    if (!row.exists) {
      table.toggleRowSelection(row, true);
    }
  }
}

function buildChannelTestPayload(channel) {
  return {
    id: channel.id,
    name: channel.name,
    type: channel.type,
    baseurl: channel.baseurl,
    apikey: channel.apikey,
    auth_mode: channel.auth_mode,
    headers: channel.headers || {},
    timeout_seconds: Number(channel.timeout_seconds || 120),
    retry_count: Number(channel.retry_count ?? 3),
    priority: Number(channel.priority ?? 0),
    capacity: normalizeCapacityValue(channel.capacity),
    compat: channel.compat || {},
    models: channel.models || [],
    enabled: channel.enabled !== false,
    model: "",
    input: "你好",
    max_output_tokens: 256
  };
}

function buildChannelTestRequest(channelId, model, prompt, maxOutputTokens) {
  return {
    channel_id: channelId,
    model,
    input: prompt,
    max_output_tokens: maxOutputTokens
  };
}

function channelTestModelSuggestions(query, callback) {
  callback(buildSuggestions(channelTestModelOptions.value, query));
}

function buildGroupedChannelSections(sourceChannels) {
  const sections = new Map();
  for (const channel of sourceChannels || []) {
    const groupName = normalizeGroupNameText(channel.group_name);
    const groupKey = groupName || "__ungrouped";
    const baseurl = normalizeBaseUrlText(channel.baseurl);
    const baseKey = baseurl || "__empty_baseurl";

    if (!sections.has(groupKey)) {
      sections.set(groupKey, {
        key: groupKey,
        label: groupName || "未分组",
        baseMap: new Map()
      });
    }

    const section = sections.get(groupKey);
    if (!section.baseMap.has(baseKey)) {
      section.baseMap.set(baseKey, {
        key: `${groupKey}:${baseKey}`,
        baseurl,
        channels: []
      });
    }

    section.baseMap.get(baseKey).channels.push(channel);
  }

  return Array.from(sections.values())
    .map((section) => {
      const baseUrlGroups = Array.from(section.baseMap.values())
        .map((group) => {
          const groupChannels = [...group.channels].sort(compareChannelsInGroup);
          return {
            ...group,
            channels: groupChannels,
            types: uniqueStringList(groupChannels.map((channel) => channel.type)).sort(compareChannelType),
            keyVariants: countDistinctNonEmpty(groupChannels.map((channel) => channel.apikey)),
            modelCount: groupChannels.reduce((sum, channel) => sum + normalizeModels(channel.models).length, 0),
            enabledCount: groupChannels.filter((channel) => channel.enabled !== false).length
          };
        })
        .sort((left, right) =>
          right.channels.length - left.channels.length
          || left.baseurl.localeCompare(right.baseurl)
        );

      const channelCount = baseUrlGroups.reduce((sum, group) => sum + group.channels.length, 0);
      const enabledCount = baseUrlGroups.reduce((sum, group) => sum + group.enabledCount, 0);
      return {
        key: section.key,
        label: section.label,
        baseUrlGroups,
        channelCount,
        enabledCount
      };
    })
    .sort(compareGroupedSections);
}

function compareGroupedSections(left, right) {
  if (left.key === "__ungrouped" && right.key !== "__ungrouped") return 1;
  if (right.key === "__ungrouped" && left.key !== "__ungrouped") return -1;
  return left.label.localeCompare(right.label);
}

function compareChannelsInGroup(left, right) {
  const enabledOrder = Number(right.enabled !== false) - Number(left.enabled !== false);
  if (enabledOrder !== 0) return enabledOrder;
  const priorityOrder = normalizePriorityValue(left.priority) - normalizePriorityValue(right.priority);
  if (priorityOrder !== 0) return priorityOrder;
  return String(left.name || "").localeCompare(String(right.name || ""));
}

function compareChannelType(left, right) {
  const order = { responses: 0, messages: 1, chat: 2 };
  return (order[left] ?? 99) - (order[right] ?? 99) || String(left).localeCompare(String(right));
}

function countDistinctNonEmpty(values) {
  const items = new Set();
  for (const value of values || []) {
    const text = String(value || "").trim();
    if (text) {
      items.add(text);
    }
  }
  return items.size;
}

function normalizeGroupNameText(value) {
  return String(value || "").trim();
}

function normalizeBaseUrlText(value) {
  return String(value || "").trim();
}

function defaultBulkEditFields() {
  return {
    group_name: false,
    enabled: false,
    priority: false,
    capacity: false,
    timeout_seconds: false,
    retry_count: false,
    circuit_break_duration_seconds: false
  };
}

function defaultBulkEditDraft() {
  return {
    group_name: "",
    enabled: true,
    priority: 0,
    capacity: 3,
    timeout_seconds: 120,
    retry_count: 3,
    circuit_break_duration_seconds: 0
  };
}

function resetBulkEditState() {
  Object.assign(bulkEditFields, defaultBulkEditFields());
  Object.assign(bulkEditDraft, {
    group_name: commonSelectedValue((channel) => normalizeGroupNameText(channel.group_name), ""),
    enabled: commonSelectedValue((channel) => channel.enabled !== false, true),
    priority: commonSelectedValue((channel) => normalizePriorityValue(channel.priority), 0),
    capacity: commonSelectedValue((channel) => normalizeCapacityValue(channel.capacity) || 3, 3),
    timeout_seconds: commonSelectedValue((channel) => Number(channel.timeout_seconds || 120), 120),
    retry_count: commonSelectedValue((channel) => Number(channel.retry_count ?? 3), 3),
    circuit_break_duration_seconds: commonSelectedValue(
      (channel) => Number(channel.circuit_break_duration_seconds ?? 0),
      0
    )
  });
}

function commonSelectedValue(readValue, fallback) {
  const rows = selectedChannels.value;
  if (rows.length === 0) {
    return fallback;
  }

  const first = readValue(rows[0]);
  return rows.every((row) => Object.is(readValue(row), first)) ? first : fallback;
}

function buildBulkChannelPatch() {
  const patch = {};
  if (bulkEditFields.group_name) {
    patch.group_name = normalizeGroupNameText(bulkEditDraft.group_name);
  }
  if (bulkEditFields.enabled) {
    patch.enabled = bulkEditDraft.enabled === true;
  }
  addBulkIntegerPatch(patch, bulkEditFields.priority, "priority", bulkEditDraft.priority, "优先级", 0);
  addBulkIntegerPatch(patch, bulkEditFields.capacity, "capacity", bulkEditDraft.capacity, "容量", 1);
  addBulkIntegerPatch(
    patch,
    bulkEditFields.timeout_seconds,
    "timeout_seconds",
    bulkEditDraft.timeout_seconds,
    "超时秒数",
    1
  );
  addBulkIntegerPatch(patch, bulkEditFields.retry_count, "retry_count", bulkEditDraft.retry_count, "重试次数", 0);
  addBulkIntegerPatch(
    patch,
    bulkEditFields.circuit_break_duration_seconds,
    "circuit_break_duration_seconds",
    bulkEditDraft.circuit_break_duration_seconds,
    "熔断时间",
    0
  );

  if (Object.keys(patch).length === 0) {
    throw new Error("请选择要修改的字段");
  }

  return patch;
}

function addBulkIntegerPatch(patch, enabled, key, value, label, min) {
  if (!enabled) {
    return;
  }

  const number = Number(value);
  if (!Number.isInteger(number) || number < min) {
    throw new Error(`${label}必须是大于等于 ${min} 的整数`);
  }

  patch[key] = number;
}

// --- Channel helpers ---

function defaultChannel(priority = 0) {
  return {
    owner_username: "",
    id: "",
    name: "",
    group_name: "",
    type: "chat",
    baseurl: "",
    apikey: "",
    auth_mode: "config",
    headers: {},
    timeout_seconds: 120,
    circuit_break_duration_seconds: 0,
    retry_count: 3,
    priority,
    capacity: 3,
    compat: {},
    models: [],
    enabled: true
  };
}

function handleChannelTypeChange() {
  applyChannelTypeContract(channelDraft, compatTexts);
}

function assignChannelDraft(channel) {
  Object.assign(channelDraft, defaultChannel(normalizePriorityValue(channel.priority)), channel, {
    headers: channel.headers || {},
    circuit_break_duration_seconds: Number(channel.circuit_break_duration_seconds ?? 0),
    priority: normalizePriorityValue(channel.priority),
    capacity: normalizeCapacityValue(channel.capacity),
    compat: channel.compat || {},
    models: normalizeModels(channel.models)
  });
}

function assignCompat(compat) {
  Object.assign(compatTexts, {
    enable_apply_patch_prompt_compat: compat.enable_apply_patch_prompt_compat === true,
    preserve_thinking_history: compat.preserve_thinking_history === true,
    rename_params: formatAssignmentMap(compat.rename_params || {}),
    drop_params: formatStringList(compat.drop_params || []),
    drop_tool_types: formatStringList(compat.drop_tool_types || []),
    force_params: formatAssignmentMap(compat.force_params || {}),
    default_params: formatAssignmentMap(compat.default_params || {}),
    unsupported_params: formatStringList(compat.unsupported_params || []),
    images_api_dialect: compat.images_api_dialect || "openai"
  });
}

function buildChannelFromDraft() {
  const headers = parseJsonText(headersText.value || "{}", "请求头");
  if (!headers || typeof headers !== "object" || Array.isArray(headers)) {
    throw new Error("请求头必须是 JSON 对象");
  }
  const priority = normalizePriorityValue(channelDraft.priority);
  const capacity = normalizeCapacityValue(channelDraft.capacity);
  if (!Number.isInteger(priority) || priority < 0) {
    throw new Error("优先级必须是大于等于 0 的整数");
  }
  if (!Number.isInteger(capacity) || capacity <= 0) {
    throw new Error("容量必须是正整数");
  }
  const id = ensureChannelId(channelDraft.id, channelDraft.name);
  return {
    owner_username: channelDraft.owner_username || undefined,
    id,
    name: channelDraft.name.trim(),
    group_name: normalizeGroupNameText(channelDraft.group_name),
    type: channelDraft.type,
    baseurl: channelDraft.baseurl.trim(),
    apikey: channelDraft.apikey,
    auth_mode: channelDraft.auth_mode,
    headers,
    timeout_seconds: Number(channelDraft.timeout_seconds || 120),
    circuit_break_duration_seconds: Number(channelDraft.circuit_break_duration_seconds ?? 0),
    retry_count: isImagesChannel(channelDraft) ? 0 : Number(channelDraft.retry_count ?? 3),
    priority,
    capacity,
    enabled: channelDraft.enabled === true,
    models: normalizeModels(channelDraft.models).filter((item) => item.model),
    compat: buildCompat()
  };
}

function buildCompat() {
  const compat = {
    enable_apply_patch_prompt_compat: supportsApplyPatchPromptCompat(channelDraft.type)
      ? compatTexts.enable_apply_patch_prompt_compat === true
      : false,
    preserve_thinking_history: channelDraft.type === 'messages'
      ? compatTexts.preserve_thinking_history === true
      : false,
    rename_params: parseAssignmentMap(compatTexts.rename_params, false),
    drop_params: parseStringList(compatTexts.drop_params),
    drop_tool_types: parseStringList(compatTexts.drop_tool_types),
    force_params: parseAssignmentMap(compatTexts.force_params, true),
    default_params: parseAssignmentMap(compatTexts.default_params, true),
    unsupported_params: parseStringList(compatTexts.unsupported_params)
  };
  if (isImagesChannel(channelDraft)) {
    return buildImagesCompat({
      ...compat,
      images_api_dialect: compatTexts.images_api_dialect
    });
  }
  for (const key of Object.keys(compat)) {
    const value = compat[key];
    if ((Array.isArray(value) && value.length === 0) || (isPlainObject(value) && Object.keys(value).length === 0)) {
      delete compat[key];
    }
  }
  return compat;
}

function supportsApplyPatchPromptCompat(channelType) {
  return channelType === "chat" || channelType === "messages";
}

function normalizeModels(models) {
  if (!Array.isArray(models)) return [];
  return models
    .map((item) => {
      const model = String(item?.model || "").trim();
      return {
        model,
        upstream_model: String(item?.upstream_model || model).trim() || model
      };
    })
    .filter((item) => item.model);
}

function defaultModelMapping(model = "") {
  const normalized = String(model || "").trim();
  return {
    model: normalized,
    upstream_model: normalized
  };
}

function ensureChannelId(id, name) {
  const normalizedId = String(id || "").trim();
  if (normalizedId) {
    return normalizedId;
  }

  return `${slugifyChannelName(name) || "channel"}-${randomChannelSuffix()}`;
}

function slugifyChannelName(name) {
  return String(name || "")
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "")
    .slice(0, 24);
}

function randomChannelSuffix() {
  return Math.random().toString(36).slice(2, 8);
}

// --- Shared utils ---

function buildSuggestions(values, query) {
  const lowered = String(query || "").toLowerCase();
  return (values || [])
    .filter((value) => String(value).toLowerCase().includes(lowered))
    .map((value) => ({ value: String(value) }));
}

function uniqueStringList(values) {
  const seen = new Set();
  const result = [];
  for (const value of values || []) {
    const text = String(value || "").trim();
    if (text && !seen.has(text)) {
      seen.add(text);
      result.push(text);
    }
  }
  return result;
}

function parseJsonText(text, label) {
  try { return JSON.parse(text || "{}"); } catch { throw new Error(`${label} 不是合法 JSON`); }
}

function parseStringList(text) {
  return String(text || "").split("\n").map((l) => l.trim()).filter(Boolean);
}

function parseAssignmentMap(text, parseValue) {
  const result = {};
  for (const [index, line] of String(text || "").split("\n").entries()) {
    const trimmed = line.trim();
    if (!trimmed) continue;
    const separator = trimmed.indexOf("=");
    if (separator === -1) throw new Error(`第 ${index + 1} 行缺少 =`);
    const key = trimmed.slice(0, separator).trim();
    const rawValue = trimmed.slice(separator + 1).trim();
    result[key] = parseValue ? parseLooseValue(rawValue) : rawValue;
  }
  return result;
}

function parseLooseValue(value) {
  try { return JSON.parse(value); } catch { return value; }
}

function formatAssignmentMap(value) {
  return Object.entries(value || {})
    .map(([key, item]) => `${key}=${typeof item === "string" ? item : JSON.stringify(item)}`)
    .join("\n");
}

function formatStringList(value) {
  return Array.isArray(value) ? value.join("\n") : "";
}

function displayMs(value) {
  return value === null || value === undefined ? "-" : `${value} ms`;
}

function formatBulkTestResult(row) {
  switch (row.status) {
    case "pending":
      return "等待测试";
    case "running":
      return formatChannelTestResult(row.result);
    case "cancelled":
      return "已取消";
    default:
      return formatChannelTestResult(row.result);
  }
}

function formatBulkTestStatus(status) {
  switch (status) {
    case "pending":
      return "等待";
    case "running":
      return "测试中";
    case "success":
      return "成功";
    case "error":
      return "失败";
    case "cancelled":
      return "取消";
    default:
      return status || "-";
  }
}

function bulkTestStatusTagType(status) {
  switch (status) {
    case "success":
      return "success";
    case "error":
      return "danger";
    case "running":
      return "warning";
    default:
      return "info";
  }
}

function hasChannelTestDetails(result) {
  return Boolean(result?.details || result?.raw_events?.length);
}

function channelTestSummaryDetails(result) {
  const details = result?.details || {};
  return {
    status_code: details.status_code,
    duration_ms: details.duration_ms ?? result?.duration_ms,
    request_model: details.request_model,
    upstream_model: details.upstream_model,
    channel_id: details.channel_id,
    channel_type: details.channel_type,
    error: details.error
  };
}

function formatChannelTestJson(value) {
  if (value === null || value === undefined) {
    return "";
  }

  if (typeof value === "string") {
    return value;
  }

  try {
    return JSON.stringify(value, null, 2);
  } catch {
    return String(value);
  }
}

function formatCapacityStatus(channel) {
  const activeRequests = Number(channel?.active_requests ?? 0);
  const capacity = normalizeCapacityValue(channel?.capacity);
  return `${activeRequests} / ${capacity ?? "-"}`;
}

function normalizeBulkMaxOutputTokens() {
  const value = Number(bulkTestForm.max_output_tokens);
  return Number.isInteger(value) && value > 0 ? value : 256;
}

function normalizeBulkConcurrency() {
  const value = Number(bulkTestForm.concurrency);
  if (!Number.isInteger(value) || value <= 0) {
    return 3;
  }

  return Math.min(value, 10);
}

function isAbortError(error) {
  return error?.name === "AbortError";
}

function formatHealthStatus(value) {
  switch (value) {
    case "disabled":
      return "停用";
    case "open":
      return "熔断开启";
    case "half_open":
      return "降级";
    default:
      return "健康";
  }
}

function healthStatusTagType(value) {
  switch (value) {
    case "disabled":
      return "info";
    case "open":
      return "danger";
    case "half_open":
      return "warning";
    default:
      return "success";
  }
}

function formatJson(value) {
  return JSON.stringify(value, null, 2);
}

function nextChannelPriority() {
  return channels.value.reduce((maxPriority, channel) => {
    const priority = normalizePriorityValue(channel?.priority);
    return Math.max(maxPriority, priority);
  }, -1) + 1;
}

function normalizePriorityValue(value) {
  const priority = Number(value ?? 0);
  return Number.isInteger(priority) && priority >= 0 ? priority : 0;
}

function normalizeCapacityValue(value) {
  if (value === null || value === undefined || value === "") {
    return null;
  }

  const capacity = Number(value);
  return Number.isInteger(capacity) && capacity > 0 ? capacity : null;
}

function applyRuntimePayload(payload) {
  const list = Array.isArray(payload?.channels) ? payload.channels : [];
  const runtimeMap = new Map();
  for (const item of list) {
    if (item?.id) runtimeMap.set(String(item.id), item);
  }
  if (runtimeMap.size === 0) return;

  config.channels = config.channels.map((channel) => {
    const runtime = runtimeMap.get(String(channel.id));
    if (!runtime) return channel;
    return {
      ...channel,
      active_requests: runtime.active_requests ?? channel.active_requests ?? 0,
      health_status: runtime.health_status ?? channel.health_status,
      capacity: runtime.capacity ?? channel.capacity,
      enabled: runtime.enabled ?? channel.enabled
    };
  });
}

function isPlainObject(value) {
  return value && typeof value === "object" && !Array.isArray(value);
}

// Expose loadConfig so App can call it on init
onMounted(async () => {
  mobileMediaQuery = window.matchMedia("(max-width: 767px)");
  updateMobileViewport(mobileMediaQuery);
  mobileMediaQuery.addEventListener("change", updateMobileViewport);
  await loadConfig();
  runtimeStream.start();
});

onBeforeUnmount(() => {
  mobileMediaQuery?.removeEventListener("change", updateMobileViewport);
  runtimeStream.stop();
});
</script>

<style scoped>
.channel-view-tabs {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
}

.channel-view-tabs :deep(.el-tabs__content) {
  flex: 1;
  min-height: 0;
  overflow: hidden;
}

.channel-view-tabs :deep(.el-tab-pane) {
  height: 100%;
  min-height: 0;
  display: flex;
  flex-direction: column;
}

.channel-raw-table {
  flex: 1;
  min-height: 0;
  width: 100%;
}

.channel-grouped-view {
  flex: 1;
  min-height: 0;
  overflow: auto;
  padding: 2px 2px 12px;
}

.channel-group-section + .channel-group-section {
  margin-top: 14px;
}

.channel-group-header,
.base-url-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
}

.channel-group-header {
  min-height: 40px;
  padding: 0 2px 8px;
  border-bottom: 1px solid var(--el-border-color-lighter);
}

.channel-group-title,
.base-url-types,
.base-url-stats {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}

.channel-group-title {
  min-width: 0;
  font-size: 15px;
  font-weight: 650;
}

.channel-group-meta,
.base-url-stats {
  color: var(--el-text-color-secondary);
  font-size: 12px;
}

.base-url-section {
  padding: 10px 0 12px;
  border-bottom: 1px solid var(--el-border-color-extra-light);
}

.base-url-header {
  margin-bottom: 8px;
  padding: 0 2px;
}

.base-url-main {
  display: flex;
  align-items: center;
  gap: 10px;
  min-width: 0;
}

.base-url-main strong {
  min-width: 0;
  max-width: min(680px, 58vw);
  overflow: hidden;
  color: var(--el-text-color-primary);
  font-size: 13px;
  font-weight: 650;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.channel-group-table {
  width: 100%;
}

.mobile-channel-list,
.model-mapping-list,
.discover-model-list,
.pricing-model-list,
.pricing-rule-list,
.bulk-test-card-list {
  display: grid;
  gap: 10px;
  min-width: 0;
}

.mobile-selection-bar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 2px 2px 8px;
  color: var(--el-text-color-secondary);
  font-size: 13px;
}

.mobile-channel-card,
.model-mapping-card,
.pricing-model-card,
.pricing-rule-card,
.bulk-test-card {
  min-width: 0;
  padding: 12px;
  border: 1px solid var(--el-border-color-lighter);
  border-radius: 6px;
  background: var(--el-bg-color);
}

.mobile-channel-card__header,
.pricing-model-card__header,
.pricing-rule-card__header,
.bulk-test-card__header {
  display: flex;
  align-items: center;
  gap: 10px;
  min-width: 0;
}

.mobile-channel-card__identity {
  display: flex;
  flex: 1;
  min-width: 0;
  flex-direction: column;
  gap: 2px;
}

.mobile-channel-card__identity strong,
.pricing-model-card__header strong,
.bulk-test-card__header strong {
  min-width: 0;
  overflow-wrap: anywhere;
}

.mobile-channel-card__identity span {
  overflow: hidden;
  color: var(--el-text-color-secondary);
  font-size: 12px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.mobile-channel-card__tags {
  display: flex;
  gap: 6px;
  flex-wrap: wrap;
  margin-top: 10px;
}

.mobile-channel-card__details,
.pricing-model-card__rules,
.bulk-test-card__details {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 10px 16px;
  margin: 12px 0;
}

.mobile-channel-card__details > div,
.pricing-model-card__rules > div,
.bulk-test-card__details > div {
  min-width: 0;
}

.mobile-channel-card__details dt,
.pricing-model-card__rules dt,
.bulk-test-card__details dt {
  margin-bottom: 3px;
  color: var(--el-text-color-secondary);
  font-size: 12px;
}

.mobile-channel-card__details dd,
.pricing-model-card__rules dd,
.bulk-test-card__details dd {
  min-width: 0;
  margin: 0;
  overflow-wrap: anywhere;
  font-size: 13px;
}

.mobile-channel-card__url {
  grid-column: 1 / -1;
}

.mobile-channel-card__actions {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 8px;
}

.mobile-channel-card__actions :deep(.el-button),
.mobile-channel-card__actions :deep(.el-dropdown) {
  width: 100%;
  margin: 0;
}

.model-mapping-empty {
  padding: 24px 12px;
  color: var(--el-text-color-secondary);
  text-align: center;
}

.model-mapping-card {
  display: grid;
  gap: 12px;
}

.model-mapping-card__field {
  display: grid;
  gap: 6px;
}

.model-mapping-card__field > span {
  color: var(--el-text-color-regular);
  font-size: 13px;
  font-weight: 600;
}

.discover-model-card {
  display: grid;
  grid-template-columns: auto minmax(0, 1fr) auto;
  align-items: center;
  gap: 10px;
  min-width: 0;
  min-height: 44px;
  padding: 8px 10px;
  border-bottom: 1px solid var(--el-border-color-extra-light);
}

.discover-model-card__name {
  min-width: 0;
  overflow-wrap: anywhere;
}

.pricing-model-card__header,
.bulk-test-card__header {
  justify-content: space-between;
  align-items: flex-start;
}

.pricing-model-card__info {
  margin-top: 8px;
  color: var(--el-text-color-secondary);
  font-size: 13px;
  line-height: 1.5;
  overflow-wrap: anywhere;
}

.pricing-rule-card__header {
  justify-content: space-between;
  margin-bottom: 14px;
}

.pricing-rule-card :deep(.el-form-item:last-child) {
  margin-bottom: 0;
}

.bulk-test-card .bulk-test-output {
  max-height: 180px;
  padding: 10px;
  border-radius: 4px;
  background: var(--el-fill-color-light);
}

.bulk-edit-alert {
  margin-bottom: 14px;
}

.bulk-edit-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 12px 16px;
}

.bulk-edit-row {
  display: grid;
  grid-template-columns: 112px minmax(0, 1fr);
  align-items: center;
  gap: 10px;
  min-width: 0;
}

.bulk-edit-row :deep(.el-checkbox) {
  margin-right: 0;
  min-width: 0;
}

.pricing-rule-table {
  margin-top: 8px;
}

.advanced-collapse {
  margin-top: 12px;
}

@media (max-width: 900px) {
  .channel-view-tabs,
  .channel-view-tabs :deep(.el-tabs__content),
  .channel-view-tabs :deep(.el-tab-pane),
  .channel-raw-table,
  .channel-grouped-view {
    flex: none;
    min-height: 0;
  }

  .channel-grouped-view {
    overflow: visible;
  }

  .channel-group-header,
  .base-url-header,
  .base-url-main {
    align-items: flex-start;
    flex-direction: column;
  }

  .base-url-main strong {
    max-width: 100%;
  }

  .bulk-edit-grid {
    grid-template-columns: 1fr;
  }
}

@media (max-width: 767px) {
  .channels-page {
    min-width: 0;
  }

  .channels-page .toolbar {
    margin-bottom: 14px;
  }

  .channels-page .toolbar-actions {
    display: grid;
    grid-template-columns: repeat(2, minmax(0, 1fr));
    gap: 8px;
    width: 100%;
    padding-top: 12px;
  }

  .channel-stats {
    row-gap: 12px;
  }

  .channels-page .toolbar-actions > *,
  .channels-page .toolbar-actions > :deep(.el-button),
  .channels-page .toolbar-actions > :deep(.el-tooltip__trigger),
  .channels-page .toolbar-actions > span > :deep(.el-button) {
    width: 100%;
    margin: 0;
  }

  .channels-page :deep(.el-button) {
    min-height: 44px;
  }

  .channels-page :deep(.el-input__inner),
  .channels-page :deep(.el-textarea__inner) {
    font-size: 16px;
  }

  .channel-view-tabs :deep(.el-tabs__header) {
    margin-bottom: 12px;
  }

  .channel-group-section + .channel-group-section {
    margin-top: 18px;
  }

  .channel-group-header {
    padding-bottom: 10px;
  }

  .base-url-section {
    padding: 12px 0 16px;
  }

  .base-url-main strong {
    white-space: normal;
    overflow-wrap: anywhere;
  }

  .mobile-channel-list--grouped {
    margin-top: 10px;
  }

  .mobile-channel-card__header {
    align-items: flex-start;
  }

  .mobile-channel-card__header :deep(.el-switch) {
    flex-shrink: 0;
  }

  .mobile-channel-card__header > :deep(.el-checkbox),
  .discover-model-card > :deep(.el-checkbox) {
    min-width: 44px;
    min-height: 44px;
    margin-right: 0;
    justify-content: center;
  }

  .mobile-channel-card__actions :deep(.el-button) {
    padding-right: 8px;
    padding-left: 8px;
  }

  .bulk-edit-row {
    grid-template-columns: 104px minmax(0, 1fr);
  }
}

@media (max-width: 360px) {
  .mobile-channel-card__actions {
    grid-template-columns: 1fr;
  }

  .mobile-channel-card__details,
  .pricing-model-card__rules,
  .bulk-test-card__details {
    grid-template-columns: 1fr;
  }
}

@media (max-width: 767px) {
  :global(.channel-editor-drawer) {
    width: 100% !important;
    max-width: 100vw;
  }

  :global(.channel-editor-drawer .el-drawer__header) {
    min-height: 56px;
    margin-bottom: 0;
    padding: 14px 16px;
  }

  :global(.channel-editor-drawer .el-drawer__body) {
    min-width: 0;
    padding: 12px 16px 20px;
    overflow-x: hidden;
  }

  :global(.channel-editor-drawer .el-drawer__footer) {
    padding: 10px 16px calc(10px + env(safe-area-inset-bottom));
    border-top: 1px solid var(--el-border-color-lighter);
  }

  :global(.channel-editor-drawer .el-col),
  :global(.channel-pricing-editor-dialog .el-col) {
    max-width: 100% !important;
    flex: 0 0 100% !important;
  }

  :global(.channel-mobile-dialog.is-fullscreen) {
    display: flex;
    height: 100dvh;
    max-width: 100vw;
    margin: 0;
    overflow: hidden;
    flex-direction: column;
  }

  :global(.channel-mobile-dialog .el-dialog__header) {
    flex-shrink: 0;
    min-height: 56px;
    margin-right: 0;
    padding: 16px 52px 12px 16px;
    border-bottom: 1px solid var(--el-border-color-lighter);
  }

  :global(.channel-mobile-dialog .el-dialog__title) {
    display: block;
    font-size: 16px;
    line-height: 1.4;
    overflow-wrap: anywhere;
  }

  :global(.channel-mobile-dialog .el-dialog__body) {
    flex: 1;
    min-width: 0;
    min-height: 0;
    padding: 14px 16px;
    overflow: auto;
  }

  :global(.channel-mobile-dialog .el-dialog__footer) {
    flex-shrink: 0;
    padding: 10px 16px calc(10px + env(safe-area-inset-bottom));
    border-top: 1px solid var(--el-border-color-lighter);
  }

  :global(.channel-mobile-dialog .drawer-footer),
  :global(.channel-editor-drawer .drawer-footer) {
    display: flex;
    gap: 8px;
    justify-content: flex-end;
    flex-wrap: wrap;
  }

  :global(.channel-mobile-dialog .el-button),
  :global(.channel-editor-drawer .el-button) {
    min-height: 44px;
  }

  :global(.channel-mobile-dialog .el-input__inner),
  :global(.channel-mobile-dialog .el-textarea__inner),
  :global(.channel-editor-drawer .el-input__inner),
  :global(.channel-editor-drawer .el-textarea__inner) {
    font-size: 16px;
  }

  :global(.channel-mobile-dialog .el-divider__text),
  :global(.channel-editor-drawer .el-divider__text) {
    max-width: calc(100vw - 64px);
    white-space: normal;
  }

  :global(.channel-test-dialog .el-descriptions__cell) {
    overflow-wrap: anywhere;
  }

  :global(.channel-test-dialog .channel-test-json) {
    max-width: 100%;
    overflow-x: auto;
    overflow-wrap: anywhere;
  }
}
</style>
