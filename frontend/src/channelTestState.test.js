import test from "node:test";
import assert from "node:assert/strict";
import {
  applyChannelTestStreamEvent,
  createChannelTestState,
  finalizeChannelTestResult,
  formatChannelTestResult,
  getChannelTestAlertTitle,
  getChannelTestAlertType
} from "./channelTestState.js";

test("测试开始后未收到任何事件时不应显示成功", () => {
  const state = createChannelTestState();

  assert.equal(state.phase, "connecting");
  assert.equal(getChannelTestAlertTitle(state), "正在测试连接");
  assert.equal(getChannelTestAlertType(state), "info");
  assert.equal(formatChannelTestResult(state), "正在建立连接，等待上游响应...");

  finalizeChannelTestResult(state);

  assert.equal(state.phase, "error");
  assert.equal(state.error, "未收到上游响应事件");
  assert.equal(getChannelTestAlertTitle(state), "连接测试失败");
  assert.equal(getChannelTestAlertType(state), "error");
});

test("收到文本增量时应进入 streaming 阶段而不是 success", () => {
  const state = createChannelTestState();

  applyChannelTestStreamEvent(state, {
    event: "message",
    data: { type: "response.output_text.delta", delta: "你好" }
  });

  assert.equal(state.phase, "streaming");
  assert.equal(formatChannelTestResult(state), "你好");
  assert.equal(getChannelTestAlertTitle(state), "正在测试连接");
  assert.equal(getChannelTestAlertType(state), "info");
});

test("只有收到 response.completed 后才应显示成功", () => {
  const state = createChannelTestState();

  applyChannelTestStreamEvent(state, {
    event: "message",
    data: { type: "response.output_text.delta", delta: "你好" }
  });
  applyChannelTestStreamEvent(state, {
    event: "message",
    data: {
      type: "response.completed",
      response: {
        id: "resp_1",
        model: "gpt-5.4",
        output: []
      }
    }
  });

  assert.equal(state.phase, "success");
  assert.equal(formatChannelTestResult(state), "你好");
  assert.equal(getChannelTestAlertTitle(state), "连接测试成功");
  assert.equal(getChannelTestAlertType(state), "success");
});
