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

test("收到 channel_test.completed 后应保存可展示的响应详情", () => {
  const state = createChannelTestState();

  applyChannelTestStreamEvent(state, {
    event: "channel_test.completed",
    data: {
      status_code: 200,
      duration_ms: 123,
      request_model: "public-model",
      upstream_model: "upstream-model",
      upstream_request: { model: "upstream-model", stream: true },
      upstream_response: {
        id: "resp_1",
        model: "upstream-model",
        output_text: "pong"
      }
    }
  });

  assert.equal(state.phase, "success");
  assert.equal(state.duration_ms, 123);
  assert.equal(state.details.status_code, 200);
  assert.deepEqual(state.details.upstream_request, { model: "upstream-model", stream: true });
  assert.equal(formatChannelTestResult(state), "pong");
});

test("上游失败时应展示原始错误信息而不是客户端脱敏文案", () => {
  const state = createChannelTestState();

  applyChannelTestStreamEvent(state, {
    event: "channel_test.error",
    data: {
      error: {
        message: "An upstream error occurred. Please try again later.",
        type: "upstream_error"
      }
    }
  });
  applyChannelTestStreamEvent(state, {
    event: "channel_test.completed",
    data: {
      status_code: 401,
      duration_ms: 88,
      error: "upstream returned HTTP 401",
      upstream_response: {
        error: {
          error: {
            message: "Incorrect API key provided",
            type: "invalid_request_error"
          }
        },
        _opencodex_capture: { completed: false, termination: "UpstreamError" }
      },
      error_response: {
        error: {
          message: "An upstream error occurred. Please try again later.",
          type: "upstream_error"
        }
      }
    }
  });

  assert.equal(state.phase, "error");
  assert.equal(getChannelTestAlertTitle(state), "连接测试失败");
  assert.equal(getChannelTestAlertType(state), "error");

  const text = formatChannelTestResult(state);
  assert.match(text, /Incorrect API key provided/);
  assert.match(text, /upstream returned HTTP 401/);
  assert.doesNotMatch(text, /An upstream error occurred/);
});

test("上游错误体不是 JSON 时也应展示原文", () => {
  const state = createChannelTestState();

  applyChannelTestStreamEvent(state, {
    event: "channel_test.completed",
    data: {
      status_code: 502,
      error: "upstream returned HTTP 502",
      upstream_response: { error: "<html>502 Bad Gateway</html>" },
      error_response: {
        error: {
          message: "An upstream error occurred. Please try again later.",
          type: "upstream_error"
        }
      }
    }
  });

  assert.equal(state.phase, "error");
  assert.match(formatChannelTestResult(state), /502 Bad Gateway/);
});
