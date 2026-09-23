function toFiniteNumber(value) {
  if (value === null || value === undefined || value === "") return null;
  const number = Number(value);
  return Number.isFinite(number) ? number : null;
}

/**
 * 端到端输出速度：输出 token 数除以请求总耗时。
 * 所有请求类型统一使用该口径，包含排队、首 token 延迟与工具执行时间。
 */
export function computeEndToEndTps(log) {
  const outputTokens = toFiniteNumber(log?.output_tokens);
  const durationMs = toFiniteNumber(log?.duration_ms);
  if (outputTokens === null || outputTokens <= 0) return null;
  if (durationMs === null || durationMs <= 0) return null;
  return outputTokens / (durationMs / 1000);
}

/**
 * 生成阶段速度：输出 token 数除以扣除首 token 延迟后的耗时。
 * 仅在 TTFT 有效且小于总耗时时可计算，非流式请求没有该值。
 */
export function computeDecodeTps(log) {
  const outputTokens = toFiniteNumber(log?.output_tokens);
  const durationMs = toFiniteNumber(log?.duration_ms);
  const ttftMs = toFiniteNumber(log?.ttft_ms);
  if (outputTokens === null || outputTokens <= 0) return null;
  if (durationMs === null || durationMs <= 0) return null;
  if (ttftMs === null || ttftMs <= 0 || ttftMs >= durationMs) return null;
  return outputTokens / ((durationMs - ttftMs) / 1000);
}

export function formatTps(value) {
  const number = toFiniteNumber(value);
  if (number === null || number <= 0) return "-";
  return `${number.toFixed(1)} tok/s`;
}

export function formatEndToEndTps(log) {
  return formatTps(computeEndToEndTps(log));
}

export function formatDecodeTps(log) {
  return formatTps(computeDecodeTps(log));
}
