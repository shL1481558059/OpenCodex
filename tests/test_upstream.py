from __future__ import annotations

import io
import http.client
import unittest
import urllib.error
from email.message import Message
from unittest.mock import call, patch

from opencodex_proxy.errors import UpstreamError
from opencodex_proxy.upstream import post_upstream, stream_upstream


class FakeResponse:
    def __init__(self, body: str, lines: list[bytes] | None = None):
        self.body = body.encode("utf-8")
        self.lines = lines if lines is not None else [self.body]

    def __enter__(self):
        return self

    def __exit__(self, exc_type, exc, traceback):
        return False

    def __iter__(self):
        return iter(self.lines)

    def read(self):
        return self.body


def make_channel(retry_count: int = 3) -> dict:
    return {
        "id": "chat",
        "type": "chat",
        "baseurl": "https://example.test/v1",
        "retry_count": retry_count,
    }


def http_error(status_code: int, body: str = "{}", headers: Message | None = None):
    return urllib.error.HTTPError(
        "https://example.test/v1/chat/completions",
        status_code,
        "error",
        headers or Message(),
        io.BytesIO(body.encode("utf-8")),
    )


class UpstreamRetryTests(unittest.TestCase):
    @patch("opencodex_proxy.upstream.time.sleep")
    @patch("opencodex_proxy.upstream.urllib.request.urlopen")
    def test_post_upstream_retries_url_errors_until_success(self, mock_urlopen, mock_sleep):
        mock_urlopen.side_effect = [
            urllib.error.URLError("temporary"),
            urllib.error.URLError("temporary"),
            FakeResponse('{"ok": true}'),
        ]

        result = post_upstream(make_channel(retry_count=3), {"model": "m"}, None, 30)

        self.assertEqual(result, {"ok": True})
        self.assertEqual(mock_urlopen.call_count, 3)
        mock_sleep.assert_has_calls([call(0.5), call(1.0)])

    @patch("opencodex_proxy.upstream.time.sleep")
    @patch("opencodex_proxy.upstream.urllib.request.urlopen")
    def test_post_upstream_retries_remote_disconnected_until_success(
        self, mock_urlopen, mock_sleep
    ):
        mock_urlopen.side_effect = [
            http.client.RemoteDisconnected("remote end closed connection without response"),
            FakeResponse('{"ok": true}'),
        ]

        result = post_upstream(make_channel(retry_count=1), {"model": "m"}, None, 30)

        self.assertEqual(result, {"ok": True})
        self.assertEqual(mock_urlopen.call_count, 2)
        mock_sleep.assert_called_once_with(0.5)

    @patch("opencodex_proxy.upstream.time.sleep")
    @patch("opencodex_proxy.upstream.urllib.request.urlopen")
    def test_post_upstream_retries_retryable_http_statuses(self, mock_urlopen, mock_sleep):
        for status_code in (429, 500):
            with self.subTest(status_code=status_code):
                mock_urlopen.reset_mock()
                mock_sleep.reset_mock()
                mock_urlopen.side_effect = [
                    http_error(status_code),
                    FakeResponse('{"ok": true}'),
                ]

                result = post_upstream(make_channel(retry_count=1), {"model": "m"}, None, 30)

                self.assertEqual(result, {"ok": True})
                self.assertEqual(mock_urlopen.call_count, 2)
                mock_sleep.assert_called_once_with(0.5)

    @patch("opencodex_proxy.upstream.time.sleep")
    @patch("opencodex_proxy.upstream.urllib.request.urlopen")
    def test_post_upstream_does_not_retry_non_retryable_http_status(self, mock_urlopen, mock_sleep):
        mock_urlopen.side_effect = http_error(400, '{"error":"bad request"}')

        with self.assertRaises(UpstreamError) as raised:
            post_upstream(make_channel(retry_count=3), {"model": "m"}, None, 30)

        self.assertEqual(raised.exception.status_code, 400)
        self.assertEqual(mock_urlopen.call_count, 1)
        mock_sleep.assert_not_called()

    @patch("opencodex_proxy.upstream.time.sleep")
    @patch("opencodex_proxy.upstream.urllib.request.urlopen")
    def test_post_upstream_retry_count_zero_disables_retries(self, mock_urlopen, mock_sleep):
        mock_urlopen.side_effect = urllib.error.URLError("temporary")

        with self.assertRaises(UpstreamError):
            post_upstream(make_channel(retry_count=0), {"model": "m"}, None, 30)

        self.assertEqual(mock_urlopen.call_count, 1)
        mock_sleep.assert_not_called()

    @patch("opencodex_proxy.upstream.time.sleep")
    @patch("opencodex_proxy.upstream.urllib.request.urlopen")
    def test_post_upstream_remote_disconnected_becomes_upstream_error(
        self, mock_urlopen, mock_sleep
    ):
        mock_urlopen.side_effect = http.client.RemoteDisconnected(
            "remote end closed connection without response"
        )

        with self.assertRaises(UpstreamError) as raised:
            post_upstream(make_channel(retry_count=0), {"model": "m"}, None, 30)

        self.assertEqual(raised.exception.status_code, 502)
        self.assertEqual(raised.exception.channel_id, "chat")
        self.assertIn("remote end closed connection", str(raised.exception))
        self.assertEqual(mock_urlopen.call_count, 1)
        mock_sleep.assert_not_called()

    @patch("opencodex_proxy.upstream.time.sleep")
    @patch("opencodex_proxy.upstream.urllib.request.urlopen")
    def test_post_upstream_raises_last_error_after_retries_are_exhausted(
        self, mock_urlopen, mock_sleep
    ):
        mock_urlopen.side_effect = [
            http_error(500, '{"error":"first"}'),
            http_error(502, '{"error":"last"}'),
        ]

        with self.assertRaises(UpstreamError) as raised:
            post_upstream(make_channel(retry_count=1), {"model": "m"}, None, 30)

        self.assertEqual(raised.exception.status_code, 502)
        self.assertEqual(raised.exception.body, {"error": "last"})
        self.assertEqual(mock_urlopen.call_count, 2)
        mock_sleep.assert_called_once_with(0.5)

    @patch("opencodex_proxy.upstream.time.sleep")
    @patch("opencodex_proxy.upstream.urllib.request.urlopen")
    def test_post_upstream_uses_retry_after_delay_when_available(
        self, mock_urlopen, mock_sleep
    ):
        headers = Message()
        headers["Retry-After"] = "7"
        mock_urlopen.side_effect = [
            http_error(429, headers=headers),
            FakeResponse('{"ok": true}'),
        ]

        result = post_upstream(make_channel(retry_count=1), {"model": "m"}, None, 30)

        self.assertEqual(result, {"ok": True})
        mock_sleep.assert_called_once_with(7.0)

    @patch("opencodex_proxy.upstream.time.sleep")
    @patch("opencodex_proxy.upstream.urllib.request.urlopen")
    def test_stream_upstream_retries_before_response_starts(self, mock_urlopen, mock_sleep):
        mock_urlopen.side_effect = [
            urllib.error.URLError("temporary"),
            FakeResponse("", lines=[b"data: first\n", b"\n"]),
        ]

        lines = list(stream_upstream(make_channel(retry_count=1), {"model": "m"}, None, 30))

        self.assertEqual(lines, ["data: first\n", "\n"])
        self.assertEqual(mock_urlopen.call_count, 2)
        mock_sleep.assert_called_once_with(0.5)


if __name__ == "__main__":
    unittest.main()
