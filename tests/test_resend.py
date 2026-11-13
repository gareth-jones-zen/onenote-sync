"""A OneNote PATCH or POST that may have landed isn't resent (13 Nov 2026)."""

import io
import pathlib
import sys
import unittest
import urllib.error
from unittest import mock

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1] / "src"))
from onenote_sync import onenote  # noqa: E402


def http_error(code):
    return urllib.error.HTTPError("https://graph", code, "x", {}, io.BytesIO(b""))


class Response:
    def __enter__(self):
        return self

    def __exit__(self, *args):
        return False

    def read(self):
        return b"{}"


class Fake:
    def __init__(self, *answers):
        self.answers, self.calls = list(answers), 0

    def __call__(self, request, timeout=None):
        self.calls += 1
        answer = self.answers.pop(0)
        if isinstance(answer, BaseException):
            raise answer
        return Response()


def notebook():
    book = onenote.Notebook.__new__(onenote.Notebook)
    book.graph = mock.Mock(token=lambda scopes: "t")
    book.root = "/me/onenote"
    return book


def raw(fake, method, url="/me/onenote/pages/1/content"):
    with mock.patch.object(onenote.urllib.request, "urlopen", fake), mock.patch.object(onenote.time, "sleep", lambda s: None):
        return notebook()._raw(url, method, b"{}" if method != "GET" else None)


class Resend(unittest.TestCase):
    def test_get_retried(self):
        fake = Fake(http_error(502), b"")
        raw(fake, "GET")
        self.assertEqual(fake.calls, 2)

    def test_patch_not_resent(self):
        for error in (http_error(502), http_error(504), TimeoutError(), urllib.error.URLError(ConnectionResetError())):
            fake = Fake(error, b"")
            with self.assertRaises((RuntimeError, urllib.error.URLError, TimeoutError)) as caught:
                raw(fake, "PATCH")
            self.assertEqual(fake.calls, 1, error)
            if isinstance(caught.exception, RuntimeError):
                self.assertIn("not resent", str(caught.exception))

    def test_create_not_resent(self):
        fake = Fake(http_error(504), b"")
        with self.assertRaises(RuntimeError):
            raw(fake, "POST", "/me/onenote/sections/1/pages")
        self.assertEqual(fake.calls, 1)

    def test_throttled_patch_resent(self):
        fake = Fake(http_error(429), b"")
        raw(fake, "PATCH")
        self.assertEqual(fake.calls, 2)


if __name__ == "__main__":
    unittest.main()
