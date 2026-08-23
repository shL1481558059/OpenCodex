import { request, createEventSource } from "./client.js";

export function getActiveChannels() {
  return request("/monitor/active-channels");
}

export function getRecentErrors() {
  return request("/monitor/recent-errors");
}

export function streamActiveChannels() {
  return createEventSource("/monitor/active-channels/stream");
}

export function streamRecentErrors() {
  return createEventSource("/monitor/recent-errors/stream");
}
