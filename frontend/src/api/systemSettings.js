import { request } from "./client.js";

export function getSystemSettings() {
  return request("/system-settings");
}

export function updateSystemSettings(body) {
  return request("/system-settings", { method: "PUT", body: JSON.stringify(body) });
}
