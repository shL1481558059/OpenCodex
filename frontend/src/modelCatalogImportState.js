export const MODEL_CATALOG_TYPE = "model_catalog";
export const MODEL_CATALOG_VERSION = 1;

export function createModelCatalogImportState() {
  return {
    phase: "idle",
    fileName: "",
    document: null,
    dryRun: null,
    errors: [],
    confirmed: false,
    preview: false
  };
}

export function parseModelCatalogFile(text, fileName) {
  let parsed;
  try {
    parsed = JSON.parse(text);
  } catch {
    throw new Error("文件不是合法 JSON");
  }

  if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) {
    throw new Error("导入文件必须是 JSON 对象");
  }
  if (parsed.type !== MODEL_CATALOG_TYPE) {
    throw new Error("文件类型必须是 model_catalog");
  }
  if (parsed.version !== MODEL_CATALOG_VERSION) {
    throw new Error("仅支持版本 1 的导入文件");
  }
  if (!Array.isArray(parsed.providers)) {
    throw new Error("providers 必须是数组");
  }
  if (!Array.isArray(parsed.models)) {
    throw new Error("models 必须是数组");
  }

  return {
    ...parsed,
    type: MODEL_CATALOG_TYPE,
    version: MODEL_CATALOG_VERSION,
    providers: parsed.providers.map((item) => normalizeProvider(item)),
    models: parsed.models.map((item) => normalizeModel(item))
  };
}

export function applyModelCatalogDryRun(state, fileName, document, result) {
  state.phase = result?.dry_run ? "preview" : "error";
  state.fileName = fileName;
  state.document = document;
  state.dryRun = result || null;
  state.errors = Array.isArray(result?.errors) ? result.errors.slice() : [];
  state.confirmed = false;
  state.preview = result?.dry_run === true;
  return state;
}

export function applyModelCatalogImport(state, result) {
  state.phase = result?.dry_run ? "preview" : "done";
  state.dryRun = result || null;
  state.errors = Array.isArray(result?.errors) ? result.errors.slice() : [];
  state.confirmed = result?.dry_run === false;
  state.preview = false;
  return state;
}

export function failModelCatalogImport(state, message) {
  state.phase = "error";
  state.dryRun = null;
  state.errors = [message].filter(Boolean);
  state.confirmed = false;
  state.preview = false;
  return state;
}

export function resetModelCatalogImport(state) {
  Object.assign(state, createModelCatalogImportState());
  return state;
}

export function importSummary(state) {
  if (!state.dryRun) return "";
  const providers = state.dryRun.providers || {};
  const models = state.dryRun.models || {};
  return [
    `供应商新增 ${providers.created || 0}`,
    `更新 ${providers.updated || 0}`,
    `无变化 ${providers.unchanged || 0}`
  ].join("，");
}

function normalizeProvider(item) {
  if (!item || typeof item !== "object" || Array.isArray(item)) {
    throw new Error("providers 内存在非法条目");
  }
  return {
    code: String(item.code || "").trim().toLowerCase(),
    name: String(item.name || "").trim(),
    enabled: item.enabled !== false,
    sort_order: Number(item.sort_order || 0)
  };
}

function normalizeModel(item) {
  if (!item || typeof item !== "object" || Array.isArray(item)) {
    throw new Error("models 内存在非法条目");
  }
  const pricing = item.pricing == null
    ? null
    : {
        currency: String(item.pricing?.currency || "USD").trim().toUpperCase() || "USD",
        enabled: item.pricing?.enabled !== false,
        rules: Array.isArray(item.pricing?.rules)
          ? item.pricing.rules.map((rule) => normalizeRule(rule))
          : []
      };
  return {
    provider_code: String(item.provider_code || "").trim().toLowerCase(),
    model_key: String(item.model_key || "").trim(),
    display_name: String(item.display_name || "").trim(),
    description: String(item.description || "").trim(),
    match_type: String(item.match_type || "exact").trim().toLowerCase(),
    match_pattern: String(item.match_pattern || "").trim(),
    catalog: item.catalog && typeof item.catalog === "object" && !Array.isArray(item.catalog)
      ? item.catalog
      : {},
    capabilities: item.capabilities && typeof item.capabilities === "object" && !Array.isArray(item.capabilities)
      ? item.capabilities
      : {},
    enabled: item.enabled !== false,
    pricing
  };
}

function normalizeRule(rule) {
  if (!rule || typeof rule !== "object" || Array.isArray(rule)) {
    throw new Error("pricing.rules 内存在非法条目");
  }
  return {
    billing_item: String(rule.billing_item || "").trim().toLowerCase(),
    billing_mode: String(rule.billing_mode || "per_million_tokens").trim().toLowerCase(),
    unit_price: Number(rule.unit_price || 0),
    tiers: Array.isArray(rule.tiers)
      ? rule.tiers.map((tier) => ({
          up_to: tier?.up_to == null || tier?.up_to === "" ? null : Number(tier.up_to),
          unit_price: Number(tier?.unit_price || 0)
        }))
      : [],
    enabled: rule.enabled !== false
  };
}
