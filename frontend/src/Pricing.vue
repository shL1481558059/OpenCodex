<template>
  <div>
    <div class="toolbar">
      <div>
        <h2>定价管理</h2>
        <div class="text-muted">USD / 1M tokens</div>
      </div>
      <div class="toolbar-actions">
        <el-input
          v-model="filters.query"
          clearable
          placeholder="搜索模型、供应商"
          style="width: 240px"
          @keyup.enter="loadPrices"
          @clear="loadPrices"
        />
        <el-select v-model="filters.enabled" clearable placeholder="状态" style="width: 120px" @change="loadPrices">
          <el-option label="启用" :value="true" />
          <el-option label="停用" :value="false" />
        </el-select>
        <el-button :icon="Search" @click="loadPrices">搜索</el-button>
        <el-button :icon="Refresh" @click="loadPrices">刷新</el-button>
        <el-button :icon="Download" :loading="seedLoading" @click="seedDefaults">补齐默认</el-button>
        <el-button type="primary" :icon="Plus" @click="openPriceDialog()">新增定价</el-button>
      </div>
    </div>

    <div class="table-area">
      <el-table
        v-loading="pricesLoading"
        :data="pagedPrices"
        row-key="id"
        style="width: 100%"
        empty-text="暂无定价"
      >
        <el-table-column prop="vendor" label="供应商" width="120" show-overflow-tooltip />
        <el-table-column prop="model_id" label="模型 ID" min-width="210" show-overflow-tooltip />
        <el-table-column prop="name" label="名称" min-width="180" show-overflow-tooltip />
        <el-table-column prop="match_pattern" label="匹配键" min-width="190" show-overflow-tooltip />
        <el-table-column label="输入" width="110" align="right">
          <template #default="{ row }">{{ formatPrice(row.input_price) }}</template>
        </el-table-column>
        <el-table-column label="缓存输入" width="110" align="right">
          <template #default="{ row }">{{ row.cached_input_price == null ? "-" : formatPrice(row.cached_input_price) }}</template>
        </el-table-column>
        <el-table-column label="输出" width="110" align="right">
          <template #default="{ row }">{{ formatPrice(row.output_price) }}</template>
        </el-table-column>
        <el-table-column label="状态" width="90">
          <template #default="{ row }">
            <el-tag :type="row.enabled === false ? 'warning' : 'success'">
              {{ row.enabled === false ? "停用" : "启用" }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column prop="source" label="来源" width="150" show-overflow-tooltip />
        <el-table-column label="更新时间" width="180">
          <template #default="{ row }">{{ formatTime(row.updated_at) || "-" }}</template>
        </el-table-column>
        <el-table-column label="操作" width="210" align="center">
          <template #default="{ row }">
            <div class="inline-actions channel-table-actions">
              <el-button size="small" :icon="Edit" @click="openPriceDialog(row)">编辑</el-button>
              <el-popconfirm :title="`删除定价 ${row.model_id}？`" @confirm="deletePrice(row)">
                <template #reference>
                  <el-button size="small" type="danger" :icon="Delete">删除</el-button>
                </template>
              </el-popconfirm>
            </div>
          </template>
        </el-table-column>
      </el-table>

      <div class="pagination-wrap">
        <el-pagination
          v-model:current-page="page"
          v-model:page-size="pageSize"
          layout="total, sizes, prev, pager, next"
          :page-sizes="[25, 50, 100]"
          :total="prices.length"
        />
      </div>
    </div>

    <el-dialog v-model="priceDialogVisible" :title="priceDraft.id ? '编辑定价' : '新增定价'" width="640px">
      <el-form label-position="top" :model="priceDraft">
        <el-row :gutter="16">
          <el-col :span="12">
            <el-form-item label="模型 ID">
              <el-input v-model="priceDraft.model_id" autocomplete="off" />
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item label="供应商">
              <el-input v-model="priceDraft.vendor" autocomplete="off" />
            </el-form-item>
          </el-col>
        </el-row>
        <el-form-item label="名称">
          <el-input v-model="priceDraft.name" autocomplete="off" />
        </el-form-item>
        <el-form-item label="匹配键">
          <el-input v-model="priceDraft.match_pattern" autocomplete="off" />
        </el-form-item>
        <el-row :gutter="16">
          <el-col :span="8">
            <el-form-item label="输入价格">
              <el-input-number v-model="priceDraft.input_price" :min="0" :precision="6" :step="0.01" style="width: 100%" />
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="缓存输入价格">
              <div class="input-with-action">
                <el-input-number v-model="priceDraft.cached_input_price" :min="0" :precision="6" :step="0.01" style="width: 100%" />
                <el-button @click="priceDraft.cached_input_price = null">置空</el-button>
              </div>
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="输出价格">
              <el-input-number v-model="priceDraft.output_price" :min="0" :precision="6" :step="0.01" style="width: 100%" />
            </el-form-item>
          </el-col>
        </el-row>
        <el-form-item label="启用">
          <el-switch v-model="priceDraft.enabled" />
        </el-form-item>
      </el-form>

      <template #footer>
        <div class="drawer-footer">
          <el-button @click="priceDialogVisible = false">取消</el-button>
          <el-button type="primary" :loading="priceSaving" @click="savePrice">保存</el-button>
        </div>
      </template>
    </el-dialog>
  </div>
</template>

<script setup>
import { computed, onMounted, reactive, ref } from "vue";
import { ElMessage } from "element-plus/es/components/message/index.mjs";
import { Delete, Download, Edit, Plus, Refresh, Search } from "@element-plus/icons-vue";

const props = defineProps({
  api: { type: Function, required: true }
});

const prices = ref([]);
const pricesLoading = ref(false);
const seedLoading = ref(false);
const priceDialogVisible = ref(false);
const priceSaving = ref(false);
const page = ref(1);
const pageSize = ref(25);
const filters = reactive({
  query: "",
  enabled: null
});
const priceDraft = reactive(emptyPriceDraft());

const pagedPrices = computed(() => {
  const start = (page.value - 1) * pageSize.value;
  return prices.value.slice(start, start + pageSize.value);
});

async function loadPrices() {
  pricesLoading.value = true;
  try {
    const params = new URLSearchParams();
    if (filters.query.trim()) params.set("query", filters.query.trim());
    if (filters.enabled !== null && filters.enabled !== undefined) params.set("enabled", String(filters.enabled));
    const suffix = params.toString() ? `?${params.toString()}` : "";
    const data = await props.api(`/pricing${suffix}`);
    prices.value = Array.isArray(data.prices) ? data.prices : [];
    page.value = 1;
  } catch (error) {
    ElMessage.error(error.message);
  } finally {
    pricesLoading.value = false;
  }
}

function openPriceDialog(row = null) {
  Object.assign(priceDraft, emptyPriceDraft(), row ? {
    id: row.id,
    model_id: row.model_id,
    vendor: row.vendor,
    name: row.name,
    match_pattern: row.match_pattern,
    input_price: Number(row.input_price || 0),
    cached_input_price: row.cached_input_price == null ? null : Number(row.cached_input_price),
    output_price: Number(row.output_price || 0),
    enabled: row.enabled !== false
  } : {});
  priceDialogVisible.value = true;
}

async function savePrice() {
  priceSaving.value = true;
  try {
    const body = JSON.stringify({
      model_id: priceDraft.model_id,
      vendor: priceDraft.vendor,
      name: priceDraft.name,
      match_pattern: priceDraft.match_pattern,
      input_price: Number(priceDraft.input_price || 0),
      cached_input_price: priceDraft.cached_input_price == null ? null : Number(priceDraft.cached_input_price),
      output_price: Number(priceDraft.output_price || 0),
      enabled: priceDraft.enabled
    });
    if (priceDraft.id) {
      await props.api(`/pricing/${priceDraft.id}`, { method: "PATCH", body });
    } else {
      await props.api("/pricing", { method: "POST", body });
    }

    priceDialogVisible.value = false;
    await loadPrices();
    ElMessage.success("定价已保存");
  } catch (error) {
    ElMessage.error(error.message);
  } finally {
    priceSaving.value = false;
  }
}

async function seedDefaults() {
  seedLoading.value = true;
  try {
    const data = await props.api("/pricing/seed-defaults", { method: "POST", body: "{}" });
    await loadPrices();
    ElMessage.success(`已新增 ${data.inserted || 0} 条，跳过 ${data.skipped || 0} 条`);
  } catch (error) {
    ElMessage.error(error.message);
  } finally {
    seedLoading.value = false;
  }
}

async function deletePrice(row) {
  try {
    await props.api(`/pricing/${row.id}`, { method: "DELETE" });
    await loadPrices();
    ElMessage.success("定价已删除");
  } catch (error) {
    ElMessage.error(error.message);
  }
}

function emptyPriceDraft() {
  return {
    id: null,
    model_id: "",
    vendor: "",
    name: "",
    match_pattern: "",
    input_price: 0,
    cached_input_price: null,
    output_price: 0,
    enabled: true
  };
}

function formatPrice(value) {
  return Number(value || 0).toLocaleString(undefined, {
    minimumFractionDigits: 0,
    maximumFractionDigits: 6
  });
}

function formatTime(timestamp) {
  if (!timestamp) return "";
  return new Date(Number(timestamp) * 1000).toLocaleString();
}

onMounted(() => loadPrices());
</script>
