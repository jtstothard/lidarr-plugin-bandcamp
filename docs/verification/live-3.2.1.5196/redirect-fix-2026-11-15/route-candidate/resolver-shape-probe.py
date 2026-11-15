"""Plan-only resolver diagnostic. --live needs fresh approval after the failed grab.

Run on media-vm. At most two HTTPS GETs, no redirects/retries, no archive URL
follow, no archive saved. A resolver GET may still affect server-side download
accounting/conversion. Python urllib differs from Lidarr's managed transport;
its result must not be presented as proof of the native response or root cause.
"""
import argparse
import html
from html.parser import HTMLParser
import json
import os
import re
from pathlib import Path
import sqlite3
import time
import urllib.error
import urllib.parse
import urllib.request

ROOT = Path('/srv/docker-state/rollback-preparation/lidarr-resolver-route-20261115T150021Z/approved-native-attempt')


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None


class Page(HTMLParser):
    def __init__(self):
        super().__init__()
        self.blobs = []

    def handle_starttag(self, tag, attrs):
        values = dict(attrs)
        if 'data-blob' in values:
            try:
                self.blobs.append(json.loads(html.unescape(values['data-blob'])))
            except (ValueError, TypeError):
                pass


def origin_kind(url):
    try:
        uri = urllib.parse.urlsplit(url)
        if uri.scheme != 'https' or uri.username or uri.password or uri.port not in (None, 443) or uri.fragment:
            return 'Refused'
        if uri.hostname == 'bandcamp.com':
            return 'AccountHttps'
        if uri.hostname and (uri.hostname.endswith('.bandcamp.com') or uri.hostname == 'bcbits.com' or uri.hostname.endswith('.bcbits.com')):
            return 'BandcampCdnHttps'
    except ValueError:
        pass
    return 'Refused'


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--live', action='store_true')
    args = parser.parse_args()
    if not args.live:
        print(json.dumps({'mode': 'PLAN_ONLY', 'maximum_requests': 2,
                          'follow_redirects': False, 'fetch_returned_archive_url': False,
                          'save_archive': False, 'transport': 'Python urllib, not native Lidarr'}))
        return

    stage = 'preflight'
    report = {'mode': 'METADATA_PROBE', 'transport': 'Python urllib, not native Lidarr', 'requests': 0}
    output = None
    try:
        selected = json.loads((ROOT / 'release-search.json').read_text())
        if len(selected) != 1:
            raise ValueError('ambiguous selected release')
        page_url = urllib.parse.urldefrag(selected[0]['downloadUrl']).url
        if origin_kind(page_url) != 'AccountHttps' or urllib.parse.urlsplit(page_url).path != '/download':
            raise ValueError('unexpected owned download-page origin/path')
        with sqlite3.connect('file:/srv/docker-state/stacks/media/lidarr/lidarr.db?mode=ro', uri=True) as db:
            settings = json.loads(db.execute('SELECT Settings FROM DownloadClients WHERE Id=12').fetchone()[0])
        raw = next(v for k, v in settings.items() if k.lower() == 'cookies')
        identity = next((v.strip()[9:] for v in raw.split(';') if v.strip().startswith('identity=')), raw)
        if not identity or any(ord(c) <= 32 or ord(c) >= 127 or c in ';,' for c in identity):
            raise ValueError('minimal identity unavailable')
        fd = os.open(ROOT / 'resolver-shape-probe-dispatch.json', os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
        with os.fdopen(fd, 'w') as stream:
            json.dump({'maximum_requests': 2, 'no_archive_follow': True}, stream)
        output = ROOT / 'resolver-shape-probe-result.json'
        opener = urllib.request.build_opener(NoRedirect())

        def get(url):
            if report['requests'] >= 2 or origin_kind(url) == 'Refused':
                raise ValueError('request boundary refused')
            headers = {'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36',
                       'Accept': 'text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8',
                       'Accept-Language': 'en-US,en;q=0.9'}
            if origin_kind(url) == 'AccountHttps':
                headers['Cookie'] = 'identity=' + identity
            if report['requests']:
                time.sleep(2)
            report['requests'] += 1
            try:
                return opener.open(urllib.request.Request(url, headers=headers), timeout=20)
            except urllib.error.HTTPError as response:
                return response

        stage = 'owned_download_page'
        with get(page_url) as response:
            report['page_status'] = response.code
            if response.code != 200:
                raise ValueError('page refused; no follow/retry')
            body = response.read(4194305)
            if len(body) > 4194304:
                raise ValueError('page limit')
        page = Page()
        page.feed(body.decode('utf-8', errors='replace'))
        urls = [item['downloads']['flac']['url'] for blob in page.blobs
                for item in blob.get('digital_items', blob.get('download_items', []))
                if item.get('downloads', {}).get('flac', {}).get('url')]
        if len(urls) != 1:
            raise ValueError('FLAC resolver ambiguous')
        stage = 'resolver_once'
        raw_url = urls[0]
        if origin_kind(raw_url) == 'Refused':
            raise ValueError('format origin refused')
        parsed = urllib.parse.urlsplit(raw_url)
        authority_end = raw_url.index('/', raw_url.index('://') + 3)
        if parsed.path.startswith('/download/'):
            raw_url = raw_url[:authority_end] + '/statdownload/' + raw_url[authority_end + len('/download/'):]
        url = raw_url + ('&' if '?' in raw_url else '?') + '.format=flac&.json=true' 
        with get(url) as response:
            report['resolver_status'] = response.code
            locations = response.headers.get_all('Location', [])
            report['location_count'] = len(locations)
            report['location_kind'] = origin_kind(urllib.parse.urljoin(url, locations[0])) if len(locations) == 1 else 'AbsentOrAmbiguous'
            first = response.read(4)
            if first == b'PK\x03\x04':
                report['body_kind'] = 'ZipHeaderOnly'
            else:
                body = first + response.read(1048573)
                if len(body) > 1048576:
                    report['body_kind'] = 'OverLimit'
                else:
                    text = body.decode('utf-8', errors='replace')
                    def shape(value):
                        if not isinstance(value, str): return 'AbsentOrNonString'
                        if not value.strip(): return 'Empty'
                        if value.startswith('//'): return 'ProtocolRelative'
                        if value.startswith('/'): return 'RootRelative'
                        return origin_kind(value)
                    # Mirror the current regex without exposing matched values.
                    match = re.search(r'"url"\s*:\s*"([^"]+)"', text)
                    report['current_regex_match_kind'] = shape(match.group(1).replace('\\/', '/').replace('\\u0026','&')) if match else 'Absent'
                    try:
                        value = json.loads(text)
                        report['body_kind'] = 'Json'
                    except ValueError:
                        try:
                            value = json.loads(text[text.index('{'):text.rindex('}')+1])
                            report['body_kind'] = 'WrappedJson'
                        except (ValueError, TypeError):
                            value = None
                            report['body_kind'] = 'Html' if '<html' in text.lower() or '<!doctype html' in text.lower() else 'Other'
                    if isinstance(value,dict):
                        report['json_url_kind'] = shape(value.get('url'))
                        report['json_retry_url_kind'] = shape(value.get('retry_url'))
        report['result'] = 'OBSERVED_NO_ARCHIVE_FOLLOW'
    except Exception as error:
        report.update(result='STOP_NO_RETRY', stage=stage, error_type=type(error).__name__)
    if output is not None:
        fd = os.open(output, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
        with os.fdopen(fd, 'w') as stream:
            json.dump(report, stream, indent=2)
    print(json.dumps(report))


if __name__ == '__main__':
    main()
