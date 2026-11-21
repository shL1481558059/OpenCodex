import { normalizeOffPeakWindows } from "./pricingOffPeak.js";

export const MODEL_BILLING_ITEMS = [
  { value: "input", label: "输入" },
  { value: "output", label: "输出" },
  { value: "cache_write", label: "缓存写" },
  { value: "cache_read", label: "缓存读" }
];

const clone = (value) => JSON.parse(JSON.stringify(value));

export function normalizeModelMatchPatterns(values) {
  return [...new Set((Array.isArray(values) ? values : [values])
    .map((value) => String(value || "").trim()).filter(Boolean))];
}

export function modelMatchPatterns(model) {
  const patterns = normalizeModelMatchPatterns(model?.match_patterns);
  return patterns.length ? patterns : normalizeModelMatchPatterns(model?.match_pattern);
}

export function normalizeModelPricing(pricing) {
  const rules = new Map((pricing?.rules || []).map((rule) => [rule.billing_item, rule]));
  return {
    currency: pricing?.currency || "USD",
    enabled: pricing?.enabled !== false,
    time_zone: pricing?.time_zone || "",
    off_peak_windows: normalizeOffPeakWindows(pricing?.off_peak_windows),
    rules: MODEL_BILLING_ITEMS.map(({ value }) => {
      const rule = rules.get(value) || {};
      return {
        billing_item: value,
        billing_mode: rule.billing_mode || "per_million_tokens",
        unit_price: Number(rule.unit_price || 0),
        tiers: clone(rule.tiers || []),
        off_peak_enabled: rule.off_peak_enabled === true,
        off_peak_unit_price: Number(rule.off_peak_unit_price || 0),
        off_peak_tiers: clone(rule.off_peak_tiers || []),
        enabled: rule.enabled !== false
      };
    })
  };
}

export function createModelEditorDraft(model = null, providerCode = "") {
  const patterns = modelMatchPatterns(model);
  const capabilities = clone(model?.capabilities || {});
  return {
    id: model?.id || null,
    provider_code: model?.provider_code || providerCode,
    model_key: model?.model_key || "",
    display_name: model?.display_name || "",
    description: model?.description || "",
    match_type: model?.match_type || "exact",
    match_pattern: model?.match_pattern || patterns[0] || model?.model_key || "",
    match_patterns: patterns,
    enabled: model?.enabled !== false,
    capabilities: {
      ...capabilities,
      supports_image: capabilities.supports_image === true,
      context_window: Number(model?.catalog?.context_window || capabilities.context_window || (model ? 0 : 128000))
    },
    agent_simulation: capabilities.v2_agent_simulation === true ? "enabled" : "disabled",
    pricing: normalizeModelPricing(model?.pricing)
  };
}

export function effectiveChannelModel(row) {
  if (!row?.override_model) return row?.global_model || null;
  const global = row.global_model;
  const override = row.override_model;
  const catalog = { ...global?.catalog, ...override.catalog };
  const channelWindow = [override.catalog?.context_window, override.capabilities?.context_window]
    .map(Number).find((value) => Number.isFinite(value) && value > 0);
  if (channelWindow) catalog.context_window = channelWindow;
  return {
    ...global,
    ...override,
    catalog,
    capabilities: { ...global?.capabilities, ...override.capabilities },
    pricing: override.pricing || global?.pricing || null
  };
}

export function createChannelModelEditorDraft(row, providerCode = "") {
  const model = effectiveChannelModel(row);
  const requestModel = row.request_model || model?.request_model || "";
  const override = row.override_model;
  const draft = createModelEditorDraft(model, providerCode);
  const patterns = modelMatchPatterns(override);
  draft.request_model = requestModel;
  draft.upstream_model = row.upstream_model || "";
  draft.model_key ||= requestModel;
  draft.display_name ||= requestModel;
  draft.match_type = override?.match_type || "exact";
  draft.match_pattern = override?.match_pattern || patterns[0] || requestModel;
  draft.match_patterns = patterns.length ? patterns : normalizeModelMatchPatterns(requestModel);
  const simulation = override?.capabilities?.v2_agent_simulation;
  draft.agent_simulation = simulation === true ? "enabled" : simulation === false ? "disabled" : "inherit";
  return draft;
}

export function modelCatalogText(model, modelKey = "") {
  return JSON.stringify(model?.catalog || {
    slug: modelKey,
    display_name: modelKey,
    visibility: "list",
    supported_in_api: true
  }, null, 2);
}

export function changeModelMatchType(draft) {
  if (draft.match_type === "exact") {
    if (!draft.match_patterns.length) {
      draft.match_patterns = normalizeModelMatchPatterns(draft.match_pattern);
    }
  } else {
    draft.match_pattern = draft.match_patterns[0] || draft.match_pattern || draft.model_key || "";
    draft.match_patterns = [];
  }
}

export function buildModelEditorPayload(draft, catalogText, channel = false) {
  let catalog;
  try {
    catalog = JSON.parse(catalogText || "{}");
  } catch {
    throw new Error("Catalog JSON 不是合法 JSON");
  }
  if (!catalog || typeof catalog !== "object" || Array.isArray(catalog)) {
    throw new Error("Catalog JSON 必须是 JSON 对象");
  }
  const patterns = normalizeModelMatchPatterns(draft.match_type === "exact"
    ? draft.match_patterns : draft.match_pattern);
  const capabilities = clone(draft.capabilities);
  capabilities.context_window = Number(capabilities.context_window || 0);
  if (channel && draft.agent_simulation === "inherit") {
    delete capabilities.v2_agent_simulation;
  } else {
    capabilities.v2_agent_simulation = draft.agent_simulation === "enabled";
  }
  return {
    ...(channel ? { request_model: draft.request_model, upstream_model: draft.upstream_model } : {}),
    provider_code: draft.provider_code,
    model_key: draft.model_key,
    display_name: draft.display_name,
    description: draft.description,
    match_type: draft.match_type,
    match_pattern: patterns[0] || draft.request_model || draft.model_key || "",
    match_patterns: patterns,
    catalog,
    capabilities,
    pricing: {
      currency: draft.pricing.currency || "USD",
      enabled: draft.pricing.enabled !== false,
      time_zone: draft.pricing.time_zone || "",
      off_peak_windows: draft.pricing.time_zone
        ? normalizeOffPeakWindows(draft.pricing.off_peak_windows) : [],
      rules: draft.pricing.rules.map((rule) => ({
        ...rule,
        unit_price: Number(rule.unit_price || 0),
        off_peak_unit_price: Number(rule.off_peak_unit_price || 0),
        tiers: rule.billing_mode === "tiered_tokens" ? normalizeTiers(rule.tiers) : [],
        off_peak_tiers: rule.billing_mode === "tiered_tokens" && rule.off_peak_enabled === true
          ? normalizeTiers(rule.off_peak_tiers) : []
      }))
    },
    enabled: draft.enabled !== false
  };
}

function normalizeTiers(tiers) {
  return (tiers || []).map((tier) => ({
    up_to: tier.up_to === null || tier.up_to === undefined || tier.up_to === "" ? null : Number(tier.up_to),
    unit_price: Number(tier.unit_price || 0)
  }));
}
