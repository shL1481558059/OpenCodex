<template>
  <div class="dashboard">
    <div class="dashboard-toolbar">
      <div>
        <h2>仪表盘</h2>
        <div class="text-muted">实时监控代理服务的消费、Token、延迟与请求趋势</div>
      </div>
      <div class="dashboard-controls">
        <el-radio-group v-model="window" size="small" @change="handleWindowChange">
          <el-radio-button v-for="w in windowOptions" :key="w" :label="w" :value="w" />
        </el-radio-group>
        <el-select v-model="refreshInterval" size="small" style="width: 110px" @change="handleRefreshChange">
          <el-option v-for="opt in refreshOptions" :key="opt.value" :label="opt.label" :value="opt.value" />
        </el-select>
        <el-button size="small" :icon="Refresh" :loading="loading" @click="fetchStats">刷新</el-button>
      </div>
    </div>

    <div v-loading="loading" class="dashboard-grid">
      <!-- 消费趋势 -->
      <div class="dashboard-card">
        <div class="dashboard-card__header">
          <span>消费趋势</span>
          <el-segmented v-model="costCurrency" :options="costCurrencyOptions" size="small" />
        </div>
        <div ref="costChartRef" class="dashboard-card__chart" />
      </div>

      <!-- Token 使用趋势 -->
      <div class="dashboard-card">
        <div class="dashboard-card__header">
          <span>Token 使用趋势</span>
          <el-segmented v-model="tokenUnit" :options="tokenUnitOptions" size="small" />
        </div>
        <div ref="tokenChartRef" class="dashboard-card__chart" />
      </div>

      <!-- 首字延迟趋势 -->
      <div class="dashboard-card">
        <div class="dashboard-card__header">
          <span>首字延迟趋势</span>
          <span class="dashboard-card__unit">ms</span>
        </div>
        <div ref="ttftChartRef" class="dashboard-card__chart" />
      </div>

      <!-- 缓存命中率趋势 -->
      <div class="dashboard-card">
        <div class="dashboard-card__header">
          <span>缓存命中率趋势</span>
          <span class="dashboard-card__unit">%</span>
        </div>
        <div ref="cacheChartRef" class="dashboard-card__chart" />
      </div>

      <!-- 每分钟请求趋势 -->
      <div class="dashboard-card">
        <div class="dashboard-card__header">
          <span>每分钟请求趋势</span>
          <span class="dashboard-card__unit">req/min</span>
        </div>
        <div ref="rpmChartRef" class="dashboard-card__chart" />
      </div>

      <!-- 请求模型分布 -->
      <div class="dashboard-card">
        <div class="dashboard-card__header">
          <span>请求模型分布</span>
        </div>
        <div ref="modelChartRef" class="dashboard-card__chart" />
      </div>
    </div>
  </div>
</template>

<script setup>
import { ref, reactive, computed, watch, onMounted, onBeforeUnmount, nextTick, shallowRef } from "vue";
import * as echarts from "echarts";
import { Refresh } from "@element-plus/icons-vue";
import { ElMessage } from "element-plus";

const props = defineProps({
  api: { type: Function, required: true },
  active: { type: Boolean, required: true }
});

const windowOptions = ["1h", "3h", "6h", "12h", "24h", "48h", "72h"];
const refreshOptions = [
  { label: "30s", value: 30 },
  { label: "1min", value: 60 },
  { label: "3min", value: 180 },
  { label: "5min", value: 300 },
  { label: "10min", value: 600 }
];

const costCurrencyOptions = [
  { label: "¥", value: "CNY" },
  { label: "$", value: "USD" }
];
const tokenUnitOptions = [
  { label: "K", value: "K" },
  { label: "M", value: "M" }
];

const window = ref("1h");
const refreshInterval = ref(30);
const loading = ref(false);
const costCurrency = ref("CNY");
const tokenUnit = ref("K");

const statsData = reactive({
  window: "1h",
  currency_rate: 7.25,
  points: [],
  model_distribution: []
});

// Chart refs
const costChartRef = ref(null);
const tokenChartRef = ref(null);
const ttftChartRef = ref(null);
const cacheChartRef = ref(null);
const rpmChartRef = ref(null);
const modelChartRef = ref(null);

// ECharts instances
const costChart = shallowRef(null);
const tokenChart = shallowRef(null);
const ttftChart = shallowRef(null);
const cacheChart = shallowRef(null);
const rpmChart = shallowRef(null);
const modelChart = shallowRef(null);

let refreshTimer = null;

// --- Data fetching ---
async function fetchStats() {
  loading.value = true;
  try {
    const data = await props.api(`/admin/api/stats?window=${window.value}`);
    statsData.window = data.window || window.value;
    statsData.currency_rate = data.currency_rate || 7.25;
    statsData.points = data.points || [];
    statsData.model_distribution = data.model_distribution || [];
    renderAllCharts();
  } catch (err) {
    ElMessage.error(err.message || "获取统计数据失败");
  } finally {
    loading.value = false;
  }
}

// --- Auto-refresh ---
function startRefreshTimer() {
  stopRefreshTimer();
  if (refreshInterval.value > 0) {
    refreshTimer = window.setInterval(() => {
      if (props.active) fetchStats();
    }, refreshInterval.value * 1000);
  }
}

function stopRefreshTimer() {
  if (refreshTimer !== null) {
    clearInterval(refreshTimer);
    refreshTimer = null;
  }
}

function handleWindowChange() {
  fetchStats();
  startRefreshTimer();
}

function handleRefreshChange() {
  startRefreshTimer();
}

// --- Chart init / resize ---
function initChart(domRef) {
  if (!domRef) return null;
  const instance = echarts.init(domRef);
  const ro = new ResizeObserver(() => instance.resize());
  ro.observe(domRef);
  instance._ro = ro;
  return instance;
}

function disposeChart(instance) {
  if (!instance) return;
  if (instance._ro) { instance._ro.disconnect(); instance._ro = null; }
  instance.dispose();
}

function initAllCharts() {
  costChart.value = initChart(costChartRef.value);
  tokenChart.value = initChart(tokenChartRef.value);
  ttftChart.value = initChart(ttftChartRef.value);
  cacheChart.value = initChart(cacheChartRef.value);
  rpmChart.value = initChart(rpmChartRef.value);
  modelChart.value = initChart(modelChartRef.value);
}

function disposeAllCharts() {
  disposeChart(costChart.value);
  disposeChart(tokenChart.value);
  disposeChart(ttftChart.value);
  disposeChart(cacheChart.value);
  disposeChart(rpmChart.value);
  disposeChart(modelChart.value);
  costChart.value = null;
  tokenChart.value = null;
  ttftChart.value = null;
  cacheChart.value = null;
  rpmChart.value = null;
  modelChart.value = null;
}

// --- Chart rendering ---
const timeLabels = computed(() => statsData.points.map(p => {
  const iso = p.time || "";
  // Show HH:mm for short windows, MM-DD HH:mm for long
  const short = iso.length >= 16 ? iso.slice(11, 16) : iso;
  if (["48h", "72h"].includes(statsData.window)) {
    return iso.length >= 10 ? iso.slice(5, 16) : iso;
  }
  return short;
}));

function baseLineSeries(data, opts = {}) {
  return {
    type: "line",
    symbol: "none",
    smooth: false,
    connectNulls: opts.connectNulls !== false,
    data,
    lineStyle: { width: 2 },
    itemStyle: opts.itemStyle || {},
    emphasis: { focus: "series" }
  };
}

function baseLineOpts() {
  return {
    animation: false,
    grid: { top: 32, right: 16, bottom: 28, left: 56 },
    tooltip: {
      trigger: "axis",
      axisPointer: { type: "line" }
    },
    xAxis: {
      type: "category",
      data: timeLabels.value,
      axisLabel: { fontSize: 11, rotate: 0 },
      axisTick: { show: false },
      boundaryGap: false
    },
    yAxis: {
      type: "value",
      axisLabel: { fontSize: 11 },
      splitLine: { lineStyle: { type: "dashed", opacity: 0.4 } }
    }
  };
}

function renderCostChart() {
  if (!costChart.value) return;
  const rate = statsData.currency_rate || 7.25;
  const isCNY = costCurrency.value === "CNY";
  const data = statsData.points.map(p => {
    const raw = p.cost || 0;
    return isCNY ? raw * rate : raw;
  });
  const precision = data.some(v => v > 1) ? 2 : 6;
  costChart.value.setOption({
    ...baseLineOpts(),
    yAxis: {
      ...baseLineOpts().yAxis,
      axisLabel: {
        fontSize: 11,
        formatter: v => isCNY ? `¥${v.toFixed(precision)}` : `$${v.toFixed(precision)}`
      }
    },
    tooltip: {
      ...baseLineOpts().tooltip,
      formatter: params => {
        const p = params[0];
        const v = p.value;
        return `${p.axisValue}<br/>${isCNY ? "¥" : "$"}${v.toFixed(precision)}`;
      }
    },
    series: [baseLineSeries(data, { itemStyle: { color: "#409EFF" } })]
  }, true);
}

function renderTokenChart() {
  if (!tokenChart.value) return;
  const divisor = tokenUnit.value === "M" ? 1_000_000 : 1000;
  const suffix = tokenUnit.value;
  const input = statsData.points.map(p => (p.input_tokens || 0) / divisor);
  const cached = statsData.points.map(p => (p.cached_tokens || 0) / divisor);
  const output = statsData.points.map(p => (p.output_tokens || 0) / divisor);
  tokenChart.value.setOption({
    ...baseLineOpts(),
    legend: { top: 0, right: 0, textStyle: { fontSize: 11 } },
    yAxis: {
      ...baseLineOpts().yAxis,
      axisLabel: { fontSize: 11, formatter: v => `${v}${suffix}` }
    },
    series: [
      { ...baseLineSeries(input, { itemStyle: { color: "#409EFF" } }), name: "输入" },
      { ...baseLineSeries(cached, { itemStyle: { color: "#67C23A" } }), name: "缓存" },
      { ...baseLineSeries(output, { itemStyle: { color: "#E6A23C" } }), name: "输出" }
    ]
  }, true);
}

function renderTtftChart() {
  if (!ttftChart.value) return;
  const data = statsData.points.map(p => p.avg_ttft_ms != null ? p.avg_ttft_ms : null);
  ttftChart.value.setOption({
    ...baseLineOpts(),
    yAxis: {
      ...baseLineOpts().yAxis,
      axisLabel: { fontSize: 11, formatter: v => `${v}` }
    },
    tooltip: {
      ...baseLineOpts().tooltip,
      formatter: params => {
        const p = params[0];
        return p.value != null ? `${p.axisValue}<br/>${p.value} ms` : `${p.axisValue}<br/>-`;
      }
    },
    series: [baseLineSeries(data, { connectNulls: false, itemStyle: { color: "#F56C6C" } })]
  }, true);
}

function renderCacheChart() {
  if (!cacheChart.value) return;
  const data = statsData.points.map(p => p.cache_hit_rate != null ? +(p.cache_hit_rate * 100).toFixed(2) : null);
  cacheChart.value.setOption({
    ...baseLineOpts(),
    yAxis: {
      ...baseLineOpts().yAxis,
      min: 0,
      max: 100,
      axisLabel: { fontSize: 11, formatter: v => `${v}%` }
    },
    tooltip: {
      ...baseLineOpts().tooltip,
      formatter: params => {
        const p = params[0];
        return p.value != null ? `${p.axisValue}<br/>${p.value}%` : `${p.axisValue}<br/>-`;
      }
    },
    series: [baseLineSeries(data, { connectNulls: false, itemStyle: { color: "#67C23A" } })]
  }, true);
}

function renderRpmChart() {
  if (!rpmChart.value) return;
  const data = statsData.points.map(p => p.rpm || 0);
  rpmChart.value.setOption({
    ...baseLineOpts(),
    yAxis: {
      ...baseLineOpts().yAxis,
      axisLabel: { fontSize: 11, formatter: v => `${v}` }
    },
    series: [baseLineSeries(data, { itemStyle: { color: "#909399" } })]
  }, true);
}

function renderModelChart() {
  if (!modelChart.value) return;
  const dist = statsData.model_distribution || [];
  const data = dist.map(d => ({ name: d.model, value: d.count }));
  modelChart.value.setOption({
    animation: false,
    tooltip: {
      trigger: "item",
      formatter: p => `${p.name}<br/>${p.value} 次 (${p.percent}%)`
    },
    legend: {
      type: "scroll",
      orient: "vertical",
      right: 8,
      top: 16,
      bottom: 16,
      textStyle: { fontSize: 11 }
    },
    series: [{
      type: "pie",
      radius: ["40%", "70%"],
      center: ["35%", "50%"],
      avoidLabelOverlap: true,
      itemStyle: { borderRadius: 6, borderColor: "#fff", borderWidth: 2 },
      label: { show: false },
      emphasis: { label: { show: true, fontSize: 13, fontWeight: "bold" } },
      data
    }]
  }, true);
}

function renderAllCharts() {
  renderCostChart();
  renderTokenChart();
  renderTtftChart();
  renderCacheChart();
  renderRpmChart();
  renderModelChart();
}

// --- Watch currency / unit switches ---
watch(costCurrency, () => renderCostChart());
watch(tokenUnit, () => renderTokenChart());

// --- Visibility handling ---
watch(() => props.active, (now) => {
  if (now) {
    fetchStats();
    startRefreshTimer();
  } else {
    stopRefreshTimer();
  }
});

onMounted(async () => {
  await nextTick();
  initAllCharts();
  if (props.active) {
    fetchStats();
    startRefreshTimer();
  }
});

onBeforeUnmount(() => {
  stopRefreshTimer();
  disposeAllCharts();
});
</script>

<style scoped>
.dashboard {
  min-height: 0;
}

.dashboard-toolbar {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
  margin-bottom: 20px;
}

.dashboard-toolbar h2 {
  margin: 0 0 4px;
  line-height: 1.25;
}

.dashboard-controls {
  display: flex;
  align-items: center;
  gap: 12px;
  padding-top: 16px;
  flex-wrap: wrap;
}

.dashboard-grid {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 16px;
}

.dashboard-card {
  padding: 16px;
  border: 1px solid var(--el-border-color-lighter);
  border-radius: 8px;
  background: var(--el-bg-color);
}

.dashboard-card__header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 8px;
  font-weight: 600;
  font-size: 14px;
}

.dashboard-card__unit {
  color: var(--el-text-color-secondary);
  font-size: 12px;
  font-weight: 400;
}

.dashboard-card__chart {
  width: 100%;
  height: 240px;
}

@media (max-width: 900px) {
  .dashboard-toolbar {
    display: block;
  }

  .dashboard-controls {
    justify-content: flex-start;
    padding-top: 16px;
  }

  .dashboard-grid {
    grid-template-columns: 1fr;
  }
}
</style>

