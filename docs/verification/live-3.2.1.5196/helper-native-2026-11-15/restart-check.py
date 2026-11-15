import json,hashlib,os,sqlite3,subprocess,time,urllib.request,xml.etree.ElementTree as E
from pathlib import Path
ROOT=Path('/srv/docker-state/rollback-preparation/bandcamp-helper-native-20261115T170243Z');C=Path('/srv/docker-state/stacks/media/lidarr');dll=C/'plugins/jtstothard/lidarr-plugin-bandcamp/Lidarr.Plugin.Bandcamp.dll';expected='674d2de854890f625e84d0d3702d0a9cbc3f0860c9479f5eee6f86aa93c26cc4';image='sha256:5618a549ba6ef21318d24f869764fa58f51629b063a1a1ebf11cf8f32d1f5b8a'
cfg=E.parse(C/'config.xml').getroot();base='http://192.168.10.252:'+cfg.findtext('Port')+(cfg.findtext('UrlBase') or '')+'/api/v1/'
def api(path,data=None):
 req=urllib.request.Request(base+path,data=None if data is None else json.dumps(data).encode(),headers={'X-Api-Key':cfg.findtext('ApiKey'),'Content-Type':'application/json'})
 with urllib.request.urlopen(req,timeout=90) as r:return json.load(r)
def inspect():return json.loads(subprocess.check_output(['docker','inspect','lidarr']))[0]
def sha(p):return hashlib.sha256(p.read_bytes()).hexdigest()
def definitions_match():
 with sqlite3.connect((C/'lidarr.db').as_uri()+'?mode=ro',uri=True) as live,sqlite3.connect((ROOT/'lidarr.db').as_uri()+'?mode=ro',uri=True) as old:
  return all(live.execute('select Name,Settings from '+t+' where Id=?',(i,)).fetchone()==old.execute('select Name,Settings from '+t+' where Id=?',(i,)).fetchone() for t,i in [('DownloadClients',12),('Indexers',26)])
stage='preflight'
try:
 state=inspect();assert state['State']['Running'] and state['Image']==image and sha(dll)==expected and definitions_match()
 assert sha(C/'config.xml')==sha(ROOT/'config.xml') and sha(C.parent/'compose.yaml')==sha(ROOT/'compose.yaml')
 assert sum(bool(t.get('trackFileId')) for t in api('track?albumId=235835'))==11
 assert not [x for x in api('queue?pageSize=100')['records'] if x.get('downloadClientId')==12]
 assert not [x for x in api('command') if x.get('status') in ['started','queued'] and any(k in x.get('name','').lower() for k in ['import','downloadedalbumsscan'])]
 fd=os.open(ROOT/'restart-dispatch.jsonl',os.O_WRONLY|os.O_CREAT|os.O_EXCL,0o600)
 with os.fdopen(fd,'w') as receipt:
  def record(phase):receipt.write(json.dumps({'phase':phase,'utc':time.time()})+'\n');receipt.flush();os.fsync(receipt.fileno());print('phase='+phase,flush=True)
  record('dispatched')
  for action in ['stop','start']:
   stage=action
   with (ROOT/('restart-'+action+'.log')).open('x') as f:subprocess.run(['docker','compose','-f',str(C.parent/'compose.yaml'),action,'lidarr'],stdout=f,stderr=subprocess.STDOUT,check=True)
   record(action)
  stage='readiness';ready=False
  for _ in range(15):
   try:ready=api('system/status').get('version')=='3.2.1.5196'
   except Exception:pass
   if ready:break
   time.sleep(2)
  assert ready and inspect()['Id']==state['Id'] and inspect()['Image']==image and sha(dll)==expected and definitions_match()
  record('verified')
  stage='provider_tests'
  for kind,i in [('downloadclient',12),('indexer',26)]:assert api(kind+'/test',api(kind+'/'+str(i))) in [[],{},None]
  releases=api('release?albumId=235835');bc=[r for r in releases if r.get('indexerId')==26 and r.get('quality',{}).get('quality',{}).get('name')=='FLAC']
  assert bc and definitions_match()
  result={'restart_completed':True,'same_container':True,'same_image':True,'same_dll':True,'saved_provider_definitions_unchanged':True,'downloader_test_pass':True,'shared_indexer_test_pass':True,'flac_search_results':len(bc),'flac_search_approved':sum(bool(r.get('approved')) for r in bc),'linked_files_after_restart':sum(bool(t.get('trackFileId')) for t in api('track?albumId=235835')),'retry_flag':api('config/downloadclient')['autoRedownloadFailed'],'identity_changed':False,'changed_cookie_restart_qualified':False}
  (ROOT/'restart-result.json').write_text(json.dumps(result,indent=2));print(json.dumps(result))
except Exception as ex:
 print(json.dumps({'result':'STOP','stage':stage,'error_type':type(ex).__name__,'rollback_performed':False}));raise SystemExit(1)
