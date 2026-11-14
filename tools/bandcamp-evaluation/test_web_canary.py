import argparse
from email.message import Message
import json
import unittest
from unittest.mock import Mock
import web_canary


class WebCanaryTest(unittest.TestCase):
    def args(self, **values):
        return argparse.Namespace(**dict(live=False, authorized_read_only=None, identity_file=None, max_requests=1) | values)

    def test_default_never_opens_network(self):
        opener = Mock()
        self.assertEqual(0, web_canary.run(self.args(), opener)["requests"])
        opener.open.assert_not_called()

    def test_live_requires_authorization_and_bounds(self):
        for args in (self.args(live=True), self.args(live=True, authorized_read_only="I_HAVE_SEPARATE_AUTHORIZATION", max_requests=4)):
            with self.assertRaises(ValueError):
                web_canary.run(args, Mock())

    def test_refusal_stops_and_emits_no_response_secrets(self):
        response = Mock()
        response.__enter__ = Mock(return_value=response)
        response.__exit__ = Mock(return_value=False)
        response.code = 403
        response.headers = Message()
        response.headers.add_header("Set-Cookie", "identity=identity-53-private; Secure; Path=/")
        response.read.return_value = b"password-53-private verification-53-private generated-53-private"
        opener = Mock()
        opener.open.return_value = response
        result = web_canary.run(self.args(live=True, authorized_read_only="I_HAVE_SEPARATE_AUTHORIZATION", max_requests=3), opener)
        self.assertEqual(1, result["requests"])
        self.assertEqual("ChallengeOrUnrecognized", result["observations"][0]["outcome"])
        self.assertNotIn("53-private", json.dumps(result))
        opener.open.assert_called_once()


if __name__ == "__main__":
    unittest.main()
