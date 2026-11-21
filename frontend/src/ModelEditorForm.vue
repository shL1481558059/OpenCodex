<template>
  <el-form label-position="top" :model="draft" class="model-editor-form">
    <el-row :gutter="16">
      <el-col :xs="24" :span="16">
        <el-form-item label="供应商">
          <el-select v-model="draft.provider_code" class="full-width">
            <el-option v-for="provider in providers" :key="provider.code" :label="provider.name" :value="provider.code" />
          </el-select>
        </el-form-item>
      </el-col>
      <el-col :xs="24" :span="8">
        <el-form-item label="状态"><el-switch v-model="draft.enabled" /></el-form-item>
      </el-col>
    </el-row>

    <el-row v-if="channel" :gutter="16">
      <el-col :xs="24" :span="12">
        <el-form-item label="请求模型"><el-input :model-value="draft.request_model" disabled /></el-form-item>
      </el-col>
      <el-col :xs="24" :span="12">
        <el-form-item label="上游模型"><el-input :model-value="draft.upstream_model" disabled /></el-form-item>
      </el-col>
    </el-row>

    <el-row :gutter="16">
      <el-col :xs="24" :span="12">
        <el-form-item label="模型标识"><el-input v-model="draft.model_key" autocomplete="off" /></el-form-item>
      </el-col>
      <el-col :xs="24" :span="12">
        <el-form-item label="显示名称"><el-input v-model="draft.display_name" autocomplete="off" /></el-form-item>
      </el-col>
    </el-row>
    <el-row :gutter="16">
      <el-col :xs="24" :span="8">
        <el-form-item label="匹配类型">
          <el-select v-model="draft.match_type" class="full-width" @change="changeModelMatchType(draft)">
            <el-option label="精确" value="exact" />
            <el-option label="前缀" value="prefix" />
            <el-option label="后缀" value="suffix" />
            <el-option label="包含" value="contains" />
          </el-select>
        </el-form-item>
      </el-col>
      <el-col :xs="24" :span="16">
        <el-form-item label="匹配键">
          <el-select v-if="draft.match_type === 'exact'" v-model="draft.match_patterns" multiple filterable allow-create
            default-first-option clearable placeholder="输入后回车添加" class="full-width">
            <el-option v-for="pattern in draft.match_patterns" :key="pattern" :label="pattern" :value="pattern" />
          </el-select>
          <el-input v-else v-model="draft.match_pattern" autocomplete="off" />
        </el-form-item>
      </el-col>
    </el-row>
    <el-form-item label="描述"><el-input v-model="draft.description" type="textarea" :rows="2" /></el-form-item>
    <el-row :gutter="16">
      <el-col :xs="24" :span="8">
        <el-form-item label="支持图片"><el-switch v-model="draft.capabilities.supports_image" /></el-form-item>
      </el-col>
      <el-col :xs="24" :span="8">
        <el-form-item label="上下文窗口">
          <el-input-number v-model="draft.capabilities.context_window" :min="0" :step="8192" :precision="0" class="full-width" />
        </el-form-item>
      </el-col>
      <el-col :xs="24" :span="8">
        <el-form-item label="币种"><el-input v-model="draft.pricing.currency" autocomplete="off" /></el-form-item>
      </el-col>
    </el-row>
    <el-form-item label="v2 Agent 模拟">
      <el-radio-group v-model="draft.agent_simulation">
        <el-radio-button v-if="channel" label="继承全局" value="inherit" />
        <el-radio-button label="开启" value="enabled" />
        <el-radio-button label="关闭" value="disabled" />
      </el-radio-group>
    </el-form-item>

    <el-divider content-position="left">计费规则</el-divider>
    <div class="off-peak-panel">
      <div class="off-peak-head">
        <el-select v-model="draft.pricing.time_zone" filterable allow-create default-first-option clearable
          placeholder="峰谷时区，留空表示不启用" class="off-peak-zone">
          <el-option v-for="zone in OFF_PEAK_TIME_ZONES" :key="zone" :label="zone" :value="zone" />
        </el-select>
        <span class="phase-label">{{ currentPhaseLabel(draft.pricing, new Date(now)) }}</span>
      </div>
      <div v-for="(window, index) in draft.pricing.off_peak_windows" :key="index" class="off-peak-window">
        <el-select v-model="window.start" filterable allow-create default-first-option class="off-peak-time" aria-label="谷段开始">
          <el-option v-for="time in OFF_PEAK_TIME_OPTIONS" :key="time" :label="time" :value="time" />
        </el-select>
        <span>至</span>
        <el-select v-model="window.end" filterable allow-create default-first-option class="off-peak-time" aria-label="谷段结束">
          <el-option v-for="time in OFF_PEAK_TIME_OPTIONS" :key="time" :label="time" :value="time" />
        </el-select>
        <el-select v-model="window.days" multiple collapse-tags collapse-tags-tooltip placeholder="每天" class="off-peak-days">
          <el-option v-for="day in OFF_PEAK_WEEKDAYS" :key="day.value" :label="day.label" :value="day.value" />
        </el-select>
        <el-tooltip content="删除谷段窗口">
          <el-button link type="danger" :icon="Delete" aria-label="删除谷段窗口" @click="draft.pricing.off_peak_windows.splice(index, 1)" />
        </el-tooltip>
        <span v-if="crossesMidnight(window)" class="midnight-note">保存后按起始日拆成两段</span>
      </div>
      <div class="off-peak-actions">
        <el-button link type="primary" :icon="Plus" @click="draft.pricing.off_peak_windows.push(emptyOffPeakWindow())">添加谷段窗口</el-button>
        <div class="off-peak-discount">
          <span>谷段折扣</span>
          <el-input-number v-model="discount" :min="0" :max="1" :step="0.05" :precision="2" size="small" aria-label="谷段折扣" />
          <el-button size="small" @click="fillDiscount">按折扣填充谷价</el-button>
        </div>
      </div>
    </div>

    <div v-if="!isMobile" class="rule-table-wrap">
      <el-table :data="draft.pricing.rules" border size="small" scrollbar-always-on>
        <el-table-column label="计费项" width="100">
          <template #default="{ row }">{{ billingLabel(row.billing_item) }}</template>
        </el-table-column>
        <el-table-column label="模式" width="170">
          <template #default="{ row }">
            <el-select v-model="row.billing_mode" class="full-width">
              <el-option v-for="mode in billingModes" :key="mode.value" :label="mode.label" :value="mode.value" />
            </el-select>
          </template>
        </el-table-column>
        <el-table-column label="单价" width="160">
          <template #default="{ row }"><el-input-number v-model="row.unit_price" :min="0" :precision="8" :step="0.01" class="full-width" /></template>
        </el-table-column>
        <el-table-column label="峰谷" width="70" align="center">
          <template #default="{ row }"><el-switch v-model="row.off_peak_enabled" :disabled="!offPeakEnabled" /></template>
        </el-table-column>
        <el-table-column label="谷段单价" width="160">
          <template #default="{ row }">
            <el-input-number v-model="row.off_peak_unit_price" :min="0" :precision="8" :step="0.01"
              :disabled="!offPeakEnabled || !row.off_peak_enabled" class="full-width" />
          </template>
        </el-table-column>
        <el-table-column label="窗口档" width="430">
          <template #default="{ row }">
            <ModelTierEditor v-if="row.billing_mode === 'tiered_tokens'" :rule="row" />
          </template>
        </el-table-column>
        <el-table-column label="启用" width="80" align="center">
          <template #default="{ row }"><el-switch v-model="row.enabled" /></template>
        </el-table-column>
      </el-table>
    </div>
    <div v-else class="mobile-rules">
      <section v-for="rule in draft.pricing.rules" :key="rule.billing_item" class="rule-item">
        <div class="rule-heading">
          <strong>{{ billingLabel(rule.billing_item) }}</strong>
          <el-switch v-model="rule.enabled" :aria-label="`${billingLabel(rule.billing_item)}计费启用`" />
        </div>
        <el-form-item label="计费模式">
          <el-select v-model="rule.billing_mode" class="full-width">
            <el-option v-for="mode in billingModes" :key="mode.value" :label="mode.label" :value="mode.value" />
          </el-select>
        </el-form-item>
        <el-form-item label="单价"><el-input-number v-model="rule.unit_price" :min="0" :precision="8" :step="0.01" class="full-width" /></el-form-item>
        <el-form-item label="峰谷计费"><el-switch v-model="rule.off_peak_enabled" :disabled="!offPeakEnabled" /></el-form-item>
        <el-form-item v-if="rule.off_peak_enabled" label="谷段单价">
          <el-input-number v-model="rule.off_peak_unit_price" :min="0" :precision="8" :step="0.01" :disabled="!offPeakEnabled" class="full-width" />
        </el-form-item>
        <ModelTierEditor v-if="rule.billing_mode === 'tiered_tokens'" :rule="rule" />
      </section>
    </div>
    <el-collapse class="advanced-collapse">
      <el-collapse-item title="Catalog JSON" name="catalog">
        <el-input v-model="catalogText" type="textarea" :rows="8" />
      </el-collapse-item>
    </el-collapse>
  </el-form>
</template>

<script setup>
import { computed, onBeforeUnmount, ref } from "vue";
import { Delete, Plus } from "@element-plus/icons-vue";
import { ElMessage } from "element-plus/es/components/message/index.mjs";
import { ElRadioButton, ElRadioGroup } from "element-plus/es/components/radio/index.mjs";
import ModelTierEditor from "./ModelTierEditor.vue";
import { changeModelMatchType, MODEL_BILLING_ITEMS } from "./modelEditorState.js";
import {
  OFF_PEAK_TIME_OPTIONS, OFF_PEAK_TIME_ZONES, OFF_PEAK_WEEKDAYS,
  applyOffPeakDiscount, crossesMidnight, currentPhaseLabel, emptyOffPeakWindow, offPeakConfigured
} from "./pricingOffPeak.js";

const props = defineProps({
  draft: { type: Object, required: true },
  providers: { type: Array, default: () => [] },
  channel: Boolean,
  isMobile: Boolean
});
const catalogText = defineModel("catalogText", { type: String, default: "{}" });
const billingModes = [
  { value: "per_request", label: "按次" },
  { value: "per_million_tokens", label: "每百万 token" },
  { value: "tiered_tokens", label: "上下文窗口档" }
];
const discount = ref(0.5);
const now = ref(Date.now());
const clock = setInterval(() => { now.value = Date.now(); }, 30000);
onBeforeUnmount(() => clearInterval(clock));
const offPeakEnabled = computed(() => offPeakConfigured(props.draft.pricing));
const billingLabel = (value) => MODEL_BILLING_ITEMS.find((item) => item.value === value)?.label || value;
function fillDiscount() {
  const changed = applyOffPeakDiscount(props.draft.pricing.rules, discount.value);
  if (!changed) ElMessage.warning("先为需要打折的计费项打开峰谷开关");
  else ElMessage.success(`已按峰价的 ${discount.value} 倍填充 ${changed} 项谷价`);
}
</script>

<style scoped>
.model-editor-form { min-width: 0; }
.full-width { width: 100%; }
.off-peak-panel { margin: 8px 0 16px; }
.off-peak-head, .off-peak-window, .off-peak-actions, .off-peak-discount {
  display: flex; align-items: center; gap: 8px; flex-wrap: wrap;
}
.off-peak-head, .off-peak-window { margin-bottom: 8px; }
.off-peak-actions { justify-content: space-between; }
.off-peak-zone { width: 260px; max-width: 100%; }
.off-peak-time { width: 104px; }
.off-peak-days { width: 200px; max-width: 100%; }
.off-peak-discount :deep(.el-input-number) { width: 116px; }
.phase-label, .midnight-note { color: var(--el-text-color-secondary); font-size: 12px; }
.midnight-note { color: var(--el-color-warning); }
.rule-table-wrap { width: 100%; min-width: 0; overflow: hidden; }
.mobile-rules { display: grid; gap: 16px; }
.rule-item { min-width: 0; padding: 12px 0; border-top: 1px solid var(--el-border-color-lighter); }
.rule-heading { display: flex; align-items: center; justify-content: space-between; margin-bottom: 12px; }
.advanced-collapse { margin-top: 16px; }
:global(.model-editor-dialog) {
  display: flex;
  flex-direction: column;
  max-height: calc(100dvh - 48px);
  margin-top: 24px;
  margin-bottom: 24px;
}
:global(.model-editor-dialog .el-dialog__header) { flex-shrink: 0; }
:global(.model-editor-dialog .el-dialog__body) { min-height: 0; overflow: auto; }
:global(.model-editor-dialog .el-dialog__footer) { flex-shrink: 0; }
@media (max-width: 767px) {
  .off-peak-discount { width: 100%; }
  .off-peak-zone { width: 100%; }
  :global(.model-editor-dialog.is-fullscreen) {
    height: 100dvh;
    max-height: 100dvh;
    max-width: 100vw;
    margin: 0;
    overflow: hidden;
  }
  :global(.model-editor-dialog.is-fullscreen .el-dialog__header) {
    min-height: 56px;
    margin-right: 0;
    padding: 16px 52px 12px 16px;
    border-bottom: 1px solid var(--el-border-color-lighter);
  }
  :global(.model-editor-dialog.is-fullscreen .el-dialog__title) { font-size: 16px; line-height: 1.4; }
  :global(.model-editor-dialog.is-fullscreen .el-dialog__body) {
    flex: 1;
    min-width: 0;
    padding: 14px 16px;
    overflow: auto;
  }
  :global(.model-editor-dialog.is-fullscreen .el-dialog__footer) {
    padding: 10px 16px calc(10px + env(safe-area-inset-bottom));
    border-top: 1px solid var(--el-border-color-lighter);
  }
  :global(.model-editor-dialog.is-fullscreen .drawer-footer) { display: flex; gap: 10px; }
  :global(.model-editor-dialog.is-fullscreen .drawer-footer .el-button) {
    flex: 1;
    min-height: 40px;
    margin: 0;
  }
}
</style>
