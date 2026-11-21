import test from "node:test";
import assert from "node:assert/strict";
import {
  buildModelEditorPayload, createChannelModelEditorDraft, createModelEditorDraft,
  effectiveChannelModel, modelCatalogText
} from "./modelEditorState.js";

const model = {
  model_key: "family",
  match_type: "prefix",
  match_pattern: "gpt-",
  match_patterns: ["gpt-"],
  catalog: { context_window: 1000000, max_context_window: 872000 },
  capabilities: { supports_image: true, context_window: 990000, extra_capability: { enabled: true } },
  pricing: {
    currency: "USD", time_zone: "UTC",
    off_peak_windows: [{ start: "00:00", end: "08:00", days: [1] }],
    rules: [{
      billing_item: "input", billing_mode: "tiered_tokens", enabled: true,
      tiers: [{ up_to: 272000, unit_price: 2 }, { up_to: null, unit_price: 4 }],
      off_peak_enabled: true,
      off_peak_tiers: [{ up_to: null, unit_price: 1 }]
    }]
  }
};
const row = { request_model: "gpt-6.1-sol", upstream_model: "upstream-sol", override_model: model };

test("渠道模型原样保存保留前缀匹配和额外能力字段", () => {
  const draft = createChannelModelEditorDraft(row);
  const payload = buildModelEditorPayload(draft, modelCatalogText(model), true);
  assert.equal(payload.match_pattern, "gpt-");
  assert.deepEqual(payload.match_patterns, ["gpt-"]);
  assert.deepEqual(payload.capabilities.extra_capability, { enabled: true });
  draft.capabilities.extra_capability.enabled = false;
  assert.equal(model.capabilities.extra_capability.enabled, true);
});

test("全局和渠道共同字段与窗口档位提交一致", () => {
  const global = createModelEditorDraft(model);
  const channel = createChannelModelEditorDraft(row);
  const globalPayload = buildModelEditorPayload(global, modelCatalogText(model));
  const channelPayload = buildModelEditorPayload(channel, modelCatalogText(model), true);
  assert.equal(global.capabilities.context_window, 1000000);
  assert.equal(channel.capabilities.context_window, 1000000);
  assert.deepEqual(channelPayload.pricing, globalPayload.pricing);
  assert.equal(channelPayload.catalog.max_context_window, 872000);
  channel.pricing.rules[0].tiers.push({ up_to: null, unit_price: 6 });
  assert.equal(model.pricing.rules[0].tiers.length, 2);
});

test("渠道 Agent 继承时省略字段，显式关闭和开启分别保存 false 和 true", () => {
  const inheritedRow = { ...row, override_model: null, global_model: {
    ...model, capabilities: { ...model.capabilities, v2_agent_simulation: true }
  } };
  const draft = createChannelModelEditorDraft(inheritedRow);
  assert.equal(draft.agent_simulation, "inherit");
  assert.equal(draft.match_type, "exact");
  assert.deepEqual(draft.match_patterns, ["gpt-6.1-sol"]);
  assert.equal(Object.hasOwn(buildModelEditorPayload(draft, "{}", true).capabilities, "v2_agent_simulation"), false);
  for (const [setting, expected] of [["disabled", false], ["enabled", true]]) {
    draft.agent_simulation = setting;
    assert.equal(buildModelEditorPayload(draft, "{}", true).capabilities.v2_agent_simulation, expected);
    const saved = createChannelModelEditorDraft({
      ...inheritedRow, override_model: { ...model, capabilities: { v2_agent_simulation: expected } }
    });
    assert.equal(saved.agent_simulation, setting);
  }
});

test("全局和渠道统一拒绝非对象 Catalog", () => {
  for (const channel of [false, true]) {
    const draft = createModelEditorDraft(model);
    for (const text of ["null", "[]", "123", "\"text\""]) {
      assert.throws(() => buildModelEditorPayload(draft, text, channel), /必须是 JSON 对象/);
    }
    assert.throws(() => buildModelEditorPayload(draft, "{", channel), /不是合法 JSON/);
  }
});

test("额外能力和 Catalog 从全局继承，渠道值优先", () => {
  const merged = effectiveChannelModel({
    global_model: model,
    override_model: { catalog: { context_window: 650000 }, capabilities: { supports_image: false } }
  });
  assert.equal(merged.catalog.context_window, 650000);
  assert.equal(merged.catalog.max_context_window, 872000);
  assert.equal(merged.capabilities.supports_image, false);
  assert.deepEqual(merged.capabilities.extra_capability, { enabled: true });
});

test("渠道能力窗口优先于全局 Catalog，原样保存不扩大窗口", () => {
  const partialOverride = {
    request_model: "gpt-6.1-sol",
    upstream_model: "upstream-sol",
    global_model: model,
    override_model: { catalog: {}, capabilities: { context_window: 650000 } }
  };
  const effective = effectiveChannelModel(partialOverride);
  const draft = createChannelModelEditorDraft(partialOverride);

  assert.equal(effective.catalog.context_window, 650000);
  assert.equal(draft.capabilities.context_window, 650000);
  const payload = buildModelEditorPayload(draft, modelCatalogText(effective), true);
  assert.equal(payload.capabilities.context_window, 650000);
  assert.equal(payload.catalog.context_window, 650000);
  assert.equal(payload.catalog.max_context_window, 872000);
});
