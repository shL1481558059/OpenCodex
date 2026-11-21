<template>
  <div class="tier-editor">
    <section v-for="group in groups" :key="group.key" class="tier-section">
      <div class="tier-title">{{ group.label }}</div>
      <div v-for="(tier, index) in rule[group.key]" :key="index" class="tier-row">
        <span>≤</span>
        <el-input-number v-model="tier.up_to" :min="0" :precision="0" :step="1000" :controls="false"
          placeholder="不限留空" aria-label="窗口档上限" />
        <el-input-number v-model="tier.unit_price" :min="0" :precision="8" :step="0.01" :controls="false" aria-label="窗口档单价" />
        <el-tooltip content="删除档位">
          <el-button text type="danger" :icon="Delete" aria-label="删除档位" @click="rule[group.key].splice(index, 1)" />
        </el-tooltip>
      </div>
      <el-button text type="primary" :icon="Plus" @click="rule[group.key].push({ up_to: null, unit_price: 0 })">添加档位</el-button>
    </section>
  </div>
</template>

<script setup>
import { computed } from "vue";
import { Delete, Plus } from "@element-plus/icons-vue";
const props = defineProps({ rule: { type: Object, required: true } });
const groups = computed(() => [
  { key: "tiers", label: "窗口档（峰）" },
  ...(props.rule.off_peak_enabled ? [{ key: "off_peak_tiers", label: "窗口档（谷）" }] : [])
]);
</script>

<style scoped>
.tier-editor { min-width: 0; }
.tier-section + .tier-section { margin-top: 10px; }
.tier-title { margin-bottom: 6px; font-size: 12px; color: var(--el-text-color-secondary); }
.tier-row { display: grid; grid-template-columns: 16px minmax(0, 1fr) minmax(0, 1fr) 32px; gap: 6px; align-items: center; margin-bottom: 6px; }
.tier-row :deep(.el-input-number) { width: 100%; min-width: 0; }
.tier-row :deep(.el-button) { width: 32px; min-width: 0; padding: 0; margin: 0; }
</style>
