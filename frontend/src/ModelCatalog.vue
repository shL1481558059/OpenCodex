<template>
  <div class="model-catalog-page">
    <div class="toolbar">
      <div>
        <h2>模型信息</h2>
        <div class="text-muted">全局模型目录与计费规则</div>
      </div>
      <div class="toolbar-actions">
        <el-input
          v-model="filters.query"
          class="toolbar-query"
          clearable
          placeholder="搜索模型、名称、匹配键"
          @keyup.enter="loadModels"
          @clear="loadModels"
        />
        <el-select v-model="filters.enabled" class="toolbar-status" clearable placeholder="状态" @change="loadModels">
          <el-option label="启用" :value="true" />
          <el-option label="停用" :value="false" />
        </el-select>
        <template v-if="selectedModels.length > 0">
          <el-button
            class="batch-action-button"
            :icon="Select"
            :loading="batchAction === 'enable'"
            @click="batchEnable"
          >批量启用 ({{ selectedModels.length }})</el-button>
          <el-button
            class="batch-action-button"
            :icon="CircleClose"
            :loading="batchAction === 'disable'"
            @click="batchDisable"
          >批量停用 ({{ selectedModels.length }})</el-button>
          <el-button
            class="batch-action-button batch-delete-button"
            type="danger"
            :icon="Delete"
            :loading="batchAction === 'delete'"
            @click="batchDelete"
          >批量删除 ({{ selectedModels.length }})</el-button>
          <el-button :icon="Close" @click="clearSelection">清空</el-button>
        </template>
        <el-button :icon="Search" @click="loadModels">搜索</el-button>
        <el-button :icon="Refresh" @click="loadAll">刷新</el-button>
        <el-button :icon="Download" :loading="catalogExporting" @click="exportCatalog">导出</el-button>
        <el-dropdown trigger="click" @command="handleCatalogImportCommand">
          <el-button :icon="Upload" :loading="catalogImporting || catalogSyncing">
            导入<el-icon class="el-icon--right"><ArrowDown /></el-icon>
          </el-button>
          <template #dropdown>
            <el-dropdown-menu>
              <el-dropdown-item command="sync" :icon="RefreshRight">同步最新模型</el-dropdown-item>
              <el-dropdown-item command="file" :icon="Upload">导入本地 json</el-dropdown-item>
            </el-dropdown-menu>
          </template>
        </el-dropdown>
        <el-button :icon="Plus" @click="openProviderDialog(false)">新增供应商</el-button>
        <el-button
          class="create-model-button"
          type="primary"
          :icon="Plus"
          :loading="providersLoading"
          @click="openModelDialog()"
        >新增模型</el-button>
      </div>
      <input
        ref="catalogFileInput"
        type="file"
        accept="application/json,.json"
        class="catalog-file-input"
        @change="handleCatalogFileSelected"
      />
    </div>

    <div v-if="isMobile" class="mobile-provider-filter">
      <span class="mobile-provider-label">供应商</span>
      <el-select v-model="activeProvider" aria-label="供应商筛选">
        <el-option label="全部供应商" value="all" />
        <el-option
          v-for="provider in providers"
          :key="provider.code"
          :label="provider.name"
          :value="provider.code"
        />
      </el-select>
    </div>
    <el-tabs v-else v-model="activeProvider" class="provider-tabs">
      <el-tab-pane label="全部" name="all" />
      <el-tab-pane
        v-for="provider in providers"
        :key="provider.code"
        :name="provider.code"
      >
        <template #label>
          <el-dropdown trigger="hover" @command="handleProviderCommand($event, provider)">
            <span class="provider-tab-label">{{ provider.name }}</span>
            <template #dropdown>
              <el-dropdown-menu>
                <el-dropdown-item command="edit" :icon="Edit">编辑</el-dropdown-item>
                <el-dropdown-item command="delete" :icon="Delete">删除</el-dropdown-item>
              </el-dropdown-menu>
            </template>
          </el-dropdown>
        </template>
      </el-tab-pane>
    </el-tabs>

    <el-dialog
      v-model="catalogImportVisible"
      class="catalog-import-dialog"
      :title="catalogDialogTitle"
      :width="isMobile ? undefined : '620px'"
      :fullscreen="isMobile"
    >
      <template v-if="catalogImportState.phase === 'preview'">
        <template v-if="catalogImportState.origin === 'overwrite'">
          <el-alert
            type="warning"
            show-icon
            :closable="false"
            title="覆盖已有模型"
            description="将按远端改写已存在模型的名称、匹配规则、能力与价格，本地修改不可恢复；启用状态、供应商信息、本地独有模型不受影响。"
          />
        </template>
        <template v-else>
          <el-alert
            type="success"
            show-icon
            :closable="false"
            title="预检通过，等待确认"
            description="确认后写入数据库，未确认前不修改任何数据。"
          />
        </template>

        <dl v-if="catalogImportState.origin !== 'sync' || (catalogImportState.dryRun?.models?.created || 0) > 0" class="catalog-import-summary">
          <div>
            <dt>供应商</dt>
            <dd>新增 {{ catalogImportState.dryRun?.providers?.created || 0 }}，无变化 {{ catalogImportState.dryRun?.providers?.unchanged || 0 }}</dd>
          </div>
          <div>
            <dt>模型</dt>
            <dd>
              新增 {{ catalogImportState.dryRun?.models?.created || 0 }}
              <template v-if="catalogImportState.origin === 'overwrite'">，覆盖 {{ (catalogImportState.dryRun?.overwritten_model_keys || []).length }}</template>
              <template v-else>，跳过 {{ catalogImportState.dryRun?.skipped || 0 }}</template>
            </dd>
          </div>
        </dl>

        <div v-if="catalogImportState.origin !== 'file'" class="catalog-import-keys">
          <template v-if="(catalogImportState.dryRun?.created_model_keys || []).length > 0">
            <div class="catalog-import-key-section">
              <span class="catalog-import-key-label">新增模型</span>
              <div class="catalog-import-key-list">
                <el-tag v-for="key in (catalogImportState.dryRun?.created_model_keys || []).slice(0, 20)" :key="key" size="small" type="success">{{ key }}</el-tag>
                <span v-if="(catalogImportState.dryRun?.created_model_keys || []).length > 20" class="catalog-import-key-more">等 {{ (catalogImportState.dryRun?.created_model_keys || []).length }} 条</span>
              </div>
            </div>
          </template>
          <template v-if="catalogImportState.origin === 'overwrite' && (catalogImportState.dryRun?.overwritten_model_keys || []).length > 0">
            <div class="catalog-import-key-section">
              <span class="catalog-import-key-label">覆盖模型</span>
              <div class="catalog-import-key-list">
                <el-tag v-for="key in (catalogImportState.dryRun?.overwritten_model_keys || []).slice(0, 20)" :key="key" size="small" type="warning">{{ key }}</el-tag>
                <span v-if="(catalogImportState.dryRun?.overwritten_model_keys || []).length > 20" class="catalog-import-key-more">等 {{ (catalogImportState.dryRun?.overwritten_model_keys || []).length }} 条</span>
              </div>
            </div>
          </template>
          <template v-if="catalogImportState.origin === 'sync' && (catalogImportState.dryRun?.skipped || 0) > 0">
            <div class="catalog-import-key-section">
              <span class="catalog-import-key-label">跳过模型</span>
              <div class="catalog-import-key-list">
                <el-tag v-for="key in (catalogImportState.dryRun?.skipped_model_keys || []).slice(0, 20)" :key="key" size="small" type="info">{{ key }}</el-tag>
                <span v-if="(catalogImportState.dryRun?.skipped_model_keys || []).length > 20" class="catalog-import-key-more">等 {{ (catalogImportState.dryRun?.skipped_model_keys || []).length }} 条</span>
              </div>
            </div>
          </template>
        </div>

        <div v-if="catalogImportState.origin === 'overwrite'" class="catalog-overwrite-confirm">
        <el-checkbox v-model="catalogImportState.overwriteConfirmed">我已了解本地修改将被覆盖</el-checkbox>
       </div>
     </template>

      <template v-else-if="catalogImportState.phase === 'confirm'">
        <el-alert
          type="info"
          show-icon
          :closable="false"
          title="即将从远端同步模型目录"
          description="将拉取远端目录并与本地对比，确认前不会修改任何数据。"
        />
        <div class="catalog-sync-confirm">
          <el-checkbox v-model="syncOverwrite">覆盖已有模型</el-checkbox>
        </div>
        <el-alert
          v-if="syncOverwrite"
          type="warning"
          show-icon
          :closable="false"
          title="覆盖模式"
          description="将按远端改写已存在模型的名称、匹配规则、能力与价格，本地修改不可恢复；启用状态、供应商信息、本地独有模型不受影响。"
        />
      </template>

      <template v-else-if="catalogImportState.phase === 'loading'">
        <div class="catalog-import-loading" v-loading="true" element-loading-text="正在拉取远端目录…">
          <div class="catalog-import-loading-spacer"></div>
        </div>
      </template>

      <template v-else-if="catalogImportState.phase === 'done'">
        <el-alert type="success" show-icon :closable="false" title="导入完成" description="供应商和模型列表已刷新。" />
      </template>

      <template v-else>
        <el-alert type="error" show-icon :closable="false" title="导入失败" :description="catalogImportState.errors[0] || '请检查文件后重试'" />
      </template>

      <template #footer>
        <div class="drawer-footer">
          <el-button @click="closeCatalogImportDialog">取消</el-button>
          <el-button
            v-if="catalogImportState.phase === 'confirm'"
            type="primary"
            :loading="catalogSyncing"
            @click="beginSync"
          >开始同步</el-button>
          <el-button
            v-if="catalogImportState.origin === 'overwrite' && catalogImportState.phase === 'preview'"
            :loading="catalogExporting"
            @click="exportCatalog"
          >先导出当前目录</el-button>
          <el-button
            v-if="catalogImportState.phase === 'preview'"
            type="primary"
            :loading="catalogImporting || catalogSyncing"
            :disabled="catalogImportState.origin === 'overwrite' && !catalogImportState.overwriteConfirmed"
            @click="confirmCatalogImport"
          >确认导入</el-button>
          <el-button v-else-if="catalogImportState.phase === 'done'" @click="closeCatalogImportDialog">完成</el-button>
        </div>
      </template>
    </el-dialog>

    <div class="table-area">
      <el-table
        v-if="!isMobile"
        v-loading="modelsLoading"
        :data="pagedModels"
        row-key="id"
        style="width: 100%"
        empty-text="暂无模型信息"
      >
        <el-table-column width="48" align="center">
          <template #header>
            <el-checkbox
              :model-value="selectedAll"
              :indeterminate="selection.length > 0 && !selectedAll"
              @change="toggleSelectAll"
            />
          </template>
          <template #default="{ row }">
            <el-checkbox
              :model-value="selection.includes(row.id)"
              @change="(value) => toggleModelSelection(row, value)"
            />
          </template>
        </el-table-column>
        <el-table-column prop="provider_name" label="供应商" width="120" show-overflow-tooltip />
        <el-table-column prop="model_key" label="模型" min-width="190" show-overflow-tooltip />
        <el-table-column prop="display_name" label="名称" min-width="170" show-overflow-tooltip />
        <el-table-column label="匹配" min-width="220" show-overflow-tooltip>
          <template #default="{ row }">
            <el-tag size="small">{{ formatMatchType(row.match_type) }}</el-tag>
            <span class="match-pattern">{{ row.match_pattern }}</span>
          </template>
        </el-table-column>
        <el-table-column label="输入" width="120" align="right">
          <template #default="{ row }">{{ pricingSummary(row, "input") }}</template>
        </el-table-column>
        <el-table-column label="输出" width="120" align="right">
          <template #default="{ row }">{{ pricingSummary(row, "output") }}</template>
        </el-table-column>
        <el-table-column label="缓存写" width="120" align="right">
          <template #default="{ row }">{{ pricingSummary(row, "cache_write") }}</template>
        </el-table-column>
        <el-table-column label="缓存读" width="120" align="right">
          <template #default="{ row }">{{ pricingSummary(row, "cache_read") }}</template>
        </el-table-column>
        <el-table-column label="状态" width="90">
          <template #default="{ row }">
            <el-tag :type="row.enabled === false ? 'warning' : 'success'">
              {{ row.enabled === false ? "停用" : "启用" }}
            </el-tag>
          </template>
        </el-table-column>
       <el-table-column prop="source" label="来源" width="140" show-overflow-tooltip />
        <el-table-column label="操作" width="210" align="center" fixed="right">
        <template #default="{ row }">
          <div class="inline-actions channel-table-actions">
            <el-button size="small" :icon="Edit" @click="openModelDialog(row)">编辑</el-button>
            <el-popconfirm
              :title="row.enabled === false ? `删除模型 ${row.model_key}？删除后不可恢复` : `停用模型 ${row.model_key}？`"
              @confirm="deleteModel(row)"
            >
              <template #reference>
                <el-button size="small" type="danger" :icon="Delete">
                  {{ row.enabled === false ? "删除" : "停用" }}
                </el-button>
              </template>
            </el-popconfirm>
          </div>
        </template>
      </el-table-column>
    </el-table>

    <div v-else v-loading="modelsLoading" class="mobile-model-list">
      <article v-for="model in pagedModels" :key="model.id" class="model-card">
        <el-checkbox
          :model-value="selection.includes(model.id)"
          class="model-card-select"
          @change="(value) => toggleModelSelection(model, value)"
        />
        <div class="model-card-header">
            <div class="model-card-title-wrap">
              <strong class="model-card-title">{{ model.display_name || model.model_key }}</strong>
              <span class="model-card-key">{{ model.model_key }}</span>
            </div>
            <el-tag :type="model.enabled === false ? 'warning' : 'success'" size="small">
              {{ model.enabled === false ? "停用" : "启用" }}
            </el-tag>
          </div>

          <dl class="model-card-meta">
            <div>
              <dt>供应商</dt>
              <dd>{{ model.provider_name || model.provider_code || "-" }}</dd>
            </div>
            <div>
              <dt>来源</dt>
              <dd>{{ model.source || "-" }}</dd>
            </div>
          </dl>

          <div class="model-card-match">
            <el-tag size="small">{{ formatMatchType(model.match_type) }}</el-tag>
            <span>{{ model.match_pattern || "-" }}</span>
          </div>

          <dl class="model-card-pricing">
            <div>
              <dt>输入</dt>
              <dd>{{ pricingSummary(model, "input") }}</dd>
            </div>
            <div>
              <dt>输出</dt>
              <dd>{{ pricingSummary(model, "output") }}</dd>
            </div>
            <div>
              <dt>缓存写</dt>
              <dd>{{ pricingSummary(model, "cache_write") }}</dd>
            </div>
            <div>
              <dt>缓存读</dt>
              <dd>{{ pricingSummary(model, "cache_read") }}</dd>
            </div>
          </dl>

        <div class="model-card-actions">
          <el-button :icon="Edit" @click="openModelDialog(model)">编辑</el-button>
          <el-popconfirm
            :title="model.enabled === false ? `删除模型 ${model.model_key}？删除后不可恢复` : `停用模型 ${model.model_key}？`"
            @confirm="deleteModel(model)"
          >
            <template #reference>
              <el-button type="danger" :icon="Delete">
                {{ model.enabled === false ? "删除" : "停用" }}
              </el-button>
            </template>
          </el-popconfirm>
        </div>
      </article>
        <el-empty v-if="!modelsLoading && pagedModels.length === 0" description="暂无模型信息" />
      </div>

      <div class="pagination-wrap">
        <el-pagination
          v-if="!isMobile"
          v-model:current-page="page"
          v-model:page-size="pageSize"
          layout="total, sizes, prev, pager, next"
          :page-sizes="[25, 50, 100]"
          :total="models.length"
        />
        <div v-else class="mobile-pagination">
          <span>共 {{ models.length }} 项</span>
          <el-pagination
            v-model:current-page="page"
            :page-size="pageSize"
            :pager-count="5"
            layout="prev, pager, next"
            :total="models.length"
          />
        </div>
      </div>
    </div>

    <el-dialog
      v-model="modelDialogVisible"
      class="model-catalog-model-dialog"
      :title="modelDraft.id ? '编辑模型' : '新增模型'"
      :width="isMobile ? undefined : '880px'"
      :fullscreen="isMobile"
    >
      <el-form label-position="top" :model="modelDraft">
        <el-row :gutter="16">
          <el-col :xs="24" :span="16">
            <el-form-item label="供应商">
              <el-select v-model="modelDraft.provider_code" class="full-width">
                <el-option
                  v-for="provider in providers"
                  :key="provider.code"
                  :label="provider.name"
                  :value="provider.code"
                />
              </el-select>
            </el-form-item>
          </el-col>
          <el-col :xs="24" :span="8">
            <el-form-item label="状态">
              <el-switch v-model="modelDraft.enabled" />
            </el-form-item>
          </el-col>
        </el-row>

        <el-row :gutter="16">
          <el-col :xs="24" :span="12">
            <el-form-item label="模型标识">
              <el-input v-model="modelDraft.model_key" autocomplete="off" />
            </el-form-item>
          </el-col>
          <el-col :xs="24" :span="12">
            <el-form-item label="显示名称">
              <el-input v-model="modelDraft.display_name" autocomplete="off" />
            </el-form-item>
          </el-col>
        </el-row>

        <el-row :gutter="16">
          <el-col :xs="24" :span="8">
            <el-form-item label="匹配类型">
              <el-select v-model="modelDraft.match_type" class="full-width">
                <el-option label="精确" value="exact" />
                <el-option label="前缀" value="prefix" />
                <el-option label="后缀" value="suffix" />
                <el-option label="包含" value="contains" />
              </el-select>
            </el-form-item>
          </el-col>
          <el-col :xs="24" :span="16">
            <el-form-item label="匹配键">
              <el-input v-model="modelDraft.match_pattern" autocomplete="off" />
            </el-form-item>
          </el-col>
        </el-row>

        <el-form-item label="描述">
          <el-input v-model="modelDraft.description" type="textarea" :rows="2" />
        </el-form-item>

        <el-row :gutter="16">
          <el-col :xs="24" :span="8">
            <el-form-item label="支持图片">
              <el-switch v-model="modelDraft.capabilities.supports_image" />
            </el-form-item>
          </el-col>
          <el-col :xs="24" :span="8">
            <el-form-item label="上下文窗口">
              <el-input-number
                v-model="modelDraft.capabilities.context_window"
                :min="0"
                :step="8192"
                class="full-width"
              />
            </el-form-item>
          </el-col>
          <el-col :xs="24" :span="8">
            <el-form-item label="币种">
              <el-input v-model="modelDraft.pricing.currency" autocomplete="off" />
            </el-form-item>
          </el-col>
        </el-row>

        <el-divider content-position="left">计费规则</el-divider>
        <div class="off-peak-panel">
          <div class="off-peak-panel__head">
            <el-select
              v-model="modelDraft.pricing.time_zone"
              filterable
              allow-create
              default-first-option
              clearable
              placeholder="峰谷时区，留空表示不启用"
              class="off-peak-zone"
            >
              <el-option v-for="zone in offPeakTimeZones" :key="zone" :label="zone" :value="zone" />
            </el-select>
            <span class="off-peak-panel__hint">{{ offPeakHint }}</span>
          </div>

          <div
            v-for="(window, index) in modelDraft.pricing.off_peak_windows"
            :key="index"
            class="off-peak-window"
          >
            <el-select v-model="window.start" filterable allow-create default-first-option class="off-peak-time">
              <el-option v-for="time in offPeakTimeOptions" :key="`s-${time}`" :label="time" :value="time" />
            </el-select>
            <span class="off-peak-window__sep">至</span>
            <el-select v-model="window.end" filterable allow-create default-first-option class="off-peak-time">
              <el-option v-for="time in offPeakTimeOptions" :key="`e-${time}`" :label="time" :value="time" />
            </el-select>
            <el-select
              v-model="window.days"
              multiple
              collapse-tags
              collapse-tags-tooltip
              placeholder="每天"
              class="off-peak-days"
            >
              <el-option v-for="day in offPeakWeekdays" :key="day.value" :label="day.label" :value="day.value" />
            </el-select>
            <el-button link type="danger" :icon="Delete" @click="removeOffPeakWindow(index)" />
            <span v-if="crossesMidnight(window)" class="off-peak-window__note">保存后按起始日拆成两段</span>
          </div>

          <div class="off-peak-panel__actions">
            <el-button link type="primary" :icon="Plus" @click="addOffPeakWindow">添加谷段窗口</el-button>
            <div class="off-peak-discount">
              <span>谷段折扣</span>
              <el-input-number
                v-model="offPeakDiscount"
                :min="0"
                :max="1"
                :step="0.05"
                :precision="2"
                size="small"
              />
              <el-button size="small" @click="fillOffPeakDiscount">按折扣填充谷价</el-button>
            </div>
          </div>
      </div>
      <div v-if="!isMobile" class="rule-table-scroll">
        <el-table
          :data="modelDraft.pricing.rules"
          border
          size="small"
          class="model-catalog-rule-table"
        >
          <el-table-column label="计费项" width="110">
            <template #default="{ row }">{{ formatBillingItem(row.billing_item) }}</template>
          </el-table-column>
          <el-table-column label="模式" width="170">
            <template #default="{ row }">
              <el-select v-model="row.billing_mode" class="full-width">
                <el-option label="按次" value="per_request" />
                <el-option label="每百万 token" value="per_million_tokens" />
                <el-option label="上下文窗口档" value="tiered_tokens" />
              </el-select>
            </template>
          </el-table-column>
          <el-table-column label="单价" width="160">
            <template #default="{ row }">
              <el-input-number v-model="row.unit_price" :min="0" :precision="8" :step="0.01" class="full-width" />
            </template>
          </el-table-column>
          <el-table-column label="峰谷" width="70" align="center">
            <template #default="{ row }">
              <el-switch v-model="row.off_peak_enabled" :disabled="!offPeakEnabledForDraft" />
            </template>
          </el-table-column>
          <el-table-column label="谷段单价" width="160">
            <template #default="{ row }">
              <el-input-number
                v-model="row.off_peak_unit_price"
                :min="0"
                :precision="8"
                :step="0.01"
                :disabled="!offPeakEnabledForDraft || row.off_peak_enabled !== true"
                class="full-width"
              />
            </template>
          </el-table-column>
          <el-table-column label="阶梯" width="420">
            <template #default="{ row }">
              <div v-if="row.billing_mode === 'tiered_tokens'" class="tier-editor">
                <div class="tier-editor-section">
                  <div class="tier-editor-head">窗口档（峰）</div>
                  <div v-for="(tier, i) in tiersOf(row)" :key="i" class="tier-editor-row">
                    <span class="tier-editor-label">≤</span>
                    <el-input-number
                      v-model="tier.up_to"
                      :min="0"
                      :step="1000"
                      :controls="false"
                      class="tier-editor-up-to"
                      placeholder="不限留空"
                      @change="syncTiers(row, '_tiers', 'tiers_text')"
                    />
                    <el-input-number
                      v-model="tier.unit_price"
                      :min="0"
                      :precision="8"
                      :step="0.01"
                      :controls="false"
                      class="tier-editor-price"
                      @change="syncTiers(row, '_tiers', 'tiers_text')"
                    />
                    <el-button text type="danger" :icon="Delete" @click="removeTier(row, i)" />
                  </div>
                  <el-button text type="primary" :icon="Plus" @click="addTier(row)">添加档位</el-button>
                </div>
                <div v-if="row.off_peak_enabled === true" class="tier-editor-section off-peak-tiers">
                  <div class="tier-editor-head">窗口档（谷）</div>
                  <div v-for="(tier, i) in offPeakTiersOf(row)" :key="i" class="tier-editor-row">
                    <span class="tier-editor-label">≤</span>
                    <el-input-number
                      v-model="tier.up_to"
                      :min="0"
                      :step="1000"
                      :controls="false"
                      class="tier-editor-up-to"
                      placeholder="不限留空"
                      @change="syncTiers(row, '_offPeakTiers', 'off_peak_tiers_text')"
                    />
                    <el-input-number
                      v-model="tier.unit_price"
                      :min="0"
                      :precision="8"
                      :step="0.01"
                      :controls="false"
                      class="tier-editor-price"
                      @change="syncTiers(row, '_offPeakTiers', 'off_peak_tiers_text')"
                    />
                    <el-button text type="danger" :icon="Delete" @click="removeOffPeakTier(row, i)" />
                  </div>
                  <el-button text type="primary" :icon="Plus" @click="addOffPeakTier(row)">添加档位</el-button>
                </div>
              </div>
            </template>
          </el-table-column>
          <el-table-column label="启用" width="80" align="center">
            <template #default="{ row }">
              <el-switch v-model="row.enabled" />
            </template>
          </el-table-column>
        </el-table>
      </div>

        <div v-else class="mobile-model-catalog-rules">
          <section v-for="rule in modelDraft.pricing.rules" :key="rule.billing_item" class="model-catalog-rule-card">
            <div class="model-catalog-rule-heading">
              <strong>{{ formatBillingItem(rule.billing_item) }}</strong>
              <div class="model-catalog-rule-switch">
                <span>{{ rule.enabled ? "启用" : "停用" }}</span>
                <el-switch v-model="rule.enabled" />
              </div>
            </div>
            <label class="model-catalog-rule-field">
              <span>计费模式</span>
             <el-select v-model="rule.billing_mode" class="full-width">
               <el-option label="按次" value="per_request" />
               <el-option label="每百万 token" value="per_million_tokens" />
                <el-option label="上下文窗口档" value="tiered_tokens" />
             </el-select>
           </label>
            <label class="model-catalog-rule-field">
              <span>单价</span>
              <el-input-number
                v-model="rule.unit_price"
                :min="0"
                :precision="8"
                :step="0.01"
                class="full-width"
              />
            </label>
            <div class="model-catalog-rule-switch">
              <span>峰谷计费</span>
              <el-switch v-model="rule.off_peak_enabled" :disabled="!offPeakEnabledForDraft" />
            </div>
            <label v-if="rule.off_peak_enabled === true" class="model-catalog-rule-field">
              <span>谷段单价</span>
              <el-input-number
                v-model="rule.off_peak_unit_price"
                :min="0"
                :precision="8"
                :step="0.01"
                :disabled="!offPeakEnabledForDraft"
                class="full-width"
              />
            </label>
            <div v-if="rule.billing_mode === 'tiered_tokens'" class="tier-editor mobile-tier-editor">
              <div class="tier-editor-section">
                <div class="tier-editor-head">窗口档（峰）</div>
                <div v-for="(tier, i) in tiersOf(rule)" :key="i" class="tier-editor-row">
                  <span class="tier-editor-label">≤</span>
                  <el-input-number
                    v-model="tier.up_to"
                    :min="0"
                    :step="1000"
                    :controls="false"
                    class="tier-editor-up-to"
                    placeholder="不限留空"
                    @change="syncTiers(rule, '_tiers', 'tiers_text')"
                  />
                  <el-input-number
                    v-model="tier.unit_price"
                    :min="0"
                    :precision="8"
                    :step="0.01"
                    :controls="false"
                    class="tier-editor-price"
                    @change="syncTiers(rule, '_tiers', 'tiers_text')"
                  />
                  <el-button text type="danger" :icon="Delete" @click="removeTier(rule, i)" />
                </div>
                <el-button text type="primary" :icon="Plus" @click="addTier(rule)">添加档位</el-button>
              </div>
              <div v-if="rule.off_peak_enabled === true" class="tier-editor-section off-peak-tiers">
                <div class="tier-editor-head">窗口档（谷）</div>
                <div v-for="(tier, i) in offPeakTiersOf(rule)" :key="i" class="tier-editor-row">
                  <span class="tier-editor-label">≤</span>
                  <el-input-number
                    v-model="tier.up_to"
                    :min="0"
                    :step="1000"
                    :controls="false"
                    class="tier-editor-up-to"
                    placeholder="不限留空"
                    @change="syncTiers(rule, '_offPeakTiers', 'off_peak_tiers_text')"
                  />
                  <el-input-number
                    v-model="tier.unit_price"
                    :min="0"
                    :precision="8"
                    :step="0.01"
                    :controls="false"
                    class="tier-editor-price"
                    @change="syncTiers(rule, '_offPeakTiers', 'off_peak_tiers_text')"
                  />
                  <el-button text type="danger" :icon="Delete" @click="removeOffPeakTier(rule, i)" />
                </div>
                <el-button text type="primary" :icon="Plus" @click="addOffPeakTier(rule)">添加档位</el-button>
              </div>
            </div>
          </section>
        </div>

        <el-collapse class="advanced-collapse">
          <el-collapse-item title="Catalog JSON" name="catalog">
            <el-input v-model="catalogText" type="textarea" :rows="8" />
          </el-collapse-item>
        </el-collapse>
      </el-form>

      <template #footer>
        <div class="drawer-footer">
          <el-button @click="modelDialogVisible = false">取消</el-button>
          <el-button type="primary" :loading="modelSaving" @click="saveModel">保存</el-button>
        </div>
      </template>
    </el-dialog>

    <el-dialog
      v-model="providerDialogVisible"
      class="model-catalog-provider-dialog"
      :title="providerDraft.id ? '编辑供应商' : '新增供应商'"
      :width="isMobile ? undefined : '480px'"
      :fullscreen="isMobile"
    >
      <el-form label-position="top" :model="providerDraft">
        <el-form-item label="供应商编码">
          <el-input v-model="providerDraft.code" autocomplete="off" placeholder="例如 custom-ai" :disabled="!!providerDraft.id" />
        </el-form-item>
        <el-form-item label="显示名称">
          <el-input v-model="providerDraft.name" autocomplete="off" placeholder="例如 Custom AI" />
        </el-form-item>
        <el-row :gutter="16">
          <el-col :xs="24" :span="12">
            <el-form-item label="排序">
              <el-input-number v-model="providerDraft.sort_order" :min="0" :step="10" class="full-width" />
            </el-form-item>
          </el-col>
          <el-col :xs="24" :span="12">
            <el-form-item label="状态">
              <el-switch v-model="providerDraft.enabled" />
            </el-form-item>
          </el-col>
        </el-row>
      </el-form>

      <template #footer>
        <div class="drawer-footer">
          <el-button @click="providerDialogVisible = false">取消</el-button>
          <el-button type="primary" :loading="providerSaving" @click="saveProvider">保存</el-button>
        </div>
      </template>
    </el-dialog>
  </div>
</template>

<script setup>
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from "vue";
import { ElMessage } from "element-plus/es/components/message/index.mjs";
import { ElMessageBox } from "element-plus/es/components/message-box/index.mjs";
import { ArrowDown, CircleClose, Close, Delete, Download, Edit, Plus, Refresh, RefreshRight, Search, Select, Upload, Warning } from "@element-plus/icons-vue";
import {
  createModelCatalogImportState,
  parseModelCatalogFile,
  applyModelCatalogDryRun,
  applyModelCatalogImport,
  failModelCatalogImport,
  resetModelCatalogImport,
  syncModelKeys
} from "./modelCatalogImportState.js";
import {
  OFF_PEAK_TIME_OPTIONS,
  OFF_PEAK_TIME_ZONES,
  OFF_PEAK_WEEKDAYS,
  applyOffPeakDiscount,
  crossesMidnight,
  currentPhaseLabel,
  emptyOffPeakWindow,
  normalizeOffPeakWindows,
  offPeakConfigured
} from "./pricingOffPeak.js";

const props = defineProps({
  api: { type: Function, required: true }
});

const providers = ref([]);
const models = ref([]);
const providersLoading = ref(false);
const providersLoadFailed = ref(false);
const modelsLoading = ref(false);
const catalogExporting = ref(false);
const catalogImporting = ref(false);
const catalogSyncing = ref(false);
const catalogImportVisible = ref(false);
const syncOverwrite = ref(false);
const catalogFileInput = ref(null);
const catalogImportState = reactive(createModelCatalogImportState());
const modelDialogVisible = ref(false);
const offPeakDiscount = ref(0.5);
const offPeakClock = ref(Date.now());
const offPeakTimeZones = OFF_PEAK_TIME_ZONES;
const offPeakTimeOptions = OFF_PEAK_TIME_OPTIONS;
const offPeakWeekdays = OFF_PEAK_WEEKDAYS;
const modelSaving = ref(false);
const providerDialogVisible = ref(false);
const providerSaving = ref(false);
const isMobile = ref(false);
const activeProvider = ref("all");
const page = ref(1);
const pageSize = ref(25);
const selection = ref([]);
const batchAction = ref("");
const catalogText = ref("{}");
const createModelAfterProvider = ref(false);
const matchTypes = {
  exact: "精确",
  prefix: "前缀",
  suffix: "后缀",
  contains: "包含"
};
const billingItems = [
  { value: "input", label: "输入" },
  { value: "output", label: "输出" },
  { value: "cache_write", label: "缓存写" },
  { value: "cache_read", label: "缓存读" }
];
const filters = reactive({
  query: "",
  enabled: null
});
const modelDraft = reactive(emptyModelDraft());
const providerDraft = reactive(emptyProviderDraft());
let mobileMediaQuery = null;
let offPeakTimer = null;

const pagedModels = computed(() => {
  const start = (page.value - 1) * pageSize.value;
  return models.value.slice(start, start + pageSize.value);
});

const catalogDialogTitle = computed(() => {
  const origin = catalogImportState.origin;
  if (origin === "sync") return "同步最新模型";
  if (origin === "overwrite") return "覆盖已有模型";
  return "导入模型目录";
});

const selectedModels = computed(() => {
  const selectedIds = new Set(selection.value);
  return models.value.filter((model) => selectedIds.has(model.id));
});

const selectedAll = computed(() =>
  models.value.length > 0 && selectedModels.value.length === models.value.length
);

watch(activeProvider, () => {
  loadModels();
});

async function loadAll() {
  await loadProviders();
  await loadModels();
}

async function loadProviders() {
  providersLoading.value = true;
  providersLoadFailed.value = false;
  try {
    const data = await props.api("/model-providers");
    providers.value = Array.isArray(data.providers) ? data.providers : [];
    if (activeProvider.value !== "all" && !providers.value.some((provider) => provider.code === activeProvider.value)) {
      activeProvider.value = "all";
    }
    return true;
  } catch (error) {
    providersLoadFailed.value = true;
    ElMessage.error(error.message);
    return false;
  } finally {
    providersLoading.value = false;
  }
}

async function loadModels() {
  modelsLoading.value = true;
  try {
    const params = new URLSearchParams();
    if (filters.query.trim()) params.set("query", filters.query.trim());
    if (activeProvider.value !== "all") params.set("provider", activeProvider.value);
    if (filters.enabled !== null && filters.enabled !== undefined) params.set("enabled", String(filters.enabled));
    const suffix = params.toString() ? `?${params.toString()}` : "";
    const data = await props.api(`/model-infos${suffix}`);
    models.value = Array.isArray(data.models) ? data.models : [];
    page.value = 1;
    clearSelection();
  } catch (error) {
    ElMessage.error(error.message);
  } finally {
    modelsLoading.value = false;
  }
}

async function exportCatalog() {
  catalogExporting.value = true;
  try {
    const data = await props.api("/model-catalog/export");
    const blob = new Blob([JSON.stringify(data, null, 2)], { type: "application/json" });
    const link = document.createElement("a");
    link.href = URL.createObjectURL(blob);
    link.download = `model-catalog-${Date.now()}.json`;
    link.click();
    URL.revokeObjectURL(link.href);
  } catch (error) {
    ElMessage.error(error.message);
  } finally {
    catalogExporting.value = false;
  }
}

function selectCatalogFile() {
  catalogFileInput.value?.click();
}

async function handleCatalogFileSelected(event) {
  const file = event.target.files?.[0];
  event.target.value = "";
  if (!file) return;

  try {
    const text = await file.text();
    const document = parseModelCatalogFile(text, file.name);
    catalogImportState.document = document;
    catalogImportState.origin = "file";
    catalogImportVisible.value = true;
    catalogImporting.value = true;
    try {
      const data = await props.api("/model-catalog/import?dryRun=true", {
        method: "POST",
        body: JSON.stringify(document)
      });
      applyModelCatalogDryRun(catalogImportState, file.name, document, data);
    } catch (error) {
      failModelCatalogImport(catalogImportState, error.message);
    } finally {
      catalogImporting.value = false;
    }
  } catch (error) {
    ElMessage.error(error.message);
  }
}

function handleCatalogImportCommand(command) {
  if (command === "file") {
    selectCatalogFile();
  } else if (command === "sync") {
    syncOverwrite.value = false;
    resetModelCatalogImport(catalogImportState);
    catalogImportState.origin = "sync";
    catalogImportState.phase = "confirm";
    catalogImportVisible.value = true;
  }
}

function beginSync() {
  startSync(syncOverwrite.value ? "overwrite" : "incremental");
}

async function startSync(mode) {
  catalogSyncing.value = true;
  catalogImportState.origin = mode === "overwrite" ? "overwrite" : "sync";
  catalogImportVisible.value = true;
  resetModelCatalogImport(catalogImportState);
  catalogImportState.origin = mode === "overwrite" ? "overwrite" : "sync";
  catalogImportState.phase = "loading";
  try {
    const data = await props.api(`/model-catalog/sync?mode=${mode}&dryRun=true`, {
      method: "POST"
    });
    if (mode === "incremental" && (data.models?.created || 0) === 0 && (data.skipped || 0) >= 0) {
      const hasNew = (data.created_model_keys || []).length > 0;
      if (!hasNew) {
        closeCatalogImportDialog();
        ElMessage.info("没有新模型，已是最新");
        return;
      }
    }
    applyModelCatalogDryRun(catalogImportState, "", null, data);
  } catch (error) {
    failModelCatalogImport(catalogImportState, error.message);
  } finally {
    catalogSyncing.value = false;
  }
}

async function confirmCatalogImport() {
  if (catalogImportState.origin === "file" && !catalogImportState.document) return;
  if (catalogImportState.origin === "overwrite" && !catalogImportState.overwriteConfirmed) return;

  const isSync = catalogImportState.origin === "sync" || catalogImportState.origin === "overwrite";
  const mode = catalogImportState.origin === "overwrite" ? "overwrite" : "incremental";
  catalogImporting.value = true;
  try {
    let data;
    if (isSync) {
      data = await props.api(`/model-catalog/sync?mode=${mode}&dryRun=false`, {
        method: "POST"
      });
    } else {
      data = await props.api("/model-catalog/import?dryRun=false", {
        method: "POST",
        body: JSON.stringify(catalogImportState.document)
      });
    }
    applyModelCatalogImport(catalogImportState, data);
    await loadAll();
  } catch (error) {
    failModelCatalogImport(catalogImportState, error.message);
  } finally {
    catalogImporting.value = false;
  }
}

function closeCatalogImportDialog() {
  catalogImportVisible.value = false;
  resetModelCatalogImport(catalogImportState);
}

function openModelDialog(row = null) {
  if (!row && providersLoading.value) {
    ElMessage.info("供应商正在加载，请稍后");
    return;
  }

  if (!row && providersLoadFailed.value) {
    ElMessage.error("供应商加载失败，请先刷新");
    return;
  }

  if (!row && providers.value.length === 0) {
    ElMessage.warning("请先新增供应商");
    openProviderDialog(true);
    return;
  }

  Object.assign(modelDraft, emptyModelDraft());
  catalogText.value = "{}";
  if (row) {
    Object.assign(modelDraft, {
      id: row.id,
      provider_code: row.provider_code || providers.value[0]?.code || "",
      model_key: row.model_key || "",
      display_name: row.display_name || "",
      description: row.description || "",
      match_type: row.match_type || "exact",
      match_pattern: row.match_pattern || row.model_key || "",
      enabled: row.enabled !== false,
      capabilities: {
        supports_image: row.capabilities?.supports_image === true,
        context_window: Number(row.capabilities?.context_window || 0)
      },
      pricing: normalizePricing(row.pricing)
    });
    catalogText.value = JSON.stringify(row.catalog || {}, null, 2);
  } else {
    modelDraft.provider_code = activeProvider.value !== "all"
      ? activeProvider.value
      : providers.value[0]?.code || "";
    modelDraft.pricing = normalizePricing(null);
    catalogText.value = JSON.stringify(defaultCatalog(), null, 2);
  }
  modelDialogVisible.value = true;
}

function openProviderDialog(continueToModel = false) {
  createModelAfterProvider.value = continueToModel;
  Object.assign(providerDraft, emptyProviderDraft());
  providerDialogVisible.value = true;
}

async function saveProvider() {
  providerSaving.value = true;
  try {
    const code = normalizeProviderCode(providerDraft.code);
    const name = providerDraft.name.trim();
   if (!code) throw new Error("供应商编码不能为空");
   if (!/^[a-z0-9._-]+$/.test(code)) throw new Error("供应商编码仅支持字母、数字、点、下划线和连字符");
    if (!name) throw new Error("显示名称不能为空");

    const payload = {
      code,
      name,
      enabled: providerDraft.enabled !== false,
      sort_order: Number(providerDraft.sort_order || 0)
    };

    if (providerDraft.id) {
      await props.api(`/model-providers/${providerDraft.id}`, {
        method: "PATCH",
        body: JSON.stringify(payload)
      });
      providerDialogVisible.value = false;
      if (!await loadProviders()) return;
      if (activeProvider.value === code) {
        await loadModels();
      }
      ElMessage.success("供应商已更新");
    } else {
      const data = await props.api("/model-providers", {
        method: "POST",
        body: JSON.stringify(payload)
      });
      const continueToModel = createModelAfterProvider.value;
      createModelAfterProvider.value = false;
      providerDialogVisible.value = false;
      if (!await loadProviders()) return;
      const createdCode = data.provider?.code || code;
      if (activeProvider.value === createdCode) {
        await loadModels();
      } else {
        activeProvider.value = createdCode;
      }
      ElMessage.success("供应商已新增");
      if (continueToModel) openModelDialog();
    }
  } catch (error) {
    ElMessage.error(error.message);
  } finally {
    providerSaving.value = false;
  }
}

async function saveModel() {
  modelSaving.value = true;
  try {
    const body = JSON.stringify(buildModelPayload());
    if (modelDraft.id) {
      await props.api(`/model-infos/${modelDraft.id}`, { method: "PATCH", body });
    } else {
      await props.api("/model-infos", { method: "POST", body });
    }

    modelDialogVisible.value = false;
    await loadModels();
    ElMessage.success("模型信息已保存");
  } catch (error) {
    ElMessage.error(error.message);
  } finally {
    modelSaving.value = false;
  }
}

async function deleteModel(row) {
  try {
    await props.api(`/model-infos/${row.id}`, { method: "DELETE" });
    await loadModels();
    ElMessage.success(row.enabled === false ? "模型已删除" : "模型已停用");
  } catch (error) {
    ElMessage.error(error.message);
  }
}

function clearSelection() {
  selection.value = [];
}

function toggleModelSelection(model, checked) {
  const index = selection.value.indexOf(model.id);
  if (checked && index === -1) {
    selection.value.push(model.id);
  } else if (!checked && index !== -1) {
    selection.value.splice(index, 1);
  }
}

function toggleSelectAll() {
  if (selectedAll.value) {
    clearSelection();
  } else {
    selection.value = models.value.map((model) => model.id);
  }
}

async function batchEnable() {
  await runBatch("enable");
}

async function batchDisable() {
  await runBatch("disable");
}

async function batchDelete() {
  const targetIds = selection.value;
  const enabledCount = selectedModels.value.filter((model) => model.enabled !== false).length;
  const willDelete = selectedModels.value.filter((model) => model.enabled === false);
  if (enabledCount > 0) {
    ElMessage.warning(`批量删除仅支持停用状态模型，其中 ${enabledCount} 个启用模型请先停用`);
    return;
  }
  if (willDelete.length === 0) return;
  try {
    await ElMessageBox.confirm(
      `确定删除 ${willDelete.length} 个模型？删除后不可恢复。`,
      "批量删除",
      { type: "warning", confirmButtonText: "删除", cancelButtonText: "取消" }
    );
  } catch {
    return;
  }
  await runBatch("delete", targetIds);
}

async function runBatch(action, ids = selection.value) {
  if (ids.length === 0) return;
  batchAction.value = action;
  try {
    const data = await props.api("/model-infos/batch", {
      method: "POST",
      body: JSON.stringify({ action, ids })
    });
    const updated = data?.updated_ids?.length || 0;
    const deleted = data?.deleted_ids?.length || 0;
    const errors = data?.errors || [];
    if (action === "delete") {
      ElMessage.success(`已删除 ${deleted} 个模型`);
    } else {
      ElMessage.success(`已${action === "enable" ? "启用" : "停用"} ${updated} 个模型`);
    }
    if (errors.length > 0) {
      ElMessage.warning(errors.join("；"));
    }
    clearSelection();
    await loadModels();
  } catch (error) {
    ElMessage.error(error.message);
  } finally {
    batchAction.value = "";
  }
}

function buildModelPayload() {
  const catalog = parseJson(catalogText.value || "{}", "Catalog JSON");
  const rules = modelDraft.pricing.rules.map((rule) => ({
    billing_item: rule.billing_item,
    billing_mode: rule.billing_mode,
    unit_price: Number(rule.unit_price || 0),
    tiers: rule.billing_mode === "tiered_tokens"
      ? parseTiers(rule.tiers_text)
      : [],
    off_peak_enabled: rule.off_peak_enabled === true,
    off_peak_unit_price: Number(rule.off_peak_unit_price || 0),
    off_peak_tiers: rule.billing_mode === "tiered_tokens" && rule.off_peak_enabled === true
      ? parseTiers(rule.off_peak_tiers_text)
      : [],
    enabled: rule.enabled !== false
  }));
  return {
    provider_code: modelDraft.provider_code,
    model_key: modelDraft.model_key,
    display_name: modelDraft.display_name,
    description: modelDraft.description,
    match_type: modelDraft.match_type,
    match_pattern: modelDraft.match_pattern,
    catalog,
    capabilities: {
      ...modelDraft.capabilities,
      context_window: Number(modelDraft.capabilities.context_window || 0)
    },
    pricing: {
      currency: modelDraft.pricing.currency || "USD",
      enabled: modelDraft.pricing.enabled !== false,
      time_zone: modelDraft.pricing.time_zone || "",
      off_peak_windows: modelDraft.pricing.time_zone
        ? normalizeOffPeakWindows(modelDraft.pricing.off_peak_windows)
        : [],
      rules
    },
    enabled: modelDraft.enabled !== false
  };
}

function emptyModelDraft() {
  return {
    id: null,
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
    pricing: normalizePricing(null),
    enabled: true
  };
}

function emptyProviderDraft() {
  return {
    id: null,
    code: "",
    name: "",
    sort_order: nextProviderSortOrder(),
    enabled: true
  };
}
function openEditProviderDialog(provider) {
  createModelAfterProvider.value = false;
  Object.assign(providerDraft, {
    id: provider.id,
    code: provider.code || "",
    name: provider.name || "",
    sort_order: Number(provider.sort_order || 0),
    enabled: provider.enabled !== false
  });
  providerDialogVisible.value = true;
}

function handleProviderCommand(command, provider) {
  if (command === "edit") {
    openEditProviderDialog(provider);
  } else if (command === "delete") {
    confirmDeleteProvider(provider);
  }
}

async function confirmDeleteProvider(provider) {
  const modelCount = models.value.filter(
    (m) => m.provider_code === provider.code
  ).length;
  if (modelCount > 0) {
    ElMessage.warning(`该供应商下还有 ${modelCount} 个模型，请先删除或迁移模型后再删除供应商`);
    return;
  }
  try {
    await ElMessageBox.confirm(
      `确定删除供应商"${provider.name}"？此操作不可恢复。`,
      "删除供应商",
      { type: "warning", confirmButtonText: "删除", cancelButtonText: "取消" }
    );
  } catch {
    return;
  }
  try {
    await props.api(`/model-providers/${provider.id}`, { method: "DELETE" });
    if (activeProvider.value === provider.code) {
      activeProvider.value = "all";
    }
    await loadProviders();
    ElMessage.success("供应商已删除");
  } catch (error) {
    ElMessage.error(error.message);
  }
}

function nextProviderSortOrder() {
  const maxSort = providers.value.reduce((max, provider) => Math.max(max, Number(provider.sort_order || 0)), 0);
  return maxSort + 10;
}

function normalizeProviderCode(value) {
  return String(value || "").trim().toLowerCase();
}

function normalizePricing(pricing) {
  const rulesByItem = new Map();
  for (const rule of pricing?.rules || []) {
    rulesByItem.set(rule.billing_item, normalizeRule(rule));
  }
  return {
    currency: pricing?.currency || "USD",
    enabled: pricing?.enabled !== false,
    time_zone: pricing?.time_zone || "",
    off_peak_windows: normalizeOffPeakWindows(pricing?.off_peak_windows),
    rules: billingItems.map((item) => rulesByItem.get(item.value) || defaultRule(item.value))
  };
}

function normalizeRule(rule) {
  return {
    billing_item: rule.billing_item,
    billing_mode: rule.billing_mode || "per_million_tokens",
    unit_price: Number(rule.unit_price || 0),
    tiers_text: JSON.stringify(rule.tiers || [], null, 2),
    _tiers: (rule.tiers || []).map((tier) => ({
      up_to: tier.up_to === null || tier.up_to === undefined || tier.up_to === "" ? null : Number(tier.up_to),
      unit_price: Number(tier.unit_price || 0)
    })),
    off_peak_enabled: rule.off_peak_enabled === true,
    off_peak_unit_price: Number(rule.off_peak_unit_price || 0),
    off_peak_tiers_text: JSON.stringify(rule.off_peak_tiers || [], null, 2),
    _offPeakTiers: (rule.off_peak_tiers || []).map((tier) => ({
      up_to: tier.up_to === null || tier.up_to === undefined || tier.up_to === "" ? null : Number(tier.up_to),
      unit_price: Number(tier.unit_price || 0)
    })),
    enabled: rule.enabled !== false
  };
}

function defaultRule(item) {
  return {
    billing_item: item,
    billing_mode: "per_million_tokens",
    unit_price: 0,
    tiers_text: "[]",
    _tiers: [],
    off_peak_enabled: false,
    off_peak_unit_price: 0,
    off_peak_tiers_text: "[]",
    _offPeakTiers: [],
    enabled: true
  };
}

function pricingSummary(row, item) {
  const rule = (row.pricing?.rules || []).find((entry) => entry.billing_item === item && entry.enabled !== false);
  if (!rule) return "-";
  const offPeak = offPeakConfigured(row.pricing) && rule.off_peak_enabled === true;
  if (rule.billing_mode === "tiered_tokens") return offPeak ? "窗口档（峰/谷）" : "窗口档";
  const suffix = rule.billing_mode === "per_request" ? " / 次" : "";
  const peakPrice = `${formatPrice(rule.unit_price)}${suffix}`;
  return offPeak
    ? `${peakPrice} 峰 / ${formatPrice(rule.off_peak_unit_price)} 谷`
    : peakPrice;
}

function parseTiers(text) {
  const value = parseJson(text || "[]", "阶梯");
  if (!Array.isArray(value)) {
    throw new Error("阶梯必须是 JSON 数组");
  }
  return value.map((tier) => ({
    up_to: tier.up_to === null || tier.up_to === undefined || tier.up_to === "" ? null : Number(tier.up_to),
    unit_price: Number(tier.unit_price || 0)
  }));
}

// 窗口档可视化编辑：tiers_text 是存储中间态，tiersOf 把它解析成可编辑数组并缓存到行上，
// 增删改档位后用 syncTiers 写回 tiers_text，保持 buildRulePayload 提交链路不变。
function tiersOf(rule) {
  return rule._tiers || parseTiers(rule.tiers_text);
}

function offPeakTiersOf(rule) {
  return rule._offPeakTiers || parseTiers(rule.off_peak_tiers_text);
}

function syncTiers(rule, cacheKey, textKey) {
  rule[textKey] = JSON.stringify(rule[cacheKey] || [], null, 2);
}

function addTier(rule) {
  tiersOf(rule).push({ up_to: null, unit_price: 0 });
  syncTiers(rule, "_tiers", "tiers_text");
}

function removeTier(rule, index) {
  tiersOf(rule).splice(index, 1);
  syncTiers(rule, "_tiers", "tiers_text");
}

function addOffPeakTier(rule) {
  offPeakTiersOf(rule).push({ up_to: null, unit_price: 0 });
  syncTiers(rule, "_offPeakTiers", "off_peak_tiers_text");
}

function removeOffPeakTier(rule, index) {
  offPeakTiersOf(rule).splice(index, 1);
  syncTiers(rule, "_offPeakTiers", "off_peak_tiers_text");
}

const offPeakEnabledForDraft = computed(() => offPeakConfigured(modelDraft.pricing));

const offPeakHint = computed(() => currentPhaseLabel(modelDraft.pricing, new Date(offPeakClock.value)));

function addOffPeakWindow() {
  modelDraft.pricing.off_peak_windows.push(emptyOffPeakWindow());
}

function removeOffPeakWindow(index) {
  modelDraft.pricing.off_peak_windows.splice(index, 1);
}

function fillOffPeakDiscount() {
  const changed = applyOffPeakDiscount(modelDraft.pricing.rules, offPeakDiscount.value);
  if (changed === 0) {
    ElMessage.warning("先为需要打折的计费项打开峰谷开关");
    return;
  }
  ElMessage.success(`已按峰价的 ${offPeakDiscount.value} 倍填充 ${changed} 项谷价`);
}

function parseJson(text, label) {
  try {
    return JSON.parse(text || "{}");
  } catch {
    throw new Error(`${label} 不是合法 JSON`);
  }
}

function defaultCatalog() {
  return {
    slug: modelDraft.model_key,
    display_name: modelDraft.display_name || modelDraft.model_key,
    visibility: "list",
    supported_in_api: true
  };
}

function formatMatchType(value) {
  return matchTypes[value] || value || "-";
}

function formatBillingItem(value) {
  return billingItems.find((item) => item.value === value)?.label || value || "-";
}

function formatPrice(value) {
  return Number(value || 0).toLocaleString(undefined, {
    minimumFractionDigits: 0,
    maximumFractionDigits: 8
  });
}

function syncMobileState(event) {
  isMobile.value = event.matches;
}

onMounted(() => {
  mobileMediaQuery = window.matchMedia("(max-width: 767px)");
  syncMobileState(mobileMediaQuery);
  mobileMediaQuery.addEventListener("change", syncMobileState);
  loadAll();
  offPeakTimer = window.setInterval(() => {
    offPeakClock.value = Date.now();
  }, 30000);
});

onBeforeUnmount(() => {
  mobileMediaQuery?.removeEventListener("change", syncMobileState);
  if (offPeakTimer) {
    window.clearInterval(offPeakTimer);
    offPeakTimer = null;
  }
});
</script>

<style scoped>
.toolbar-query {
  width: 260px;
}

.toolbar-status {
  width: 120px;
}

.provider-tabs {
  margin-bottom: 12px;
}

.provider-tab-label {
  cursor: pointer;
}

.match-pattern {
  margin-left: 8px;
}

.model-catalog-rule-table {
  margin-bottom: 16px;
}

/* 计费规则表格列总宽超过抽屉容器时整表横向滚动，表头表身一起移动、不错位。 */
.rule-table-scroll {
  overflow-x: auto;
  margin-bottom: 16px;
}
.rule-table-scroll .model-catalog-rule-table {
  min-width: 1090px;
  margin-bottom: 0;
}
.rule-table-scroll::-webkit-scrollbar {
  height: 8px;
}
.rule-table-scroll::-webkit-scrollbar-thumb {
  background: var(--el-border-color);
  border-radius: 4px;
}

.off-peak-panel {
  margin-bottom: 12px;
}

.off-peak-panel__head {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
  margin-bottom: 8px;
}

.off-peak-panel__hint {
  font-size: 12px;
  color: var(--el-text-color-secondary);
}

.off-peak-panel__actions {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  flex-wrap: wrap;
}

.off-peak-window {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
  margin-bottom: 6px;
}

.off-peak-window__sep {
  font-size: 12px;
  color: var(--el-text-color-secondary);
}

.off-peak-window__note {
  font-size: 12px;
  color: var(--el-color-warning);
}

.off-peak-zone {
  width: 240px;
}

.off-peak-time {
  width: 110px;
}

.off-peak-days {
  width: 220px;
}

.off-peak-discount {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 12px;
  color: var(--el-text-color-secondary);
}

.off-peak-tiers {
  margin-top: 6px;
}

.tier-editor {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.tier-editor-section {
  border: 1px solid var(--el-border-color-lighter);
  border-radius: 6px;
  padding: 8px;
}

.tier-editor-head {
  font-size: 12px;
  color: var(--el-text-color-secondary);
  margin-bottom: 6px;
}

.tier-editor-row {
  display: flex;
  align-items: center;
  gap: 6px;
  margin-bottom: 6px;
}

.tier-editor-label {
  color: var(--el-text-color-secondary);
  white-space: nowrap;
}

.tier-editor-up-to {
  width: 110px;
  flex-shrink: 0;
}

.tier-editor-price {
  flex: 1;
  min-width: 110px;
}

.mobile-tier-editor {
  min-width: 0;
  width: 100%;
}

.model-card-select {
  margin-bottom: 8px;
}

.batch-action-button {
  margin-left: 8px;
}

.batch-delete-button {
  margin-left: 8px;
}

.advanced-collapse {
  margin-top: 16px;
}

.full-width {
  width: 100%;
}

.catalog-file-input {
  display: none;
}

.catalog-import-summary {
  display: grid;
  gap: 8px;
  margin-top: 16px;
}

.catalog-import-summary > div {
  display: flex;
  justify-content: space-between;
  gap: 16px;
  padding: 10px 12px;
  border: 1px solid var(--el-border-color-lighter);
  border-radius: 6px;
  background: var(--el-bg-color);
}

.catalog-import-summary dt {
  color: var(--el-text-color-secondary);
  font-size: 13px;
}

.catalog-import-summary dd {
  margin: 0;
  font-size: 14px;
  text-align: right;
}

.catalog-import-keys {
  margin-top: 12px;
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.catalog-import-key-section {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.catalog-import-key-label {
  font-size: 13px;
  color: var(--el-text-color-secondary);
}

.catalog-import-key-list {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}

.catalog-import-key-more {
  font-size: 12px;
  color: var(--el-text-color-secondary);
  align-self: center;
}

.catalog-overwrite-confirm {
  margin-top: 16px;
  padding: 12px;
  border: 1px solid var(--el-border-color-lighter);
  border-radius: 6px;
  background: var(--el-bg-color-page);
}

.catalog-sync-confirm {
  margin: 16px 0;
}

.catalog-import-loading {
  min-height: 120px;
  display: flex;
  align-items: center;
  justify-content: center;
}

.catalog-import-loading-spacer {
  height: 80px;
}

.mobile-provider-filter,
.mobile-model-list {
  display: none;
}

@media (max-width: 767px) {
  .model-catalog-page .toolbar {
    display: block;
    margin-bottom: 16px;
  }

  .off-peak-zone,
  .off-peak-days,
  .off-peak-time {
    width: 100%;
  }

  .off-peak-window {
    padding-bottom: 8px;
    border-bottom: 1px solid var(--el-border-color-lighter);
  }

  .model-catalog-page .toolbar-actions {
    display: grid;
    grid-template-columns: repeat(2, minmax(0, 1fr));
    gap: 8px;
    width: 100%;
    margin-top: 16px;
  }

  .toolbar-query {
    grid-column: 1 / -1;
    width: 100%;
  }

  .toolbar-status {
    width: 100%;
  }

  .model-catalog-page .toolbar-actions > :deep(.el-button) {
    width: 100%;
    min-height: 44px;
    margin-left: 0;
  }

  .pricing-page .toolbar-actions > :deep(.el-dropdown) {
    grid-column: 1 / -1;
    width: 100%;
  }

  .pricing-page .toolbar-actions > :deep(.el-dropdown) .el-button {
    width: 100%;
    min-height: 44px;
    margin-left: 0;
  }

  .create-model-button {
    grid-column: 1 / -1;
  }

  .catalog-import-summary > div {
    flex-direction: column;
    gap: 4px;
  }

  .catalog-import-summary dd {
    text-align: left;
  }

  .model-catalog-page .toolbar-actions > :deep(.el-input),
  .model-catalog-page .toolbar-actions > :deep(.el-select) {
    min-width: 0;
  }

  .mobile-provider-filter {
    display: grid;
    grid-template-columns: auto minmax(0, 1fr);
    align-items: center;
    gap: 12px;
    margin-bottom: 12px;
  }

  .mobile-provider-label {
    color: var(--el-text-color-regular);
    font-size: 14px;
    font-weight: 600;
  }

  .mobile-model-list {
    display: grid;
    gap: 12px;
    min-height: 96px;
  }

  .model-card {
    min-width: 0;
    padding: 14px;
    border: 1px solid var(--el-border-color-lighter);
    border-radius: 6px;
    background: var(--el-bg-color);
  }

  .model-card-header {
    display: flex;
    align-items: flex-start;
    justify-content: space-between;
    gap: 12px;
  }

  .model-card-title-wrap {
    display: grid;
    min-width: 0;
    gap: 4px;
  }

  .model-card-title,
  .model-card-key,
  .model-card-match span,
  .model-card-meta dd {
    overflow-wrap: anywhere;
  }

  .model-card-title {
    color: var(--el-text-color-primary);
    font-size: 16px;
    line-height: 1.4;
  }

  .model-card-key {
    color: var(--el-text-color-secondary);
    font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    font-size: 12px;
    line-height: 1.45;
  }

  .model-card-meta,
  .model-card-pricing {
    display: grid;
    margin: 14px 0 0;
  }

  .model-card-meta {
    grid-template-columns: repeat(2, minmax(0, 1fr));
    gap: 10px 16px;
  }

  .model-card-meta div,
  .model-card-pricing div {
    min-width: 0;
  }

  .model-card-meta dt,
  .model-card-pricing dt {
    margin-bottom: 3px;
    color: var(--el-text-color-secondary);
    font-size: 12px;
  }

  .model-card-meta dd,
  .model-card-pricing dd {
    margin: 0;
    color: var(--el-text-color-primary);
    font-size: 14px;
    line-height: 1.45;
  }

  .model-card-match {
    display: flex;
    align-items: flex-start;
    gap: 8px;
    min-width: 0;
    margin-top: 14px;
    padding-top: 12px;
    border-top: 1px solid var(--el-border-color-lighter);
    color: var(--el-text-color-regular);
    font-size: 13px;
    line-height: 24px;
  }

  .model-card-match span {
    min-width: 0;
  }

  .model-card-pricing {
    grid-template-columns: repeat(2, minmax(0, 1fr));
    gap: 10px 16px;
    padding: 12px;
    border-radius: 4px;
    background: var(--el-fill-color-light);
  }

  .model-card-pricing dd {
    font-variant-numeric: tabular-nums;
  }

  .model-card-actions {
    display: grid;
    grid-template-columns: repeat(2, minmax(0, 1fr));
    gap: 8px;
    margin-top: 14px;
  }

  .model-card-actions > :deep(.el-button),
  .model-card-actions > :deep(.el-tooltip__trigger) {
    width: 100%;
    min-height: 44px;
    margin-left: 0;
  }

  .pagination-wrap {
    margin-top: 16px;
  }

  .mobile-pagination {
    display: grid;
    justify-items: center;
    gap: 8px;
    width: 100%;
    color: var(--el-text-color-secondary);
    font-size: 13px;
  }

  .mobile-pagination :deep(.el-pagination) {
    display: flex;
    justify-content: center;
    max-width: 100%;
  }

  .mobile-pagination :deep(.btn-prev),
  .mobile-pagination :deep(.btn-next),
  .mobile-pagination :deep(.el-pager li) {
    min-width: 32px;
    margin: 0;
  }

  .mobile-model-catalog-rules {
    display: grid;
    gap: 12px;
    overflow-x: auto;
    max-width: 100%;
  }

  .model-catalog-rule-card {
    min-width: 0;
    max-width: 100%;
    padding: 14px;
    border: 1px solid var(--el-border-color-lighter);
    border-radius: 6px;
    background: var(--el-fill-color-extra-light);
  }

  .model-catalog-rule-heading,
  .model-catalog-rule-switch {
    display: flex;
    align-items: center;
  }

  .model-catalog-rule-heading {
    justify-content: space-between;
    gap: 12px;
    margin-bottom: 14px;
  }

  .model-catalog-rule-switch {
    gap: 8px;
    color: var(--el-text-color-secondary);
    font-size: 13px;
  }

  .model-catalog-rule-field {
    display: grid;
    gap: 7px;
    margin-top: 12px;
    color: var(--el-text-color-regular);
    font-size: 13px;
  }

  .model-catalog-rule-field:first-of-type {
    margin-top: 0;
  }

  .model-catalog-page :deep(.el-input__inner),
  .model-catalog-page :deep(.el-textarea__inner) {
    font-size: 16px;
  }
}

@media (max-width: 380px) {
  .model-card-meta,
  .model-card-pricing {
    grid-template-columns: 1fr;
  }
}

:global(.model-catalog-model-dialog.is-fullscreen),
:global(.model-catalog-provider-dialog.is-fullscreen) {
  display: flex;
  flex-direction: column;
  height: 100dvh;
  margin: 0;
  overflow: hidden;
}

:global(.model-catalog-model-dialog.is-fullscreen .el-dialog__header),
:global(.model-catalog-provider-dialog.is-fullscreen .el-dialog__header) {
  flex: none;
  padding: 16px;
  padding-top: max(16px, env(safe-area-inset-top));
  border-bottom: 1px solid var(--el-border-color-lighter);
}

:global(.model-catalog-model-dialog.is-fullscreen .el-dialog__body),
:global(.model-catalog-provider-dialog.is-fullscreen .el-dialog__body) {
  flex: 1;
  min-height: 0;
  overflow: auto;
  overscroll-behavior: contain;
  padding: 14px 16px;
}

:global(.model-catalog-model-dialog.is-fullscreen .el-dialog__footer),
:global(.model-catalog-provider-dialog.is-fullscreen .el-dialog__footer) {
  flex: none;
  padding: 12px 16px;
  padding-bottom: max(12px, env(safe-area-inset-bottom));
  border-top: 1px solid var(--el-border-color-lighter);
  background: var(--el-bg-color);
}

:global(.model-catalog-model-dialog.is-fullscreen .drawer-footer),
:global(.model-catalog-provider-dialog.is-fullscreen .drawer-footer) {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 8px;
}

:global(.model-catalog-model-dialog.is-fullscreen .drawer-footer .el-button),
:global(.model-catalog-provider-dialog.is-fullscreen .drawer-footer .el-button) {
  width: 100%;
  min-height: 44px;
  margin-left: 0;
}

:global(.model-catalog-model-dialog.is-fullscreen .el-input__inner),
:global(.model-catalog-model-dialog.is-fullscreen .el-textarea__inner),
:global(.model-catalog-provider-dialog.is-fullscreen .el-input__inner) {
  font-size: 16px;
}
</style>
