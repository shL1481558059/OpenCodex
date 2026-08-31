// 渠道列表已由后端按 owner、启用状态、更新时间和 ID 排序。
// 启停单个渠道后，该渠道就是当前状态分组中最新更新的项，因此只需局部移到对应分组首位。
export function reorderChannelAfterToggle(channels, channelId, enabled) {
  if (!Array.isArray(channels)) {
    return channels;
  }

  const targetIndex = channels.findIndex((channel) => channel?.id === channelId);
  if (targetIndex < 0) {
    return channels;
  }

  const target = {
    ...channels[targetIndex],
    enabled: enabled === true
  };
  const remaining = channels.filter((_, index) => index !== targetIndex);
  const ownerUsername = ownerOf(target);
  const targetEnabled = target.enabled !== false;
  const ownerIndexes = remaining.reduce((indexes, channel, index) => {
    if (ownerOf(channel) === ownerUsername) {
      indexes.push(index);
    }
    return indexes;
  }, []);

  let insertIndex;
  if (ownerIndexes.length > 0) {
    const sameStateIndex = remaining.findIndex(
      (channel) => ownerOf(channel) === ownerUsername && isEnabled(channel) === targetEnabled
    );

    if (sameStateIndex >= 0) {
      insertIndex = sameStateIndex;
    } else if (targetEnabled) {
      insertIndex = ownerIndexes[0];
    } else {
      insertIndex = ownerIndexes[ownerIndexes.length - 1] + 1;
    }
  } else {
    insertIndex = remaining.findIndex(
      (channel) => compareOwnerNames(ownerUsername, ownerOf(channel)) < 0
    );
    if (insertIndex < 0) {
      insertIndex = remaining.length;
    }
  }

  return [
    ...remaining.slice(0, insertIndex),
    target,
    ...remaining.slice(insertIndex)
  ];
}

function ownerOf(channel) {
  return String(channel?.owner_username || "");
}

function isEnabled(channel) {
  return channel?.enabled !== false;
}

function compareOwnerNames(left, right) {
  if (left === right) {
    return 0;
  }

  return left < right ? -1 : 1;
}
