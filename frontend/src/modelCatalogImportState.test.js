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
  importSummary
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
    () => parseModelCatalogFile(JSON.stringify({ type: MODEL_CATALOG_TYPE, version: 2 }), "x.json"),
    /版本 1/);
  assert.throws(
    () => parseModelCatalogFile(JSON.stringify({
      type: MODEL_CATALOG_TYPE,
      version: 1,
      providers: [],
      models: [null]
    }), "x.json"),
    /非法条目/);
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
