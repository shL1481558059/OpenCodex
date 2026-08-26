const DEFAULT_STALE_TIMEOUT_MS = 45000;
const DEFAULT_RETRY_BASE_MS = 1000;
const DEFAULT_RETRY_MAX_MS = 30000;
const HEARTBEAT_EVENT = "heartbeat";

function resolveDevPrefix() {
  // 与 client.js 同规则;可选链保证在纯 Node 测试环境下 import.meta.env 不存在时不炸。
  if (!import.meta.env?.DEV) return "";
  return import.meta.env.BASE_URL.replace(/\/$/, "");
}

function buildStreamUrl(path) {
  return `${resolveDevPrefix()}${path}`;
}

function parseEventData(raw) {
  if (raw === null || raw === undefined || raw === "") return undefined;
  try {
    return JSON.parse(raw);
  } catch {
    return undefined;
  }
}

/**
 * 统一 SSE 客户端:具名事件 + 心跳保鲜 + 退避重连 + 可见性恢复。
 * 状态机: idle -> connecting -> live,任何断线路径回 disconnected 后自动重连。
 * 依赖可注入(eventSourceFactory / setTimeout / clearTimeout / random / documentRef),
 * 便于在 node:test 下用 FakeEventSource + 假计时器驱动。
 */
export function createSseStream(options) {
  const {
    path,
    events = {},
    onStatus,
    staleTimeoutMs = DEFAULT_STALE_TIMEOUT_MS,
    retryBaseMs = DEFAULT_RETRY_BASE_MS,
    retryMaxMs = DEFAULT_RETRY_MAX_MS,
    eventSourceFactory = (url, opts) => new EventSource(url, opts),
    setTimeoutFn = setTimeout,
    clearTimeoutFn = clearTimeout,
    random = Math.random,
    documentRef = typeof document !== "undefined" ? document : null
  } = options;

  let source = null;
  let status = "idle";
  let generation = 0;
  let staleTimer = null;
  let retryTimer = null;
  let attempt = 0;
  let started = false;
  let cleanupVisibility = null;

  function setStatus(next) {
    if (status === next) return;
    status = next;
    onStatus?.(next);
  }

  function clearStaleTimer() {
    if (staleTimer !== null) {
      clearTimeoutFn(staleTimer);
      staleTimer = null;
    }
  }

  function clearRetryTimer() {
    if (retryTimer !== null) {
      clearTimeoutFn(retryTimer);
      retryTimer = null;
    }
  }

  function closeSource() {
    if (source) {
      source.close();
      source = null;
    }
  }

  function nextRetryDelay() {
    const exponent = Math.min(Math.max(attempt - 1, 0), 30);
    const capped = Math.min(retryBaseMs * 2 ** exponent, retryMaxMs);
    return Math.round(capped * (0.8 + random() * 0.4));
  }

  function scheduleReconnect() {
    clearRetryTimer();
    retryTimer = setTimeoutFn(() => {
      retryTimer = null;
      if (started) start({ resetBackoff: false });
    }, nextRetryDelay());
  }

  function handleBroken() {
    if (!started) return;
    clearStaleTimer();
    closeSource();
    setStatus("disconnected");
    attempt += 1;
    scheduleReconnect();
  }

  function markAlive() {
    clearRetryTimer();
    attempt = 0;
    setStatus("live");
    scheduleStaleCheck();
  }

  function scheduleStaleCheck() {
    clearStaleTimer();
    staleTimer = setTimeoutFn(() => {
      staleTimer = null;
      handleBroken();
    }, staleTimeoutMs);
  }

  function registerVisibility() {
    if (!documentRef || cleanupVisibility) return;
    const handler = () => {
      if (documentRef.visibilityState === "visible" && started && status === "disconnected") {
        clearRetryTimer();
        attempt = 0;
        start();
      }
    };
    documentRef.addEventListener("visibilitychange", handler);
    cleanupVisibility = () => documentRef.removeEventListener("visibilitychange", handler);
  }

  function teardown() {
    generation += 1;
    started = false;
    clearStaleTimer();
    clearRetryTimer();
    closeSource();
    cleanupVisibility?.();
    cleanupVisibility = null;
  }

  function start(opts = {}) {
    // 先收尾再开,保证同一时间只有一个 EventSource;代次自增,旧实例回调全部失效。
    teardown();
    started = true;
    setStatus("connecting");
    if (opts.resetBackoff !== false) attempt = 0;
    registerVisibility();
    scheduleStaleCheck();

    const myGeneration = generation;
    const es = eventSourceFactory(buildStreamUrl(path), { withCredentials: true });
    source = es;

    function isCurrent() {
      return started && myGeneration === generation && source === es;
    }

    for (const name of Object.keys(events)) {
      es.addEventListener(name, (event) => {
        if (!isCurrent()) return;
        markAlive();
        const data = parseEventData(event.data);
        if (data !== undefined) events[name](data);
      });
    }

    es.addEventListener(HEARTBEAT_EVENT, () => {
      if (!isCurrent()) return;
      // 心跳只保鲜,不回调业务。
      markAlive();
    });

    es.onopen = () => {
      if (!isCurrent()) return;
      scheduleStaleCheck();
    };

    es.onerror = () => {
      if (!isCurrent()) return;
      handleBroken();
    };
  }

  function stop() {
    teardown();
    setStatus("idle");
  }

  return {
    start,
    stop,
    get status() {
      return status;
    }
  };
}
