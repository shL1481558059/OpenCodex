import test from "node:test";
import assert from "node:assert/strict";
import {
  applyOffPeakDiscount,
  crossesMidnight,
  currentPhaseLabel,
  localTimeParts,
  matchesWindow,
  offPeakConfigured,
  parseMinute
} from "./pricingOffPeak.js";

test("parseMinute 只接受 HH:mm，24:00 表示当日结束", () => {
  assert.equal(parseMinute("00:00"), 0);
  assert.equal(parseMinute("22:30"), 1350);
  assert.equal(parseMinute("24:00"), 1440);
  assert.equal(parseMinute("2200"), null);
  assert.equal(parseMinute("22:70"), null);
  assert.equal(parseMinute("25:00"), null);
  assert.equal(parseMinute("24:30"), null);
  assert.equal(parseMinute(""), null);
});

test("普通窗口按半开区间命中", () => {
  const window = { start: "22:00", end: "23:00", days: [] };
  assert.equal(matchesWindow(window, { day: 1, minute: 1320 }), true);
  assert.equal(matchesWindow(window, { day: 1, minute: 1379 }), true);
  assert.equal(matchesWindow(window, { day: 1, minute: 1380 }), false);
  assert.equal(matchesWindow(window, { day: 1, minute: 1319 }), false);
});

test("跨午夜窗口按起始日归属", () => {
  const window = { start: "22:00", end: "06:00", days: [1] };
  // 周一 22:30 命中
  assert.equal(matchesWindow(window, { day: 1, minute: 1350 }), true);
  // 周二 01:00 属于周一晚间那一段
  assert.equal(matchesWindow(window, { day: 2, minute: 60 }), true);
  // 周一 01:00 不属于（起始日是周日，未选中）
  assert.equal(matchesWindow(window, { day: 1, minute: 60 }), false);
  // 周二 22:30 不属于（周二晚间未选中）
  assert.equal(matchesWindow(window, { day: 2, minute: 1350 }), false);
  assert.equal(crossesMidnight(window), true);
});

test("星期为空表示每天", () => {
  const window = { start: "01:00", end: "02:00", days: [] };
  for (let day = 1; day <= 7; day += 1) {
    assert.equal(matchesWindow(window, { day, minute: 90 }), true);
  }
});

test("offPeakConfigured 要求时区与窗口同时存在", () => {
  assert.equal(offPeakConfigured(null), false);
  assert.equal(offPeakConfigured({ time_zone: "UTC", off_peak_windows: [] }), false);
  assert.equal(offPeakConfigured({ time_zone: "", off_peak_windows: [{}] }), false);
  assert.equal(offPeakConfigured({ time_zone: "UTC", off_peak_windows: [{}] }), true);
});

test("localTimeParts 按时区换算，时区非法返回 null", () => {
  const instant = new Date("2026-01-05T14:30:00Z");
  assert.deepEqual(
    { day: localTimeParts("UTC", instant).day, minute: localTimeParts("UTC", instant).minute },
    { day: 1, minute: 870 });
  assert.deepEqual(
    {
      day: localTimeParts("Asia/Shanghai", instant).day,
      minute: localTimeParts("Asia/Shanghai", instant).minute
    },
    { day: 1, minute: 1350 });
  assert.equal(localTimeParts("Not/AZone", instant), null);
});

test("currentPhaseLabel 按时区给出峰谷提示", () => {
  const pricing = {
    time_zone: "Asia/Shanghai",
    off_peak_windows: [{ start: "22:00", end: "24:00", days: [] }]
  };
  const instant = new Date("2026-01-05T14:30:00Z");

  assert.match(currentPhaseLabel(pricing, instant), /当前谷段/);
  assert.match(
    currentPhaseLabel({ ...pricing, time_zone: "UTC" }, instant),
    /当前峰段/);
  assert.match(
    currentPhaseLabel({ time_zone: "", off_peak_windows: [] }, instant),
    /未启用峰谷/);
  assert.match(
    currentPhaseLabel({ ...pricing, time_zone: "Not/AZone" }, instant),
    /时区无效/);
});

test("折扣填充只改开启峰谷的规则", () => {
  const rules = [
    { billing_item: "input", unit_price: 1, off_peak_enabled: true, off_peak_unit_price: 0 },
    { billing_item: "cache_read", unit_price: 0.1, off_peak_enabled: false, off_peak_unit_price: 0 }
  ];

  const changed = applyOffPeakDiscount(rules, 0.5);

  assert.equal(changed, 1);
  assert.equal(rules[0].off_peak_unit_price, 0.5);
  assert.equal(rules[1].off_peak_unit_price, 0);
});
