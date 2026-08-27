// 峰谷计费的共享纯逻辑：模型价格页与渠道价格页共用，避免两份实现漂移。
// 这里的判定只用于界面提示，实际计费由后端按价格快照完成。

export const OFF_PEAK_WEEKDAYS = [
  { value: 1, label: "周一" },
  { value: 2, label: "周二" },
  { value: 3, label: "周三" },
  { value: 4, label: "周四" },
  { value: 5, label: "周五" },
  { value: 6, label: "周六" },
  { value: 7, label: "周日" }
];

export const OFF_PEAK_TIME_ZONES = [
  "UTC",
  "Asia/Shanghai",
  "Asia/Hong_Kong",
  "Asia/Singapore",
  "Asia/Tokyo",
  "Asia/Seoul",
  "Asia/Kolkata",
  "Europe/London",
  "Europe/Berlin",
  "Europe/Moscow",
  "America/New_York",
  "America/Chicago",
  "America/Los_Angeles",
  "Australia/Sydney"
];

export const OFF_PEAK_TIME_OPTIONS = buildTimeOptions();

export function offPeakConfigured(pricing) {
  return Boolean(pricing?.time_zone) && (pricing?.off_peak_windows || []).length > 0;
}

export function emptyOffPeakWindow() {
  return { start: "22:00", end: "06:00", days: [] };
}

export function normalizeOffPeakWindows(windows) {
  return (windows || []).map((window) => ({
    start: String(window?.start || "").trim(),
    end: String(window?.end || "").trim(),
    days: Array.isArray(window?.days) ? window.days.map((day) => Number(day)) : []
  }));
}

export function crossesMidnight(window) {
  const start = parseMinute(window?.start);
  const end = parseMinute(window?.end);
  return start !== null && end !== null && start > end;
}

export function describeWindow(window) {
  const days = (window?.days || []).length === 0
    ? "每天"
    : OFF_PEAK_WEEKDAYS
        .filter((day) => window.days.includes(day.value))
        .map((day) => day.label)
        .join("、");
  return `${window?.start || "--:--"} - ${window?.end || "--:--"} ${days}`;
}

// 界面提示用：按所选时区判断"此刻"落在峰段还是谷段。
export function currentPhaseLabel(pricing, now = new Date()) {
  if (!offPeakConfigured(pricing)) {
    return "未启用峰谷，全时段按基础单价";
  }

  const local = localTimeParts(pricing.time_zone, now);
  if (!local) {
    return "时区无效，将按基础单价计费";
  }

  const hit = (pricing.off_peak_windows || []).find((window) => matchesWindow(window, local));
  return hit
    ? `当前谷段（${local.text}，${describeWindow(hit)}）`
    : `当前峰段（${local.text}）`;
}

export function matchesWindow(window, local) {
  const start = parseMinute(window?.start);
  const end = parseMinute(window?.end);
  if (start === null || end === null || start === end) {
    return false;
  }

  const days = Array.isArray(window?.days) && window.days.length > 0
    ? window.days.map((day) => Number(day))
    : [1, 2, 3, 4, 5, 6, 7];
  if (start < end) {
    return days.includes(local.day) && local.minute >= start && local.minute < end;
  }

  // 跨午夜窗口按"起始日"归属：当日 start 之后，或次日 end 之前。
  if (local.minute >= start) {
    return days.includes(local.day);
  }
  return local.minute < end && days.includes(previousDay(local.day));
}

export function localTimeParts(timeZone, now = new Date()) {
  try {
    const parts = new Intl.DateTimeFormat("en-US", {
      timeZone,
      hourCycle: "h23",
      weekday: "short",
      hour: "2-digit",
      minute: "2-digit"
    }).formatToParts(now);
    const lookup = {};
    for (const part of parts) {
      lookup[part.type] = part.value;
    }
    const day = WEEKDAY_BY_SHORT_NAME[lookup.weekday];
    const hour = Number(lookup.hour);
    const minute = Number(lookup.minute);
    if (!day || Number.isNaN(hour) || Number.isNaN(minute)) {
      return null;
    }
    const normalizedHour = hour % 24;
    const text = `${String(normalizedHour).padStart(2, "0")}:${String(minute).padStart(2, "0")} ${
      OFF_PEAK_WEEKDAYS.find((item) => item.value === day)?.label || ""
    }`;
    return { day, minute: (normalizedHour * 60) + minute, text };
  } catch {
    return null;
  }
}

export function parseMinute(value) {
  const text = String(value || "").trim();
  if (!/^\d{2}:\d{2}$/.test(text)) {
    return null;
  }
  const hour = Number(text.slice(0, 2));
  const minute = Number(text.slice(3));
  if (minute > 59) {
    return null;
  }
  if (hour === 24) {
    return minute === 0 ? 1440 : null;
  }
  return hour > 23 ? null : (hour * 60) + minute;
}

export function applyOffPeakDiscount(rules, discount) {
  const ratio = Number(discount);
  if (!Number.isFinite(ratio) || ratio < 0) {
    return 0;
  }

  let changed = 0;
  for (const rule of rules || []) {
    if (rule.off_peak_enabled !== true) {
      continue;
    }
    rule.off_peak_unit_price = roundPrice(Number(rule.unit_price || 0) * ratio);
    changed += 1;
  }
  return changed;
}

function roundPrice(value) {
  return Number(value.toFixed(8));
}

function previousDay(day) {
  return day === 1 ? 7 : day - 1;
}

function buildTimeOptions() {
  const options = [];
  for (let minute = 0; minute < 1440; minute += 30) {
    options.push(
      `${String(Math.floor(minute / 60)).padStart(2, "0")}:${String(minute % 60).padStart(2, "0")}`
    );
  }
  options.push("24:00");
  return options;
}

const WEEKDAY_BY_SHORT_NAME = {
  Mon: 1,
  Tue: 2,
  Wed: 3,
  Thu: 4,
  Fri: 5,
  Sat: 6,
  Sun: 7
};
