import { request } from "./client.js";

export function getActiveChannels() {
  return request("/monitor/active-channels");
}

export function getRecentErrors() {
  return request("/monitor/recent-errors");
}
