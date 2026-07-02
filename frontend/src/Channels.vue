<template>
  <div>
    <div class="toolbar">
      <div>
        <h2>渠道配置</h2>
        <div class="text-muted">保存单个渠道后立即生效</div>
      </div>
      <div class="toolbar-actions">
        <el-button :icon="Refresh" @click="loadConfig">刷新</el-button>
        <el-button :icon="Connection" :disabled="selectedChannels.length === 0" @click="openBulkChannelTest">
          批量测试
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

    <el-row :gutter="12">
      <el-col :span="12">
        <el-statistic title="渠道总数" :value="channels.length" />
      </el-col>
      <el-col :span="12">
        <el-statistic title="启用渠道" :value="enabledChannelCount" />
      </el-col>
    </el-row>

    <div class="table-area">
      <el-table
        ref="channelTableRef"
        v-loading="configLoading"
        :data="channels"
        row-key="id"
        style="width: 100%; margin-top: 16px"
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
                    <el-dropdown-item @click="openChannelTest(row)">
                      <el-icon><Connection /></el-icon>测试连接
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
    </div>

    <!-- 渠道编辑 Drawer -->
    <el-drawer v-model="channelDrawerVisible" :title="editingIndex === -1 ? '新增渠道' : '编辑渠道'" size="720px">
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
                class="full-width"
              />
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
          <el-col :span="12">
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

    <el-dialog v-model="discoverModelsVisible" title="发现模型" width="720px">
      <el-table
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

    <el-dialog v-model="batchEditVisible" title="批量编辑模型映射" width="640px">
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

    <el-dialog v-model="channelPricingVisible" :title="channelPricingTitle" width="1080px">
      <el-table
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
        <el-table :data="channelPricingDraft.pricing.rules" border size="small" class="pricing-rule-table">
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

    <!-- 批量测试 Dialog -->
    <el-dialog
      v-model="bulkTestVisible"
      title="批量测试渠道"
      width="960px"
      :close-on-click-modal="!bulkTestRunning"
      :before-close="handleBulkTestBeforeClose"
    >
      <el-form label-position="top" :model="bulkTestForm" class="channel-test-form">
        <el-row :gutter="12">
          <el-col :span="12">
            <el-form-item label="提示词">
              <el-input
                v-model="bulkTestForm.prompt"
                type="textarea"
                :rows="3"
                placeholder="请输入用于测试连接的提示词"
              />
            </el-form-item>
          </el-col>
          <el-col :span="6">
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
          <el-col :span="6">
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
            {{ row.model || "-" }}
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
import { ref, reactive, computed, nextTick, onMounted } from "vue";
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
const devApiPrefix = import.meta.env.DEV ? import.meta.env.BASE_URL.replace(/\/$/, "") : "";
const configLoading = ref(false);
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
  unsupported_params: ""
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

const channels = computed(() => config.channels || []);
const enabledChannelCount = computed(() => channels.value.filter((c) => c.enabled !== false).length);
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
}

async function openChannelDrawer(channel = null, index = -1) {
  editingIndex.value = index;
  const draftSource = channel || defaultChannel(nextChannelPriority());
  assignChannelDraft(draftSource);
  headersText.value = formatJson(draftSource.headers || {});
  assignCompat(draftSource.compat || {});
  channelDrawerVisible.value = true;
}

function openChannelTest(channel) {
  testingChannel.value = channel;
  channelTestForm.model = normalizeModels(channel.models)[0]?.model || "";
  channelTestForm.prompt = "你好";
  testResult.value = null;
  channelTestVisible.value = true;
}

function handleChannelSelectionChange(selection) {
  selectedChannels.value = Array.isArray(selection) ? selection : [];
}

function openBulkChannelTest() {
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
  const copiedChannel = cloneChannelForTest(channel);
  const model = normalizeModels(copiedChannel.models)[0]?.model || "";
  return {
    key: `${copiedChannel.id || "channel"}:${index}`,
    channel: copiedChannel,
    model,
    status: "pending",
    result: createChannelTestState()
  };
}

function resetBulkTestRows() {
  for (const row of bulkTestRows.value) {
    row.result = createChannelTestState();
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
    const payload = buildChannelTestPayload(row.channel);
    payload.model = row.model;
    payload.input = bulkTestForm.prompt || "你好";
    payload.max_output_tokens = normalizeBulkMaxOutputTokens();
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
  config.channels = Array.isArray(data?.channels) ? data.channels : channels.value.filter((_, i) => i !== index);
  ElMessage.success("渠道已删除");
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
    itemIndex === index ? { ...item, enabled: nextEnabled } : item
  );

  config.channels = nextChannels;
  try {
    await persistChannel({ ...channel, enabled: nextEnabled }, "PUT");
    ElMessage.success(nextEnabled ? "渠道已启用" : "渠道已停用");
  } catch (error) {
    config.channels = previousChannels;
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
  testLoading.value = true;
  const startedAt = performance.now();
  testResult.value = createChannelTestState();
  try {
    const channel = testingChannel.value;
    const payload = buildChannelTestPayload(channel);
    payload.model = channelTestForm.model || normalizeModels(channel.models)[0]?.model || "";
    payload.input = channelTestForm.prompt || "你好";
    payload.max_output_tokens = 256;
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

function isDiscoveredModelSelectable(row) {
  return !row.exists;
}

function selectDefaultDiscoveredModels() {
  const table = discoveredModelsTableRef.value;
  if (!table) return;
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

function channelTestModelSuggestions(query, callback) {
  callback(buildSuggestions(channelTestModelOptions.value, query));
}

async function streamChannelTest(payload, onEvent, options = {}) {
  const response = await fetch(`${devApiPrefix}/test-channel/stream`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(payload),
    signal: options.signal
  });
  if (!response.ok) {
    throw new Error(await response.text() || response.statusText);
  }
  if (!response.body) {
    throw new Error("浏览器不支持流式响应读取");
  }

  const reader = response.body.getReader();
  const decoder = new TextDecoder();
  let buffer = "";
  while (true) {
    const { value, done } = await reader.read();
    if (done) break;
    buffer += decoder.decode(value, { stream: true });
    buffer = consumeSseBuffer(buffer, onEvent);
  }
  buffer += decoder.decode();
  consumeSseBuffer(`${buffer}\n\n`, onEvent);
}

function consumeSseBuffer(buffer, onEvent) {
  let remaining = buffer;
  while (true) {
    const separator = remaining.indexOf("\n\n");
    if (separator === -1) {
      return remaining;
    }
    const chunk = remaining.slice(0, separator);
    remaining = remaining.slice(separator + 2);
    const event = parseSseChunk(chunk);
    if (event) onEvent(event);
  }
}

function parseSseChunk(chunk) {
  const lines = chunk.split(/\r?\n/);
  let eventName = "message";
  const data = [];
  for (const line of lines) {
    if (line.startsWith("event:")) {
      eventName = line.slice("event:".length).trim();
    } else if (line.startsWith("data:")) {
      data.push(line.slice("data:".length).trimStart());
    }
  }
  if (data.length === 0) return null;
  const text = data.join("\n");
  if (text === "[DONE]") {
    return { event: eventName, data: text };
  }
  try {
    return { event: eventName, data: JSON.parse(text), raw: text };
  } catch {
    return { event: eventName, data: text, raw: text };
  }
}

// --- Channel helpers ---

function defaultChannel(priority = 0) {
  return {
    owner_username: "",
    id: "",
    name: "",
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
    unsupported_params: formatStringList(compat.unsupported_params || [])
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
    type: channelDraft.type,
    baseurl: channelDraft.baseurl.trim(),
    apikey: channelDraft.apikey,
    auth_mode: channelDraft.auth_mode,
    headers,
    timeout_seconds: Number(channelDraft.timeout_seconds || 120),
    circuit_break_duration_seconds: Number(channelDraft.circuit_break_duration_seconds ?? 0),
    retry_count: Number(channelDraft.retry_count ?? 3),
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

function cloneChannelForTest(channel) {
  return JSON.parse(JSON.stringify(channel || {}));
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

function isPlainObject(value) {
  return value && typeof value === "object" && !Array.isArray(value);
}

// Expose loadConfig so App can call it on init
onMounted(async () => {
  await loadConfig();
});
</script>

<style scoped>
.pricing-rule-table {
  margin-top: 8px;
}

.advanced-collapse {
  margin-top: 12px;
}
</style>
