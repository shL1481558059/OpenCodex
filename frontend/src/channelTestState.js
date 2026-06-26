export function createChannelTestState() {
  return {
    phase: "connecting",
    response: { output_text: "" },
    details: null,
    raw_events: [],
    hasReceivedEvent: false,
    error: "",
    body: null,
    duration_ms: undefined
  };
}

export function applyChannelTestStreamEvent(result, event) {
  if (!result) return;
  result.raw_events = [...(result.raw_events || []), event].slice(-20);
  result.hasReceivedEvent = true;
  if (result.phase === "connecting") {
    result.phase = "streaming";
  }

  if (event.event === "channel_test.error") {
    result.phase = "error";
    result.body = event.data;
    result.error = extractErrorMessage(event.data) || "上游请求失败";
    return;
  }

  if (event.event === "channel_test.completed") {
    applyChannelTestDetails(result, event.data);
    return;
  }

  const data = event.data;
  if (!data || typeof data !== "object") return;
  if (data.type === "response.output_text.delta" && typeof data.delta === "string") {
    result.response.output_text = `${result.response.output_text || ""}${data.delta}`;
  }
  if (data.type === "response.completed" && data.response) {
    result.response = {
      ...data.response,
      output_text: result.response.output_text || extractResponseText(data.response)
    };
    result.phase = data.response.error ? "error" : "success";
    if (data.response.error) {
      result.error = extractErrorMessage(data.response) || "上游请求失败";
      result.body = data.response;
    }
  }
}

function applyChannelTestDetails(result, data) {
  if (!data || typeof data !== "object") return;
  result.details = data;
  if (Number.isFinite(data.duration_ms)) {
    result.duration_ms = data.duration_ms;
  }

  if (data.upstream_response && typeof data.upstream_response === "object") {
    result.response = {
      ...data.upstream_response,
      output_text: result.response.output_text || extractResponseText(data.upstream_response)
    };
  }

  if (data.error_response) {
    result.body = data.error_response;
  }

  const statusCode = Number(data.status_code || 0);
  const errorMessage = extractErrorMessage(data.error_response) || extractErrorMessage(data);
  if (statusCode >= 400 || errorMessage) {
    result.phase = "error";
    result.error = errorMessage || result.error || "上游请求失败";
    return;
  }

  result.phase = "success";
}

export function finalizeChannelTestResult(result) {
  if (!result) return;
  if (result.phase === "connecting" || result.phase === "streaming") {
    const responseText = extractResponseText(result.response);
    if (responseText) {
      result.phase = "success";
      result.response.output_text = responseText;
      return;
    }

    if (!result.hasReceivedEvent) {
      result.phase = "error";
      result.error = "未收到上游响应事件";
      return;
    }

    result.phase = "success";
  }
}

export function getChannelTestAlertTitle(result) {
  if (!result) return "";
  switch (result.phase) {
    case "connecting":
    case "streaming":
      return "正在测试连接";
    case "error":
      return "连接测试失败";
    case "success":
      return "连接测试成功";
    default:
      return "";
  }
}

export function getChannelTestAlertType(result) {
  if (!result) return "info";
  switch (result.phase) {
    case "error":
      return "error";
    case "success":
      return "success";
    default:
      return "info";
  }
}

export function formatChannelTestResult(result) {
  if (!result) return "";
  if (result.phase === "connecting") {
    return "正在建立连接，等待上游响应...";
  }
  if (result.phase === "streaming") {
    const responseText = extractResponseText(result.response);
    return responseText || "已连接到上游，正在接收响应...";
  }
  if (result.phase === "error") {
    const details = extractErrorMessage(result.body);
    return [result.error || "上游请求失败", details].filter(Boolean).join("\n");
  }
  const responseText = extractResponseText(result.response);
  if (responseText) return responseText;
  return "连接已打通，但响应中没有可展示的文本内容。";
}

function extractErrorMessage(value) {
  if (!value) return "";
  if (typeof value === "string") return value;
  if (typeof value.error === "string") return value.error;
  if (value.error?.message) return String(value.error.message);
  if (value.message) return String(value.message);
  return "";
}

function extractResponseText(response) {
  if (!response || typeof response !== "object") return "";
  const outputText = String(response.output_text || "").trim();
  if (outputText) return outputText;
  const choiceContent = response.choices?.[0]?.message?.content;
  const choiceText = stringifyContent(choiceContent).trim();
  if (choiceText) return choiceText;
  const messageText = stringifyContent(response.content).trim();
  if (messageText) return messageText;
  const output = Array.isArray(response.output) ? response.output : [];
  const parts = [];
  for (const item of output) {
    const content = Array.isArray(item?.content) ? item.content : [];
    for (const block of content) {
      const text = block?.text || block?.output_text;
      if (text) parts.push(String(text));
    }
  }
  return parts.join("\n").trim();
}

function stringifyContent(content) {
  if (typeof content === "string") return content;
  if (!Array.isArray(content)) return "";
  return content.map((item) => (typeof item === "string" ? item : item?.text || "")).filter(Boolean).join("\n");
}
