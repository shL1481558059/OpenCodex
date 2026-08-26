import test from "node:test";
import assert from "node:assert/strict";
import { createSseStream } from "./sseClient.js";

class FakeEventSource {
  constructor(url, options) {
    this.url = url;
    this.options = options;
    this.listeners = new Map();
    this.closed = false;
    this.onopen = null;
    this.onerror = null;
  }

  addEventListener(name, fn) {
    if (!this.listeners.has(name)) this.listeners.set(name, []);
    this.listeners.get(name).push(fn);
  }

  dispatch(name, data) {
    for (const fn of this.listeners.get(name) || []) {
      fn({ data });
    }
  }

  open() {
    this.onopen?.();
  }

  error() {
    this.onerror?.();
  }

  close() {
    this.closed = true;
  }
}

function createTimers() {
  let now = 0;
  const timers = new Map();
  let nextId = 1;

  return {
    now,
    set: (fn, delay) => {
      const id = nextId++;
      timers.set(id, { fn, due: now + delay });
      return id;
    },
    clear: (id) => timers.delete(id),
    advance: (ms) => {
      now += ms;
      const due = [...timers.values()].filter((t) => t.due <= now);
      due.sort((a, b) => a.due - b.due);
      for (const t of due) {
        timers.delete([...timers.entries()].find(([k, v]) => v === t)?.[0]);
        t.fn();
      }
    },
    pending: () => timers.size
  };
}

function setup(options = {}) {
  const sources = [];
  const timers = createTimers();
  const deps = {
    eventSourceFactory: (url, opts) => {
      const es = new FakeEventSource(url, opts);
      sources.push(es);
      return es;
    },
    setTimeoutFn: timers.set,
    clearTimeoutFn: timers.clear,
    random: () => 0.5,
    documentRef: {
      visibilityState: "visible",
      addEventListener() {},
      removeEventListener() {}
    }
  };
  const stream = createSseStream({
    path: "/channels/runtime/stream",
    staleTimeoutMs: 1000,
    retryBaseMs: 100,
    retryMaxMs: 400,
    ...options,
    ...deps
  });
  return { stream, sources, timers };
}

test("心跳不触发业务回调,且跨过保鲜窗口不重连", () => {
  const calls = [];
  const { stream, sources, timers } = setup({
    events: { runtime: (data) => calls.push(data) }
  });

  stream.start();
  const es = sources[0];
  es.open();
  es.dispatch("runtime", '{"i":1}');
  assert.deepEqual(calls, [{ i: 1 }]);

  // 两个心跳周期(每次 markAlive 重置保鲜),不应重连。
  es.dispatch("heartbeat", "{}");
  timers.advance(500);
  es.dispatch("heartbeat", "{}");
  timers.advance(500);

  assert.equal(es.closed, false);
  assert.equal(sources.length, 1);
  assert.equal(stream.status, "live");
});

test("完全静默超过保鲜窗口触发重连", () => {
  const { stream, sources, timers } = setup();

  stream.start();
  const es = sources[0];
  es.open();

  // 无任何事件,保鲜窗口 1000ms 到点。
  timers.advance(1001);

  assert.equal(es.closed, true);
  assert.equal(stream.status, "disconnected");

  // 再推进一个退避周期,重连应当发生。
  timers.advance(100);
  assert.equal(sources.length, 2);
  assert.equal(stream.status, "connecting");
});

test("onerror 关闭旧实例并按退避序列重连", () => {
  const { stream, sources, timers } = setup();

  stream.start();
  const first = sources[0];
  first.open();
  first.dispatch("runtime", '{"i":1}');

  first.error();
  assert.equal(first.closed, true);
  assert.equal(sources.length, 1);
  assert.equal(stream.status, "disconnected");

  // 第一次退避 100ms。
  timers.advance(100);
  assert.equal(sources.length, 2);

  sources[1].error();
  // 第二次退避 200ms。
  timers.advance(200);
  assert.equal(sources.length, 3);

  sources[2].error();
  // 第三次退避 400ms(上限)。
  timers.advance(400);
  assert.equal(sources.length, 4);
});

test("重连成功收到事件后退避回落到基数", () => {
  const calls = [];
  const { stream, sources, timers } = setup({
    events: { runtime: (data) => calls.push(data) }
  });

  stream.start();
  sources[0].open();
  sources[0].error();
  assert.equal(stream.status, "disconnected");

  timers.advance(100);
  sources[1].open();
  sources[1].dispatch("runtime", '{"i":2}');
  assert.equal(stream.status, "live");
  assert.deepEqual(calls, [{ i: 2 }]);

  // 之后断线,第一次退避应回到 100ms 而非继续翻倍。
  sources[1].error();
  timers.advance(100);
  assert.equal(sources.length, 3);
});

test("stop 后不再重连,旧实例回调不改变状态", () => {
  const { stream, sources, timers } = setup();

  stream.start();
  const es = sources[0];
  es.open();
  stream.stop();

  assert.equal(es.closed, true);
  assert.equal(stream.status, "idle");

  // 旧实例事后触发事件/错误,状态不应被改变。
  es.dispatch("runtime", '{"i":9}');
  es.error();
  timers.advance(5000);
  assert.equal(stream.status, "idle");
  assert.equal(sources.length, 1);
});
