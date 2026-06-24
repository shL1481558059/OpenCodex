<template>
  <div class="dashboard">
    <div class="dashboard-toolbar">
      <div>
        <h2>仪表盘</h2>
        <div class="text-muted">实时监控代理服务的消费、Token、延迟与请求趋势</div>
      </div>
      <div class="dashboard-controls">
        <el-segmented v-model="range" :options="rangeOptions" size="small" @change="handleRangeChange" />
        <el-date-picker
          v-if="range === 'custom'"
          v-model="customRange"
          type="datetimerange"
          size="small"
          start-placeholder="开始时间"
          end-placeholder="结束时间"
          range-separator="至"
          value-format="x"
          :clearable="false"
          @change="handleCustomRangeChange"
        />
        <el-button size="small" :icon="Refresh" :loading="loading" @click="fetchStats">刷新</el-button>
        <el-dropdown trigger="click" @command="setAutoRefreshSeconds">
          <el-button size="small" :type="autoRefreshSeconds ? 'primary' : 'default'" :icon="Refresh">
            {{ autoRefreshLabel }}
          </el-button>
          <template #dropdown>
            <el-dropdown-menu class="log-auto-refresh-menu">
              <div class="log-auto-refresh-menu__title">启用自动刷新</div>
              <el-dropdown-item :command="0">
                <span class="log-auto-refresh-menu__item">
                  <span>关闭</span>
                  <el-icon v-if="autoRefreshSeconds === 0"><Check /></el-icon>
                </span>
              </el-dropdown-item>
              <el-dropdown-item v-for="seconds in autoRefreshOptions" :key="seconds" :command="seconds">
                <span class="log-auto-refresh-menu__item">
                  <span>{{ seconds }} 秒</span>
                  <el-icon v-if="autoRefreshSeconds === seconds"><Check /></el-icon>
                </span>
              </el-dropdown-item>
            </el-dropdown-menu>
          </template>
        </el-dropdown>
      </div>
    </div>

    <div class="dashboard-summary-grid">
      <div
        v-for="card in summaryCards"
        :key="card.key"
        class="dashboard-summary-card"
        :class="`dashboard-summary-card--${card.tone}`"
      >
        <div class="dashboard-summary-card__icon">
          <el-icon><component :is="card.icon" /></el-icon>
        </div>
        <div class="dashboard-summary-card__title">{{ card.title }}</div>
        <div class="dashboard-summary-card__value">{{ card.value }}</div>
        <div class="dashboard-summary-card__meta">{{ card.meta }}</div>
      </div>
    </div>

    <div v-loading="initialChartLoading" class="dashboard-grid">
      <div class="dashboard-top-grid">
        <div class="dashboard-card dashboard-top-grid__model">
          <div class="dashboard-card__header">
            <span>请求模型分布</span>
          </div>
          <div ref="modelChartRef" class="dashboard-card__chart dashboard-card__chart--top" />
        </div>

        <div class="dashboard-card dashboard-top-grid__queue">
          <div class="dashboard-card__header">
            <span>请求队列</span>
            <span class="dashboard-queue__status" :class="{ 'dashboard-queue__status--live': queueConnected }">
              {{ queueStatusText }}
            </span>
          </div>

          <div class="dashboard-queue">
            <div v-if="queueLoading" class="dashboard-queue__empty">正在连接实时队列...</div>
            <div v-else-if="queueTopItems.length === 0" class="dashboard-queue__empty">当前没有正在处理的渠道</div>
            <template v-else>
              <div
                v-for="item in queueTopItems"
                :key="item.channel_id"
                class="dashboard-queue__item"
              >
                <span class="dashboard-queue__name">{{ item.channel_name }}</span>
                <span class="dashboard-queue__count">{{ item.processing_count }}</span>
              </div>

              <div v-if="queueOverflowItems.length > 0" class="dashboard-queue__more">
                <el-popover placement="bottom-end" :width="320" trigger="click">
                  <template #reference>
                    <el-button size="small" text class="dashboard-queue__more-button">
                      查看更多 +{{ queueOverflowItems.length }}
                    </el-button>
                  </template>

                  <div class="dashboard-queue-popover">
                    <div
                      v-for="item in queueOverflowItems"
                      :key="`overflow-${item.channel_id}`"
                      class="dashboard-queue__item dashboard-queue__item--popover"
                    >
                      <span class="dashboard-queue__name">{{ item.channel_name }}</span>
                      <span class="dashboard-queue__count">{{ item.processing_count }}</span>
                    </div>
                  </div>
                </el-popover>
              </div>
            </template>
          </div>
        </div>

        <div class="dashboard-card dashboard-top-grid__cost">
          <div class="dashboard-card__header">
            <span>消费趋势</span>
            <el-segmented v-model="costCurrency" :options="costCurrencyOptions" size="small" />
          </div>
          <div ref="costChartRef" class="dashboard-card__chart dashboard-card__chart--top" />
        </div>
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

    </div>
  </div>
</template>

<script setup>
import { ref, reactive, computed, watch, onMounted, onBeforeUnmount, nextTick, shallowRef } from "vue";
import { LineChart, PieChart } from "echarts/charts";
import { GridComponent, LegendComponent, TooltipComponent } from "echarts/components";
import { init, use } from "echarts/core";
import { CanvasRenderer } from "echarts/renderers";
import { Box, Check, Coin, DataLine, Lightning, Refresh, Timer } from "@element-plus/icons-vue";
import { ElMessage } from "element-plus/es/components/message/index.mjs";

use([LineChart, PieChart, GridComponent, LegendComponent, TooltipComponent, CanvasRenderer]);

const props = defineProps({
  api: { type: Function, required: true },
  active: { type: Boolean, required: true }
});

const rangeOptions = [
  { label: "1 小时", value: "1h" },
  { label: "6 小时", value: "6h" },
  { label: "24 小时", value: "24h" },
  { label: "7 天", value: "7d" },
  { label: "30 天", value: "30d" },
  { label: "自定义", value: "custom" }
];
const autoRefreshOptions = [5, 10, 30, 60];

const costCurrencyOptions = [
  { label: "¥", value: "CNY" },
  { label: "$", value: "USD" }
];
const tokenUnitOptions = [
  { label: "K", value: "K" },
  { label: "M", value: "M" }
];

const range = ref("1h");
const customRange = ref(defaultCustomRange());
const autoRefreshSeconds = ref(0);
const loading = ref(false);
const hasLoadedStats = ref(false);
const costCurrency = ref("CNY");
const tokenUnit = ref("K");
const queueItems = ref([]);
const queueConnected = ref(false);
const queueLoading = ref(true);

const statsData = reactive({
  range: "1h",
  start: "",
  end: "",
  granularity_minutes: 1,
  currency_rate: 7.25,
  summary: defaultSummary(),
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
let queueEventSource = null;
let queueStaleTimer = null;
const QUEUE_STALE_TIMEOUT_MS = 5000;

const autoRefreshLabel = computed(() =>
  autoRefreshSeconds.value ? `${autoRefreshSeconds.value} 秒刷新` : "自动刷新"
);
const initialChartLoading = computed(() => loading.value && !hasLoadedStats.value);
const queueTopItems = computed(() => queueItems.value.slice(0, 3));
const queueOverflowItems = computed(() => queueItems.value.slice(3));
const queueStatusText = computed(() => {
  if (queueConnected.value) return "实时更新中";
  if (queueLoading.value) return "连接中";
  return "未连接";
});
const summaryCards = computed(() => {
  const summary = statsData.summary || defaultSummary();
  return [
    {
      key: "requests",
      title: "总请求数",
      value: formatInteger(summary.request_count),
      meta: `成功: ${formatInteger(summary.success_count)}  近 1 小时: ${formatInteger(summary.recent_1h_request_count)}`,
      icon: DataLine,
      tone: "blue"
    },
    {
      key: "tokens",
      title: "总 TOKEN 数",
      value: formatInteger(summary.total_tokens),
      meta: `输入: ${formatInteger(summary.input_tokens)}  缓存: ${formatInteger(summary.cached_tokens)}  输出: ${formatInteger(summary.output_tokens)}`,
      icon: Box,
      tone: "cyan"
    },
    {
      key: "cost",
      title: "总计费",
      value: formatDualCurrency(summary.cost),
      meta: `近 1 小时: ${formatDualCurrency(summary.recent_1h_cost)}`,
      icon: Coin,
      tone: "green"
    },
    {
      key: "rpm",
      title: "RPM",
      value: formatCompactNumber(summary.rpm),
      meta: "每分钟请求数",
      icon: Timer,
      tone: "green"
    },
    {
      key: "tpm",
      title: "TPM",
      value: formatCompactNumber(summary.tpm),
      meta: "每分钟 Token 数",
      icon: Lightning,
      tone: "red"
    }
  ];
});

// --- Data fetching ---
async function fetchStats() {
  loading.value = true;
  try {
    const data = await props.api(`/stats?${buildStatsQuery()}`);
    statsData.range = data.range || range.value;
    statsData.start = data.start || "";
    statsData.end = data.end || "";
    statsData.granularity_minutes = data.granularity_minutes || 1;
    statsData.currency_rate = data.currency_rate || 7.25;
    statsData.summary = { ...defaultSummary(), ...(data.summary || {}) };
    statsData.points = data.points || [];
    statsData.model_distribution = data.model_distribution || [];
    hasLoadedStats.value = true;
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
  if (autoRefreshSeconds.value > 0) {
    refreshTimer = window.setInterval(() => {
      if (props.active) fetchStats();
    }, autoRefreshSeconds.value * 1000);
  }
}

function stopRefreshTimer() {
  if (refreshTimer !== null) {
    clearInterval(refreshTimer);
    refreshTimer = null;
  }
}

function handleRangeChange() {
  if (range.value === "custom" && !customRange.value) {
    customRange.value = defaultCustomRange();
  }
  fetchStats();
  startRefreshTimer();
}

function handleCustomRangeChange() {
  if (range.value === "custom") {
    fetchStats();
    startRefreshTimer();
  }
}

function setAutoRefreshSeconds(seconds) {
  const nextSeconds = Number(seconds || 0);
  autoRefreshSeconds.value = autoRefreshOptions.includes(nextSeconds) ? nextSeconds : 0;
  startRefreshTimer();
  if (autoRefreshSeconds.value > 0) {
    fetchStats();
  }
}

function buildStatsQuery() {
  const params = new URLSearchParams({ range: range.value });
  if (range.value === "custom" && Array.isArray(customRange.value) && customRange.value.length === 2) {
    params.set("start", String(Math.floor(Number(customRange.value[0]) / 1000)));
    params.set("end", String(Math.floor(Number(customRange.value[1]) / 1000)));
  }
  return params.toString();
}

function defaultCustomRange() {
  const end = Date.now();
  return [end - 60 * 60 * 1000, end];
}

function defaultSummary() {
  return {
    request_count: 0,
    success_count: 0,
    recent_1h_request_count: 0,
    input_tokens: 0,
    cached_tokens: 0,
    output_tokens: 0,
    total_tokens: 0,
    recent_1h_tokens: 0,
    cost: 0,
    recent_1h_cost: 0,
    rpm: 0,
    tpm: 0
  };
}

function applyQueuePayload(payload) {
  const channels = Array.isArray(payload?.channels) ? payload.channels : [];
  queueItems.value = channels
    .map((item) => ({
      channel_id: String(item?.channel_id || "").trim(),
      channel_name: String(item?.channel_name || "").trim(),
      processing_count: Number(item?.processing_count || 0)
    }))
    .filter((item) => item.channel_id.length > 0 && item.channel_name.length > 0 && item.processing_count > 0);
  queueLoading.value = false;
}

function resetQueueState(loadingState = false) {
  queueItems.value = [];
  queueConnected.value = false;
  queueLoading.value = loadingState;
}

function stopQueueStaleTimer() {
  if (queueStaleTimer !== null) {
    clearTimeout(queueStaleTimer);
    queueStaleTimer = null;
  }
}

function scheduleQueueStaleTimer() {
  stopQueueStaleTimer();
  queueStaleTimer = window.setTimeout(() => {
    resetQueueState(false);
  }, QUEUE_STALE_TIMEOUT_MS);
}

function buildQueueStreamUrl() {
  const base = import.meta.env.DEV ? import.meta.env.BASE_URL.replace(/\/$/, "") : "";
  return `${base}/stats/active-channels/stream`;
}

function stopQueueStream() {
  stopQueueStaleTimer();
  if (queueEventSource) {
    queueEventSource.close();
    queueEventSource = null;
  }
  resetQueueState(true);
}

function startQueueStream() {
  stopQueueStream();
  if (!props.active) {
    resetQueueState(true);
    return;
  }

  resetQueueState(true);
  const source = new EventSource(buildQueueStreamUrl(), { withCredentials: true });
  queueEventSource = source;

  source.addEventListener("queue", (event) => {
    try {
      applyQueuePayload(JSON.parse(event.data || "{}"));
      queueConnected.value = true;
      scheduleQueueStaleTimer();
    } catch {
      stopQueueStaleTimer();
      resetQueueState(false);
    }
  });

  source.onerror = () => {
    stopQueueStaleTimer();
    if (queueEventSource === source) {
      resetQueueState(false);
    }
  };
}

function formatInteger(value) {
  return Math.round(Number(value || 0)).toLocaleString();
}

function formatCompactNumber(value) {
  const number = Number(value || 0);
  if (Number.isInteger(number)) {
    return formatInteger(number);
  }
  return number.toLocaleString(undefined, { maximumFractionDigits: 2 });
}

function formatDualCurrency(value) {
  const cny = Number(value || 0);
  const usd = cny / (statsData.currency_rate || 7.25);
  return `¥${formatCurrencyNumber(cny)}/$${formatCurrencyNumber(usd)}`;
}

function formatCurrencyNumber(value) {
  return Number(value || 0).toLocaleString(undefined, {
    minimumFractionDigits: 2,
    maximumFractionDigits: 2
  });
}

// --- Chart init / resize ---
function initChart(domRef) {
  if (!domRef) return null;
  const instance = init(domRef);
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
const DASHBOARD_TIME_ZONE = "Asia/Shanghai";
const DASHBOARD_TIME_OFFSET = "+08:00";
const TIME_ZONE_SUFFIX_RE = /(?:Z|[+-]\d{2}:?\d{2})$/i;
const dashboardTimeFormatter = new Intl.DateTimeFormat("en-US", {
  timeZone: DASHBOARD_TIME_ZONE,
  month: "2-digit",
  day: "2-digit",
  hour: "2-digit",
  minute: "2-digit",
  hour12: false,
  hourCycle: "h23"
});

const timeLabels = computed(() => statsData.points.map(p => {
  return formatDashboardTimeLabel(p.time, statsData.range);
}));

function formatDashboardTimeLabel(value, rangeKey) {
  const date = parseDashboardTime(value);
  if (!date) return fallbackDashboardTimeLabel(value, rangeKey);

  const parts = Object.fromEntries(
    dashboardTimeFormatter.formatToParts(date).map(part => [part.type, part.value])
  );
  const clock = `${parts.hour}:${parts.minute}`;
  if (["7d", "30d", "custom"].includes(rangeKey)) {
    return `${parts.month}-${parts.day} ${clock}`;
  }
  return clock;
}

function parseDashboardTime(value) {
  const raw = String(value || "").trim();
  if (!raw) return null;

  const isoLike = raw.includes("T") ? raw : raw.replace(" ", "T");
  const normalized = TIME_ZONE_SUFFIX_RE.test(isoLike)
    ? isoLike
    : `${isoLike}${DASHBOARD_TIME_OFFSET}`;
  const date = new Date(normalized);
  return Number.isNaN(date.getTime()) ? null : date;
}

function fallbackDashboardTimeLabel(value, rangeKey) {
  const text = String(value || "").replace("T", " ");
  if (["7d", "30d", "custom"].includes(rangeKey)) {
    return text.length >= 16 ? text.slice(5, 16) : text;
  }
  return text.length >= 16 ? text.slice(11, 16) : text;
}

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
    return isCNY ? raw : raw / rate;
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
      right: 0,
      top: 16,
      bottom: 16,
      textStyle: { fontSize: 11 }
    },
    series: [{
      type: "pie",
      radius: ["40%", "70%"],
      center: ["32%", "50%"],
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
    startQueueStream();
  } else {
    stopRefreshTimer();
    stopQueueStream();
  }
});

onMounted(async () => {
  await nextTick();
  initAllCharts();
  if (props.active) {
    fetchStats();
    startRefreshTimer();
    startQueueStream();
  }
});

onBeforeUnmount(() => {
  stopRefreshTimer();
  stopQueueStream();
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

.dashboard-summary-grid {
  display: grid;
  grid-template-columns: repeat(5, minmax(180px, 1fr));
  gap: 16px;
  margin-bottom: 16px;
}

.dashboard-summary-card {
  position: relative;
  min-height: 124px;
  box-sizing: border-box;
  padding: 20px 18px 16px;
  border: 1px solid #d8dee8;
  border-radius: 8px;
  background: #fff;
  box-shadow: 0 2px 8px rgb(31 45 61 / 10%);
  overflow: hidden;
}

.dashboard-summary-card__icon {
  position: absolute;
  top: 16px;
  right: 16px;
  display: grid;
  place-items: center;
  width: 50px;
  height: 50px;
  border-radius: 8px;
  font-size: 24px;
}

.dashboard-summary-card__title {
  padding-right: 56px;
  color: var(--el-text-color-secondary);
  font-size: 15px;
  font-weight: 700;
}

.dashboard-summary-card__value {
  margin-top: 28px;
  color: #121826;
  font-size: 18px;
  font-weight: 700;
  line-height: 1.1;
  white-space: nowrap;
}

.dashboard-summary-card__meta {
  margin-top: 12px;
  color: var(--el-text-color-secondary);
  font-size: 10px;
  line-height: 1.4;
  white-space: normal;
  overflow-wrap: anywhere;
}

.dashboard-summary-card--blue .dashboard-summary-card__icon {
  background: #eef5ff;
  color: #356fc7;
}

.dashboard-summary-card--cyan .dashboard-summary-card__icon {
  background: #eef7fb;
  color: #337ea3;
}

.dashboard-summary-card--green .dashboard-summary-card__icon {
  background: #edf8f0;
  color: #32865c;
}

.dashboard-summary-card--red .dashboard-summary-card__icon {
  background: #fff0f0;
  color: #d9504f;
}

.dashboard-summary-card--green .dashboard-summary-card__value {
  color: #32865c;
}

.dashboard-summary-card--red .dashboard-summary-card__value {
  color: #121826;
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

.dashboard-top-grid {
  display: grid;
  grid-column: 1 / -1;
  grid-template-columns: minmax(0, 1fr) minmax(0, 1fr) minmax(0, 2fr);
  gap: 16px;
}

.dashboard-top-grid__model,
.dashboard-top-grid__queue,
.dashboard-top-grid__cost {
  min-width: 0;
}

.dashboard-top-grid__queue {
  display: flex;
  flex-direction: column;
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

.dashboard-card__chart--top {
  height: 260px;
}

.dashboard-queue {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: 10px;
  min-height: 260px;
  padding-top: 4px;
}

.dashboard-queue__status {
  color: var(--el-text-color-secondary);
  font-size: 12px;
  font-weight: 400;
}

.dashboard-queue__status--live {
  color: var(--el-color-success);
}

.dashboard-queue__item {
  display: grid;
  grid-template-columns: minmax(0, 1fr) auto;
  align-items: center;
  gap: 12px;
  min-height: 44px;
  padding: 0 12px;
  border: 1px solid var(--el-border-color-lighter);
  border-radius: 8px;
  background: #fbfcfe;
}

.dashboard-queue__item--popover + .dashboard-queue__item--popover {
  margin-top: 8px;
}

.dashboard-queue__name {
  min-width: 0;
  color: var(--el-text-color-primary);
  font-size: 14px;
  font-weight: 500;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.dashboard-queue__count {
  color: var(--el-color-primary);
  font-size: 18px;
  font-weight: 700;
  line-height: 1;
}

.dashboard-queue__empty {
  display: flex;
  align-items: center;
  justify-content: center;
  flex: 1;
  min-height: 180px;
  color: var(--el-text-color-secondary);
  font-size: 13px;
  border: 1px dashed var(--el-border-color);
  border-radius: 8px;
  background: #fbfcfe;
}

.dashboard-queue__more {
  padding-top: 2px;
}

.dashboard-queue__more-button {
  padding-right: 0;
  padding-left: 0;
  font-weight: 500;
}

.dashboard-queue-popover {
  max-height: 280px;
  overflow: auto;
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

  .dashboard-top-grid {
    grid-template-columns: 1fr;
  }

  .dashboard-summary-grid {
    grid-template-columns: 1fr;
  }
}

@media (min-width: 901px) and (max-width: 1400px) {
  .dashboard-summary-grid {
    grid-template-columns: repeat(2, minmax(240px, 1fr));
  }
}
</style>
