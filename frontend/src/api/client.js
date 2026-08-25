const devApiPrefix = import.meta.env.DEV
  ? import.meta.env.BASE_URL.replace(/\/$/, "")
  : "";

let onUnauthorized = null;

export function setUnauthorizedHandler(handler) {
  onUnauthorized = handler;
}

export async function request(url, options = {}) {
  const response = await fetch(`${devApiPrefix}${url}`, {
    headers: { "Content-Type": "application/json", ...(options.headers || {}) },
    ...options,
  });

  if (response.status === 401 && onUnauthorized) {
    onUnauthorized();
  }

  const contentType = response.headers.get("content-type") || "";
  const data = contentType.includes("application/json")
    ? await response.json()
    : await response.text();

  if (!response.ok) {
    const message =
      typeof data === "string"
        ? data
        : data.ErrorMsg || data.error?.message || data.error || response.statusText;
    throw new Error(message);
  }

  // 统一 envelope 解包：所有端点返回 { ErrorCode, ErrorMsg, Data }，无条件解包 Data。
  if (data && typeof data === "object" && "ErrorCode" in data && "ErrorMsg" in data) {
    return data.Data;
  }

  return data;
}

export function buildQuery(params = {}) {
  const entries = Object.entries(params).filter(
    ([, v]) => v !== null && v !== undefined && v !== ""
  );
  if (entries.length === 0) return "";
  const search = new URLSearchParams();
  for (const [key, value] of entries) {
    search.set(key, String(value));
  }
  return `?${search.toString()}`;
}
