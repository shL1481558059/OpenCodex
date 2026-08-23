import { request } from "./client.js";

export function getWebSearchConfig() {
  return request("/web-search");
}

export function saveWebSearchConfig(body) {
  return request("/web-search", { method: "POST", body: JSON.stringify(body) });
}

export function importWebSearchConfig(body) {
  return request("/web-search/import", { method: "POST", body: JSON.stringify(body) });
}

export function testWebSearchKey(body) {
  return request("/web-search/test-key", { method: "POST", body: JSON.stringify(body) });
}
