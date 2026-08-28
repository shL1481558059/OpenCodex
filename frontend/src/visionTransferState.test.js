import test from "node:test";
import assert from "node:assert/strict";
import {
  applyCandidates,
  applyServerSettings,
  canSave,
  channelOptions,
  clearFallback,
  createVisionTransferState,
  hasCandidates,
  hasModelCapability,
  modelOptions,
  reasonText,
  selectChannel,
  statusSummary,
  toSaveRequest,
  validationMessage
} from "./visionTransferState.js";

const CANDIDATES = [
  { channel_id: "c1", channel_name: "视觉渠道", channel_type: "chat", model: "vision-a", upstream_model: "upstream-a" },
  { channel_id: "c1", channel_name: "视觉渠道", channel_type: "chat", model: "vision-b", upstream_model: "upstream-b" },
  { channel_id: "c2", channel_name: "备用渠道", channel_type: "responses", model: "vision-c", upstream_model: "upstream-c" }
];

function stateWithCandidates() {
  return applyCandidates(createVisionTransferState("alice"), CANDIDATES);
}

test("候选按渠道去重后给出渠道选项", () => {
  const state = stateWithCandidates();

  assert.deepEqual(channelOptions(state).map((item) => item.value), ["c1", "c2"]);
  assert.deepEqual(modelOptions(state, "c1").map((item) => item.value), ["vision-a", "vision-b"]);
  assert.deepEqual(modelOptions(state, ""), []);
  assert.equal(hasCandidates(state), true);
  assert.equal(hasModelCapability(state, { channelId: "c1", model: "vision-a" }), true);
  assert.equal(hasModelCapability(state, { channelId: "c1", model: "text-x" }), false);
});

test("换渠道时清掉不属于新渠道的模型", () => {
  const state = stateWithCandidates();
  selectChannel(state, "primary", "c1");
  state.draft.primary.model = "vision-a";

  selectChannel(state, "primary", "c2");

  assert.equal(state.draft.primary.channelId, "c2");
  assert.equal(state.draft.primary.model, "");
});

test("同渠道内换选时保留已选模型", () => {
  const state = stateWithCandidates();
  selectChannel(state, "primary", "c1");
  state.draft.primary.model = "vision-b";

  selectChannel(state, "primary", "c1");

  assert.equal(state.draft.primary.model, "vision-b");
});

test("主未选完整时不允许保存", () => {
  const state = stateWithCandidates();

  assert.equal(validationMessage(state), "请先选择主视觉渠道和模型");
  assert.equal(canSave(state), false);

  state.draft.primary = { channelId: "c1", model: "vision-a" };

  assert.equal(validationMessage(state), "");
  assert.equal(canSave(state), true);
});

test("兜底只选一半时给出提示,整组留空则合法", () => {
  const state = stateWithCandidates();
  state.draft.primary = { channelId: "c1", model: "vision-a" };
  state.draft.fallback = { channelId: "c2", model: "" };

  assert.equal(validationMessage(state), "兜底需要同时选择渠道和模型,或者整组留空");

  clearFallback(state);

  assert.equal(canSave(state), true);
  assert.equal(toSaveRequest(state).fallback, null);
});

test("兜底与主完全相同时拒绝保存", () => {
  const state = stateWithCandidates();
  state.draft.primary = { channelId: "c1", model: "vision-a" };
  state.draft.fallback = { channelId: "c1", model: "vision-a" };

  assert.equal(validationMessage(state), "兜底不能与主完全相同");
});

test("保存载荷带上 owner 与两组配置", () => {
  const state = stateWithCandidates();
  state.draft.primary = { channelId: "c1", model: "vision-a" };
  state.draft.fallback = { channelId: "c2", model: "vision-c" };

  assert.deepEqual(toSaveRequest(state, "bob"), {
    owner_username: "bob",
    primary: { channel_id: "c1", model: "vision-a" },
    fallback: { channel_id: "c2", model: "vision-c" }
  });
});

test("服务端配置回填草稿并保留失效状态", () => {
  const state = createVisionTransferState();

  applyServerSettings(state, {
    owner_username: "alice",
    configured: true,
    updated_at: 1774000000,
    primary: { channel_id: "c1", channel_name: "视觉渠道", model: "vision-a", available: true, reason: "" },
    fallback: { channel_id: "c2", channel_name: "备用渠道", model: "vision-c", available: false, reason: "channel_disabled" }
  });

  assert.equal(state.ownerUsername, "alice");
  assert.equal(state.configured, true);
  assert.deepEqual(state.draft.primary, { channelId: "c1", model: "vision-a" });
  assert.deepEqual(state.draft.fallback, { channelId: "c2", model: "vision-c" });
  assert.deepEqual(statusSummary(state.server.primary), { type: "success", text: "视觉渠道 / vision-a" });
  assert.deepEqual(statusSummary(state.server.fallback), {
    type: "danger",
    text: "备用渠道 / vision-c(渠道已禁用)"
  });
});

test("未配置与未知原因的文案兜底", () => {
  assert.deepEqual(statusSummary(null), { type: "info", text: "未配置" });
  assert.equal(reasonText("image_capability_revoked"), "该模型的图片能力已被撤销");
  assert.equal(reasonText("something_new"), "something_new");
  assert.equal(reasonText(""), "");
});
