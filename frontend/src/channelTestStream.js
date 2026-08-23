function getDevApiPrefix() {
  if (!import.meta.env?.DEV) return "";
  return import.meta.env.BASE_URL.replace(/\/$/, "");
}

export async function streamChannelTest(payload, onEvent, options = {}) {
  const response = await fetch(`${getDevApiPrefix()}/test-channel/stream`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(payload),
    signal: options.signal
  });
  if (!response.ok) {
    throw new Error((await response.text()) || response.statusText);
  }
  if (!response.body) {
    throw new Error("浏览器不支持流式响应读取");
  }

  const reader = response.body.getReader();
  const decoder = new TextDecoder();
  let buffer = "";
  while (true) {
    const { value, done } = await reader.read();
    if (done) break;
    buffer += decoder.decode(value, { stream: true });
    buffer = consumeSseBuffer(buffer, onEvent);
  }
  buffer += decoder.decode();
  consumeSseBuffer(`${buffer}\n\n`, onEvent);
}

export function consumeSseBuffer(buffer, onEvent) {
  let remaining = buffer;
  while (true) {
    const separator = remaining.indexOf("\n\n");
    if (separator === -1) {
      return remaining;
    }
    const chunk = remaining.slice(0, separator);
    remaining = remaining.slice(separator + 2);
    const event = parseSseChunk(chunk);
    if (event) onEvent(event);
  }
}

export function parseSseChunk(chunk) {
  const lines = chunk.split(/\r?\n/);
  let eventName = "message";
  const data = [];
  for (const line of lines) {
    if (line.startsWith("event:")) {
      eventName = line.slice("event:".length).trim();
    } else if (line.startsWith("data:")) {
      data.push(line.slice("data:".length).trimStart());
    }
  }
  if (data.length === 0) return null;
  const text = data.join("\n");
  if (text === "[DONE]") {
    return { event: eventName, data: text };
  }
  try {
    return { event: eventName, data: JSON.parse(text), raw: text };
  } catch {
    return { event: eventName, data: text, raw: text };
  }
}
