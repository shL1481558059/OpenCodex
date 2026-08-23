import test from "node:test";
import assert from "node:assert/strict";
import { consumeSseBuffer, parseSseChunk } from "./channelTestStream.js";

test("多个事件在一个 chunk 里到达", () => {
  const collected = [];
  const remaining = consumeSseBuffer(
    [
      'event: message',
      'data: {"type":"response.output_text.delta","delta":"你"}',
      "",
      "",
      'event: message',
      'data: {"type":"response.output_text.delta","delta":"好"}',
      "",
      ""
    ].join("\n"),
    (event) => collected.push(event)
  );

  assert.equal(remaining, "");
  assert.equal(collected.length, 2);
  assert.deepEqual(collected[0].data, {
    type: "response.output_text.delta",
    delta: "你"
  });
  assert.deepEqual(collected[1].data, {
    type: "response.output_text.delta",
    delta: "好"
  });
});

test("单个事件被拆成多个 chunk 时跨调用保留缓冲", () => {
  const collected = [];
  let buffer = 'event: message\ndata: {"type":"response.output';
  buffer = consumeSseBuffer(buffer, (event) => collected.push(event));
  assert.equal(collected.length, 0);

  buffer += '_text.delta","delta":"你"}\n\n';
  buffer = consumeSseBuffer(buffer, (event) => collected.push(event));
  assert.equal(collected.length, 1);
  assert.deepEqual(collected[0].data, {
    type: "response.output_text.delta",
    delta: "你"
  });
});

test("data: [DONE] 终止", () => {
  const collected = [];
  consumeSseBuffer('data: [DONE]\n\n', (event) => collected.push(event));

  assert.equal(collected.length, 1);
  assert.equal(collected[0].event, "message");
  assert.equal(collected[0].data, "[DONE]");
});

test("event: 缺失时使用默认事件名", () => {
  const event = parseSseChunk('data: {"type":"response.completed"}');
  assert.equal(event.event, "message");
  assert.deepEqual(event.data, { type: "response.completed" });
});

test("多行 data: 拼接", () => {
  const event = parseSseChunk("data: 第一行\ndata: 第二行");
  assert.equal(event.event, "message");
  assert.equal(event.raw, "第一行\n第二行");
  assert.equal(event.data, "第一行\n第二行");
});

test("流结束时缓冲区残留事件能被 flush 出来", () => {
  const collected = [];
  let buffer = 'event: channel_test.completed\ndata: {"status_code":200}';
  buffer = consumeSseBuffer(buffer, (event) => collected.push(event));
  assert.equal(collected.length, 0);

  buffer += "\n\n";
  consumeSseBuffer(buffer, (event) => collected.push(event));
  assert.equal(collected.length, 1);
  assert.equal(collected[0].event, "channel_test.completed");
  assert.deepEqual(collected[0].data, { status_code: 200 });
});

test("无数据的事件应被忽略", () => {
  const event = parseSseChunk("event: message\n:");
  assert.equal(event, null);
});
