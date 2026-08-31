import test from "node:test";
import assert from "node:assert/strict";
import { reorderChannelAfterToggle } from "./channelOrdering.js";

function channel(id, owner, enabled) {
  return { id, owner_username: owner, enabled };
}

test("启用渠道只移动到当前所属用户的启用分组第一位", () => {
  const channels = [
    channel("alice-enabled-old", "alice", true),
    channel("alice-disabled", "alice", false),
    channel("bob-enabled", "bob", true)
  ];

  const result = reorderChannelAfterToggle(channels, "alice-disabled", true);

  assert.deepEqual(result.map((item) => item.id), [
    "alice-disabled",
    "alice-enabled-old",
    "bob-enabled"
  ]);
  assert.equal(result[0].enabled, true);
});

test("禁用渠道只移动到当前所属用户的禁用分组第一位", () => {
  const channels = [
    channel("alice-enabled-old", "alice", true),
    channel("alice-enabled-new", "alice", true),
    channel("alice-disabled-old", "alice", false),
    channel("bob-enabled", "bob", true)
  ];

  const result = reorderChannelAfterToggle(channels, "alice-enabled-new", false);

  assert.deepEqual(result.map((item) => item.id), [
    "alice-enabled-old",
    "alice-enabled-new",
    "alice-disabled-old",
    "bob-enabled"
  ]);
  assert.equal(result[1].enabled, false);
});

test("没有其他同状态渠道时仍保持所属用户分组顺序", () => {
  const channels = [
    channel("alice-enabled", "alice", true),
    channel("bob-disabled", "bob", false),
    channel("charlie-enabled", "charlie", true)
  ];

  const result = reorderChannelAfterToggle(channels, "bob-disabled", true);

  assert.deepEqual(result.map((item) => item.id), [
    "alice-enabled",
    "bob-disabled",
    "charlie-enabled"
  ]);
});

test("找不到目标渠道时不改变原列表", () => {
  const channels = [channel("channel-1", "alice", true)];

  assert.deepEqual(
    reorderChannelAfterToggle(channels, "missing", false),
    channels
  );
});
