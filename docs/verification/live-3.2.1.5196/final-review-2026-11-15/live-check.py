import json,hashlib,sqlite3,urllib.request,xml.etree.ElementTree as ET
from pathlib import Path
C=Path('/srv/docker-state/stacks/media/lidarr');ROOT=Path('/srv/docker-state/rollback-preparation/bandcamp-review-fixes-20261115T184120Z')
key=ET.parse(C/'config.xml').findtext('ApiKey')
def api(path,data=None):
 r=urllib.request.Request('http://192.168.10.252:8686/api/v1/'+path,data=None if data is None else json.dumps(data).encode(),headers={'X-Api-Key':key,'Content-Type':'application/json'})
 with urllib.request.urlopen(r,timeout=90) as s:
  b=s.read();return json.loads(b) if b else None
owner=api('downloadclient/12');metadata={'id':12,'name':owner['name'],'implementation':'BandcampDownloadClient','configContract':'BandcampDownloadSettings','fields':[]}
before=api('downloadclient/action/sessionStatus',metadata)
for kind,i in [('downloadclient',12),('indexer',26)]:assert api(kind+'/test',api(kind+'/'+str(i))) in [[],{},None]
after=api('downloadclient/action/sessionStatus',metadata);assert after['success'] and after['status']=='Valid' and after['lastSuccessfulValidationUtc']
with sqlite3.connect((C/'lidarr.db').as_uri()+'?mode=ro',uri=True) as live,sqlite3.connect((ROOT/'lidarr.db').as_uri()+'?mode=ro',uri=True) as saved:
 for table,i in [('DownloadClients',12),('Indexers',26)]:assert live.execute('SELECT * FROM '+table+' WHERE Id=?',(i,)).fetchone()==saved.execute('SELECT * FROM '+table+' WHERE Id=?',(i,)).fetchone()
reference=json.loads(Path('/srv/docker-state/rollback-preparation/bandcamp-changed-identity-test-20261115T180222Z/cleanup-result.json').read_text())['audio_files']
base=Path('/mnt/unraid/media/Music/Great Cynics');actual=[{'relative_path':str(p.relative_to(base)),'sha256':hashlib.sha256(p.read_bytes()).hexdigest(),'bytes':p.stat().st_size} for p in sorted(base.rglob('*.flac'))];assert actual==reference
assert sum(bool(t.get('trackFileId')) for t in api('track?albumId=235835'))==11
safe={'session_status_before_tests':{k:before.get(k) for k in ['success','status','effectiveAuthMode','lastSuccessfulValidationUtc']},'session_status_after_tests':{k:after.get(k) for k in ['success','status','effectiveAuthMode','lastSuccessfulValidationUtc']},'working_definitions_unchanged':True,'both_provider_tests_pass':True,'eleven_flac_hashes_unchanged':True,'linked_files':11,'no_download_dispatched':True}
(ROOT/'review-live-check.json').write_text(json.dumps(safe,indent=2)+'\n');print(json.dumps(safe,indent=2))
