from __future__ import annotations

from dataclasses import dataclass
from typing import Any, Literal, Optional, Union


PatchOpType = Literal["add", "delete", "update", "replace"]

_TOOL_ACTIONS = {
    "apply_patch_add_file": "add",
    "apply_patch_delete_file": "delete",
    "apply_patch_update_file": "update",
    "apply_patch_replace_file": "replace",
}

_BATCH_ACTIONS = {
    "add_file": "add",
    "delete_file": "delete",
    "update_file": "update",
    "replace_file": "replace",
}


@dataclass(frozen=True)
class FileStarted:
    path: str
    op: PatchOpType


@dataclass(frozen=True)
class ContentProgress:
    path: str
    chars: int


@dataclass(frozen=True)
class FileFinished:
    path: str


PatchSemanticEvent = Union[FileStarted, ContentProgress, FileFinished]


def action_from_tool_name(name: str) -> Optional[PatchOpType]:
    return _TOOL_ACTIONS.get(name.replace("-", "_"))  # type: ignore[return-value]


def action_from_batch_type(value: Any) -> Optional[PatchOpType]:
    return _BATCH_ACTIONS.get(str(value or ""))  # type: ignore[return-value]


def valid_preview_path(value: Any) -> Optional[str]:
    if not isinstance(value, str):
        return None
    path = value.strip()
    if not path or "\n" in path or "\r" in path:
        return None
    return path


def semantic_events_from_operation(operation: dict[str, Any]) -> list[PatchSemanticEvent]:
    action = action_from_batch_type(operation.get("type"))
    path = valid_preview_path(operation.get("path"))
    if action is None or path is None:
        return []
    events: list[PatchSemanticEvent] = [FileStarted(path, action)]
    if action in {"add", "replace"}:
        content = operation.get("content")
        if isinstance(content, str):
            events.append(ContentProgress(path, len(content)))
    events.append(FileFinished(path))
    return events
