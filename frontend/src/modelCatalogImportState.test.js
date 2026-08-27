import test from "node:test";
import assert from "node:assert/strict";
import {
  MODEL_CATALOG_TYPE,
  MODEL_CATALOG_VERSION,
  createModelCatalogImportState,
  parseModelCatalogFile,
  applyModelCatalogDryRun,
  applyModelCatalogImport,
  failModelCatalogImport,
  resetModelCatalogImport,
  importSummary,
  syncModelKeys
} from "./modelCatalogImportState.js";

test("合法文件解析后保留 catalog 与 pricing", () => {
  const document = parseModelCatalogFile(JSON.stringify({
    type: MODEL_CATALOG_TYPE,
    version: MODEL_CATALOG_VERSION,
    providers: [{ code: "OpenAI", name: "OpenAI", sort_order: 10 }],
    models: [{
      provider_code: "OpenAI",
      model_key: "gpt-test",
      match_type: "exact",
      match_pattern: "gpt-test",
      catalog: { slug: "gpt-test" },
      capabilities: { supports_image: true },
      pricing: {
        currency: "usd",
        rules: [{ billing_item: "input", unit_price: 1.25 }]
      }
    }]
  }), "catalog.json");

  assert.equal(document.type, MODEL_CATALOG_TYPE);
  assert.equal(document.version, MODEL_CATALOG_VERSION);
  assert.equal(document.providers[0].code, "openai");
  assert.equal(document.models[0].provider_code, "openai");
  assert.equal(document.models[0].catalog.slug, "gpt-test");
  assert.equal(document.models[0].pricing.currency, "USD");
  assert.equal(document.models[0].pricing.rules[0].unit_price, 1.25);
});

test("pricing 为 null 时保留删除语义", () => {
  const document = parseModelCatalogFile(JSON.stringify({
    type: MODEL_CATALOG_TYPE,
    version: MODEL_CATALOG_VERSION,
    providers: [],
    models: [{ provider_code: "p", model_key: "m", pricing: null }]
  }), "catalog.json");

  assert.equal(document.models[0].pricing, null);
});

test("非法文件抛错", () => {
  assert.throws(() => parseModelCatalogFile("not-json", "x.json"), /合法 JSON/);
  assert.throws(
    () => parseModelCatalogFile(JSON.stringify({ type: "other", version: 1 }), "x.json"),
    /model_catalog/);
  assert.throws(
    () => parseModelCatalogFile(JSON.stringify({ type: MODEL_CATALOG_TYPE, version: 3 }), "x.json"),
    /版本 1 或 2/);
  assert.throws(
    () => parseModelCatalogFile(JSON.stringify({
      type: MODEL_CATALOG_TYPE,
      version: 1,
      providers: [],
      models: [null]
    }), "x.json"),
    /非法条目/);
});

test("v2 文件保留峰谷配置，v1 文件按未启用处理", () => {
  const v2 = parseModelCatalogFile(JSON.stringify({
    type: MODEL_CATALOG_TYPE,
    version: 2,
    providers: [],
    models: [{
      provider_code: "p",
      model_key: "m",
      pricing: {
        currency: "usd",
        time_zone: "Asia/Shanghai",
        off_peak_windows: [{ start: "22:00", end: "24:00", days: [1, 2, 3, 4, 5] }],
        rules: [{
          billing_item: "output",
          unit_price: 1.1,
          off_peak_enabled: true,
          off_peak_unit_price: 0.55
        }]
      }
    }]
  }), "catalog.json");

  assert.equal(v2.models[0].pricing.time_zone, "Asia/Shanghai");
  assert.deepEqual(v2.models[0].pricing.off_peak_windows, [
    { start: "22:00", end: "24:00", days: [1, 2, 3, 4, 5] }
  ]);
  assert.equal(v2.models[0].pricing.rules[0].off_peak_enabled, true);
  assert.equal(v2.models[0].pricing.rules[0].off_peak_unit_price, 0.55);

  const v1 = parseModelCatalogFile(JSON.stringify({
    type: MODEL_CATALOG_TYPE,
    version: 1,
    providers: [],
    models: [{
      provider_code: "p",
      model_key: "m",
      pricing: { currency: "usd", rules: [{ billing_item: "input", unit_price: 1 }] }
    }]
  }), "catalog.json");

  assert.equal(v1.version, MODEL_CATALOG_VERSION);
  assert.equal(v1.models[0].pricing.time_zone, "");
  assert.deepEqual(v1.models[0].pricing.off_peak_windows, []);
  assert.equal(v1.models[0].pricing.rules[0].off_peak_enabled, false);
});

test("dry run 预览与正式导入推进状态", () => {
  const state = createModelCatalogImportState();
  const result = {
    dry_run: true,
    providers: { created: 1, updated: 0, unchanged: 2 },
    models: { created: 0, updated: 1, unchanged: 0 },
    errors: []
  };
  applyModelCatalogDryRun(state, "catalog.json", { providers: [] }, result);

  assert.equal(state.phase, "preview");
  assert.equal(state.fileName, "catalog.json");
  assert.equal(state.preview, true);
  assert.equal(state.confirmed, false);
  assert.equal(importSummary(state), "供应商新增 1，更新 0，无变化 2");

  applyModelCatalogImport(state, { ...result, dry_run: false });
  assert.equal(state.phase, "done");
  assert.equal(state.confirmed, true);
  assert.equal(state.preview, false);
});

test("失败与重置", () => {
  const state = createModelCatalogImportState();
  failModelCatalogImport(state, "服务不可用");
  assert.equal(state.phase, "error");
  assert.deepEqual(state.errors, ["服务不可用"]);

  resetModelCatalogImport(state);
  assert.equal(state.phase, "idle");
  assert.equal(state.document, null);
});

test("同步增量模式摘要包含跳过计数", () => {
  const state = createModelCatalogImportState();
  state.origin = "sync";
  const result = {
    dry_run: true,
    providers: { created: 1, updated: 0, unchanged: 2 },
    models: { created: 2, updated: 0, unchanged: 0 },
    skipped: 5,
    created_model_keys: ["model-a", "model-b"],
    skipped_model_keys: ["existing-1", "existing-2", "existing-3", "existing-4", "existing-5"],
    overwritten_model_keys: [],
    errors: []
  };
  applyModelCatalogDryRun(state, "", null, result);

  const summary = importSummary(state);
  assert.ok(summary.includes("跳过 5"));
  assert.ok(summary.includes("模型新增 2"));

  const keys = syncModelKeys(state);
  assert.equal(keys.created.length, 2);
  assert.equal(keys.skipped.length, 5);
  assert.equal(keys.overwritten.length, 0);
});

test("覆盖模式摘要包含覆盖计数", () => {
  const state = createModelCatalogImportState();
  state.origin = "overwrite";
  const result = {
    dry_run: true,
    providers: { created: 0, updated: 0, unchanged: 3 },
    models: { created: 1, updated: 0, unchanged: 0 },
    skipped: 0,
    created_model_keys: ["new-model"],
    skipped_model_keys: [],
    overwritten_model_keys: ["existing-1", "existing-2"],
    errors: []
  };
  applyModelCatalogDryRun(state, "", null, result);

  const summary = importSummary(state);
  assert.ok(summary.includes("覆盖 2"));
  assert.ok(summary.includes("模型新增 1"));

  const keys = syncModelKeys(state);
  assert.equal(keys.overwritten.length, 2);
  assert.equal(keys.created.length, 1);
});

test("覆盖模式未勾选时 confirmed 为 false", () => {
  const state = createModelCatalogImportState();
  state.origin = "overwrite";
  assert.equal(state.overwriteConfirmed, false);
  state.overwriteConfirmed = true;
  assert.equal(state.overwriteConfirmed, true);
});

test("同步失败保留 errors 且可 reset", () => {
  const state = createModelCatalogImportState();
  state.origin = "sync";
  failModelCatalogImport(state, "远端不可达");
  assert.equal(state.phase, "error");
  assert.deepEqual(state.errors, ["远端不可达"]);
  assert.equal(state.overwriteConfirmed, false);

  resetModelCatalogImport(state);
  assert.equal(state.phase, "idle");
  assert.equal(state.origin, "file");
});
