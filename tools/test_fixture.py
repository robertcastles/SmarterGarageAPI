"""Black-box tests for local deterministic mode. Uses only Python's standard library."""
import json
import ssl
import sys
import time
import unittest
import urllib.error
import urllib.request
from concurrent.futures import ThreadPoolExecutor

CONTROL = 'http://127.0.0.1:3001'
API = 'https://127.0.0.1:3000'
# Deliberately restricted to the hard-coded local development fixture.
TLS = ssl._create_unverified_context()
DEVICE = '/v1/devices/11111111-1111-4111-8111-111111111111'


def request(path, body=None, control=False, headers=None):
    data = None if body is None else json.dumps(body).encode()
    req = urllib.request.Request((CONTROL if control else API) + path, data=data,
        headers={'Content-Type': 'application/json', **(headers or {})})
    try:
        with urllib.request.urlopen(req, context=TLS, timeout=5) as response:
            return response.status, response.read().decode()
    except urllib.error.HTTPError as error:
        with error:
            return error.code, error.read().decode()


def reset(**options):
    code, _ = request('/__test/reset', options, control=True)
    assert code == 200


def snapshot():
    return json.loads(request('/__test/snapshot', control=True)[1])


def command(value):
    return request(DEVICE + '/commands', {'commands': [{'command': value}]})


class FixtureTest(unittest.TestCase):
    def setUp(self):
        reset(startDelayMs=20, motionMs=100)

    def test_counted_open_close_and_header_presence(self):
        self.assertEqual(200, command('open')[0])
        time.sleep(.05)
        self.assertEqual('opening', snapshot()['status'])
        time.sleep(.12)
        self.assertEqual('open', snapshot()['status'])
        self.assertEqual(200, command('close')[0])
        time.sleep(.18)
        self.assertEqual('closed', snapshot()['status'])
        request('/ping', {}, headers={'Authorization': 'Bearer invented-test-token'})
        result = snapshot()
        self.assertEqual(['open', 'close'], result['acceptedCommands'])
        self.assertEqual(2, result['requests']['commands'])
        self.assertEqual(1, result['authorizationPresent']['ping'])
        self.assertNotIn('invented-test-token', json.dumps(result))

    def test_reset_cancels_old_motion(self):
        reset(startDelayMs=10, motionMs=250)
        command('open')
        time.sleep(.05)
        reset(status='closed')
        time.sleep(.3)
        result = snapshot()
        self.assertEqual('closed', result['status'])
        self.assertEqual([], result['acceptedCommands'])
        self.assertEqual({}, result['requests'])

    def test_error_and_malformed_response_scripts(self):
        request('/__test/script', {'endpoint': 'status', 'statusCode': 429, 'body': '{"error":"retry in 1000 millis"}'}, control=True)
        self.assertEqual(429, request(DEVICE + '/status')[0])
        request('/__test/script', {'endpoint': 'status', 'body': 'not-json'}, control=True)
        self.assertEqual('not-json', request(DEVICE + '/status')[1])
        self.assertEqual(200, request(DEVICE + '/status')[0])
        request('/__test/script', {'endpoint': 'commands', 'statusCode': 500}, control=True)
        self.assertEqual(500, command('open')[0])
        self.assertEqual([], snapshot()['acceptedCommands'])

    def test_reset_invalidates_delayed_command(self):
        request('/__test/script', {'endpoint': 'commands', 'delayMs': 300}, control=True)
        with ThreadPoolExecutor() as pool:
            pending = pool.submit(command, 'open')
            deadline = time.monotonic() + 2
            while snapshot()['requests'].get('commands', 0) != 1:
                self.assertLess(time.monotonic(), deadline)
                time.sleep(.01)
            reset()
            self.assertEqual(409, pending.result()[0])
        self.assertEqual([], snapshot()['acceptedCommands'])

    def test_replacement_command_cancels_previous_motion(self):
        reset(startDelayMs=0, motionMs=150)
        command('open')
        command('close')
        time.sleep(.2)
        self.assertEqual('closed', snapshot()['status'])
        self.assertEqual(['open', 'close'], snapshot()['acceptedCommands'])

    def test_invalid_input_and_control_boundaries(self):
        self.assertEqual(400, request(DEVICE + '/commands', {'commands': []})[0])
        self.assertEqual(400, command('toggle')[0])
        self.assertEqual(403, request('/__test/snapshot')[0])
        self.assertEqual(404, request('/v1/devices', control=True)[0])
        self.assertEqual(400, request('/__test/reset', {'motionMs': -1}, control=True)[0])
        self.assertEqual(400, request('/__test/script', {'endpoint': 'wrong'}, control=True)[0])


if __name__ == '__main__':
    try:
        result = unittest.main(exit=False, verbosity=2).result
    finally:
        reset()
    sys.exit(0 if result.wasSuccessful() else 1)


