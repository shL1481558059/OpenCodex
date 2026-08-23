<template>
  <div class="pricing-page">
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
        <el-button :icon="Search" @click="loadModels">搜索</el-button>
        <el-button :icon="Refresh" @click="loadAll">刷新</el-button>
        <el-button :icon="Download" :loading="catalogExporting" @click="exportCatalog">导出</el-button>
        <el-button :icon="Upload" :loading="catalogImporting" @click="selectCatalogFile">导入</el-button>
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
      title="导入模型目录"
      :width="isMobile ? undefined : '620px'"
      :fullscreen="isMobile"
    >
      <template v-if="catalogImportState.phase === 'preview'">
        <el-alert
          type="success"
          show-icon
          :closable="false"
          title="预检通过，等待确认"
          description="确认后写入数据库，未确认前不修改任何数据。"
        />
        <dl class="catalog-import-summary">
          <div>
            <dt>供应商</dt>
            <dd>新增 {{ catalogImportState.dryRun?.providers?.created || 0 }}，更新 {{ catalogImportState.dryRun?.providers?.updated || 0 }}，无变化 {{ catalogImportState.dryRun?.providers?.unchanged || 0 }}</dd>
          </div>
          <div>
            <dt>模型</dt>
            <dd>新增 {{ catalogImportState.dryRun?.models?.created || 0 }}，更新 {{ catalogImportState.dryRun?.models?.updated || 0 }}，无变化 {{ catalogImportState.dryRun?.models?.unchanged || 0 }}</dd>
          </div>
          <div v-if="(catalogImportState.dryRun?.pricing_deleted || 0) > 0">
            <dt>价格删除</dt>
            <dd>{{ catalogImportState.dryRun.pricing_deleted }} 个模型价格将被移除</dd>
          </div>
        </dl>
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
            v-if="catalogImportState.phase === 'preview'"
            type="primary"
            :loading="catalogImporting"
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
        <el-table-column label="操作" width="210" align="center">
          <template #default="{ row }">
            <div class="inline-actions channel-table-actions">
              <el-button size="small" :icon="Edit" @click="openModelDialog(row)">编辑</el-button>
              <el-popconfirm :title="`停用模型 ${row.model_key}？`" @confirm="deleteModel(row)">
                <template #reference>
                  <el-button size="small" type="danger" :icon="Delete">停用</el-button>
                </template>
              </el-popconfirm>
            </div>
          </template>
        </el-table-column>
      </el-table>

      <div v-else v-loading="modelsLoading" class="mobile-model-list">
        <article v-for="model in pagedModels" :key="model.id" class="model-card">
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
            <el-popconfirm :title="`停用模型 ${model.model_key}？`" @confirm="deleteModel(model)">
              <template #reference>
                <el-button type="danger" :icon="Delete">停用</el-button>
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
      class="pricing-model-dialog"
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
        <el-table
          v-if="!isMobile"
          :data="modelDraft.pricing.rules"
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

        <div v-else class="mobile-pricing-rules">
          <section v-for="rule in modelDraft.pricing.rules" :key="rule.billing_item" class="pricing-rule-card">
            <div class="pricing-rule-heading">
              <strong>{{ formatBillingItem(rule.billing_item) }}</strong>
              <div class="pricing-rule-switch">
                <span>{{ rule.enabled ? "启用" : "停用" }}</span>
                <el-switch v-model="rule.enabled" />
              </div>
            </div>
            <label class="pricing-rule-field">
              <span>计费模式</span>
              <el-select v-model="rule.billing_mode" class="full-width">
                <el-option label="按次" value="per_request" />
                <el-option label="每百万 token" value="per_million_tokens" />
                <el-option label="阶梯 token" value="tiered_tokens" />
              </el-select>
            </label>
            <label class="pricing-rule-field">
              <span>单价</span>
              <el-input-number
                v-model="rule.unit_price"
                :min="0"
                :precision="8"
                :step="0.01"
                class="full-width"
              />
            </label>
            <label v-if="rule.billing_mode === 'tiered_tokens'" class="pricing-rule-field">
              <span>阶梯 JSON</span>
              <el-input v-model="rule.tiers_text" type="textarea" :rows="4" />
            </label>
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
      class="pricing-provider-dialog"
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
import { Delete, Download, Edit, Plus, Refresh, Search, Upload } from "@element-plus/icons-vue";
import {
  createModelCatalogImportState,
  parseModelCatalogFile,
  applyModelCatalogDryRun,
  applyModelCatalogImport,
  failModelCatalogImport,
  resetModelCatalogImport
} from "./modelCatalogImportState.js";

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
const catalogImportVisible = ref(false);
const catalogFileInput = ref(null);
const catalogImportState = reactive(createModelCatalogImportState());
const modelDialogVisible = ref(false);
const modelSaving = ref(false);
const providerDialogVisible = ref(false);
const providerSaving = ref(false);
const isMobile = ref(false);
const activeProvider = ref("all");
const page = ref(1);
const pageSize = ref(25);
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

const pagedModels = computed(() => {
  const start = (page.value - 1) * pageSize.value;
  return models.value.slice(start, start + pageSize.value);
});

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

async function confirmCatalogImport() {
  if (!catalogImportState.document) return;
  catalogImporting.value = true;
  try {
    const data = await props.api("/model-catalog/import?dryRun=false", {
      method: "POST",
      body: JSON.stringify(catalogImportState.document)
    });
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
    ElMessage.success("模型已停用");
  } catch (error) {
    ElMessage.error(error.message);
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
    rules: billingItems.map((item) => rulesByItem.get(item.value) || defaultRule(item.value))
  };
}

function normalizeRule(rule) {
  return {
    billing_item: rule.billing_item,
    billing_mode: rule.billing_mode || "per_million_tokens",
    unit_price: Number(rule.unit_price || 0),
    tiers_text: JSON.stringify(rule.tiers || [], null, 2),
    enabled: rule.enabled !== false
  };
}

function defaultRule(item) {
  return {
    billing_item: item,
    billing_mode: "per_million_tokens",
    unit_price: 0,
    tiers_text: "[]",
    enabled: true
  };
}

function pricingSummary(row, item) {
  const rule = (row.pricing?.rules || []).find((entry) => entry.billing_item === item && entry.enabled !== false);
  if (!rule) return "-";
  if (rule.billing_mode === "tiered_tokens") return "阶梯";
  if (rule.billing_mode === "per_request") return `${formatPrice(rule.unit_price)} / 次`;
  return formatPrice(rule.unit_price);
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
});

onBeforeUnmount(() => {
  mobileMediaQuery?.removeEventListener("change", syncMobileState);
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

.pricing-rule-table {
  margin-bottom: 16px;
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

.mobile-provider-filter,
.mobile-model-list {
  display: none;
}

@media (max-width: 767px) {
  .pricing-page .toolbar {
    display: block;
    margin-bottom: 16px;
  }

  .pricing-page .toolbar-actions {
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

  .pricing-page .toolbar-actions > :deep(.el-button) {
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

  .pricing-page .toolbar-actions > :deep(.el-input),
  .pricing-page .toolbar-actions > :deep(.el-select) {
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

  .mobile-pricing-rules {
    display: grid;
    gap: 12px;
  }

  .pricing-rule-card {
    min-width: 0;
    padding: 14px;
    border: 1px solid var(--el-border-color-lighter);
    border-radius: 6px;
    background: var(--el-fill-color-extra-light);
  }

  .pricing-rule-heading,
  .pricing-rule-switch {
    display: flex;
    align-items: center;
  }

  .pricing-rule-heading {
    justify-content: space-between;
    gap: 12px;
    margin-bottom: 14px;
  }

  .pricing-rule-switch {
    gap: 8px;
    color: var(--el-text-color-secondary);
    font-size: 13px;
  }

  .pricing-rule-field {
    display: grid;
    gap: 7px;
    margin-top: 12px;
    color: var(--el-text-color-regular);
    font-size: 13px;
  }

  .pricing-rule-field:first-of-type {
    margin-top: 0;
  }

  .pricing-page :deep(.el-input__inner),
  .pricing-page :deep(.el-textarea__inner) {
    font-size: 16px;
  }
}

@media (max-width: 380px) {
  .model-card-meta,
  .model-card-pricing {
    grid-template-columns: 1fr;
  }
}

:global(.pricing-model-dialog.is-fullscreen),
:global(.pricing-provider-dialog.is-fullscreen) {
  display: flex;
  flex-direction: column;
  height: 100dvh;
  margin: 0;
  overflow: hidden;
}

:global(.pricing-model-dialog.is-fullscreen .el-dialog__header),
:global(.pricing-provider-dialog.is-fullscreen .el-dialog__header) {
  flex: none;
  padding: 16px;
  padding-top: max(16px, env(safe-area-inset-top));
  border-bottom: 1px solid var(--el-border-color-lighter);
}

:global(.pricing-model-dialog.is-fullscreen .el-dialog__body),
:global(.pricing-provider-dialog.is-fullscreen .el-dialog__body) {
  flex: 1;
  min-height: 0;
  overflow: auto;
  overscroll-behavior: contain;
  padding: 14px 16px;
}

:global(.pricing-model-dialog.is-fullscreen .el-dialog__footer),
:global(.pricing-provider-dialog.is-fullscreen .el-dialog__footer) {
  flex: none;
  padding: 12px 16px;
  padding-bottom: max(12px, env(safe-area-inset-bottom));
  border-top: 1px solid var(--el-border-color-lighter);
  background: var(--el-bg-color);
}

:global(.pricing-model-dialog.is-fullscreen .drawer-footer),
:global(.pricing-provider-dialog.is-fullscreen .drawer-footer) {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 8px;
}

:global(.pricing-model-dialog.is-fullscreen .drawer-footer .el-button),
:global(.pricing-provider-dialog.is-fullscreen .drawer-footer .el-button) {
  width: 100%;
  min-height: 44px;
  margin-left: 0;
}

:global(.pricing-model-dialog.is-fullscreen .el-input__inner),
:global(.pricing-model-dialog.is-fullscreen .el-textarea__inner),
:global(.pricing-provider-dialog.is-fullscreen .el-input__inner) {
  font-size: 16px;
}
</style>
