from __future__ import annotations


class ProxyError(Exception):
    status_code = 500
    error_type = "proxy_error"

    def __init__(self, message: str, status_code: int | None = None):
        super().__init__(message)
        if status_code is not None:
            self.status_code = status_code

    def to_response(self) -> dict[str, object]:
        return {
            "error": {
                "message": str(self),
                "type": self.error_type,
            }
        }


class BadRequestError(ProxyError):
    status_code = 400
    error_type = "bad_request"


class RoutingError(ProxyError):
    status_code = 400
    error_type = "routing_error"


class UpstreamError(ProxyError):
    error_type = "upstream_error"

    def __init__(
        self,
        message: str,
        status_code: int = 502,
        body: object | None = None,
        channel_id: str | None = None,
    ):
        super().__init__(message, status_code)
        self.body = body
        self.channel_id = channel_id

    def to_response(self) -> dict[str, object]:
        payload = super().to_response()
        if self.channel_id:
            payload["error"]["channel_id"] = self.channel_id  # type: ignore[index]
        if self.body is not None:
            payload["error"]["upstream"] = self.body  # type: ignore[index]
        return payload
