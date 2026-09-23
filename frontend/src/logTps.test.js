import test from "node:test";
import assert from "node:assert/strict";
import {
  computeEndToEndTps,
  computeDecodeTps,
  formatTps,
  formatEndToEndTps,
  formatDecodeTps
} from "./logTps.js";

test("端到端速度按输出 token 除以总耗时计算", () => {
  assert.equal(computeEndToEndTps({ output_tokens: 1000, duration_ms: 10000 }), 100);
});

test("端到端速度接受字符串数值", () => {
  assert.equal(computeEndToEndTps({ output_tokens: "500", duration_ms: "2000" }), 250);
});

test("输出 token 缺失、非数字或非正数时不可计算", () => {
  assert.equal(computeEndToEndTps({ duration_ms: 1000 }), null);
  assert.equal(computeEndToEndTps({ output_tokens: 0, duration_ms: 1000 }), null);
  assert.equal(computeEndToEndTps({ output_tokens: -5, duration_ms: 1000 }), null);
  assert.equal(computeEndToEndTps({ output_tokens: "abc", duration_ms: 1000 }), null);
});

test("总耗时缺失、非数字或非正数时不可计算", () => {
  assert.equal(computeEndToEndTps({ output_tokens: 10 }), null);
  assert.equal(computeEndToEndTps({ output_tokens: 10, duration_ms: 0 }), null);
  assert.equal(computeEndToEndTps({ output_tokens: 10, duration_ms: -1 }), null);
  assert.equal(computeEndToEndTps({ output_tokens: 10, duration_ms: "abc" }), null);
});

test("端到端速度对空日志返回不可计算", () => {
  assert.equal(computeEndToEndTps(null), null);
  assert.equal(computeEndToEndTps(undefined), null);
  assert.equal(computeEndToEndTps({}), null);
});

test("生成速度扣除首 token 延迟", () => {
  assert.equal(computeDecodeTps({ output_tokens: 480, duration_ms: 12000, ttft_ms: 2000 }), 48);
});

test("TTFT 缺失、非正数或不小于总耗时的时候生成速度不可计算", () => {
  assert.equal(computeDecodeTps({ output_tokens: 480, duration_ms: 12000 }), null);
  assert.equal(computeDecodeTps({ output_tokens: 480, duration_ms: 12000, ttft_ms: 0 }), null);
  assert.equal(computeDecodeTps({ output_tokens: 480, duration_ms: 12000, ttft_ms: 12000 }), null);
  assert.equal(computeDecodeTps({ output_tokens: 480, duration_ms: 12000, ttft_ms: 15000 }), null);
  assert.equal(computeDecodeTps({ output_tokens: 480, duration_ms: 12000, ttft_ms: "abc" }), null);
});

test("极小耗时下速度仍然可计算", () => {
  assert.equal(computeEndToEndTps({ output_tokens: 1, duration_ms: 1 }), 1000);
});

test("格式化保留一位小数并带单位", () => {
  assert.equal(formatTps(48.04), "48.0 tok/s");
  assert.equal(formatTps(1000 / 3), "333.3 tok/s");
});

test("不可计算的日志格式化为占位符", () => {
  assert.equal(formatEndToEndTps({ output_tokens: 0, duration_ms: 1000 }), "-");
  assert.equal(formatEndToEndTps(null), "-");
  assert.equal(formatDecodeTps({ output_tokens: 10, duration_ms: 1000, ttft_ms: null }), "-");
});
