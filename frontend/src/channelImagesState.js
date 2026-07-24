export const DEFAULT_IMAGES_API_DIALECT = "openai";
export const IMAGES_API_DIALECTS = ["openai", "xai"];

export function isImagesChannel(channel) {
  return channel?.type === "images";
}

export function canUseChatStreamTest(channel) {
  return !isImagesChannel(channel);
}

export function applyChannelTypeContract(draft, compat) {
  if (!isImagesChannel(draft)) return;
  draft.retry_count = 0;
  if (!IMAGES_API_DIALECTS.includes(compat.images_api_dialect)) {
    compat.images_api_dialect = DEFAULT_IMAGES_API_DIALECT;
  }
}

export function buildImagesCompat(compat) {
  const dialect = compat?.images_api_dialect;
  if (!IMAGES_API_DIALECTS.includes(dialect)) {
    throw new Error("图片 API 方言必须是 openai 或 xai");
  }
  const {
    enable_apply_patch_prompt_compat: _applyPatch,
    preserve_thinking_history: _thinkingHistory,
    drop_tool_types: _dropToolTypes,
    ...commonCompat
  } = compat || {};
  const result = { ...commonCompat, images_api_dialect: dialect };
  for (const [key, value] of Object.entries(result)) {
    if (Array.isArray(value) && value.length === 0) delete result[key];
    if (isPlainObject(value) && Object.keys(value).length === 0) delete result[key];
  }
  return result;
}

function isPlainObject(value) {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}
