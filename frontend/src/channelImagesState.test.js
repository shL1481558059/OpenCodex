import test from "node:test";
import assert from "node:assert/strict";
import {
  applyChannelTypeContract,
  buildImagesCompat,
  canUseChatStreamTest,
  isImagesChannel
} from "./channelImagesState.js";

test("切换到 images 时应设置默认方言并将重试次数固定为 0", () => {
  const draft = { type: "images", retry_count: 3 };
  const compat = {};

  applyChannelTypeContract(draft, compat);

  assert.equal(draft.retry_count, 0);
  assert.equal(compat.images_api_dialect, "openai");
});

test("images 渠道应保留已有合法方言", () => {
  const draft = { type: "images", retry_count: 9 };
  const compat = { images_api_dialect: "xai" };

  applyChannelTypeContract(draft, compat);

  assert.equal(draft.retry_count, 0);
  assert.equal(compat.images_api_dialect, "xai");
  assert.deepEqual(buildImagesCompat(compat), { images_api_dialect: "xai" });
});

test("images 渠道应保留通用参数兼容规则并移除语言专属规则", () => {
  const compat = buildImagesCompat({
    images_api_dialect: "openai",
    rename_params: { prompt: "text" },
    drop_params: ["stream"],
    force_params: { quality: "high" },
    default_params: { n: 1 },
    unsupported_params: ["response_format"],
    enable_apply_patch_prompt_compat: true,
    preserve_thinking_history: true,
    drop_tool_types: ["image_generation"]
  });

  assert.deepEqual(compat, {
    images_api_dialect: "openai",
    rename_params: { prompt: "text" },
    drop_params: ["stream"],
    force_params: { quality: "high" },
    default_params: { n: 1 },
    unsupported_params: ["response_format"]
  });
});

test("images 渠道不得使用聊天流测试", () => {
  assert.equal(isImagesChannel({ type: "images" }), true);
  assert.equal(canUseChatStreamTest({ type: "images" }), false);
  assert.equal(canUseChatStreamTest({ type: "chat" }), true);
});

test("非法图片方言应在保存前拒绝", () => {
  assert.throws(
    () => buildImagesCompat({ images_api_dialect: "unknown" }),
    /图片 API 方言/
  );
});
