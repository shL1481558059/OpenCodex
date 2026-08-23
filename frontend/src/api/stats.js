import { request, buildQuery } from "./client.js";

export function getStatsSummary(params = {}) {
  return request(`/stats/summary${buildQuery(params)}`);
}

export function getStatsTimeseries(params = {}) {
  return request(`/stats/timeseries${buildQuery(params)}`);
}

export function getStatsModelDistribution(params = {}) {
  return request(`/stats/model-distribution${buildQuery(params)}`);
}

export function getStatsErrorDistribution(params = {}) {
  return request(`/stats/error-distribution${buildQuery(params)}`);
}

export function getStats(params = {}) {
  return request(`/stats${buildQuery(params)}`);
}
