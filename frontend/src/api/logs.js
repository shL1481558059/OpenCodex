import { request, buildQuery } from "./client.js";

export function listLogs(params = {}) {
  return request(`/logs${buildQuery(params)}`);
}

export function getLogDetail(id) {
  return request(`/logs/${id}`);
}

export function getLogFilterOptions(field, params = {}) {
  return request(`/log-filter-options${buildQuery({ field, ...params })}`);
}

export function clearLogs() {
  return request("/logs", { method: "DELETE" });
}
